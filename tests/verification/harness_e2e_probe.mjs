// 悬浮球「划词问 AI」链路端到端探针。
// 站在浏览器视角做真实 HTTP：引导链接换钥匙 → 列书 → 取目录 → 起会话 → 流式提问 → 逐帧读 SSE。
// 与 ai_frontend_check.mjs 的分工：那个查前端**语法/逻辑**，这个查前后端**真的接上了**。
import { readFileSync } from "node:fs";

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";

/* 从终端横幅解析引导令牌。
   ⚠ 两个坑都踩过，注释留在这里免得下次重演：
   1) 别用「PowerShell 写小文件 → node 读」：Set-Content 会带 BOM，令牌变成 "<BOM>0967d…"，
      换票必失败，而失败的样子（回引导页 + 吊销 cookie）极像服务端的 bug。
   2) 换票请求**必须带上和后续请求完全一样的 User-Agent**。sip 把会话钥匙绑在浏览器指纹
      （UA 的 SHA256）上，逐字节比对，不一致就当场吊销（Web.cs:2377 / 2399-2412）。
      这个保护是对的；错的是"换票不带 UA、后续带 UA"的探针 —— 会被判成两个浏览器。 */
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) sip-harness-probe";
const TOKEN = (readFileSync(process.env.SIP_BANNER_FILE, "utf8").match(/\?t=([0-9a-fA-F]{16,})/) || [])[1];
if (!TOKEN) { console.error("横幅里找不到 ?t= 引导令牌"); process.exit(2); }

let cookies = [];
const jar = () => cookies.map(c => c.split(";")[0]).join("; ");

