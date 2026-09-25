// A43/A44 端到端探针：RSS 文章也能问 AI + Web 端 AI 配置的分级与不回流。
//
// 设计原则：**自适应**。AiConfigWebWrite 开着或关着都是合法部署，
// 探针按 `webWriteEnabled` 的实际值去测**对应那条分支**，而不是假设某一种。
// （第一版硬编码了"默认关闭"，在一个开了开关的实例上会报假失败 —— 那是我自己的 bug。）
//
// 用法：SIP_BASE / SIP_BANNER_FILE
import { readFileSync } from "node:fs";

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) sip-harness-probe";
const H = (extra = {}) => ({ "User-Agent": UA, ...extra });

const TOKEN = (readFileSync(process.env.SIP_BANNER_FILE, "utf8").match(/\?t=([0-9a-fA-F]{16,})/) || [])[1];
if (!TOKEN) { console.error("横幅里找不到 ?t= 引导令牌"); process.exit(2); }

let cookies = [];
const jar = () => cookies.map(c => c.split(";")[0]).join("; ");
async function req(method, url, body, extra = {}) {
  const headers = H(extra);
  if (cookies.length) headers["Cookie"] = jar();
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(BASE + url, {
    method, headers, redirect: "manual",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  for (const c of (res.headers.getSetCookie?.() || [])) cookies.push(c.split(";")[0]);
  return res;
}
const out = [];
const ok = (s) => { out.push(["PASS", s]); console.log("  PASS  " + s); };
const bad = (s) => { out.push(["FAIL", s]); console.log("  FAIL  " + s); };
const skip = (s) => { out.push(["SKIP", s]); console.log("  SKIP  " + s); };
const info = (s) => console.log("        " + s);

await req("GET", "/?t=" + TOKEN);
if (!cookies.length) { console.error("换票失败"); process.exit(2); }

// ── 找一篇**真的 RSS 文章**：排除「本地导入」源，且正文非空 ──
console.log("=== 找一篇有正文的 RSS 文章（排除本地导入源）===");
const feedsRaw = await (await req("GET", "/api/feeds")).text();
let feeds = [];
try { const j = JSON.parse(feedsRaw); feeds = j.data?.feeds || j.data || []; } catch { }
info(`源 ${feeds.length} 个：` + feeds.map(f => `#${f.id} 《${f.title}》`).join(" | "));

let article = null;
for (const f of feeds) {
  if ((f.title || "").includes("本地导入")) { info(`跳过 #${f.id}（本地导入不是文章源）`); continue; }
  const r = await req("GET", `/api/feeds/${f.id}/articles`);
  if (r.status !== 200) { info(`#${f.id} -> HTTP ${r.status}`); continue; }
  let arts = [];
  try { const j = JSON.parse(await r.text()); arts = j.data?.articles || []; } catch { }
  info(`#${f.id} 《${f.title}》 文章 ${arts.length} 篇` + (arts.length ? `，首篇字段: ${Object.keys(arts[0]).join(",")}` : ""));
  if (arts.length) {
    // 字段名是 `id`（不是 itemId）—— 第一版探针在这儿踩过
    article = { id: arts[0].id ?? arts[0].itemId, title: arts[0].title, contentLen: arts[0].contentLen ?? arts[0].ContentLen };
    info(`取 #${article.id} 《${article.title}》 contentLen=${article.contentLen}`);
    break;
  }
}

if (!article || !article.id) {
  console.log("\n这个实例里没有 RSS 文章（只有本地导入）—— 无法验证 A43");
  console.log("（这是环境限制，不是产品失败；要验 A43 需要一个真的订阅源）");
  out.push(["SKIP", "A43 缺 RSS 文章夹具"]);
} else {
  const AID = article.id;
  console.log("\n=== A43-1 文章可问（服务端不该再回 ITEM_NOT_FOUND）===");
  let cr = await req("POST", `/api/imports/${AID}/chat`, {});
  const crTxt = await cr.text();
  info(`POST /api/imports/${AID}/chat -> HTTP ${cr.status}  ${crTxt.slice(0, 160)}`);
  let sid = null;
  try { sid = JSON.parse(crTxt)?.data?.sessionId; } catch { }
  (cr.status === 200 && sid)
    ? ok("文章能建会话（说明服务端按文章分支处理了）")
    : bad(`文章建会话失败 HTTP ${cr.status}`);

  if (sid) {
    // 用**非流式**问一次：它的响应体里带完整 snapshot，能直接看出"第 3 层有没有内容"。
    // 这正对应用户实测的那个现象：在文章里问"看看正文？"，模型如实回答"没有可引用的正文"
    // —— 根因是 `SearchBook` 遍历 `Chapters`，而文章没有章节行，第 3 层恒空。
    const nr = await req("POST", `/api/imports/${AID}/ask`,
      { question: "看看正文？", anchor: { locType: "article", ord: 0 }, sessionId: sid, stream: false },
      { Accept: "application/json" });
    info(`POST /ask（非流式，问"看看正文？"）-> HTTP ${nr.status}`);
    if (nr.status === 200) {
      let snap = null;
      try { snap = JSON.parse(await nr.text())?.data?.snapshot; } catch { }
      info(`layers=[${(snap?.layers || []).join(", ")}]  degraded=[${(snap?.degraded || []).join(", ")}]`);
      info(`bookHits=${(snap?.bookHits || []).length}  libraryHits=${(snap?.libraryHits || []).length}  usedTokens=${snap?.usedTokens}`);
      const bh = snap?.bookHits || [];
      if (bh.length) {
        const h = bh[0];
        info(`首个命中: reason=${h.reason} chapterId=${JSON.stringify(h.chapterId)} snippet=${JSON.stringify(String(h.snippet || "").slice(0, 70))}`);
        (h.reason === "article" || h.reason === "article-head")
          ? ok("文章的第 3 层（整篇）有内容 —— 模型读得到正文")
          : info(`（命中来自 ${h.reason}，非整篇层）`);
        // 关键：文章的 citation 必须跳得回去才允许出现 → chapterId 留空即"只当材料"
        (h.chapterId === "" || h.chapterId === null)
          ? ok("文章的命中不带章节 id（只当材料，不产出跳不回去的引用 —— 守住 I4）")
          : bad(`文章的命中带了 chapterId=${h.chapterId}，会产生跳不回去的引用`);
      } else {
        bad("文章的第 3 层是空的 —— 模型会说'读不到正文'（这正是用户实测的现象）");
      }
    } else {
      info("响应: " + (await nr.text()).slice(0, 240));
      bad("非流式提问没有返回 200");
    }

    const askRes = await req("POST", `/api/imports/${AID}/ask`,
      { question: "这篇在说什么？", anchor: { locType: "article", ord: 0 }, sessionId: sid, stream: true },
      { Accept: "text/event-stream" });
    info(`POST /api/imports/${AID}/ask -> HTTP ${askRes.status}  ct=${askRes.headers.get("content-type")}`);
    if (askRes.status === 200) {
      const reader = askRes.body.getReader();
      const dec = new TextDecoder();
      let buf = "", kinds = [];
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        let i;
        while ((i = buf.indexOf("\n\n")) >= 0) {
          const raw = buf.slice(0, i); buf = buf.slice(i + 2);
          const ev = (raw.split("\n").find(l => l.startsWith("event:")) || "").slice(6).trim();
          if (ev && !kinds.includes(ev)) kinds.push(ev);
        }
      }
      info(`帧类型: ${kinds.join(", ")}`);
      (kinds.includes("delta") && kinds.includes("done")) ? ok("文章问答帧序正常") : bad("文章问答帧缺失");
    } else {
      info("错误体: " + (await askRes.text()).slice(0, 260));
      bad("文章提问没有返回 200");
    }
    // 反向断言：扩的是问答，不是章节模型
    const tocR = await req("GET", `/api/imports/${AID}/toc`);
    const tocB = await tocR.text();
    info(`GET /api/imports/${AID}/toc -> HTTP ${tocR.status}  ${tocB.slice(0, 110)}`);
    tocR.status === 404
      ? ok("文章的 /toc 仍是 404（章节模型没被过度扩张）")
      : bad(`文章的 /toc 返回 ${tocR.status} —— 扩张过头了`);
  }
}

console.log("\n=== A43-2 反向：不存在的 id 仍应 404 ITEM_NOT_FOUND ===");
{
  const r = await req("POST", "/api/imports/99999999/ask", { question: "hi" });
  const b = await r.text();
  info(`不存在的 id -> HTTP ${r.status}  ${b.slice(0, 140)}`);
  (r.status === 404 && b.includes("ITEM_NOT_FOUND"))
    ? ok("不存在的 id 仍 404 ITEM_NOT_FOUND（没有把 404 放宽成 200）")
    : bad("不存在的 id 行为变了");
}

console.log("\n=== A44 配置端点：key 不回流 + 按开关状态测对应分支 ===");
const g = await req("GET", "/api/ai/config");
const gt = await g.text();
let d = null;
try { d = JSON.parse(gt)?.data; } catch { }
info(`GET /api/ai/config -> HTTP ${g.status}`);
info(`webWriteEnabled=${d?.webWriteEnabled}  llmApiKeySet=${d?.llmApiKeySet}  embApiKeySet=${d?.embeddingApiKeySet}`);
info(`llmEndpoint=${d?.llm?.apiEndpoint}  model=${d?.llm?.model}`);

if (g.status !== 200 || typeof d?.llmApiKeySet !== "boolean") {
  bad("GET /api/ai/config 不可用或缺 llmApiKeySet 布尔");
} else {
  ok("GET 可用且 llmApiKeySet 是布尔");
  const leak = /"(?:apiKey|llmApiKey|embeddingApiKey)"\s*:\s*"[^"]/.test(gt);
  leak ? bad("GET 响应里出现了 key 字段") : ok("GET 没有回显任何 key 字段");
}

