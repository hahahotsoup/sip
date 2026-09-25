/* sip Web 前端冒烟：把 web/app.js 放进一个最小 DOM 桩里跑一遍**每个视图的渲染路径**。
 *
 * 为什么需要它：C# 黑盒用例能证明接口对不对，却证明不了前端不炸；
 * 而前端一旦在某个视图里抛异常，用户看到的是**白屏**（比报错更难查）。
 * 这里不测像素，只测"每个视图都能渲染出东西、且带上了应有的关键字段"。
 *
 * 零依赖，直接用 node 跑：
 *     node tools/webui-smoke.mjs
 *
 * 它**不是** dotnet test 的一部分（CI 上不强制跑）；改了 web/app.js 之后跑一次，
 * 能在打开浏览器之前挡掉绝大多数"点进去白屏"。
 */

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const appPath = join(here, "..", "web", "app.js");
const htmlPath = join(here, "..", "web", "index.html");
const code = readFileSync(appPath, "utf8");
const indexHtml = readFileSync(htmlPath, "utf8");

// ── 最小 DOM 桩 ──────────────────────────────────────────────
const elements = new Map();

/** 标记里带 `hidden` 的元素，一开始就得是隐藏的 —— 否则"忘了摘 hidden 导致整页全白"
 *  这类 bug 在这个桩里根本显不出来（真实踩过一次）。 */
const HIDDEN_IN_HTML = new Set();
for (const m of indexHtml.matchAll(/<[a-z][^>]*\bid="([^"]+)"[^>]*\shidden(?=[\s/>])[^>]*>/gi)) {
  HIDDEN_IN_HTML.add(m[1]);
}

/** 极简的"顶层块"解析：只为了能让 splitBookSections 在桩里也拿到 childNodes。
 *  它不是 HTML 解析器，只按块级标签切；对冒烟要断言的东西（切了几节、每节多大）足够。 */
function parseTopLevel(html) {
  const out = [];
  const re = /<(h[1-6]|p|div|blockquote|pre|ul|ol|table|figure|section|article|nav|aside)\b[^>]*>[\s\S]*?<\/\1\s*>|<img\b[^>]*\/?>/gi;
  let last = 0, m;
  const pushText = (s) => {
    const t = s.trim();
    if (t) out.push({ nodeType: 3, nodeValue: t, textContent: t });
  };
  while ((m = re.exec(html)) !== null) {
    pushText(html.slice(last, m.index));
    const tag = (m[0].match(/^<([a-z0-9]+)/i) || [])[1] || "div";
    const inner = m[0].replace(/<[^>]*>/g, "");
    out.push({
      nodeType: 1,
      tagName: tag.toUpperCase(),
      textContent: inner,
      outerHTML: m[0],
    });
    last = m.index + m[0].length;
  }
  pushText(html.slice(last));
  return out;
}

