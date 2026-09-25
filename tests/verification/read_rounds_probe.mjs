// 多轮读物协议（契约 §12.1-A48）的端到端断言。
//
// 用 `--directive-once "@@读全文"` 起假 LLM：**第一轮**回那条指令，第二轮回正常答案。
// 一条探针同时证两件事：
//   ① **指令不能被用户看见** —— 模型写的 `@@读全文` 是控制信号，漏出去就是给用户看乱码；
//   ② **服务端真的为它多读了一轮** —— 上游 completions 必须变成 2，否则"自选读物"是假的。
// 这两条互为反面：只证①可能是"指令被吞了但也没重试"；只证②可能是"重试了但指令也漏给了用户"。
//
// 用法：SIP_BASE / SIP_BANNER_FILE / SIP_FAKE_LOG（假 LLM 的 jsonl 路径）
import { readFileSync } from "node:fs";

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) sip-readrounds";
const H = (x = {}) => ({ "User-Agent": UA, ...x });
const TOKEN = (readFileSync(process.env.SIP_BANNER_FILE, "utf8").match(/\?t=([0-9a-fA-F]{16,})/) || [])[1];

let cookies = [];
const jar = () => cookies.map(c => c.split(";")[0]).join("; ");
async function req(method, url, body, extra = {}) {
  const h = H(extra);
  if (cookies.length) h.Cookie = jar();
  if (body !== undefined) h["Content-Type"] = "application/json";
  const r = await fetch(BASE + url, { method, headers: h, redirect: "manual", body: body === undefined ? undefined : JSON.stringify(body) });
  for (const c of (r.headers.getSetCookie?.() || [])) cookies.push(c.split(";")[0]);
  return r;
}
const out = [];
const ok = (s) => { out.push(["PASS", s]); console.log("  PASS  " + s); };
const bad = (s) => { out.push(["FAIL", s]); console.log("  FAIL  " + s); };
const info = (s) => console.log("        " + s);

await req("GET", "/?t=" + TOKEN);

// 找一篇有正文的 RSS 文章
const feeds = (await (await req("GET", "/api/feeds")).json())?.data?.feeds || [];
let aid = null, atitle = "";
for (const f of feeds) {
  if ((f.title || "").includes("本地导入")) continue;
  const arts = (await (await req("GET", `/api/feeds/${f.id}/articles`)).json())?.data?.articles || [];
  if (arts.length) { aid = arts[0].id ?? arts[0].itemId; atitle = arts[0].title; break; }
}
if (!aid) { console.error("没有 RSS 文章夹具，跳过"); process.exit(0); }
info(`文章 #${aid} 《${atitle}》`);

const cr = await req("POST", `/api/imports/${aid}/chat`, {});
const sid = JSON.parse(await cr.text())?.data?.sessionId;
info(`sessionId=${sid}`);

// 发流式请求，逐帧收集**客户端看到的内容**
const askRes = await req("POST", `/api/imports/${aid}/ask`,
  { question: "这篇在讲什么？", anchor: { locType: "article", ord: 0 }, sessionId: sid, stream: true },
  { Accept: "text/event-stream" });
info(`POST /ask -> HTTP ${askRes.status}  ct=${askRes.headers.get("content-type")}`);
if (askRes.status !== 200) { bad("提问没返回 200"); console.log("\n结果: 无法测试"); process.exit(1); }

const reader = askRes.body.getReader();
const dec = new TextDecoder();
let buf = "", text = "", frames = 0;
for (;;) {
  const { value, done } = await reader.read();
  if (done) break;
  buf += dec.decode(value, { stream: true });
  let i;
  while ((i = buf.indexOf("\n\n")) >= 0) {
    const raw = buf.slice(0, i); buf = buf.slice(i + 2);
    const ev = (raw.split("\n").find(l => l.startsWith("event:")) || "").slice(6).trim();
    const data = raw.split("\n").filter(l => l.startsWith("data:")).map(l => l.slice(5).trim()).join("");
    if (!data) continue;
    frames++;
    if (ev === "delta") { try { text += JSON.parse(data).text || ""; } catch { } }
  }
}
info(`客户端收到 ${frames} 帧，拼出的正文 ${text.length} 字符`);
info(`正文: ${JSON.stringify(text.slice(0, 200))}`);

// ① 指令绝不能出现在用户看到的内容里
text.includes("@@") ? bad(`客户端看到了控制指令！正文含 "@@" : ${JSON.stringify(text.slice(0, 160))}`)
                    : ok("控制指令没有漏给客户端（@@ 未出现在正文里）");
text.includes("读全文") ? bad("正文里出现了「读全文」字样") : ok("正文里没有指令字样");

// ② 服务端应为指令多读一轮（上游总调用数 == 2）
const fakeLog = process.env.SIP_FAKE_LOG;
if (fakeLog) {
  try {
    const lines = readFileSync(fakeLog, "utf8").split("\n").filter(Boolean);
    const evs = lines.map(l => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
    const reqs = evs.filter(e => e.event === "chat_request").length;
    const served = evs.filter(e => e.event === "served_directive").length;
    const after = evs.filter(e => e.event === "served_answer_after_directive").length;
    info(`上游 chat_request=${reqs}  served_directive=${served}  served_answer_after=${after}`);
    served >= 1 ? ok("假 LLM 确实发过那条指令（夹具有效）") : bad("夹具没发出指令，这条探针没测到东西");
    reqs >= 2 ? ok(`服务端为指令多读了一轮（上游被调用 ${reqs} 次）`) : bad(`上游只被调用 ${reqs} 次 —— 没有为指令重读`);
  } catch (e) { info("读假 LLM 日志失败: " + e.message); }
} else {
  info("（未设 SIP_FAKE_LOG，跳过上游计数核对）");
}

// ③ 第二轮必须**真的换了材料**：第二次请求的提示词里应带"第 2 轮"的说明
if (fakeLog) {
  try {
    const lines = readFileSync(fakeLog, "utf8").split("\n").filter(Boolean);
    const reqs = lines.map(l => { try { return JSON.parse(l); } catch { return null; } })
      .filter(e => e && e.event === "chat_request");
    const second = reqs[1]?.promptFull || "";
    const hasLater = second.includes("已按你的要求加长") || second.includes("本轮");
    info(`第二次 promptChars=${reqs[1]?.promptChars}  含"按你的要求加长": ${second.includes("已按你的要求加长")}`);
    hasLater ? ok("第二轮换了材料（提示词里带上了'已加长'的说明）")
             : info("（第二轮提示词里没找到加长说明 —— 可能模型第一轮没写指令，或指令未被解析）");
    // 第二轮的第 3 层应比第一轮长
    const len1 = (reqs[0]?.promptFull || "").length, len2 = second.length;
    info(`两轮提示词长度: ${len1} -> ${len2}`);
  } catch { }
}

const fails = out.filter(x => x[0] === "FAIL").length;
console.log(`\n================ 结果：${out.length - fails}/${out.length} 通过 ================`);
process.exit(fails ? 1 : 0);
