// 断言"重启后聊天记录还在"：直接打历史端点，看它是否返回已有会话与消息。
// 这是用户报的那个 bug 的**唯一直接判据** —— 库里有没有数据是另一回事（已确认有），
// 关键是接口认不认这个 itemId、返回的消息条数对不对。
import { readFileSync } from "node:fs";

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) sip-histcheck";
const TOKEN = (readFileSync(process.env.SIP_BANNER_FILE, "utf8").match(/\?t=([0-9a-fA-F]{16,})/) || [])[1];

let ck = [];
const jar = () => ck.map(c => c.split(";")[0]).join("; ");
const H = (x = {}) => ({ "User-Agent": UA, ...x });
async function get(u) {
  const h = H(); if (ck.length) h.Cookie = jar();
  const r = await fetch(BASE + u, { headers: h, redirect: "manual" });
  for (const c of (r.headers.getSetCookie?.() || [])) ck.push(c.split(";")[0]);
  return r;
}
await get("/?t=" + TOKEN);

const out = [];
const ok = (s) => { out.push("PASS " + s); console.log("  PASS  " + s); };
const bad = (s) => { out.push("FAIL " + s); console.log("  FAIL  " + s); };
const info = (s) => console.log("        " + s);

// 找有会话的 item：先在书里找，再在文章里找（用户的会话挂在文章上）
const imports = (await (await get("/api/imports")).json())?.data?.items || [];
const feeds = (await (await get("/api/feeds")).json())?.data?.feeds || [];

const candidates = [];
for (const b of imports) candidates.push({ id: b.itemId, kind: b.kind || "book", title: b.title });
for (const f of feeds) {
  if ((f.title || "").includes("本地导入")) continue;
  const arts = (await (await get(`/api/feeds/${f.id}/articles`)).json())?.data?.articles || [];
  for (const a of arts.slice(0, 8)) candidates.push({ id: a.id ?? a.itemId, kind: "article", title: a.title });
}

console.log(`候选阅读项 ${candidates.length} 个\n`);
let foundWithHistory = 0;

for (const c of candidates) {
  const r = await get(`/api/imports/${c.id}/chat`);
  if (r.status !== 200) {
    // 404 只在"这个 id 真的不存在"时才合理；文章必须能读历史
    info(`#${c.id} [${c.kind}] 《${String(c.title).slice(0, 30)}》 -> HTTP ${r.status}`);
    if (c.kind === "article") bad(`文章 #${c.id} 的历史端点回 ${r.status} —— 文章的聊天记录读不出来`);
    continue;
  }
  let d = null;
  try { d = JSON.parse(await r.text())?.data; } catch { }
  const nSess = (d?.sessions || []).length;
  const nMsg = (d?.messages || []).length;
  const flag = nMsg > 0 ? "★" : " ";
  info(`${flag} #${c.id} [${c.kind}] 《${String(c.title).slice(0, 30)}》 会话=${nSess} 当前会话消息=${nMsg} active=${d?.activeSessionId ? "有" : "无"}`);
  if (nMsg > 0) {
    foundWithHistory++;
    const first = d.messages[0];
    info(`      首条: role=${first.role} len=${String(first.content || "").length} 内容=${JSON.stringify(String(first.content || "").slice(0, 50))}`);
  }
}

console.log("");
if (foundWithHistory > 0) ok(`至少一个阅读项能读出历史（${foundWithHistory} 个）`);
else bad("所有阅读项都读不出历史 —— 重启后记录消失的现象仍在");

// ── 会话切换：**每一个**会话都必须能按 id 单独读出消息 ──
// 面板的"切换会话"走的就是这条端点。只验列表端点是不够的：
// 列表 200 不代表某个具体会话能读（归属校验、id 编码都可能在这里出问题）。
console.log("\n=== 会话切换：逐个会话按 id 读消息 ===");
let switchOk = 0, switchTested = 0;
for (const c of candidates.slice(0, 4)) {
  const lr = await get(`/api/imports/${c.id}/chat`);
  if (lr.status !== 200) continue;
  let d = null; try { d = JSON.parse(await lr.text())?.data; } catch { }
  for (const s of (d?.sessions || []).slice(0, 3)) {
    switchTested++;
    const mr = await get(`/api/imports/${c.id}/chat/${encodeURIComponent(s.id)}`);
    let n = -1;
    if (mr.status === 200) { try { n = (JSON.parse(await mr.text())?.data?.messages || []).length; } catch { } }
    info(`#${c.id} 会话 ${String(s.id).slice(0, 20)}… -> HTTP ${mr.status} 消息=${n}`);
    if (mr.status === 200 && n >= 0) switchOk++;
  }
}
console.log("");
if (switchTested === 0) info("（没有会话可测切换）");
else if (switchOk === switchTested) ok(`每个会话都能按 id 读出消息（${switchOk}/${switchTested}）`);
else bad(`有 ${switchTested - switchOk}/${switchTested} 个会话读不出来 —— 切换会失败`);

// ── 反向：不存在的会话 id 必须 404（不能悄悄回空列表）──
{
  const r = await get(`/api/imports/19/chat/chat:does-not-exist-0000`);
  info(`不存在的会话 -> HTTP ${r.status}`);
  r.status === 404 ? ok("不存在的会话 id 回 404（不会悄悄回空）") : bad(`不存在的会话回了 ${r.status}`);
}

console.log(`\n================ ${out.filter(x => x.startsWith("PASS")).length}/${out.length} 通过 ================`);
process.exit(out.some(x => x.startsWith("FAIL")) ? 1 : 0);