async function req(method, url, body, extra = {}) {
  const headers = { "User-Agent": UA, ...extra };   // UA 全程一致：它是会话指纹（见文件头注释）
  if (cookies.length) headers["Cookie"] = jar();
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(BASE + url, {
    method, headers, redirect: "manual",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const sc = res.headers.getSetCookie ? res.headers.getSetCookie() : [];
  for (const c of sc) cookies.push(c.split(";")[0]);
  return res;
}

const out = [];
const ok = (s) => { out.push(["PASS", s]); console.log("  PASS  " + s); };
const bad = (s) => { out.push(["FAIL", s]); console.log("  FAIL  " + s); };
const info = (s) => console.log("        " + s);

console.log("=== 1. 引导链接换钥匙 ===");
let r = await req("GET", "/?t=" + TOKEN);
info(`GET /?t=… -> HTTP ${r.status}`);
const cookieNames = cookies.map(c => c.split("=")[0]);
info("拿到 cookie: " + (cookieNames.join(", ") || "(无)"));
cookieNames.some(n => n.startsWith("sip_")) ? ok("引导链接换到了会话 cookie") : bad("没有拿到会话 cookie");

console.log("\n=== 2. 裸访问必须被拒（钥匙是唯一带外信道）===");
const saved = cookies.slice();
cookies = [];
r = await req("GET", "/");
info(`GET / (无 cookie) -> HTTP ${r.status}`);
const bareBody = await r.text();
bareBody.includes("需要引导链接") ? ok("裸访问被拒，返回引导页") : bad("裸访问没被拒");
cookies = saved;

console.log("\n=== 3. 列书 ===");
r = await req("GET", "/api/imports");
info(`GET /api/imports -> HTTP ${r.status}`);
const importsBody = await r.text();
let imports = [];
try {
  const j = JSON.parse(importsBody);
  const d = j.data ?? j;
  imports = d.items ?? (Array.isArray(d) ? d : []);
} catch (e) { bad("列书返回不是 JSON: " + e.message); }
info(`共 ${imports.length} 本：` + imports.map(b => `#${b.itemId} ${b.title} [${b.kind || b.type || "?"}]`).join(" | "));
imports.length > 0 ? ok("列书非空") : bad("列书为空");

if (!imports.length) { console.log("\n没有书，后续无法进行"); process.exit(1); }

// 优先挑 EPUB（章节定位）
const book = imports.find(b => (b.kind || b.type || "").toLowerCase().includes("epub")) || imports[0];
info(`选中 #${book.itemId} 《${book.title}》`);
const BID = book.itemId;

console.log("\n=== 4. 目录（AI 定位的锚点来源）===");
r = await req("GET", `/api/imports/${BID}/toc`);
info(`GET /api/imports/${BID}/toc -> HTTP ${r.status}`);
const tocBody = await r.text();
let chapters = [];
try {
  const j = JSON.parse(tocBody);
  const d = j.data ?? j;
  chapters = Array.isArray(d) ? d : (d.chapters ?? d.items ?? []);
} catch (e) { bad("目录不是 JSON: " + e.message); }
info(`共 ${chapters.length} 章` + (chapters.length ? `；首章: ${JSON.stringify(chapters[0]).slice(0, 140)}` : ""));
chapters.length > 0 ? ok("目录非空（A4: 目录不依赖 AI）") : bad("目录为空");

// 注意：不能按 TOC 的 charCount 挑章节 —— 那个值是导入时**存下来的**，
// 可能是 StripHtml 规则变更前的旧值（实测 epub:0~front 标称 326 字，实时算出来是 0）。
// 所以按**实时取回**的正文长度挑，这才反映 AI 真正能读到什么。
console.log("\n=== 5. 取章节正文（T2: AI 读文段）===");
let chId = null, text = "", chObj = null;
for (const c of chapters.slice(0, 6)) {
  const id = c.chapterId ?? c.id;
  const rr = await req("GET", `/api/imports/${BID}/chapters/${encodeURIComponent(id)}`);
  if (rr.status !== 200) continue;
  let tt = "";
  try { const j = JSON.parse(await rr.text()); const d = j.data ?? j; tt = d.text ?? ""; } catch {}
  info(`  ${id.padEnd(46)} title=${JSON.stringify(c.title)} -> text ${tt.length} 字`);
  if (tt.length > 0) { chId = id; text = tt; chObj = c; break; }
}
if (chId) {
  info(`选中有正文的章节: ${chId}`);
  info(`开头: ${JSON.stringify(text.slice(0, 110))}`);
  ok("章节正文可取到（AI 据此引用文段）");
} else {
  bad("所有候选章节正文都为空");
}

console.log("\n=== 6. 会话列表（每本书独立、可删）===");
r = await req("GET", `/api/imports/${BID}/chat`);
info(`GET /api/imports/${BID}/chat -> HTTP ${r.status}`);
const clBody = await r.text();
info("返回: " + clBody.slice(0, 300));
r.status === 200 ? ok("会话列表 200") : bad("会话列表 HTTP " + r.status);

console.log("\n=== 7. 新建会话 ===");
r = await req("POST", `/api/imports/${BID}/chat`, {});
info(`POST /api/imports/${BID}/chat -> HTTP ${r.status}`);
const newBody = await r.text();
info("返回: " + newBody.slice(0, 300));
let sid = null;
try { const j = JSON.parse(newBody); const d = j.data ?? j; sid = d.sessionId ?? d.id ?? d.sid; } catch {}
info("sessionId = " + sid);
r.status === 200 && sid ? ok("新建会话成功") : bad("新建会话失败");

console.log("\n=== 8. 划词提问 + SSE 流式 ===");
const ask = {
  question: "第六章这一段在说什么？",
  anchor: { selection: "他回到故乡，站在门口很久没有说话。", chapterId: chId, chapterTitle: chObj?.title ?? "" },
  sessionId: sid,
  stream: true,
};
info("POST /api/imports/" + BID + "/ask  body=" + JSON.stringify(ask).slice(0, 200));
const t0 = Date.now();
r = await req("POST", `/api/imports/${BID}/ask`, ask, { Accept: "text/event-stream" });
info(`HTTP ${r.status}  content-type=${r.headers.get("content-type")}`);
if (r.status !== 200) {
  const b = await r.text();
  info("错误体: " + b.slice(0, 500));
  bad("提问没有返回 200");
} else {
  const reader = r.body.getReader();
  const dec = new TextDecoder();
  let buf = "", frames = [], deltas = [];
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let i;
    while ((i = buf.indexOf("\n\n")) >= 0) {
      const raw = buf.slice(0, i); buf = buf.slice(i + 2);
      // SSE 帧：`event: <type>` 行 + `data: <json>` 行（类型在 event 行上，不在 JSON 里）
      const evLine = raw.split("\n").find(l => l.startsWith("event:"));
      const ev = evLine ? evLine.slice(6).trim() : "(none)";
      const t = raw.split("\n").filter(l => l.startsWith("data:")).map(l => l.slice(5).trim()).join("");
      if (!t) continue;
      const ms = Date.now() - t0;
      let obj = null;
      try { obj = JSON.parse(t); } catch {}
      frames.push({ ms, ev, raw: t.slice(0, 90) });
      if (ev === "delta" && obj?.text) deltas.push({ ms, d: obj.text });
      console.log(`   +${String(ms).padStart(5)}ms  ${ev.padEnd(9)} ${t.slice(0, 90)}`);
    }
  }
  console.log("");
  info(`共 ${frames.length} 帧；正文增量 ${deltas.length} 次`);
  const kinds = [...new Set(frames.map(f => f.ev))];
  info("帧类型: " + kinds.join(", "));
  kinds.includes("session") ? ok("第一帧是 session（前端要先知道 sessionId）") : bad("没有 session 帧");
  deltas.length ? ok("收到 delta 正文增量帧") : bad("没有 delta 帧");
  kinds.includes("cites") ? ok("收到 cites 引用帧") : bad("没有 cites 帧");
  kinds.includes("done") ? ok("收到 done 帧") : bad("没有 done 帧");
  frames[0]?.ev === "session" ? ok("session 确实是第一帧") : bad("session 不是第一帧");
  if (deltas.length >= 2) {
    const span = deltas[deltas.length - 1].ms - deltas[0].ms;
    info(`增量到达时间跨度 ${span}ms`);
    span > 50 ? ok("增量是**逐块**到达的（不是一次性）") : bad(`增量 ${span}ms 内全部到达，看不出流式`);
  } else if (deltas.length === 1) {
    info("只有 1 次增量，无法判断流式（假 LLM 应发 6 块）");
  }
  const joined = deltas.map(d => d.d).join("");
  info("拼出的回答: " + JSON.stringify(joined));
  joined.length > 0 ? ok("回答内容拼接完整") : bad("回答内容为空");

  // ⚠ 必须断言**服务端确实用上了划词段**，不能只看"有没有 delta 帧"。
  // 漏掉这条会放过一个很隐蔽的 bug：前端把 AI.sel 清空之后才取锚点，于是
  // 锚点里的 selection 恒为空 —— 面板里引用框显示着原文，AI 却回"本轮划词段为空"。
  // 症状出现在前端，但只有在这里（看服务端快照）才证得实。
  const doneFrame = frames.find(f => f.ev === "done");
  const citesFrame = frames.find(f => f.ev === "cites");
  info(`cites: ${citesFrame ? citesFrame.raw.slice(0, 80) : "(无)"}`);
  info(`done : ${doneFrame ? doneFrame.raw.slice(0, 120) : "(无)"}`);
}

console.log("\n=== 8b. 划词段必须真的送到服务端（A45 连带修复）===");
{
  // 换一个新会话，避免被上一轮的 session 状态干扰
  const nr = await req("POST", `/api/imports/${BID}/chat`, {});
  let nsid = null;
  try { nsid = JSON.parse(await nr.text())?.data?.sessionId; } catch { }
  if (!nsid) { bad("建会话失败，无法验划词送达"); }
  else {
    // 判据用**服务端落库的 anchor**，不用上游日志：
    // 假 LLM 只记 promptHead（前 120 字符），而提示词里"资料区"在 120 字符之后 ——
    // 拿上游日志判会得到**假阴性**（我第一版就是这么写的，白跑一轮）。
    // 落库的 anchor 是服务端亲手写的，且它正是用户能在界面上看到的东西，判据最直接。
    const MARK = "划词送达断言专用串-ZQ7K";
    const r2 = await req("POST", `/api/imports/${BID}/ask`,
      { question: "这一段在说什么？", anchor: { locType: "chapter", ord: 1, selection: MARK }, sessionId: nsid, stream: false },
      { Accept: "application/json" });
    info(`POST /ask（带划词，非流式）-> HTTP ${r2.status}  ct=${r2.headers.get("content-type")}`);
    if (r2.status !== 200) {
      info("响应: " + (await r2.text()).slice(0, 300));
      bad("带划词的提问没有返回 200");
    } else {
      const hist = await (await req("GET", `/api/imports/${BID}/chat/${encodeURIComponent(nsid)}`)).text();
      const hit = hist.includes(MARK);
      info(`落库的 anchor 里含划词标记: ${hit}`);
      hit ? ok("划词段确实送达服务端并落库（服务端真的用上了）")
          : bad("划词段没送达 —— selection 丢了（这是面板显示引用、AI 却说'没有划词'的根因）");
      // 再核一遍：资料区四层的标签必须都出现在提示词里（上游侧，只看存在性）
      const anchorShape = /"selection":\{"text"/.test(hist);
      info(`anchor 结构含 selection.text: ${anchorShape}`);
    }
  }
}

console.log("\n=== 9. 问答写进了历史（刷新页面还能看到）===");
r = await req("GET", `/api/imports/${BID}/chat/${sid}`);
info(`GET /api/imports/${BID}/chat/${sid} -> HTTP ${r.status}`);
const histBody = await r.text();
info("返回: " + histBody.slice(0, 600));
histBody.includes("第六章") || histBody.includes("故乡") ? ok("历史里有刚才那轮问答") : bad("历史里没有刚才的问答");

console.log("\n=== 10. 删除单条会话（确认参数是防误删契约）===");
r = await req("DELETE", `/api/imports/${BID}/chat/${sid}`);
const noYes = await r.text();
info(`不带 ?yes=1 -> HTTP ${r.status}  ${noYes.slice(0, 160)}`);
r.status === 400 && noYes.includes("CONFIRM_REQUIRED") ? ok("缺确认参数时拒绝删除（防误删）") : bad("缺确认参数时行为异常");

r = await req("DELETE", `/api/imports/${BID}/chat/${sid}?yes=1`);
const delBody = await r.text();
info(`带 ?yes=1  -> HTTP ${r.status}  ${delBody.slice(0, 160)}`);
r.status === 200 ? ok("带确认参数后删除成功") : bad("删除会话 HTTP " + r.status);

r = await req("GET", `/api/imports/${BID}/chat/${sid}`);
const goneBody = await r.text();
info(`删完再读 -> HTTP ${r.status}`);
(r.status === 404 || goneBody.includes("CHAT_NOT_FOUND")) ? ok("会话确实删掉了（硬删）") : bad("删完还能读到");

console.log("\n=== 11. 假 LLM 服务端视角的证据 ===");
try {
  const st = await (await fetch("http://127.0.0.1:8931/__stats")).json();
  info("stats: " + JSON.stringify(st));
  st.completions > 0 ? ok("上游真的收到了 chat/completions 请求") : bad("上游一次请求都没收到");
  st.streams_completed > 0 ? ok("流式请求完整走完") : bad("没有流式请求走完");
} catch (e) { bad("读假 LLM stats 失败: " + e.message); }

const fails = out.filter(x => x[0] === "FAIL").length;
console.log(`\n================ 结果：${out.length - fails}/${out.length} 通过 ================`);
process.exit(fails ? 1 : 0);