function makeEl(id = "") {
  const el = {
    id,
    tagName: "DIV",
    _html: "",
    childNodes: [],
    _attrs: new Set(HIDDEN_IN_HTML.has(id) ? ["hidden"] : []),
    textContent: "",
    hidden: HIDDEN_IN_HTML.has(id),
    disabled: false,
    value: "",
    max: "",
    scrollTop: 0,
    scrollHeight: 100,
    clientHeight: 100,
    clientWidth: 800,
    style: { setProperty() { }, display: "" },
    dataset: {},
    classList: {
      _s: new Set(),
      add(c) { this._s.add(c); },
      remove(c) { this._s.delete(c); },
      toggle(c, on) { if (on === undefined) { this._s.has(c) ? this._s.delete(c) : this._s.add(c); } else if (on) this._s.add(c); else this._s.delete(c); },
      contains(c) { return this._s.has(c); },
    },
    addEventListener() { },
    removeEventListener() { },
    appendChild() { },
    focus() { },
    scrollIntoView() { },
    getBoundingClientRect() { return { top: 0, height: 100, width: 800 }; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
    closest() { return null; },
    matches() { return false; },
    getAttribute(n) { return el._attrs.has(n) ? "" : null; },
    hasAttribute(n) { return el._attrs.has(n); },
    setAttribute(n) { el._attrs.add(n); },
    removeAttribute(n) { el._attrs.delete(n); if (n === "hidden") el.hidden = false; },
  };
  Object.defineProperty(el, "innerHTML", {
    get() { return el._html; },
    set(v) {
      el._html = String(v ?? "");
      el.childNodes = parseTopLevel(el._html);   // 桩里的"解析"（见 parseTopLevel 的说明）
    },
  });
  return el;
}
function $(id) {
  if (!elements.has(id)) elements.set(id, makeEl(id));
  return elements.get(id);
}

const listeners = [];
const documentStub = {
  documentElement: makeEl("html"),
  body: makeEl("body"),
  getElementById: $,
  querySelector: () => null,
  querySelectorAll: () => [],
  createElement: () => makeEl(),
  createDocumentFragment: () => makeEl(),
  createTextNode: () => ({}),
  createTreeWalker: () => ({ nextNode: () => false, currentNode: null }),
  addEventListener: (ev, fn) => listeners.push({ ev, fn }),
};

const store = new Map();
const sandbox = {
  console,
  document: documentStub,
  navigator: {},
  location: { protocol: "http:", replace() { } },
  localStorage: {
    getItem: (k) => (store.has(k) ? store.get(k) : null),
    setItem: (k, v) => store.set(k, String(v)),
    removeItem: (k) => store.delete(k),
  },
  window: {
    innerWidth: 1280,
    innerHeight: 900,
    addEventListener() { },
    matchMedia: () => ({ matches: false }),
    open() { },
    _pagerImgT: 0,
  },
  // 桩里把 --cols 报成 3、阅读区宽度 800px：**按宽度降栏**这条规则必须生效 ——
  // 800px 里塞 3 栏，每栏只有 266px（中文一行十来个字），那正是"比例很奇怪"的来源。
  getComputedStyle: () => ({ getPropertyValue: (k) => (k === "--cols" ? "3" : "") }),
  setTimeout: () => 0,
  clearTimeout() { },
  setInterval: () => 0,
  clearInterval() { },
  confirm: () => true,
  alert() { },
  fetch: async () => ({ ok: true, json: async () => ({}) }),
  Promise, JSON, Math, Date, Object, Array, String, Number, Boolean, RegExp, Error, isNaN, parseInt, parseFloat,
};
sandbox.globalThis = sandbox;
sandbox.window.document = documentStub;

// ── 每个接口的罐头数据 ───────────────────────────────────────
const FEED = { id: 1, displayNum: 1, title: "源 A", active: 3, archived: 1, deleted: 0, schedule: "1h", url: "http://a.example/feed.xml", lastChecked: "2026-09-25T10:00:00Z" };
const ARTICLE = { itemId: 11, title: "标题", quality: "full", hasHistory: true, liked: true, aiLiked: false, published: "2026-09-20T08:00:00Z" };
const VERSIONS = [
  { id: 12, version: 2, status: "active", archivedAt: "", title: "标题", length: 120, current: true },
  { id: 11, version: 1, status: "archived", archivedAt: "2026-09-21T08:00:00Z", title: "标题", length: 100, current: false },
];
const CANNED = [
  [/\/api\/status/, { sip: "sip v2.0.0", auth: "password", simon: 1 }],
  [/\/api\/feeds\/\d+\/articles/, { feedId: 1, feedTitle: "源 A", articles: [ARTICLE] }],
  [/\/api\/feeds/, { feeds: [FEED, { ...FEED, id: 9, displayNum: 9, title: "本地导入", url: "local://import" }] }],
  [/\/api\/today/, {
    date: "2026-09-25", generatedAt: "09:00", target: 5, done: 2, tracking: true,
    digest: { newTotal: 7, sourceCount: 2, newBySource: [{ source: "源 A", count: 7, flood: false }], modified: [{ itemId: 12, title: "标题", source: "源 A", titleChanged: false, addedLines: 2, removedLines: 1, wordDelta: 30 }], dedups: [{ size: 2, representativeId: 11, title: "重复", source: "源 A", minOverlap: 90, members: [11, 12] }] },
    items: [{ itemId: 11, title: "今日一篇", source: "源 A", reason: "新增", minutes: 4, score: 6 }],
  }],
  [/\/api\/likes/, { signals: [{ itemId: 11, title: "收藏的", feed: "源 A", liked: true, aiLiked: false, reason: "" }] }],
  [/\/api\/imports\/\d+\/text/, {
    itemId: 21, title: "一本很长的书", type: "epub", isPdf: false,
    // 故意造一本"整本书"（若干章、每章若干段，总量远超一节的上限）：
    // 只有真的被切成节，电子书视图才会出现「共 N 节」与节点按钮。
    bodyHtml: Array.from({ length: 6 }, (_, c) =>
      `<h2>第 ${c + 1} 章</h2>` + Array.from({ length: 12 }, () => `<p>${"字".repeat(400)}</p>`).join("")
    ).join(""),
  }],
  // 21 = 文本型电子书（要被切成节），22 = PDF（按页）。两条形状不同，别混。
  [/\/api\/imports\/21(\?|$)/, { itemId: 21, title: "一本很长的书", type: "epub", isPdf: false, pages: null, size: 4096 }],
  [/\/api\/imports\/\d+/, { itemId: 22, title: "一本扫描件", type: "pdf", isPdf: true, pages: 12, size: 1024 }],
  [/\/api\/imports/, {
    feedId: 9, items: [
      { itemId: 21, title: "一本很长的书", type: "epub", size: 4096, description: "", importedAt: "2026-09-24T08:00:00Z", pages: null, chars: 30000, isPdf: false },
      { itemId: 22, title: "一本扫描件", type: "pdf", size: 2048, description: "", importedAt: "2026-09-24T08:00:00Z", pages: 12, chars: 0, isPdf: true },
    ],
  }],
  [/\/api\/edits/, { count: 1, items: [{ itemId: 12, feedId: 1, feed: "源 A", title: "标题", from: 1, to: 2, versions: 2, lastChangedAt: "2026-09-21T08:00:00Z", stillActive: true }] }],
  [/\/api\/articles\/\d+\/versions/, { itemId: 12, title: "标题", feed: "源 A", feedId: 1, versions: VERSIONS }],
  [/\/api\/articles\/\d+\/diff/, { article: 12, from: 1, to: 2, titleOld: "标题", titleNew: "标题", titleChanged: false, added: 1, removed: 1, changes: [{ type: "Unchanged", text: "同一行" }, { type: "Deleted", text: "旧行" }, { type: "Inserted", text: "新行" }] }],
  // 导入项（21）走的是"这条其实是本地文件"的分支：界面必须把它转给阅读器
  [/\/api\/articles\/21(\?|$)/, {
    itemId: 21, shownItemId: 21, version: 1, versionCount: 1, hasHistory: false, imported: true,
    status: "active", archivedAt: "", title: "一本很长的书", feed: "本地导入", feedId: 9, guid: "import:x",
    link: "C:/x/readwithhotsoup/imported/book.epub", published: "2026-09-20T08:00:00Z", author: "本地导入",
    quality: "full", liked: false, aiLiked: false, bodyHtml: "<p>正文</p>", hasFulltext: false, summary: "", pageCount: null,
  }],
  [/\/api\/articles\/\d+/, {
    itemId: 12, shownItemId: 12, version: 2, versionCount: 2, hasHistory: true, status: "active", archivedAt: "",
    title: "标题", feed: "源 A", feedId: 1, guid: "g1", link: "http://a.example/1", published: "2026-09-20T08:00:00Z",
    author: "作者", quality: "full", liked: false, aiLiked: false, bodyHtml: "<p>正文</p>", hasFulltext: true, summary: "摘要", pageCount: null,
  }],
  [/\/api\/dedup\/diff/, { a: { itemId: 11, title: "A", feed: "源 A", length: 100, published: "2026-09-20T08:00:00Z" }, b: { itemId: 12, title: "B", feed: "源 B", length: 100, published: "2026-09-20T08:00:00Z" }, overlap: 95, added: 1, removed: 0, lines: [{ type: "Unchanged", text: "同一段" }, { type: "Inserted", text: "多出来的一段" }] }],
  [/\/api\/dedup\/scan/, { scanned: true, windowHours: 48, threshold: 0.8, clusters: [{ id: 11, representativeId: 11, title: "重复内容 A", source: "源 A", size: 2, minOverlap: 95, members: [{ itemId: 11, title: "A", feed: "源 A", length: 100, published: "2026-09-20T08:00:00Z" }, { itemId: 12, title: "B", feed: "源 B", length: 100, published: "2026-09-20T08:00:00Z" }] }], hidden: [] }],
  [/\/api\/dedup/, { scanned: false, windowHours: 48, threshold: 0.8, clusters: [{ id: 11, representativeId: 11, title: "重复内容 A", source: "源 A", size: 2, minOverlap: 95, members: [{ itemId: 11, title: "A", feed: "源 A", length: 100, published: "2026-09-20T08:00:00Z" }, { itemId: 12, title: "B", feed: "源 B", length: 100, published: "2026-09-20T08:00:00Z" }] }], hidden: [{ itemId: 12, title: "B", source: "源 B", key: "2:http://b.example/1" }] }],
  [/\/api\/policies/, { count: 1, actions: ["lower_frequency", "archive", "keep", "tag", "unsubscribe"], policies: [{ feedId: 1, feed: "源 A", action: "tag", schedule: "", tag: "ai", note: "备注", createdBy: "user", updatedAt: "2026-09-25T08:00:00Z", currentSchedule: "1h" }] }],
  [/\/api\/insights/, { windowDays: 30, generatedAt: "2026-09-25T08:00:00Z", aiCalls: { total: 3, success: 3, fail: 0, llm: 2, embedding: 1 }, feeds: [{ id: 1, title: "源 A", schedule: "1h", active: 3, backlog: 1, opened: 5, completed: 2, skipped: 0, completionRate: 40, userLikes: 1, aiLikes: 0, llmCalls: 2, embeddingCalls: 1, status: "正常", reasons: ["近 7 天有更新"] }] }],
  [/\/api\/config/, {
    version: "2.0.0", dataDir: "C:/x/readwithhotsoup", dbPath: "C:/x/readwithhotsoup/rss.db",
    web: { host: "127.0.0.1", port: 8777, passwordSet: true, networkReachable: false, sessionScope: "process" },
    ai: { configured: true, configFile: "C:/x/ai_config.json", embedding: { provider: "openai-compatible", model: "nomic-embed-text", dimensions: 768, endpoint: "http://localhost:11434/v1", searchThreshold: 0.7, keySet: true }, llm: { provider: "openai-compatible", model: "deepseek-chat", endpoint: "https://api.deepseek.com/v1", keySet: true }, allowPrivateNet: false },
    simon: 1, agentGate: false, telemetry: { enabled: false, consent: "unset" },
    insights: { interval: "off", lastAt: "" },
    thresholds: { dedup: 0.8, dedupSemantic: 0.92, changeGradePolish: 0.08, changeGradeReverse: 0.45, groupMatch: 0.75, floodPerDay: null },
    counts: { feeds: 2, items: 10, likes: 1, fulltext: 3 },
  }],
  [/\/api\/simon/, { name: "孟思琳(simon)", level: 1, canDisable: false, agentGate: false, loosenHint: "sip simon level N", events: [{ ts: "2026-09-25T08:00:00Z", type: "blocked_cmd", level: 1, detail: "web:policy" }] }],
  [/\/api\/telemetry/, { enabled: false, consent: "unset", events: 0, first: null, last: null, db: "C:/x/telemetry.db" }],
  [/\/api\/index/, { configured: true, provider: "openai-compatible", model: "nomic-embed-text", dimensions: 768, endpoint: "http://localhost:11434/v1", searchThreshold: 0.7, currentModelId: 1, vectors: 10, chunks: 2, active: 10, progress: { active: false } }],
  [/\/api\/grep/, { query: "trigram", hits: [{ itemId: 11, title: "命中", feed: "源 A", snippet: "…片段…", link: "" }] }],
  [/\/api\/search/, { query: "语义", hits: [{ itemId: 11, title: "语义命中", feed: "源 A", score: 0.9, snippet: "" }] }],
  [/\/api\/progress/, { active: false }],
  [/\/api\/reading-progress/, { positions: { "12": 420 } }],
];

function cannedResponse(path) {
  for (const [re, data] of CANNED) if (re.test(path)) return { ok: true, status: 200, json: async () => ({ success: true, data }) };
  return { ok: false, status: 404, json: async () => ({ success: false, error: { code: "NOT_FOUND", message: path } }) };
}
sandbox.fetch = async (path) => {
  if (String(path).startsWith("/languages/")) {
    return { ok: true, json: async () => ({ Today: "今日哈汤", Feeds: "订阅源" }) };
  }
  return cannedResponse(String(path));
};

// ── 跑起来 ───────────────────────────────────────────────────
const instrumented = code + "\n;globalThis.__sip = { render, state, loadRealData, loadDedup, loadPolicies, loadEdits, DEDUP, sectionsFromNodes, setImmersive, setMetaOpen, setPaged, bumpFont, setLeading, setContentWidth, setCols, saveReadState, readStateOf, rememberReadState, api, t, $, esc };";
const context = vm.createContext(sandbox);
new vm.Script(instrumented, { filename: "web/app.js" }).runInContext(context);

const { render, state, loadRealData, loadDedup, DEDUP, setImmersive, setMetaOpen, setPaged, bumpFont, setLeading, setContentWidth, setCols, saveReadState, readStateOf, rememberReadState, $: getEl } = sandbox.__sip;

const failures = [];
function check(name, html, mustHave = []) {
  if (!html || html.length < 20) { failures.push(`${name}: 渲染结果为空`); return; }
  for (const m of mustHave) {
    if (!html.includes(m)) failures.push(`${name}: 缺少「${m}」`);
  }
  if (/undefined/.test(html) && !/undefined 页/.test(html)) {
    // 模板里出现字面 undefined 几乎总是少传了一个字段
    failures.push(`${name}: 模板里出现了字面 "undefined"`);
  }
}

await new Promise((r) => setTimeout(r, 10));   // 让顶部的 applyLang/loadRealData 跑完
await loadRealData();

// 启动形态：应用壳必须**可见**的。
// 这条是拿真实事故换来的：重写前端时删掉了页内登录壳，却忘了摘掉 `#app` 上的 hidden
// ——标记在、脚本在、render() 也跑了，用户看到的是**整页全白**。
// 桩里按 index.html 初始化 hidden，所以这类"忘了摘"现在能在跑浏览器之前就红。
if (getEl("app").hidden || getEl("app").hasAttribute("hidden")) {
  failures.push("启动后 #app 仍是 hidden —— 页面会全白（标记/脚本都在，只是画在隐藏容器里）");
}

const cases = [
  ["today", {}, ["今日哈汤", "今日一篇"]],
  ["feeds", {}, ["全部订阅", "源 A"]],
  ["feed", { feedId: 1 }, ["源 A", "更新计划"]],
  ["article", { articleId: 12 }, ["标题", "收藏", "导出 MD"]],
  ["article-versions", { articleId: 12, versions: VERSIONS }, ["改稿历史", "v1"]],
  ["likes", {}, ["收藏", "收藏的"]],
  ["imported", {}, ["本地导入", "一本很长的书", "一本扫描件", "选择文件"]],
  ["ebook-pdf", { ebookId: 22, ebookMeta: { itemId: 22, title: "一本扫描件", isPdf: true, pages: 12 }, ebookPages: 12, ebookPage: 1 }, ["第 1 / 12 页", "下一页", "用浏览器打开", "临时链接"]],
  // 文本型电子书必须和文章正文一样是**可分栏翻页**的：
  // 这里钉住 #pager + pager-bar + 跟随 --cols 偏好（桩里是 2），
  // 以及**整本书被切成节**（出现「共 N 节」与节点按钮）—— 后者是"页面无响应"的根治：
  // 整本塞进一次分栏排版会把浏览器卡死。
  ["ebook-text", { ebookId: 21, ebookMeta: { itemId: 21, title: "一本很长的书", isPdf: false, type: "epub" }, ebookPages: null }, ["一本很长的书", 'id="pager"', "pager-bar", 'data-cols="2"', "共 6 节", 'data-act="book-sec"']],
  ["search", {}, ["搜索", "全文"]],
  ["edits-list", { editsId: null }, ["改稿追踪", "标题"]],
  ["edits-detail", { editsId: 12 }, ["共 2 版", "版本清单"]],
  ["dedup-intro", { dedupRep: null }, ["跨源去重", "开始扫描"]],
  ["dedup-cluster", { dedupRep: 11, dedupPair: 12 }, ["对比", "段落级 diff"]],  ["policy", {}, ["源规则", "新增 / 覆盖规则", "源 A"]],
  ["insights", {}, ["阅读报告", "按源", "源 A"]],
  ["settings-reading", { tab: "reading" }, ["设置", "简体中文"]],
  ["settings-system", { tab: "system" }, ["数据目录", "nomic-embed-text"]],
  ["settings-governance", { tab: "governance" }, ["孟思琳", "向量索引", "补索引"]],
  ["about", {}, ["关于 sip", "安全边界", "v2.0.0"]],
];

for (const [name, patch, must] of cases) {
  // 去重视图有三个入口状态：还没扫过 / 扫过但没选簇 / 选了簇看对比
  if (name === "dedup-intro") { DEDUP.clusters.length = 0; DEDUP.hidden.length = 0; DEDUP.scanned = false; }
  if (name === "dedup-cluster") await loadDedup(false);

  const view = name.startsWith("edits") ? "edits"
    : name.startsWith("dedup") ? "dedup"
      : name.startsWith("ebook") ? "ebook"
        : name.startsWith("settings") ? "settings"
          : name === "article-versions" ? "article"
            : name;
  Object.assign(state, {
    view, feedId: null, articleId: null, articleVersion: null, versions: null,
    editsId: null, dedupRep: null, dedupPair: null, ebookId: null, ebookMeta: null,
    ebookPages: null, ebookPage: 1, ebookText: null, jumpKw: "", tab: "reading",
  }, patch);
  // 每个视图一个干净的容器。断言把 **#view + 阅读抽屉** 的 HTML 合起来看：
  // 阅读类视图把动作/元信息/目录挪进了右侧抽屉（正文上面只留一行），
  // 断言不该关心它具体住在哪一边。
  getEl("view").innerHTML = "";
  getEl("rdDrawer").innerHTML = "";
  try {
    await render();
  } catch (e) {
    failures.push(`${name}: 渲染抛异常 —— ${e && e.stack ? e.stack.split("\n")[0] : e}`);
    continue;
  }
  check(name, getEl("view").innerHTML + getEl("rdDrawer").innerHTML, must);
}

// 导入的文件点进去必须落到**阅读器**，而不是文章视图。
// 这是拿真实事故换来的：文章视图对整本书分不了页（退回滚动 → "左栏读到底、右栏还在下面"），
// 早期还会把 file:// 的图整个丢掉（"电子书没有图"）。
{
  Object.assign(state, {
    view: "article", articleId: 21, articleVersion: null, versions: null,
    ebookId: null, ebookMeta: null, ebookText: null, ebookPages: null, ebookPage: 1,
  });
  getEl("view").innerHTML = "";
  getEl("rdDrawer").innerHTML = "";
  try {
    await render();
    check("article-imported", getEl("view").innerHTML + getEl("rdDrawer").innerHTML,
      ["一本很长的书", 'id="pager"', "共 6 节", 'data-act="book-sec"']);
    if (state.view !== "ebook") failures.push(`article-imported: 没有转到阅读器（state.view=${state.view}）`);
  } catch (e) {
    failures.push("article-imported: 渲染抛异常 —— " + (e && e.message));
  }
}

// ① 电子书**最多两栏**：把阅读区放宽到 1400px（够排 3 栏），电子书仍须只排 2 栏，文章可以 3 栏。
{
  getEl("pager").clientWidth = 1400;
  Object.assign(state, { view: "ebook", ebookId: 21, ebookMeta: { itemId: 21, title: "一本很长的书", isPdf: false, type: "epub" }, ebookText: null, ebookPages: null });
  getEl("view").innerHTML = "";
  await render();
  if (!getEl("view").innerHTML.includes('data-cols="2"'))
    failures.push("电子书 2 栏上限：1400px 宽时没有收成 2 栏");

  Object.assign(state, { view: "article", articleId: 12, articleVersion: null, versions: null });
  getEl("view").innerHTML = "";
  await render();
  if (!getEl("view").innerHTML.includes('data-cols="3"'))
    failures.push("文章 3 栏：1400px 宽时应当能排 3 栏（说明降栏规则没有误伤）");
  getEl("pager").clientWidth = 800;
}

// ② 全屏阅读：切换的是 body 上的一个 class（不重渲染，所以切换必须是瞬时的）
{
  setImmersive(true);
  if (!documentStub.body.classList.contains("immersive")) failures.push("全屏阅读：body 上没有 immersive");
  setImmersive(false);
  if (documentStub.body.classList.contains("immersive")) failures.push("全屏阅读：退出后 immersive 还在");
}

// ③ 阅读页的"细顶栏 + 右侧抽屉"：正文上面只留一行，动作/目录都进抽屉。
//    这条是用户反馈"上层挤压得太狠"的直接对策 —— 谁把动作搬回正文上方，这里就红。
{
  Object.assign(state, { view: "ebook", ebookId: 21, ebookMeta: { itemId: 21, title: "一本很长的书", isPdf: false, type: "epub" }, ebookText: null, ebookPages: null, metaOpen: false });
  getEl("view").innerHTML = "";
  getEl("rdDrawer").innerHTML = "";
  await render();
  const viewHtml = getEl("view").innerHTML;
  const drawerHtml = getEl("rdDrawer").innerHTML;
  if (!viewHtml.includes('class="rd-bar"')) failures.push("细顶栏：阅读页没有 .rd-bar");
  if (!viewHtml.includes('data-act="meta-toggle"')) failures.push("细顶栏：没有展开抽屉的按钮");
  if (viewHtml.includes("导出 MD")) failures.push("细顶栏：动作按钮又回到正文上方了（应在抽屉里）");
  if (!drawerHtml.includes("导出 MD")) failures.push("阅读抽屉：动作按钮不在抽屉里");
  if (!drawerHtml.includes("目录")) failures.push("阅读抽屉：没有目录");
  if (viewHtml.includes('data-act="book-sec"')) failures.push("底部节按钮：应当已经拿掉（节导航在抽屉目录里 + 翻页翻到头自动接下一节）");
  if (!getEl("rdDrawer").hidden) failures.push("阅读抽屉：默认应当是收起的");
  setMetaOpen(true);
  if (getEl("rdDrawer").hidden) failures.push("阅读抽屉：展开后仍然 hidden");
  setMetaOpen(false);
  if (!getEl("rdDrawer").hidden) failures.push("阅读抽屉：收起后没有 hidden");
}

// ④ 调整阅读参数（字号/行距/栏宽/栏数）不能抛异常 —— 位置保持逻辑在桩里退化，
//    但"调一下字号就白屏"这种事故必须在这里拦住。
{
  Object.assign(state, { view: "article", articleId: 12, articleVersion: null, versions: null });
  getEl("view").innerHTML = "";
  await render();
  try {
    bumpFont(1); bumpFont(-1);
    setLeading(180); setContentWidth(900); setCols(2); setCols(1);
  } catch (e) {
    failures.push("调整阅读参数抛异常: " + (e && e.message));
  }
  if (!getEl("view").innerHTML.includes('id="pager"')) failures.push("调整阅读参数后：正文容器没了");
}

// ⑤ 阅读状态要真的记得住（"以后再打开回到原处"的底子）。
//    翻页模式下没有滚动，所以页码必须单独记 —— 否则下次打开永远从第一页开始。
{
  saveReadState(101, { p: 4, s: 2 });
  const st = readStateOf(101);
  if (!st || st.p !== 4 || st.s !== 2) failures.push(`阅读状态：存进去读不出来（${JSON.stringify(st)}）`);
  saveReadState(101, { p: 5 });          // 同一条目只更新给出的字段
  const st2 = readStateOf(101);
  if (st2.p !== 5 || st2.s !== 2) failures.push("阅读状态：局部更新把别的字段冲掉了");
  if (readStateOf(999) !== null) failures.push("阅读状态：没读过的东西不该有记录");
  rememberReadState();                    // 非阅读视图下调用也不能抛
  setPaged(false); setPaged(true);        // 翻页/滚动切换不能抛
}

// 命令面板 / 渲染之外的几条纯函数
try {
  const html = sandbox.__sip.esc('<img src=x onerror=alert(1)>');
  if (!html.includes("&lt;img")) failures.push("esc(): 没有把 < 转义");
} catch (e) { failures.push("esc() 抛异常: " + e); }

// 分节：一本"整本书"必须被切成有界的若干节。
// 这条是拿真实事故换来的 —— 整本书塞进一个 .prose 里做多栏分页，浏览器直接卡死，
// 所以"每一节的排版量有上限"是这个功能的**前提**，不是优化。
try {
  const para = "字".repeat(400);
  const paraNode = () => ({ nodeType: 1, tagName: "P", textContent: para, outerHTML: `<p>${para}</p>` });
  const headNode = (n) => ({ nodeType: 1, tagName: "H2", textContent: `第${n}章`, outerHTML: `<h2>第${n}章</h2>` });
  // 80 段 × 400 字 = 32000 字，只在每 20 段插一个标题
  const nodes = [];
  for (let i = 0; i < 80; i++) {
    if (i % 20 === 0) nodes.push(headNode(i / 20 + 1));
    nodes.push(paraNode());
  }
  const secs = sandbox.__sip.sectionsFromNodes(nodes, "<p>raw</p>", 12000);
  if (secs.length < 3) failures.push(`分节: 32000 字只切出 ${secs.length} 节（应 >=3，否则又会把整本塞进一次分栏）`);
  const longest = Math.max(...secs.map((s) => (s.html.match(/字/g) || []).length));
  if (longest > 14000) failures.push(`分节: 最长的一节 ${longest} 字（应 <=14000，否则分栏代价仍然无界）`);
  if (!secs.every((s) => s.title)) failures.push("分节: 有节没有标题");
  const total = secs.reduce((n, s) => n + (s.html.match(/字/g) || []).length, 0);
  if (total !== 80 * 400) failures.push(`分节: 字数对不上（${total} != ${80 * 400}），有内容被丢了`);
} catch (e) { failures.push("sectionsFromNodes() 抛异常: " + (e && e.message)); }

// ⑥ 逐行差异：大段没改的段落要折起来（只留变更点上下各 3 段做上下文），折叠行能点开。
//    不折的话，"改了哪一段"得在整篇正文里自己找 —— 而且整篇 HTML 只报成一删一插时，
//    页面就是"一大块红接一大块绿"（用户报的"比对功能严重问题"）。
try {
  const line = (type, text) => ({ type, text });
  const changes = [];
  for (let i = 1; i <= 40; i++) changes.push(line("Unchanged", `第 ${i} 段没变`));
  changes.push(line("Deleted", "旧的一段"));
  changes.push(line("Inserted", "新的一段"));
  for (let i = 41; i <= 80; i++) changes.push(line("Unchanged", `第 ${i} 段没变`));
  for (const entry of CANNED) {
    if (entry[0].test("/api/articles/12/diff"))
      entry[1] = { article: 12, from: 1, to: 2, titleOld: "标题", titleNew: "标题", titleChanged: false, added: 1, removed: 1, changes };
  }

  Object.assign(state, { view: "edits", editsId: 12, editFrom: null, editTo: null, diffOpen: {} });
  getEl("view").innerHTML = "";
  await render();
  let html = getEl("view").innerHTML;
  if (!html.includes("段未改动")) failures.push("差异折叠：80 段没变的部分没有折起来");
  if (html.includes("第 20 段没变")) failures.push("差异折叠：折叠区间里的段落还在渲染（等于没折）");
  if (!html.includes("第 3 段没变") || !html.includes("第 40 段没变"))
    failures.push("差异折叠：变更点上下各 3 段应当留作上下文");
  const foldCount = (s) => (s.match(/data-act="diff-more"/g) || []).length;
  if (foldCount(html) !== 2) failures.push(`差异折叠：变更点前后各一段没变 → 应当折成 2 行，实际 ${foldCount(html)}`);
  if (!html.includes("旧的一段") || !html.includes("新的一段")) failures.push("差异折叠：删/插的行被折没了");

  state.diffOpen[0] = true;         // 点开第一段折叠
  getEl("view").innerHTML = "";
  await render();
  html = getEl("view").innerHTML;
  if (foldCount(html) !== 1) failures.push(`差异展开：展开一段后应当只剩 1 个折叠行，实际 ${foldCount(html)}`);
  if (!html.includes("第 20 段没变")) failures.push("差异展开：展开后中间的段落没有渲染出来");
} catch (e) { failures.push("差异折叠 抛异常: " + (e && e.stack ? e.stack.split("\n")[0] : e)); }

if (failures.length) {
  console.error("✗ Web 前端冒烟失败：");
  for (const f of failures) console.error("  - " + f);
  process.exit(1);
}
console.log(`✓ Web 前端冒烟通过：${cases.length} 个视图都能渲染内容，导入项会转到阅读器`);
