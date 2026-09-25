#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本地假 LLM：OpenAI 兼容的 /v1/chat/completions（流式 + 非流式）与 /v1/embeddings。

存在理由：验证「SSE 是不是真的逐字流出」不能连真模型（不确定性 + 花钱 + 网络）。
本服务把每个 chunk 用可配置的间隔发出去，并记录**服务端视角**的时间线，
于是「首字延迟」和「上游被取消」都能被证据化，而不是靠观察。

流式契约（照抄 OpenAI，客户端不该要求更少）：
    HTTP/1.1 200 text/event-stream
    data: {"id":"...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}
    data: {"id":"...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}
    data: {"id":"...","object":"chat.completion.chunk","choices":[],"usage":{...}}      # deepseek 风格
    data: [DONE]

关键开关（用于制造可复现的边界场景）：
    --chunks N        正文切成 N 块
    --delay-ms D      块间间隔（默认 120ms，用于证明"不是一次性吐出"）
    --first-delay-ms  首块前的延迟
    --hang-after-first  发完首块后睡 --hang-seconds 秒：客户端此时断开，就能证明取消真的到了上游
    --error-after N   发 N 块后返回 HTTP 500（流中断）
    --no-done         不发送 [DONE]（测试截断鲁棒性）
    --bad-json        发一块非法 JSON（测试解析容错）
    --silent-seconds  请求进来到发首块之前先睡（测试超时/取消）
    --usage-mode deepseek|openai|none
    --dims N          嵌入维度（默认 768，与 ai_config.json 默认一致）

服务端证据：所有事件写入 --log（JSONL，含相对 t0 的毫秒时间戳）：
    request / stream_headers_sent / chunk_sent / client_disconnected / done / finished
「上游是否被取消」的判据就是 client_disconnected 是否出现，以及它出现在第几块之后。

辅助端点：GET /__log 回放日志；POST /__reset 清空状态与日志；GET /__stats 摘要。
用法：python tests/verification/fake_llm_server.py --port 8931 --log path.jsonl [其它开关]
打印一行 `FAKE_LLM_READY port=...` 到 stdout 表示可接受请求（供脚本等待）。
"""
import argparse
import json
import math
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ARGS = None
T0 = time.time()
LOG_LOCK = threading.Lock()


def now_ms():
    return round((time.time() - T0) * 1000, 1)


class State:
    def __init__(self):
        self.requests = 0
        self.completions = 0
        self.embeddings = 0
        self.disconnects = 0
        self.streams_completed = 0
        # 每会话的调用次数（多轮读物自测用；按会话才能重复验证）
        self.session_calls = {}


STATE = State()


def log(event, **kv):
    rec = {"t_ms": now_ms(), "event": event, **kv}
    line = json.dumps(rec, ensure_ascii=False)
    with LOG_LOCK:
        if ARGS.log:
            with open(ARGS.log, "a", encoding="utf-8") as fh:
                fh.write(line + "\n")
        if ARGS.echo:
            print(line, flush=True)


# ── 确定性嵌入：同一个文本永远同一向量，便于"检索结果可复现" ──
def embed(text, dims):
    vec = [0.0] * dims
    h = 2166136261
    for ch in text:
        h = ((h ^ ord(ch)) * 16777619) & 0xFFFFFFFF
    for i in range(dims):
        h = (h * 1103515245 + 12345) & 0x7FFFFFFF
        vec[i] = (h / 0x7FFFFFFF) * 2.0 - 1.0
    norm = math.sqrt(sum(v * v for v in vec)) or 1.0
    return [v / norm for v in vec]


def body_text(messages):
    parts = []
    for m in messages or []:
        c = m.get("content")
        if isinstance(c, str):
            parts.append(c)
        elif isinstance(c, list):
            parts.append(" ".join(str(x.get("text", "")) for x in c if isinstance(x, dict)))
    return "\n".join(parts)


def make_chunks(text, n):
    if n <= 1:
        return [text]
    size = max(1, math.ceil(len(text) / n))
    return [text[i:i + size] for i in range(0, len(text), size)]


def session_key_of(prompt):
    """把"一个会话"识别出来：取提示词里 `【问题】` 之后那一行。
    为什么不取 prompt[:200]：同一会话的两轮里，系统提示词相同但资料区不同，
    前 200 字符会落在资料区之前 —— 实际上第一轮与第二轮的 prompt 开头**确实相同**，
    但第二轮多了一句"已按你的要求加长"。所以取**问题**最稳：同会话同问题，
    而探针每次新建会话、问题也不同，不会互相串。"""
    marker = "【问题】"
    i = prompt.find(marker)
    if i < 0:
        return prompt[:80]
    rest = prompt[i + len(marker):].lstrip("\n")
    return rest.split("\n", 1)[0].strip()[:80]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "fake-llm/1.0"

    def log_message(self, *a):  # 关掉 access 噪音，走我们的 jsonl
        pass

    # ── 工具 ──
    def _json(self, code, obj):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(n) if n else b""
        try:
            return json.loads(raw.decode("utf-8") or "{}")
        except Exception:  # noqa: BLE001
            return {"__unparsed__": raw.decode("utf-8", "replace")}

    def do_GET(self):
        if self.path.startswith("/__log"):
            try:
                with open(ARGS.log, encoding="utf-8") as fh:
                    data = fh.read()
            except OSError:
                data = ""
            self.send_response(200)
            self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
            b = data.encode("utf-8")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            self.wfile.write(b)
            return
        if self.path.startswith("/__stats"):
            self._json(200, STATE.__dict__)
            return
        self._json(404, {"error": "not found"})

    def do_POST(self):
        if self.path.startswith("/__reset"):
            STATE.__init__()
            if ARGS.log:
                open(ARGS.log, "w", encoding="utf-8").close()
            self._json(200, {"ok": True})
            return
        path = self.path.split("?")[0].rstrip("/")
        if path.endswith("/embeddings"):
            return self.handle_embeddings()
        if path.endswith("/chat/completions"):
            return self.handle_chat()
        self._json(404, {"error": f"unknown path {self.path}"})

    def handle_embeddings(self):
        body = self._read_body()
        STATE.requests += 1
        STATE.embeddings += 1
        inp = body.get("input")
        items = inp if isinstance(inp, list) else [inp or ""]
        dims = body.get("dimensions") or ARGS.dims
        log("embeddings_request", model=body.get("model"), n=len(items), dims=dims, auth=bool(self.headers.get("Authorization")))
        data = [{"object": "embedding", "index": i, "embedding": embed(str(t), dims)}
                for i, t in enumerate(items)]
        self._json(200, {"object": "list", "data": data, "model": body.get("model") or "fake",
                         "usage": {"prompt_tokens": sum(len(str(t)) for t in items), "total_tokens": 0}})

    def handle_chat(self):
        body = self._read_body()
        STATE.requests += 1
        STATE.completions += 1
        stream = bool(body.get("stream"))
        model = body.get("model") or "fake-model"
        prompt = body_text(body.get("messages"))

        # 会话键：把"第一次调用"限定在**一个会话**里（见 session_key_of 的说明）。
        session_key = session_key_of(prompt)

        log("chat_request", model=model, stream=stream, auth=bool(self.headers.get("Authorization")),
            promptChars=len(prompt), promptHead=prompt[:120], promptFull=prompt)

        if ARGS.silent_seconds:
            time.sleep(ARGS.silent_seconds)

        # 回答内容：默认回显提示词里最后一个「问句」的形状，便于断言"答案与请求对应"
        answer = ARGS.answer if ARGS.answer is not None else f"[fake] 关于「{prompt[-40:].strip()}」的回答"

        # ── 多轮读物协议的自测开关（契约 §12.1-A48）──
        # `--directive-once 【读全文】`：**每个会话的第一次**调用回那条指令，之后回正常答案。
        # 为什么按会话而不是按全局计数：全局计数只在进程生命周期里第一次有效，
        # 于是**同一条探针跑第二遍就测不到东西**（实测：第二次跑只剩 2/2，看起来像功能坏了）。
        # 按会话计数才是"可重复验证"的。
        if ARGS.directive_once:
            sid = STATE.session_calls.get(session_key, 0)
            STATE.session_calls[session_key] = sid + 1
            if sid == 0:
                answer = ARGS.directive_once
                log("served_directive", directive=answer[:60], session=session_key[:40])
            else:
                answer = ARGS.answer_after or "（第二轮）已经读到更多内容了。"
                log("served_answer_after_directive", session=session_key[:40])

        if not stream:
            usage = {"prompt_tokens": max(1, len(prompt) // 2), "completion_tokens": max(1, len(answer) // 2),
                     "total_tokens": 0}
            usage["total_tokens"] = usage["prompt_tokens"] + usage["completion_tokens"]
            time.sleep(ARGS.first_delay_ms / 1000.0)
            log("chat_response", mode="nonstream", chars=len(answer))
            return self._json(200, {
                "id": "chatcmpl-fake", "object": "chat.completion", "created": int(time.time()), "model": model,
                "choices": [{"index": 0, "message": {"role": "assistant", "content": answer}, "finish_reason": "stop"}],
                "usage": usage})

        # ── 流式 ──
        cid = "chatcmpl-fake-%d" % int(time.time() * 1000)
        try:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream; charset=utf-8")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "keep-alive")
            # 不用 chunked：显式 Content-Length 缺失 → HTTP/1.1 需要 chunked 才能到 [DONE]
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
        except (BrokenPipeError, ConnectionResetError):
            log("client_disconnected", phase="before_headers")
            STATE.disconnects += 1
            return

        def send_event(payload):
            """写一条 SSE 事件。返回 False 表示客户端已断开（= 上游已感知取消）。"""
            blob = f"data: {payload}\n\n".encode("utf-8")
            head = f"{len(blob):X}\r\n".encode("ascii")
            try:
                self.wfile.write(head + blob + b"\r\n")
                self.wfile.flush()
                return True
            except (BrokenPipeError, ConnectionResetError, OSError):
                return False

        log("stream_headers_sent", model=model)

        if not send_event(json.dumps({"id": cid, "object": "chat.completion.chunk", "created": int(time.time()),
                                      "model": model,
                                      "choices": [{"index": 0, "delta": {"role": "assistant"}, "finish_reason": None}]},
                                     ensure_ascii=False)):
            STATE.disconnects += 1
            log("client_disconnected", phase="after_headers")
            return

        if ARGS.first_delay_ms:
            time.sleep(ARGS.first_delay_ms / 1000.0)

        chunks = make_chunks(answer, ARGS.chunks)
        for i, ch in enumerate(chunks):
            if ARGS.hang_after_first and i == 1:
                log("hang_begin", seconds=ARGS.hang_seconds)
                # 关键：睡在这里，客户端断开后我们应当在下一次写时感知到
                deadline = time.time() + ARGS.hang_seconds
                while time.time() < deadline:
                    time.sleep(0.05)
                log("hang_end")
            payload = json.dumps({"id": cid, "object": "chat.completion.chunk", "created": int(time.time()),
                                  "model": model,
                                  "choices": [{"index": 0, "delta": {"content": ch}, "finish_reason": None}]},
                                 ensure_ascii=False)
            if ARGS.bad_json and i == max(0, len(chunks) // 2):
                payload = '{"id": "broken", "choices": ['
            if not send_event(payload):
                STATE.disconnects += 1
                log("client_disconnected", phase=f"after_chunk_{i}")
                return
            log("chunk_sent", index=i, chars=len(ch), head=ch[:20])
            if ARGS.error_after and i + 1 >= ARGS.error_after:
                log("abort_stream", after=ARGS.error_after)
                try:
                    self.wfile.write(b"0\r\n\r\n")
                except OSError:
                    pass
                self.close_connection = True
                return
            if ARGS.delay_ms and i < len(chunks) - 1:
                time.sleep(ARGS.delay_ms / 1000.0)

        finish = json.dumps({"id": cid, "object": "chat.completion.chunk", "created": int(time.time()),
                             "model": model,
                             "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]}, ensure_ascii=False)
        if ARGS.usage_mode == "deepseek":
            finish = json.dumps({"id": cid, "object": "chat.completion.chunk", "created": int(time.time()),
                                 "model": model, "choices": [],
                                 "usage": {"prompt_tokens": max(1, len(prompt) // 2),
                                           "completion_tokens": max(1, len(answer) // 2),
                                           "total_tokens": max(1, len(prompt) // 2) + max(1, len(answer) // 2)}},
                                ensure_ascii=False)
        elif ARGS.usage_mode == "openai":
            finish = json.dumps({"id": cid, "object": "chat.completion.chunk", "created": int(time.time()),
                                 "model": model,
                                 "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
                                 "usage": {"prompt_tokens": max(1, len(prompt) // 2),
                                           "completion_tokens": max(1, len(answer) // 2),
                                           "total_tokens": max(1, len(prompt) // 2) + max(1, len(answer) // 2)}},
                                ensure_ascii=False)
        if not send_event(finish):
            STATE.disconnects += 1
            log("client_disconnected", phase="after_finish_chunk")
            return
        log("finish_chunk_sent")

        if not ARGS.no_done:
            try:
                blob = b"data: [DONE]\n\n"
                self.wfile.write(f"{len(blob):X}\r\n".encode("ascii") + blob + b"\r\n")
                self.wfile.write(b"0\r\n\r\n")
                self.wfile.flush()
                log("done_sent")
            except (BrokenPipeError, ConnectionResetError, OSError):
                STATE.disconnects += 1
                log("client_disconnected", phase="at_done")
                return
        STATE.streams_completed += 1
        log("stream_completed", chunks=len(chunks))
        self.close_connection = True


def main():
    global ARGS
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8931)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--log", default=None)
    p.add_argument("--echo", action="store_true")
    p.add_argument("--chunks", type=int, default=6)
    p.add_argument("--delay-ms", type=int, default=120)
    p.add_argument("--first-delay-ms", type=int, default=0)
    p.add_argument("--hang-after-first", action="store_true")
    p.add_argument("--hang-seconds", type=float, default=20.0)
    p.add_argument("--silent-seconds", type=float, default=0.0)
    p.add_argument("--error-after", type=int, default=0)
    p.add_argument("--no-done", action="store_true")
    p.add_argument("--bad-json", action="store_true")
    p.add_argument("--usage-mode", choices=["deepseek", "openai", "none"], default="deepseek")
    p.add_argument("--dims", type=int, default=768)
    p.add_argument("--answer", default=None)
    # 多轮读物协议：只在第一次调用回这条指令，之后回 --answer-after
    p.add_argument("--directive-once", default=None)
    p.add_argument("--answer-after", default=None)
    ARGS = p.parse_args()

    if ARGS.log:
        open(ARGS.log, "w", encoding="utf-8").close()
    srv = ThreadingHTTPServer((ARGS.host, ARGS.port), Handler)
    srv.daemon_threads = True
    # 真实端口（--port 0 时由系统分配）
    port = srv.server_address[1]
    print(f"FAKE_LLM_READY port={port}", flush=True)
    try:
        srv.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        pass
    finally:
        srv.server_close()


if __name__ == "__main__":
    main()
