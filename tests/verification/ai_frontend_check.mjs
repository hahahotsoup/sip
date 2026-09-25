// 前端逻辑的**真**验证：把 app.js 里「AI 阅读助手」那一节原样抽出来，
// 在 Node 里喂真实输入跑。不启服务、不造 DOM —— 只测那些纯函数，
// 因为它们才是"渲染对不对"的判定点（DOM 那层只能靠用户实跑）。
//
// 为什么值得这么做：本沙箱起不了 HttpListener，Web 那一半没法端到端验证；
// 但"转义对不对、SSE 帧解没解出来、引用该不该显示"这些**与 DOM 无关**，
// 完全可以离线证伪。
import { readFileSync } from "node:fs";

const src = readFileSync("web/app.js", "utf8");
const MARK = "AI 阅读助手（划词问 AI）· 前端";
const at = src.indexOf(MARK);
if (at < 0) { console.error("找不到那一节，标记被改过？"); process.exit(2); }
// 从节的注释头开始，一直到文件末尾（那一节是文件最后的内容）
const start = src.lastIndexOf("/* ═", at);
const section = src.slice(start);

// —— 最小依赖桩：状态与两个工具函数（与 app.js 里的实现保持一致）——
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const state = { view: "ebook", articleId: null, ebookId: 7, ebookPage: 3, ebookMeta: { title: "测试书", isPdf: false }, ebookText: { sections: [{ title: "第一章" }], section: 0 } };
const $ = () => null;
const toast = () => { };
const api = async () => ({});

const mod = await import("data:text/javascript," + encodeURIComponent(
  `const esc = ${esc.toString()};\n` +
  `const state = ${JSON.stringify(state)};\n` +
  `const $ = () => null;\nconst toast = () => {};\nconst api = async () => ({});\n` +
  `const openEbook = async () => {};\nconst ebookGo = () => {};\nconst render = () => {};\nconst saveReadingPosition = () => {};\n` +
  section + "\n" +
  `export { aiMd, aiInline, aiMsgHtml, aiHandleFrame, aiWhereNow, aiBuildAnchor, aiWhyHtml, AI };`
));

let pass = 0, fail = 0;
const ok = (name, cond, extra) => {
  if (cond) { pass++; console.log("PASS " + name); }
  else { fail++; console.log("FAIL " + name + (extra ? "  :: " + extra : "")); }
};

/* ① 转义：模型输出不可信，绝不能让它的 < > 变成 HTML 执行 */
const evil = `<img src=x onerror=alert(1)>`;
const rendered = mod.aiMd(evil);
ok("aiMd 转义尖括号", !rendered.includes("<img"), rendered);
ok("aiMd 保留转义文字", rendered.includes("&lt;img"), rendered);

/* ② Markdown：只认 **粗体** 与 > 引用 */
ok("粗体", mod.aiMd("这是**重点**").includes("<b>重点</b>"));
const bq = mod.aiMd("> 引用的原文\n> 第二行");
ok("整块引用成 blockquote", bq.startsWith("<blockquote>") && bq.includes("<br />"), bq);
ok("引用里的粗体也生效", mod.aiMd("> **原文**").includes("<b>原文</b>"));
ok("空输入不炸", mod.aiMd("") === "<p></p>" || mod.aiMd("") === "");

/* ③ 引用胶囊：**没有 chapterId 也没有 page 的引用不该出现**（契约 I4：跳不回去就别显示） */
const withCites = mod.aiMsgHtml({
  role: "assistant", text: "答案", cites: [
    { chapterId: "epub:5", title: "第三章 各家的起源", label: "第 3 章 · 各家的起源" },
    { chapterId: "", page: 0 },              // 无效引用 —— 必须被过滤掉
    { chapterId: "", page: 87, label: "第 87 页" },
  ], typing: false,
}, 0);
ok("有效章节引用出现", withCites.includes("epub:5"));
ok("页码引用出现", withCites.includes('data-page="87"'));
ok("无效引用被过滤（只剩 2 个胶囊）", (withCites.match(/ai-chip/g) || []).length === 2,
  "实际 " + (withCites.match(/ai-chip/g) || []).length);
ok("引用带 title 提示", withCites.includes("各家的起源"));