const enabled = !!d?.webWriteEnabled;
if (!enabled) {
  const w = await req("POST", "/api/ai/config", { llmModel: "should-not-apply" });
  const wb = await w.text();
  info(`开关关着 -> POST HTTP ${w.status}  ${wb.slice(0, 180)}`);
  (w.status === 404 || w.status === 403)
    ? ok("开关关着时 POST 被拒（404/403）")
    : bad(`开关关着时 POST 返回 ${w.status}`);
  skip("key 写入 / 端点确认 / 审计：开关关着，本次未验证");
} else {
  console.log("  （这个实例开着 AiConfigWebWrite，测开放那条分支）");
  // 非端点字段：直接改
  const w = await req("POST", "/api/ai/config", { llmModel: "probe-model-x" });
  const wb = await w.text();
  info(`改 model -> HTTP ${w.status}  ${wb.slice(0, 160)}`);
  w.status === 200 ? ok("开着时可改 model") : bad("改 model 失败");

  // 端点：不带确认参数必须 400，带了才 200，且要留审计
  const epNo = await req("POST", "/api/ai/config", { llmEndpoint: "http://127.0.0.1:8931/v1x" });
  const epNoTxt = await epNo.text();
  info(`改端点（不带 ?yes=1）-> HTTP ${epNo.status}  ${epNoTxt.slice(0, 160)}`);
  (epNo.status === 400 && epNoTxt.includes("CONFIRM_REQUIRED"))
    ? ok("改端点缺确认参数时 400 CONFIRM_REQUIRED")
    : bad(`改端点缺确认参数返回 ${epNo.status}`);

  const epYes = await req("POST", "/api/ai/config?yes=1", { llmEndpoint: "http://127.0.0.1:8931/v1x" });
  info(`改端点（带 ?yes=1）-> HTTP ${epYes.status}`);
  epYes.status === 200 ? ok("带确认参数后端点可改") : bad("带确认参数仍失败");

  const simon = await (await req("GET", "/api/simon")).text();
  simon.includes("ai_endpoint_changed")
    ? ok("端点变更留下了审计事件 ai_endpoint_changed")
    : bad("端点变更没有留审计");
  // 审计里不该出现查询串（可能带 token）—— 只记主机
  const leakedHost = /ai_endpoint_changed[^}]*\/v1x/.test(simon);
  leakedHost ? bad("审计里记了完整 URL（可能带 token）") : ok("审计只记主机，不记完整 URL");

  // key 不回流：写一把可识别的假 key，再在所有读接口里搜
  const MARK = "sip-probe-key-DO-NOT-LEAK-7f3a91";
  const kw = await req("POST", "/api/ai/config", { llmApiKey: MARK });
  info(`写入试探 key -> HTTP ${kw.status}`);
  if (kw.status === 200) {
    const surfaces = ["/api/ai/config", "/api/config", "/api/status", "/api/simon"];
    const leaked = [];
    for (const s of surfaces) {
      const b = await (await req("GET", s)).text();
      if (b.includes(MARK)) leaked.push(s);
    }
    leaked.length ? bad(`key 泄漏在: ${leaked.join(", ")}`) : ok("key 未出现在任何读接口");
    // 收尾：把试探值改回假 LLM 的端点与模型，别把环境搞乱
    await req("POST", "/api/ai/config?yes=1", { llmEndpoint: "http://127.0.0.1:8931/v1" });
    await req("POST", "/api/ai/config", { llmModel: "fake-chat" });
    info("已把试用实例的端点/模型改回假 LLM 的值");
  }
  // 非法端点要被拒
  const badEp = await req("POST", "/api/ai/config?yes=1", { llmEndpoint: "ftp://x/y" });
  const badTxt = await badEp.text();
  info(`非法端点 -> HTTP ${badEp.status}  ${badTxt.slice(0, 130)}`);
  (badEp.status === 400 && badTxt.includes("BAD_ARGUMENT"))
    ? ok("非 http(s) 端点被拒")
    : bad(`非法端点返回 ${badEp.status}`);
}

const fails = out.filter(x => x[0] === "FAIL").length;
const skips = out.filter(x => x[0] === "SKIP").length;
console.log(`\n================ 结果：${out.length - fails - skips}/${out.length - skips} 通过${skips ? `（另有 ${skips} 项如实跳过）` : ""} ================`);
process.exit(fails ? 1 : 0);
