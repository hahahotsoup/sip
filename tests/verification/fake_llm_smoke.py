#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""假 LLM 的自检客户端：证明「工具本身」能测出逐块到达与上游取消。

用法：python tests/verification/fake_llm_smoke.py --port 8931 [--mode stream|abort|nonstream|embed]

输出 JSON：
  stream  : 每块的到达时刻（相对请求发出），first_delta_ms / total_ms / done_seen / 事件顺序问题
  abort   : 读到第 1 块就断开连接，返回本端耗时（服务端应记录 client_disconnected）
"""
import argparse
import json
import socket
import sys
import time
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")


def raw_stream(host, port, payload, read_chunks, then_close=False, timeout=30):
    """手写 HTTP/1.1 请求 + 逐块读取，才能拿到真实到达时刻（urllib 会缓冲）。"""
    body = json.dumps(payload).encode("utf-8")
    req = (f"POST /v1/chat/completions HTTP/1.1\r\nHost: {host}:{port}\r\n"
           f"Content-Type: application/json\r\nContent-Length: {len(body)}\r\n"
           f"Accept: text/event-stream\r\nConnection: close\r\n\r\n").encode("ascii") + body
    t0 = time.perf_counter()
    s = socket.create_connection((host, port), timeout=timeout)
    s.sendall(req)
    events = []
    buf = b""
    got = 0
    header_done = False
    try:
        while True:
            data = s.recv(4096)
            if not data:
                events.append({"t_ms": round((time.perf_counter() - t0) * 1000, 1), "event": "eof"})
                break
            buf += data
            if not header_done and b"\r\n\r\n" in buf:
                header_done = True
                events.append({"t_ms": round((time.perf_counter() - t0) * 1000, 1), "event": "headers"})
                buf = buf.split(b"\r\n\r\n", 1)[1]
            # 解 chunked 里的 SSE 行（够用即可：按行切）
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                line = line.strip()
                if not line:
                    continue
                if line.startswith(b"data:"):
                    got += 1
                    tok = line[5:].strip().decode("utf-8", "replace")
                    events.append({"t_ms": round((time.perf_counter() - t0) * 1000, 1),
                                   "event": "delta", "n": got, "payload": tok[:80]})
            if got >= read_chunks:
                if then_close:
                    events.append({"t_ms": round((time.perf_counter() - t0) * 1000, 1), "event": "client_abort"})
                    break
                if any(e.get("payload") == "[DONE]" for e in events):
                    break
    finally:
        s.close()
    return events


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8931)
    ap.add_argument("--mode", default="stream")
    ap.add_argument("--chunks", type=int, default=6)
    a = ap.parse_args()
    base = f"http://{a.host}:{a.port}"
    out = {"mode": a.mode, "port": a.port}

    if a.mode == "embed":
        body = json.dumps({"model": "fake-embed", "input": ["第一章 讲的是孔子", "第二章 讲的是墨子"]}).encode()
        r = urllib.request.urlopen(urllib.request.Request(base + "/v1/embeddings", body,
                                                          {"Content-Type": "application/json"}), timeout=30)
        j = json.loads(r.read())
        out["n"] = len(j["data"])
        out["dims"] = len(j["data"][0]["embedding"])
        out["deterministic"] = j["data"][0]["embedding"] == json.loads(urllib.request.urlopen(
            urllib.request.Request(base + "/v1/embeddings", body, {"Content-Type": "application/json"}),
            timeout=30).read())["data"][0]["embedding"]
    elif a.mode == "nonstream":
        body = json.dumps({"model": "fake", "messages": [{"role": "user", "content": "非流式兜底"}]}).encode()
        t0 = time.perf_counter()
        r = urllib.request.urlopen(urllib.request.Request(base + "/v1/chat/completions", body,
                                                          {"Content-Type": "application/json"}), timeout=30)
        j = json.loads(r.read())
        out["ms"] = round((time.perf_counter() - t0) * 1000, 1)
        out["content"] = j["choices"][0]["message"]["content"]
        out["usage"] = j.get("usage")
    elif a.mode == "abort":
        ev = raw_stream(a.host, a.port, {"model": "fake", "stream": True,
                                         "messages": [{"role": "user", "content": "断开测试"}]},
                        read_chunks=1, then_close=True)
        out["events"] = ev
        out["last_ms"] = ev[-1]["t_ms"] if ev else None
    else:
        ev = raw_stream(a.host, a.port, {"model": "fake", "stream": True,
                                         "messages": [{"role": "user", "content": "流式测试：请逐字回答"}]},
                        read_chunks=10**6)
        deltas = [e for e in ev if e.get("event") == "delta"]
        out["events"] = ev
        out["delta_count"] = len(deltas)
        out["first_delta_ms"] = deltas[0]["t_ms"] if deltas else None
        out["last_delta_ms"] = deltas[-1]["t_ms"] if deltas else None
        out["spread_ms"] = round(deltas[-1]["t_ms"] - deltas[0]["t_ms"], 1) if len(deltas) > 1 else 0
        out["done_seen"] = any(e.get("payload") == "[DONE]" for e in ev)
        out["order_ok"] = bool(ev) and ev[0]["event"] == "headers"
        # 逐块到达判据：不是一次性吐出 → 首块与末块之间有明显时间差
        out["progressive"] = out["delta_count"] > 1 and out["spread_ms"] > 50
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