/* ④ 流式中不显示引用（避免出现"答案还没完、引用先到"的错位） */
const typing = mod.aiMsgHtml({ role: "assistant", text: "写到一半", cites: [{ chapterId: "epub:1" }], typing: true }, 0);
ok("流式中不给引用", !typing.includes("ai-chip"));
ok("流式中有光标", typing.includes("ai-cur"));

/* ⑤ SSE 帧解析：session / delta / cites / done / error 五种 */
const AI = mod.AI;
AI.sessionId = null;
const am = { role: "assistant", text: "", cites: [], typing: true };
mod.aiHandleFrame('event: session\ndata: {"sessionId":"s-1","turnIndex":1}', am);
ok("session 帧设置 sessionId", AI.sessionId === "s-1", String(AI.sessionId));
mod.aiHandleFrame('event: delta\ndata: {"text":"你好"}', am);
mod.aiHandleFrame('event: delta\ndata: {"text":"，世界"}', am);
ok("delta 累积", am.text === "你好，世界", am.text);
mod.aiHandleFrame('event: cites\ndata: {"cites":[{"chapterId":"epub:2","label":"第二章"}]}', am);
ok("cites 帧", am.cites.length === 1 && am.cites[0].chapterId === "epub:2");
mod.aiHandleFrame('event: done\ndata: {"persisted":false,"turnIndex":1}', am);
ok("done 帧的 persisted 落到消息上", am.persisted === false, String(am.persisted));
mod.aiHandleFrame('event: error\ndata: {"code":"MODEL_UNAVAILABLE","message":"上游挂了"}', am);
ok("error 帧记下错误码", am.errCode === "MODEL_UNAVAILABLE");
ok("error 帧把说明写进正文", am.text.includes("上游挂了"));
mod.aiHandleFrame('event: delta\ndata: {坏 JSON}', am);
ok("坏 JSON 不炸", am.text.includes("你好，世界"));

/* ⑥ 位置标签 */
ok("电子书给节号", mod.aiWhereNow().includes("第 1 节"), mod.aiWhereNow());

/* 锚点必须带上划词段（契约 §12.1-A45 的连带修复）。
   这个坑很隐蔽：aiSend 里先 `AI.sel = ""`（"划词只活一轮"），再调 aiBuildAnchor()，
   而旧实现读的正是全局 AI.sel → 锚点里的 selection 恒为空。
   症状特别误导人：**面板里引用框显示着那段文字，AI 却回"本轮划词段为空"**
   —— 因为引用框走的是 um.sel 那条渲染路径，跟发出去的锚点是两条路。
   所以这里断言的是"传进去的划词段必须出现在锚点里"，而不是"锚点里有 selection 这个键"。 */
{
  const SELTEXT = "这是用户划出来的那一段原文";
  const a = mod.aiBuildAnchor(SELTEXT);
  ok("锚点带上显式传入的划词段", a.selection === SELTEXT, JSON.stringify(a));
  ok("电子书锚点带 #节序号", typeof a.chapterId === "string" && a.chapterId.startsWith("#"), JSON.stringify(a.chapterId));
  ok("电子书锚点带 ord", a.ord > 0, String(a.ord));
  // 不传参数时回落读 AI.sel —— 保持旧的直接调用方式仍可用
  const b = mod.aiBuildAnchor();
  ok("不传参数时不炸且仍是对象", b && typeof b === "object" && b.locType, JSON.stringify(b));
}


/* ⑦ 检索详情：默认收起，但内容必须在（这是"回答不准时唯一能查的证据"） */
const why = mod.aiWhyHtml({ layers: ["selection", "chapter"], budgetTokens: 16000, usedTokens: 1200, bookHits: [1, 2], libraryHits: [], degraded: ["book:budget-dropped"] }, false);
ok("检索详情是折叠的", why.includes("<details"));
ok("检索详情含层与预算", why.includes("selection → chapter") && why.includes("1200/16000"));
ok("检索详情含降级说明", why.includes("book:budget-dropped"));
ok("未落盘时明确提示", why.includes("没有存进历史"));

console.log(`\n== pass ${pass}  fail ${fail}`);
process.exit(fail === 0 ? 0 : 1);
