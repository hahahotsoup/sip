/* ══════════════════════════════════════════════════════════════════════
   sip Web · 全部前端逻辑（v2.0.0）

   为什么逻辑在这个文件里、而不是 index.html 的 <script> 里：
   服务端下了 CSP `script-src 'self'`，内联脚本一律不执行。
   好处不只是"合规"——「订阅源正文里混进 <script>」这条 XSS 路径
   在浏览器层面就是死的，净化器就算漏掉一个标签也不会被执行。

   另一条纪律：**没有任何内联事件属性**（onclick= / oninput= …）。
   全站用 data-act + 一个委托监听器，理由同上（CSP 会拦，且 inline 处理器
   是注入类漏洞最常见的落点）。

   数据来源只有一个：本机进程内的 /api/*。没有外网请求。
   ══════════════════════════════════════════════════════════════════════ */

/* ───────── 状态容器 ─────────
   全部是**真实数据容器**：接口失败时界面宁可空着，也不显示编造的数字。 */
const FEEDS = [];
const ARTICLES = {};
const LIKES = [];
const TODAY = [];
const TODAY_META = { target: 5, done: 0, tracking: false, generatedAt: "", digest: null };
const IMPORTS = [];
const EDITS = [];
const DEDUP = { clusters: [], hidden: [], threshold: 80, windowHours: 48, scanned: false };
const POLICIES = { actions: [], rows: [] };
const CONFIG = { data: null };
const SIMON = { data: null };
const TELEMETRY = { data: null };
const INDEX = { data: null };
const PALETTE = { items: [], sel: 0 };

const $ = (id) => document.getElementById(id);
const state = {
  view: "today",
  feedId: null,
  articleId: null,
  articleVersion: null,
  versions: null,
  editsId: null,
  editFrom: null,
  editTo: null,
  dedupRep: null,
  dedupPair: null,
  ebookId: null,
  ebookMeta: null,
  ebookPage: 1,
  ebookPages: null,
  jumpKw: "",
  lang: "zh-CN",
  dict: {},
  tab: "reading",
  server: null,
  metaOpen: false,     // 阅读页右侧抽屉是否展开（记在偏好里）
  immersive: false,    // 全屏阅读
  paged: true,         // 翻页模式（关掉就是普通滚动）
};

/* ───────── i18n：直接吃 sip 的语言文件（键 = 英文原文，与 Lang.T 同键） ─────────
   /languages/<code>.json 由内置服务器提供（内嵌在 exe 里），不再 404。 */
const FALLBACK = {
  "zh-CN": {
    Read: "阅读", Search: "检索", Govern: "治理", System: "系统",
    Today: "今日哈汤", Feeds: "订阅源", Likes: "收藏", Imports: "本地导入",
    Edits: "改稿追踪", Dedup: "跨源去重", Policies: "源规则", Insights: "阅读报告",
    "Reading settings": "设置", About: "关于",
    "+ Feed": "＋ 订阅", Sync: "同步", "⟨ Collapse": "⟨ 收起",
    Day: "白昼", Paper: "羊皮纸", Night: "夜读",
    Font: "字号", Columns: "分栏", "UI language": "界面语言", Other: "其它",
  },
  "en-US": {},
  "zh-Moe": {
    Today: "今日哈汤喵", Feeds: "订阅源喵", Likes: "收藏喵", Imports: "本地导入喵",
  },
};

async function loadLangDict(code) {
  if (code === "en-US") { state.dict = {}; return; }
  try {
    const res = await fetch(`/languages/${code}.json`, { cache: "no-store" });
    if (!res.ok) throw new Error(res.status);
    const data = await res.json();
    const flat = {};
    (function walk(o) {
      if (!o || typeof o !== "object") return;
      for (const [k, v] of Object.entries(o)) {
        if (typeof v === "string") flat[k] = v;
        else if (v && typeof v === "object") walk(v);
      }
    })(data);
    state.dict = flat;
  } catch (e) {
    state.dict = FALLBACK[code] || {};
  }
}

function t(key) {
  if (state.lang === "en-US") return key;
  const d = state.dict;
  if (d && d[key]) return d[key];
  const fb = FALLBACK[state.lang];
  if (fb && fb[key]) return fb[key];
  return key;
}

async function applyLang() {
  document.documentElement.lang = state.lang;
  await loadLangDict(state.lang);
  document.querySelectorAll("[data-i]").forEach((el) => { el.textContent = t(el.dataset.i); });
  try { localStorage.setItem("sip-lang", state.lang); } catch { }
}

async function setLang(code) {
  state.lang = ["zh-CN", "en-US", "zh-Moe"].includes(code) ? code : "zh-CN";
  await applyLang();
  aiSyncText();                    // 新界面的文案也要跟着语言走，别单独掉队
  render();
  toast(state.lang === "en-US" ? "Language: English" : state.lang === "zh-Moe" ? "语言：zh-Moe（卖萌）" : "语言：简体中文");
}

/* ───────── 小工具 ───────── */
function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function toast(m) {
  const el = $("toast"); if (!el) return;
  el.textContent = m; el.classList.add("show");
  clearTimeout(el._t); el._t = setTimeout(() => el.classList.remove("show"), 1900);
}

/* 面板里的**持续**状态行（toast 1.9 秒就消失，而下载/索引要几秒到几分钟 ——
   用户看到的正好是"点完就没动静"，于是只能连点试探，而连点又会重复跑）。 */
function setMsg(id, text, kind) {
  const el = $(id); if (!el) return;
  el.textContent = text || "";
  el.hidden = !text;
  el.style.color = kind === "bad" ? "var(--bad)" : kind === "ok" ? "var(--accent)" : "var(--muted)";
}

function setBusy(btn, on, busyLabel) {
  if (!btn) return;
  if (on) { if (btn.dataset.idle == null) btn.dataset.idle = btn.textContent; btn.disabled = true; btn.textContent = busyLabel || "处理中…"; }
  else { btn.disabled = false; if (btn.dataset.idle != null) btn.textContent = btn.dataset.idle; }
}

function on(id, ev, fn) {
  const el = $(id);
  if (el && el.addEventListener) el.addEventListener(ev, fn);
}

function fmtDate(s) {
  if (!s) return "";
  const d = new Date(s);
  if (isNaN(d)) return String(s);
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function fmtSize(n) {
  if (!n) return "—";
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return (n / 1024).toFixed(0) + " KB";
  return (n / 1024 / 1024).toFixed(1) + " MB";
}

/* ───────── API ─────────
   唯一的数据入口。401 = 会话随进程结束了（sip 是进程内一次性密钥）：
   直接把人送回登录页，而不是弹一句看不懂的 UNAUTHORIZED。 */
async function api(path, opts) {
  const res = await fetch(path, { headers: { "Content-Type": "application/json" }, ...opts });
  let data = null; try { data = await res.json(); } catch { }
  if (!res.ok || (data && data.success === false)) {
    if (res.status === 401 && !api._sentToLogin) { api._sentToLogin = true; location.replace("/"); }
    const e = new Error(data?.error?.message || res.statusText || "error");
    e.code = data?.error?.code || "";
    e.hint = data?.error?.hint || "";
    e.detail = data?.error || null;
    e.status = res.status;
    throw e;
  }
  return data?.data ?? data;
}

/* ───────── 真实数据加载 ───────── */
async function loadRealData() {
  try {
    const [st, feeds, today, likes, imports] = await Promise.all([
      api("/api/status").catch(() => null),
      api("/api/feeds").catch(() => null),
      api("/api/today").catch(() => null),
      api("/api/likes").catch(() => null),
      api("/api/imports").catch(() => null),
    ]);
    if (feeds?.feeds) {
      FEEDS.length = 0;
      feeds.feeds.forEach((f) => FEEDS.push({
        id: f.id, num: f.displayNum, title: f.title, url: f.url || "",
        active: f.active || 0, archived: f.archived || 0, deleted: f.deleted || 0,
        schedule: f.schedule || "manual",
        health: f.health || "",
        last: f.lastChecked || "",
        isImport: (f.url || "") === "local://import",
      }));
    }
    if (today) {
      TODAY_META.target = today.target ?? 5;
      TODAY_META.done = today.done ?? 0;
      TODAY_META.tracking = !!today.tracking;
      TODAY_META.generatedAt = today.generatedAt || "";
      if (today.digest) TODAY_META.digest = today.digest;
      TODAY.length = 0;
      (today.items || []).forEach((i) => TODAY.push({
        id: i.itemId, t: i.title || "", src: i.source || "", reason: i.reason || "", min: i.minutes || 5,
      }));
    }
    if (likes?.signals) {
      LIKES.length = 0;
      likes.signals.forEach((s) => LIKES.push({ id: s.itemId, t: s.title, feed: s.feed || "", ai: !!s.aiLiked }));
    }
    if (imports) {
      IMPORTS.length = 0;
      (imports.items || []).forEach((x) => IMPORTS.push(x));
    }
    if (st?.sip) state.server = st;
    render();
  } catch (e) {
    toast("API 不可用（请用 sip --start 打开）");
  }
}

async function loadFeedArticles(feedId) {
  try {
    const d = await api(`/api/feeds/${feedId}/articles`);
    ARTICLES[feedId] = (d.articles || []).map((a) => ({
      id: a.itemId, t: a.title, q: a.quality || "full", hist: !!a.hasHistory, liked: !!a.liked, ai: !!a.aiLiked,
      pub: a.published || "",
    }));
    return d;
  } catch { return null; }
}

/* ───────── 订阅源操作 ───────── */
async function realFeedUpdate(id) {
  const btn = document.querySelector(`[data-update="${id}"]`);
  setBusy(btn, true, "更新中…");
  toast("更新中…（完成后会报新增篇数）");
  try {
    const d = await api(`/api/feeds/${id}/update`, { method: "POST" });
    toast((d.added > 0) ? `更新完成：新增 ${d.added} 篇` : "更新完成：没有新内容");
    await loadRealData();
    await loadFeedArticles(id);
    render();
  } catch (e) {
    toast(e.code === "ALREADY_RUNNING" ? "这个源正在更新中，等它结束" : (e.message || "更新失败"));
  } finally { setBusy(document.querySelector(`[data-update="${id}"]`), false); }
}

async function realFeedArch(id, archive) {
  try {
    await api(`/api/feeds/${id}/${archive ? "archive" : "unarchive"}`, { method: "POST" });
    toast(archive ? "已归档" : "已去归档");
    await loadRealData();
    render();
  } catch (e) { toast(e.message || "操作失败"); }
}

async function realFeedInfo(id) {
  try {
    const d = await api(`/api/feeds/${id}/info`);
    toast(`${d.title} · ${d.schedule || "manual"} · ${d.lastChecked ? fmtDate(d.lastChecked) : "从未更新"}`);
  } catch (e) { toast(e.message || "无信息"); }
}

async function realFeedDel(id) {
  if (!confirm("删除该订阅源及其全部文章？")) return;
  try {
    await api(`/api/feeds/${id}`, { method: "DELETE" });
    toast("已删除");
    state.view = "feeds"; state.feedId = null;
    await loadRealData();
  } catch (e) { toast(e.message || "删除失败"); }
}

async function realFeedSchedule(id) {
  const sel = document.querySelector(`[data-sched="${id}"]`);
  const input = document.querySelector(`[data-sched-custom="${id}"]`);
  let expr = (sel?.value || "").trim();
  if (expr === "__custom") expr = (input?.value || "").trim();
  try {
    const d = await api(`/api/feeds/${id}/schedule`, { method: "POST", body: JSON.stringify({ expr }) });
    toast(d.schedule ? `更新计划：${d.schedule}` : "已设为只手动更新");
    await loadRealData();
    render();
  } catch (e) { toast(e.message || "设置失败"); }
}

/* ───────── 文章操作 ───────── */
async function realLike(id) {
  try {
    const d = await api(`/api/articles/${id}/like`, { method: "POST" });
    toast(d.liked ? "已收藏 ♥" : "已取消收藏");
    await loadRealData();
  } catch (e) { toast(e.message || "操作失败"); }
}

let pendingFulltext = 0;
async function realFulltext(id, consent) {
  toast(consent ? "已同意，正在抓全文…" : "正在抓全文…");
  try {
    const d = await api(`/api/articles/${id}/fulltext`, { method: "POST", body: JSON.stringify({ consent: !!consent }) });
    toast(`全文已缓存（${d.bytes || 0} 字）`);
    render();
  } catch (e) {
    // 第一次抓全文：后端要求先确认免责声明（与 CLI 同一道门、同一段话）
    if (e.code === "FULLTEXT_NEEDS_CONSENT") {
      pendingFulltext = id;
      $("consentText").textContent = (e.detail && e.detail.disclaimer) || e.message || "";
      $("pConsent").hidden = false;
      return;
    }
    toast(e.message || "抓取失败");
  }
}

async function realSummary(id) {
  toast("正在生成摘要…");
  try {
    const d = await api(`/api/articles/${id}/summary`, { method: "POST" });
    toast(d.summary ? "摘要已生成" : "摘要完成");
    render();
  } catch (e) { toast(e.message || "摘要失败"); }
}

async function realTodayRefresh() {
  try {
    await api("/api/today/refresh", { method: "POST" });
    toast("已重新生成今日哈汤");
    await loadRealData();
  } catch (e) { toast(e.message || "失败"); }
}

async function realTodayDigest() {
  toast("正在算今日变化…（要扫一遍 48 小时窗口）");
  try {
    const d = await api("/api/today?digest=1");
    TODAY_META.digest = d.digest || null;
    render();
  } catch (e) { toast(e.message || "计算失败"); }
}

function realTodayMarkRead() {
  // 「已读」由遥测的 article_complete 驱动；遥测关闭时不假装有进度
  if (!TODAY_META.tracking) { toast("进度未跟踪：遥测默认关闭（sip telemetry enable 可开启）"); return; }
  toast(`已读 ${TODAY_META.done} / 目标 ${TODAY_META.target}`);
}

/* ───────── 改稿追踪 ───────── */
async function loadEdits() {
  const d = await api("/api/edits?limit=100");
  EDITS.length = 0;
  (d.items || []).forEach((x) => EDITS.push(x));
  return EDITS;
}

async function loadVersions(itemId) {
  const d = await api(`/api/articles/${itemId}/versions`);
  state.versions = d.versions || [];
  return state.versions;
}

async function loadDiff(itemId, from, to) {
  const q = [];
  if (from != null) q.push("from=" + from);
  if (to != null) q.push("to=" + to);
  return await api(`/api/articles/${itemId}/diff${q.length ? "?" + q.join("&") : ""}`);
}

/* 逐行差异的行渲染：把**大段没变的段落折起来**，只留变更点上下各 3 段。
   整篇贴出来的话，改了哪一段还得自己找 —— 折起来一眼就能看到变了几处。
   run = 未变段落的第几段连片（0,1,2…），展开状态记在 state.diffOpen 里。 */
const DIFF_CTX = 3;
function diffRowsHtml(changes) {
  // 展开状态用普通对象（不用 Set）：跨 realm 的 instanceof 会失效，测试里就翻过这个坑
  const open = state.diffOpen || (state.diffOpen = {});
  changes = changes || [];
  const out = [];
  let i = 0, run = 0;
  const fold = (from, to, r) =>
    `<div class="diff-skip" data-act="diff-more" data-run="${r}">⋯ 中间 ${to - from} 段未改动 · 点开看 ⋯</div>`;
  while (i < changes.length) {
    if (changes[i].type !== "Unchanged") {
      const c = changes[i];
      out.push(c.type === "Deleted" ? `<div class="diff-del">${esc(c.text)}</div>`
        : c.type === "Inserted" ? `<div class="diff-add">${esc(c.text)}</div>`
          : `<div>${esc(c.text) || "&nbsp;"}</div>`);
      i++;
      continue;
    }
    let j = i;
    while (j < changes.length && changes[j].type === "Unchanged") j++;
    const n = j - i;
    if (n <= DIFF_CTX * 2 + 1 || open[run]) {
      for (let k = i; k < j; k++) out.push(`<div>${esc(changes[k].text) || "&nbsp;"}</div>`);
    } else {
      for (let k = i; k < i + DIFF_CTX; k++) out.push(`<div>${esc(changes[k].text) || "&nbsp;"}</div>`);
      out.push(fold(i + DIFF_CTX, j - DIFF_CTX, run));
      for (let k = j - DIFF_CTX; k < j; k++) out.push(`<div>${esc(changes[k].text) || "&nbsp;"}</div>`);
    }
    run++; i = j;
    if (out.length > 3000) { out.push(`<div class="diff-skip">⋯ 差异过长，后续省略 ⋯</div>`); break; }
  }
  return out.join("");
}

/* ───────── 跨源去重 ───────── */
async function loadDedup(scan) {
  const d = scan
    ? await api("/api/dedup/scan", { method: "POST", body: JSON.stringify({ window: DEDUP.windowHours }) })
    : await api(`/api/dedup?window=${DEDUP.windowHours}`);
  DEDUP.clusters = d.clusters || [];
  DEDUP.hidden = d.hidden || [];
  DEDUP.threshold = Math.round((d.threshold || 0.8) * 100);
  DEDUP.scanned = !!d.scanned;
  return DEDUP;
}

async function realDedupHide(hiddenId, canonicalId) {
  try {
    await api("/api/dedup/hide", { method: "POST", body: JSON.stringify({ hiddenId, canonicalId }) });
    toast("已隐藏（可撤销，没有删除任何东西）");
    await loadDedup(false);
    render();
  } catch (e) { toast(e.message || "隐藏失败"); }
}

async function realDedupHideCluster(rep) {
  try {
    const d = await api("/api/dedup/hide-cluster", { method: "POST", body: JSON.stringify({ representativeId: rep }) });
    toast(`已隐藏 ${d.hidden} 篇` + (d.fails?.length ? `（${d.fails.length} 篇跳过）` : ""));
    await loadDedup(false);
    render();
  } catch (e) { toast(e.message || "操作失败"); }
}

async function realDedupUndo(key) {
  try {
    await api("/api/dedup/undo", { method: "POST", body: JSON.stringify({ key }) });
    toast("已撤销隐藏");
    await loadDedup(false);
    render();
  } catch (e) { toast(e.message || "撤销失败"); }
}

/* ───────── 源规则 ───────── */
async function loadPolicies() {
  const d = await api("/api/policies");
  POLICIES.actions = d.actions || [];
  POLICIES.rows = d.policies || [];
  return POLICIES;
}

async function realPolicyAdd() {
  const feedId = +($("polFeed")?.value || 0);
  const action = $("polAction")?.value || "";
  const schedule = ($("polSchedule")?.value || "").trim();
  const tag = ($("polTag")?.value || "").trim();
  const note = ($("polNote")?.value || "").trim();
  if (!feedId) { setMsg("polMsg", "请先选一个订阅源", "bad"); return; }
  setBusy($("polAdd"), true, "保存中…");
  try {
    await api("/api/policies", { method: "POST", body: JSON.stringify({ feedId, action, schedule, tag, note }) });
    setMsg("polMsg", "规则已保存（createdBy 永远是你）", "ok");
    await loadPolicies();
    render();
  } catch (e) {
    setMsg("polMsg", e.message || "保存失败", "bad");
  } finally { setBusy($("polAdd"), false); }
}

async function realPolicyDel(feedId) {
  try {
    await api(`/api/policies/${feedId}`, { method: "DELETE" });
    toast("已删除规则");
    await loadPolicies();
    render();
  } catch (e) { toast(e.message || "删除失败"); }
}

/* ───────── 本地导入 / 电子书 ───────── */
async function realImportFiles(files) {
  if (!files || !files.length) return;
  let ok = 0, fail = 0;
  for (let i = 0; i < files.length; i++) {
    const f = files[i];
    setMsg("impMsg", `导入中 ${i + 1}/${files.length} · ${f.name}`, "");
    try {
      await api(`/api/imports?name=${encodeURIComponent(f.name)}`, {
        method: "POST",
        headers: { "Content-Type": "application/octet-stream" },
        body: f,
      });
      ok++;
    } catch (e) {
      fail++;
      setMsg("impMsg", `导入失败：${f.name} —— ${e.message || "未知错误"}`, "bad");
    }
  }
  toast(`导入完成：${ok} 成功 / ${fail} 失败`);
  if (fail === 0) setMsg("impMsg", `导入完成：${ok} 个文件`, "ok");
  await loadRealData();
}

/* 原文件：浏览器直接打开 + 临时链接。
   PDF 在网页里只能按页看图（没文本层、不能选字搜字），所以真正好用的是
   "把原文件交给浏览器自带的阅读器"。临时链接则是给"贴到别的标签页 / PDF 程序 / 手机"用的：
   普通接口地址在别的浏览器里会 401（会话是一浏览器一把钥匙），令牌不会。 */
function openImportedFile(itemId) {
  // 同源 GET，带上会话 cookie —— 页面里直接点开就够了
  window.open(`/api/imports/${itemId}/file`, "_blank", "noopener");
}

async function copyImportedLink(itemId, btn) {
  setBusy(btn, true, "取链接…");
  try {
    const d = await api(`/api/imports/${itemId}/link`, { method: "POST" });
    const full = location.origin + d.url;
    let copied = false;
    try {
      await navigator.clipboard.writeText(full);
      copied = true;
    } catch {
      // 剪贴板要么被策略挡、要么不在安全上下文：退化成"让你自己复制"
      window.prompt("临时链接（10 分钟内有效，只对这份文件）：", full);
    }
    toast(copied
      ? `临时链接已复制（${Math.round(d.expiresInSeconds / 60)} 分钟内有效）`
      : "已给出临时链接");
  } catch (e) {
    toast(e.message || "取链接失败");
  } finally { setBusy(btn, false); }
}

async function realImportDel(itemId) {  if (!confirm("删除这份导入（同时删掉数据目录里的那份拷贝）？")) return;
  try {
    await api(`/api/imports/${itemId}`, { method: "DELETE" });
    toast("已删除");
    if (state.view === "ebook" && state.ebookId === itemId) { state.view = "imported"; state.ebookId = null; }
    await loadRealData();
  } catch (e) { toast(e.message || "删除失败"); }
}

async function openEbook(itemId) {
  state.view = "ebook";
  state.ebookId = itemId;
  state.ebookPage = 1;
  state.ebookPages = null;
  state.ebookMeta = IMPORTS.find((x) => x.itemId === itemId) || null;
  // 换书就丢掉上一本的正文缓存（一本可能几百 KB，留着既占内存又容易串味）
  state.ebookText = null;
  try {
    const d = await api(`/api/imports/${itemId}`);
    state.ebookMeta = d;
    state.ebookPages = d.pages || null;
    const rp = _rpMap[String(itemId)];
    if (rp && rp > 0) state.ebookPage = Math.min(rp, state.ebookPages || rp);
  } catch (e) {
    toast(e.message || "打不开");
    state.view = "imported";
  }
  // **await**：调用方（比如"文章视图发现这是导入项就转给阅读器"）要等这一屏真的画完，
  // 否则会先闪一下"加载正文…"再跳走。
  await render();
  $("content").scrollTop = 0;
}

function ebookGo(page) {
  const max = state.ebookPages || 1;
  state.ebookPage = Math.min(max, Math.max(1, page | 0));
  render();
  $("content").scrollTop = 0;
  api("/api/reading-progress", { method: "POST", body: JSON.stringify({ itemId: state.ebookId, position: state.ebookPage }) }).catch(() => { });
}

/* ───────── 治理面 ───────── */
async function loadGovernance() {
  const [cfg, simon, tel, idx] = await Promise.all([
    api("/api/config").catch(() => null),
    api("/api/simon").catch(() => null),
    api("/api/telemetry").catch(() => null),
    api("/api/index").catch(() => null),
  ]);
  CONFIG.data = cfg;
  SIMON.data = simon;
  TELEMETRY.data = tel;
  INDEX.data = idx;
}

async function realSimonUp(level) {
  try {
    const d = await api("/api/simon/level", { method: "POST", body: JSON.stringify({ level }) });
    toast(d.changed ? `挡位已升到 ${d.level}（收紧任意通道都可以）` : `当前就是挡位 ${d.level}`);
    await loadGovernance();
    render();
  } catch (e) {
    toast(e.code === "SIMON_LOOSEN_REQUIRES_TERMINAL"
      ? "降档要在真实终端里执行：sip simon level " + level
      : (e.message || "操作失败"));
  }
}

async function realTelemetry(on) {
  try {
    await api("/api/telemetry", { method: "POST", body: JSON.stringify({ enabled: on }) });
    toast(on ? "遥测已开启（只写本机 telemetry.db，不上传）" : "遥测已关闭");
    await loadGovernance();
    render();
  } catch (e) { toast(e.message || "操作失败"); }
}

async function realIndexRun(reindex) {
  if (!CONFIG.data?.ai?.configured) { toast("AI 还没配置：先在终端跑 sip --init"); return; }
  if (reindex && !confirm("重建索引会清空现有向量并重新嵌入全部活跃文章，继续？")) return;
  toast(reindex ? "重建索引中…（完成后会报结果）" : "补索引中…");
  try {
    const d = await api("/api/index", { method: "POST", body: JSON.stringify({ reindex }) });
    toast(`索引完成：${d.ok} 成功 / ${d.fail} 失败（共 ${d.total} 篇）`);
    await loadGovernance();
    render();
  } catch (e) {
    toast(e.code === "AI_NOT_CONFIGURED" ? "先在终端跑 sip --init（API Key 只从终端输入）" : (e.message || "索引失败"));
  }
}

async function realSummaryAll() {
  if (!confirm("为所有还没有摘要的活跃文章生成摘要？（会调用 LLM，篇数多时较慢）")) return;
  setMsg("govMsg", "生成摘要中…", "");
  try {
    const d = await api("/api/summaries", { method: "POST" });
    setMsg("govMsg", d.total === 0 ? "所有文章都已有摘要" : `摘要完成：${d.ok} 成功 / ${d.fail} 失败（共 ${d.total} 篇）`, d.fail ? "bad" : "ok");
  } catch (e) {
    setMsg("govMsg", e.code === "AI_NOT_CONFIGURED" ? "AI 未配置：先在终端跑 sip --init" : (e.message || "失败"), "bad");
  }
}

async function realPurgeFulltext() {
  if (!confirm("清空全部全文缓存？（只删缓存，不动订阅源与文章）")) return;
  try {
    await api("/api/purge-fulltext", { method: "POST", body: JSON.stringify({ itemId: 0 }) });
    toast("全文缓存已清空");
    await loadGovernance();
    render();
  } catch (e) { toast(e.message || "清理失败"); }
}

async function realInsightsInterval(value) {
  try {
    const d = await api("/api/insights/interval", { method: "POST", body: JSON.stringify({ interval: value }) });
    toast(d.interval === "off" ? "已关闭报告定时提醒" : `报告提醒：每 ${d.interval}`);
    await loadGovernance();
    render();
  } catch (e) { toast(e.message || "设置失败"); }
}

/* ───────── 搜索 ───────── */
async function runSearch(mode, q, thr) {
  if (!q) return null;
  if (mode === "sem") return await api(`/api/search?q=${encodeURIComponent(q)}&threshold=${thr}`);
  return await api(`/api/grep?q=${encodeURIComponent(q)}&limit=50`);
}

/* ───────── 偏好 / 主题 / 阅读控制 ───────── */
const PREF_KEY = "sip-web-prefs-v1";
const BUILTIN_THEMES = ["paper", "day", "night"];

function loadPrefs() {
  try { return JSON.parse(localStorage.getItem(PREF_KEY) || "{}"); } catch { return {}; }
}
function savePrefs(patch) {
  const p = { ...loadPrefs(), ...patch };
  try { localStorage.setItem(PREF_KEY, JSON.stringify(p)); } catch { }
  return p;
}
function setTheme(th) {
  const root = document.documentElement;
  root.dataset.theme = BUILTIN_THEMES.includes(th) ? th : "paper";
  const btn = $("rtTheme"); if (btn) btn.classList.toggle("on", root.dataset.theme === "night");
  savePrefs({ theme: root.dataset.theme });
}
function cycleTheme() {
  const cur = document.documentElement.dataset.theme || "paper";
  const i = BUILTIN_THEMES.indexOf(cur);
  setTheme(BUILTIN_THEMES[(i + 1) % BUILTIN_THEMES.length]);
  if (state.view === "settings") render();
}
function bumpFont(d) {
  const cur = parseInt(getComputedStyle(document.documentElement).getPropertyValue("--reading-size")) || 17;
  const n = Math.min(24, Math.max(14, cur + d));
  document.documentElement.style.setProperty("--reading-size", n + "px");
  const el = $("rtSize"); if (el) el.textContent = n;
  savePrefs({ size: n });
  repaginateKeepingPlace();   // 字号变了 → 重新分页，但**停在原来那段**
}
function setLeading(v) {
  document.documentElement.style.setProperty("--reading-leading", String(v / 100));
  savePrefs({ lh: v });
  repaginateKeepingPlace();
}
function setContentWidth(px) {
  const vw = Math.max(560, window.innerWidth - 48);
  const n = Math.min(Math.max(2400, vw), Math.max(560, px));
  document.documentElement.style.setProperty("--content-w", n + "px");
  const el = $("rtW"); if (el) { el.max = String(Math.max(2400, vw)); if (+el.value !== n) el.value = n; }
  savePrefs({ width: n });
  repaginateKeepingPlace();
}
/** 一栏至少要有这么宽，否则中文一行只放得下十来个字 —— 看着就是"比例很奇怪"。
 *  按**字数**算而不是拍一个像素数：字号调大，需要的栏宽也跟着大。 */
function minColumnWidth() {
  const size = parseInt(getComputedStyle(document.documentElement).getPropertyValue("--reading-size")) || 17;
  return Math.max(300, Math.round(size * 18));   // 18 字/行 ≈ 中文排版的舒适下限
}

/** 用户点了 n 栏，但如果当前阅读区**装不下** n 个够宽的栏，就按可用宽度降栏。
 *  降的只是这一屏怎么排；偏好本身不动 —— 窗口拉宽、栏宽调大之后自动恢复。
 *  另外：**电子书最多两栏**（用户定的），手机上 CSS 会再压成一栏。 */
function effectiveCols(n) {
  if (n <= 1) return 1;
  if (state.view === "ebook") n = Math.min(n, 2);
  const avail = ($("pager")?.clientWidth || 0);
  if (avail <= 0) return n;                      // 量不到就先按用户选的来
  const fit = Math.max(1, Math.floor(avail / minColumnWidth()));
  return Math.min(n, fit);
}

function setCols(n, userInitiated) {
  n = Math.min(3, Math.max(1, n | 0));
  const eff = effectiveCols(n);
  // `--cols` 保存**用户选的**（设置页/按钮据此高亮），实际排版用 data-cols=eff。
  document.documentElement.style.setProperty("--cols", String(n));
  document.querySelectorAll(".prose").forEach((p) => { p.dataset.cols = String(eff); });
  document.querySelectorAll(".colbtn").forEach((b) => b.classList.toggle("on", +b.dataset.cols === n));
  savePrefs({ cols: n });
  pager._warned = false;   // 换一篇/换一次栏数，允许再提示一次
  if (userInitiated && eff < n)
    toast(`${n} 栏放不下（每栏至少要 ${minColumnWidth()}px）—— 已按当前宽度显示 ${eff} 栏；把「宽」拉大或缩小字号可恢复`);
  repaginateKeepingPlace();   // 换栏数也是重排：停在原来那段，不回到第一页
}
function restorePrefs() {
  const p = loadPrefs();
  if (p.theme) setTheme(p.theme);
  if (p.metaOpen) state.metaOpen = true;   // 阅读抽屉的展开状态也记着
  if (p.paged === false) state.paged = false;
  if (p.size) document.documentElement.style.setProperty("--reading-size", p.size + "px");
  if (p.lh) {
    document.documentElement.style.setProperty("--reading-leading", String(p.lh / 100));
    const el = $("rtLh"); if (el) el.value = p.lh;
  }
  if (p.width) setContentWidth(p.width);
  if (p.cols) setCols(p.cols);
  if (p.size && $("rtSize")) $("rtSize").textContent = p.size;
}

/* ── 分栏翻页 ──
   多栏时不纵向滚动：把 .prose 定成"一页高"，用 transform 横向平移翻页。
   页数取两种估算的较大值 —— 宁可多一页空白，也不能少算一页（少算会让后面的内容被裁掉）。

   ⚠️ **代价与文档长度成正比**：分栏要求浏览器一次性把整段内容排版出来，
   再读 scrollWidth/高度。整本书（几十万字）塞进来做这件事，页面会直接卡死 ——
   所以电子书正文**先切成"节"**（见 splitBookSections），只对当前这一节分页；
   这里再加一道长度上限兜底，宁可退回滚动，也不能把浏览器拖垮。 */
// axis："x" = 多栏横向翻页；"y" = 单栏纵向翻页（offsets 记录每页从哪个段落开始）
const pager = { on: false, art: null, page: 0, pages: 1, step: 0, axis: "x", offsets: null };
const PAGER_MAX_HEIGHT = 240000;   // px，约等于"一节"的上限；超过就不分页了
let _pagerPending = 0;

/** 合并连续的测量请求：图片是**一张一张**加载完的，每张都全量重排一次
 *  等于把一次卡顿拆成几十次。攒到下一帧只做一次。
 *
 *  ⚠️ 必须走**保位**的那条路（repaginateKeepingPlace），不能直接 pagerMeasure ——
 *  后者只保住"第几页"这个序号，而图片加载会把页边界整体推移，
 *  于是"第 3 页"在新边界下对应更靠前的内容，读起来就是"突然翻回上一页"。
 *  平板最明显：图更慢、地址栏一隐一现又反复触发 resize。 */
function pagerMeasureSoon() {
  if (_pagerPending) return;
  const raf = typeof requestAnimationFrame === "function" ? requestAnimationFrame : (fn) => setTimeout(fn, 16);
  _pagerPending = raf(() => { _pagerPending = 0; repaginateKeepingPlace(); });
}

function pagerReset() {
  pager.on = false; pager.art = null; pager.page = 0; pager.pages = 1; pager.step = 0;
  pager.axis = "x"; pager.offsets = null;
  const box = $("pager");
  if (box) {
    box.classList.remove("paged");
    const a = box.querySelector(".prose");
    if (a) { a.style.transform = ""; a.style.height = ""; }
  }
  const bar = $("pagerBar"); if (bar) bar.hidden = true;
}

/** 分不了页就别再装作分栏：**多栏 + 纵向滚动**读起来正是"左栏读到底，右栏还在下面"，
    比单栏更糟。这里只把**当前这一篇**退回单栏（不动用户的全局偏好），并说明原因。 */
function pagerFallbackToSingle(reason) {
  const art = document.querySelector(".pager .prose");
  if (!art || +(art.dataset.cols || 1) < 2) return;
  art.dataset.cols = "1";
  if (!pager._warned) { pager._warned = true; toast(reason); }
}
function pagerApply() {
  if (!pager.on || !pager.art) return;
  if (pager.axis === "y") {
    pager.art.style.transform = "translateY(" + (-(pager.offsets[pager.page] || 0)) + "px)";
  } else {
    pager.art.style.transform = "translateX(" + (-pager.page * pager.step) + "px)";
  }
  const n = $("pagerNum"); if (n) n.textContent = (pager.page + 1) + " / " + pager.pages;
  const bar = $("pagerBar"); if (bar) bar.hidden = (pager.pages < 2);
  // 翻到这一节的最后一页时，那个 › 就是"下一节"：底部那排节按钮已经拿掉了，
  // 但"读到头自然往下走"不能丢，否则每换一节都得先去开抽屉。
  const secs = state.ebookText?.sections || [];
  const isBook = state.view === "ebook" && secs.length > 1;
  const hasNext = isBook && (state.ebookText.section ?? 0) < secs.length - 1;
  const hasPrev = isBook && (state.ebookText.section ?? 0) > 0;
  const next = document.querySelector("#pagerBar [data-page='1']");
  const prev = document.querySelector("#pagerBar [data-page='-1']");
  if (next) next.textContent = (pager.page >= pager.pages - 1 && hasNext) ? "下一节 ›" : "›";
  if (prev) prev.textContent = (pager.page <= 0 && hasPrev) ? "‹ 上一节" : "‹";
  rememberReadState();   // 翻到哪就记到哪（分开防抖，见那里的注释）
}

function pagerGo(d) {
  if (!pager.on) return false;
  const p = pager.page + d;
  if (p < 0 || p > pager.pages - 1) {
    // 翻过这一节的边界 → 接着读下一节（或退回上一节的最后一页，往前读才连贯）
    const secs = state.ebookText?.sections || [];
    if (state.view === "ebook" && secs.length > 1) {
      const want = (state.ebookText.section ?? 0) + d;
      if (want >= 0 && want < secs.length) { gotoSection(want, d > 0 ? "first" : "last"); return true; }
    }
    return false;
  }
  if (p === pager.page) return false;
  pager.page = p; pagerApply(); return true;
}

/** 跳到第 i 节；edge 决定落在这一节的哪一页。 */
async function gotoSection(i, edge) {
  const secs = state.ebookText?.sections || [];
  if (i < 0 || i >= secs.length) return false;
  state.ebookText.section = i;
  saveReadingPosition(state.ebookId, i);   // 文本型电子书的阅读位置 = 节号
  state._sectionEdge = edge === "last" ? "last" : "first";
  await render();
  $("content").scrollTop = 0;
  return true;
}

/* ── 重排时**保住读到哪儿** ──
   调字号 / 行距 / 栏宽 / 栏数会让分页全变，而**页码在重排后没有可比性**
   （新字号下的第 3 页完全是别的内容）。有可比性的是**内容**：
   先抓住当前这一页开头的那一段，重排后再回到包含它的那一页。
   量不到段落时退回"按比例"，总之不把人打回第一页。 */
function pagerAnchor() {
  if (!pager.art) return null;
  try {
    const kids = pager.art.children;
    if (!pager.on) {
      // 滚动模式：锚点 = 当前视口顶部那一段（用 #content 的滚动位置换算到正文坐标）。
      // 有了它，"滚动 → 分页"的切换才能落在原地，而不是把人甩回第一页。
      const el = $("content");
      if (!el) return null;
      const artTop = pager.art.getBoundingClientRect().top - el.getBoundingClientRect().top;
      const targetY = (el.scrollTop || 0) - artTop + 4;
      let best = null;
      for (const k of kids) {
        if ((k.offsetTop || 0) <= targetY) best = k; else break;
      }
      return best;
    }
    if (pager.axis === "y") {
      const y0 = pager.offsets[pager.page] || 0;
      for (const k of kids) if ((k.offsetTop || 0) >= y0 - 2) return k;
      return null;
    }
    const x0 = pager.page * pager.step;
    for (const k of kids) {
      if ((k.offsetLeft || 0) >= x0 - 2) return k;
    }
  } catch { /* 取不到就算了，退回按比例 */ }
  return null;
}

function pagerRestoreTo(anchor, ratio) {
  if (!pager.on) return;
  if (anchor && anchor.isConnected) {
    if (pager.axis === "y") {
      const y = anchor.offsetTop || 0;
      let p = 0;
      for (let i = 0; i < pager.offsets.length; i++) if (pager.offsets[i] <= y + 2) p = i;
      pager.page = Math.min(pager.pages - 1, Math.max(0, p));
      pagerApply();
      return;
    }
    const p = Math.floor((anchor.offsetLeft || 0) / Math.max(1, pager.step));
    pager.page = Math.min(pager.pages - 1, Math.max(0, p));
    pagerApply();
    return;
  }
  if (ratio > 0) {
    pager.page = Math.min(pager.pages - 1, Math.max(0, Math.round(ratio * (pager.pages - 1))));
    pagerApply();
  }
}

function repaginateKeepingPlace() {
  const anchor = pagerAnchor();
  const ratio = (pager.on && pager.pages > 1) ? pager.page / (pager.pages - 1) : 0;
  const wasOn = pager.on;
  pagerMeasure();
  if (!wasOn && pager.on) {
    // 刚从**滚动**切到**翻页**（内容变长 / 版式变了）：按锚点落到对应的那一页，
    // 并把滚动位置归零 —— 否则画面会"唰"地回到第一页。
    pagerRestoreTo(anchor, 0);
    const el = $("content"); if (el) el.scrollTop = 0;
    return;
  }
  pagerRestoreTo(anchor, ratio);
}
function pagerMeasure() {
  try {
    // 文章正文与（文本型）电子书正文**共用**这一套分栏翻页：两者的 .pager/.prose 结构一致，
    // 行为也就该一致 —— 用户不需要知道"这里能翻页、那里不能"。
    const readable = state.view === "article" || state.view === "ebook";
    const box = $("pager"), art = box && box.querySelector(".prose");
    if (!readable || !box || !art) { pagerReset(); return; }
    // 宽度变了（缩放窗口 / 拉「宽」滑块 / 转屏）时栏数可能也该变：就地纠正。
    // 刻意**不回调 setCols** —— 那会绕回这里，成环。
    const wantCols = effectiveCols(parseInt(getComputedStyle(document.documentElement).getPropertyValue("--cols")) || 1);
    if (wantCols !== +art.dataset.cols)
      document.querySelectorAll(".prose").forEach((p) => { p.dataset.cols = String(wantCols); });
    // 用户把"翻页"关掉时就是普通滚动（右下角那个 ⇄ 按钮）
    if (!state.paged) { pagerReset(); return; }
    box.classList.remove("paged"); art.style.height = ""; art.style.transform = "";
    const natural = art.getBoundingClientRect().height || 0;
    // 兜底：内容太长就不分页。分栏的排版代价随长度增长，硬做只会把页面卡死；
    // 宁可变回普通滚动，也不能让"读书"变成"等浏览器"。（正常路径上电子书已按节切开，
    // 走不到这里；这是防"某本书没有标题、也没被切开"的保险丝。）
    if (natural > PAGER_MAX_HEIGHT)
    {
      pagerReset();
      pagerFallbackToSingle("这篇太长，翻页已自动关闭（改成滚动）");
      return;
    }
    box.classList.add("paged");
    const css = getComputedStyle(art);
    const cols = parseInt(css.columnCount) || 1;
    const gap = parseFloat(css.columnGap) || 0;
    const W = box.clientWidth || 1;
    const top = Math.max(0, art.getBoundingClientRect().top);
    const h = Math.max(300, Math.round(window.innerHeight - top - 92));
    art.style.height = h + "px";
    // 把页高告诉 CSS：正文里的图据此限高，免得一张封面高过一页、溢出到翻不到的地方
    document.documentElement.style.setProperty("--page-h", h + "px");

    // ── 单栏：纵向翻页 ──
    // 按**段落边界**分页，不按像素硬切 —— 硬切会把一行字切成两半（上半页看得见、下半页看不见），
    // 那比滚动更难受。offsets[i] = 第 i 页从正文的哪个 y 开始。
    if (cols < 2) {
      const offsets = [0];
      let pageTop = 0;
      for (const k of art.children) {
        const t = k.offsetTop || 0;
        if (t - pageTop >= h) { offsets.push(t); pageTop = t; }
      }
      if (offsets.length < 2) { pagerReset(); return; }   // 一页就装完了 → 不必翻页
      if (offsets.length > 5000) { pagerReset(); pagerFallbackToSingle("这篇太长，翻页已自动关闭（改成滚动）"); return; }
      pager.on = true; pager.art = art; pager.axis = "y";
      pager.offsets = offsets; pager.step = h; pager.pages = offsets.length;
      if (pager.page > pager.pages - 1) pager.page = pager.pages - 1;
      pagerApply();
      return;
    }

    // ── 多栏：横向翻页 ──
    const step = W + gap;
    const byHeight = Math.ceil(natural / Math.max(1, h));
    const byScroll = Math.round((art.scrollWidth + gap) / step);
    const pages = Math.max(1, byHeight, byScroll);
    if (pages < 2 || pages > 5000)
    {
      pagerReset();
      if (pages > 5000) pagerFallbackToSingle("这篇太长，翻页已自动关闭（改成滚动）");
      return;
    }
    pager.on = true; pager.art = art; pager.axis = "x"; pager.offsets = null;
    pager.step = step; pager.pages = pages;
    if (pager.page > pages - 1) pager.page = pages - 1;
    pagerApply();
  } catch (e) { pagerReset(); }
}

/* ── 把超长正文切成"节" ──
   电子书动辄几十万字。分栏分页的代价与文档长度成正比（要把整段一次排版出来），
   所以**不能**把整本书塞进一个 .prose 里做分栏 —— 那是"页面无响应"的直接原因。
   这里按标题（h1~h3）或每 ~12000 字切一刀，只对当前这一节分页：
   排版量有界、翻页灵敏，读起来也更像一本书（还能按节跳）。
   返回 [{title, html}]。 */
function splitBookSections(html, maxChars = 12000) {
  const raw = html || "";
  const tmp = document.createElement("div");
  tmp.innerHTML = raw;                       // 只用来**读结构**，不插入文档（服务端已净化）
  const nodes = Array.from(tmp.childNodes || []);
  if (nodes.length < 2) return [{ title: "", html: raw }];
  return sectionsFromNodes(nodes, raw, maxChars);
}

/** 切分本体（与 DOM 解耦，方便单独验证"一本 30 万字的书会被切成多少节"）。 */
function sectionsFromNodes(nodes, raw, maxChars = 12000) {
  const sections = [];
  let parts = [], chars = 0, title = "";
  const flush = () => {
    if (!parts.length) return;
    sections.push({ title: title || `第 ${sections.length + 1} 节`, html: parts.join("") });
    parts = []; chars = 0; title = "";
  };
  for (const node of nodes) {
    const el = node.nodeType === 1 ? node : null;
    const tag = el ? (el.tagName || "").toLowerCase() : "";
    const isHeading = /^h[1-3]$/.test(tag);
    const text = (node.textContent || "").replace(/\s+/g, "");
    // 标题即天然分界（攒过一点内容才切，免得目录页把每行标题都切成一节）
    if (isHeading && chars > 800) flush();
    if (chars > maxChars) flush();
    parts.push(el ? el.outerHTML : (node.nodeValue ? esc(node.nodeValue) : ""));
    chars += text.length;
    if (isHeading && !title) title = (node.textContent || "").trim().slice(0, 60);
  }
  flush();
  return sections.length ? sections : [{ title: "", html: raw }];
}

/* ───────── 阅读状态（"上次读到哪"，本浏览器记住） ─────────
   为什么要单独存一份：翻页模式下**没有滚动**，而服务端那份 reading_progress.json 记的是
   滚动像素（TUI 也在用）—— 翻页时它恒为 0，等于没记。所以这里按 itemId 记住
   { s: 节号, p: 页码, y: 滚动位置 }，下次打开自动回到原处。只存在本机浏览器里。 */
const READ_STATE_KEY = "sip-web-read-state-v1";
let _readStateTimer = 0;

function loadReadStates() {
  try { return JSON.parse(localStorage.getItem(READ_STATE_KEY) || "{}"); } catch { return {}; }
}
function readStateOf(itemId) {
  if (!itemId) return null;
  return loadReadStates()[String(itemId)] || null;
}
function saveReadState(itemId, patch) {
  if (!itemId) return;
  try {
    const all = loadReadStates();
    all[String(itemId)] = { ...(all[String(itemId)] || {}), ...patch, t: Date.now() };
    const keys = Object.keys(all);
    if (keys.length > 500)   // 只留最近 500 篇，别让 localStorage 无限长
      keys.sort((a, b) => (all[a].t || 0) - (all[b].t || 0)).slice(0, keys.length - 500)
        .forEach((k) => { delete all[k]; });
    localStorage.setItem(READ_STATE_KEY, JSON.stringify(all));
  } catch { /* 存不下就算了，不该影响阅读 */ }
}

/** 记"现在读到哪"：翻页记页码（电子书另记节号），滚动记像素。防抖，别每翻一页就写一次。 */
function rememberReadState() {
  const id = state.view === "ebook" ? state.ebookId : (state.view === "article" ? state.articleId : 0);
  if (!id) return;
  const patch = {};
  if (pager.on) patch.p = pager.page;
  else patch.y = Math.round($("content")?.scrollTop || 0);
  if (state.view === "ebook" && state.ebookText?.sections?.length > 1) patch.s = state.ebookText.section || 0;
  clearTimeout(_readStateTimer);
  _readStateTimer = setTimeout(() => saveReadState(id, patch), 400);
}

/* ── 搜索高亮 ──
   ⚠️ 必须用 DOM API 造 <mark>，**不能**用 innerHTML ——
   把**文本**当标记重新解析，正文里只要字面写着 <img src=x onerror=…> 就会执行。 */
function highlightIn(root, kw) {
  if (!kw) return 0;
  const re = new RegExp(kw.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi");
  let n = 0;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
  const nodes = []; while (walker.nextNode()) nodes.push(walker.currentNode);
  for (const node of nodes) {
    const text = node.nodeValue;
    if (!text) continue;
    re.lastIndex = 0;
    if (!re.test(text)) continue;
    re.lastIndex = 0;
    const frag = document.createDocumentFragment();
    let last = 0, m;
    while ((m = re.exec(text)) !== null) {
      if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
      const mk = document.createElement("mark");
      mk.className = "hit";
      mk.textContent = m[0];
      frag.appendChild(mk);
      last = m.index + m[0].length;
      n++;
      if (m[0].length === 0) re.lastIndex++;
    }
    if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
    node.parentNode.replaceChild(frag, node);
  }
  return n;
}

let hitIdx = 0;
function scrollToHit() {
  const marks = document.querySelectorAll("mark.hit");
  if (!marks.length) return;
  hitIdx = 0;
  marks[hitIdx].scrollIntoView({ behavior: "smooth", block: "center" });
}
function jumpNextHit() {
  const marks = document.querySelectorAll("mark.hit");
  if (!marks.length) { toast("无命中"); return; }
  hitIdx = (hitIdx + 1) % marks.length;
  marks[hitIdx].scrollIntoView({ behavior: "smooth", block: "center" });
  toast(`命中 ${hitIdx + 1} / ${marks.length}`);
}

/* ───────── 阅读位置（与 TUI 共用 reading_progress.json） ───────── */
let _rpTimer = 0;
let _rpMap = {};
async function loadReadingProgress() {
  try { _rpMap = (await api("/api/reading-progress")).positions || {}; } catch { _rpMap = {}; }
}
function saveReadingPosition(itemId, position) {
  if (!itemId) return;
  _rpMap[String(itemId)] = position;
  clearTimeout(_rpTimer);
  _rpTimer = setTimeout(() => {
    api("/api/reading-progress", { method: "POST", body: JSON.stringify({ itemId, position }) }).catch(() => { });
  }, 700);
}

/* ───────── 命令面板：只读白名单（服务端同样只认这一套） ───────── */
const PALETTE_ITEMS = [
  { line: "status", desc: "版本 / 数据目录 / 挡位" },
  { line: "list", desc: "全部订阅源与篇数" },
  { line: "today", desc: "今日哈汤清单" },
  { line: "edits", desc: "有改稿历史的文章" },
  { line: "dedup", desc: "跨源重复簇" },
  { line: "hidden", desc: "已隐藏的去重项" },
  { line: "policy", desc: "源规则" },
  { line: "simon", desc: "孟思琳挡位与最近事件" },
  { line: "telemetry", desc: "遥测状态" },
  { line: "index", desc: "向量索引状态" },
  { line: "config", desc: "配置总览" },
  { line: "grep ", desc: "全文搜索：grep 关键词" },
  { line: "show ", desc: "看某篇正文：show 12" },
  { line: "versions ", desc: "版本历史：versions 12" },
];

function openPalette() {
  const p = $("pPalette"); if (!p) return;
  p.hidden = false;
  const inp = $("paletteInput");
  if (inp) { inp.value = ""; inp.focus(); }
  $("paletteOut").hidden = true;
  filterPalette("");
}
function closePalette() { const p = $("pPalette"); if (p) p.hidden = true; }
function filterPalette(q) {
  const box = $("paletteList"); if (!box) return;
  const needle = (q || "").trim().toLowerCase();
  PALETTE.items = PALETTE_ITEMS.filter((it) => !needle || it.line.toLowerCase().includes(needle) || it.desc.includes(needle));
  box.innerHTML = PALETTE.items.map((it, i) => `
    <button class="palette-item ${i === 0 ? "on" : ""}" data-act="palette-run" data-line="${esc(it.line)}">
      <span class="mono">${esc(it.line)}</span><span>${esc(it.desc)}</span>
    </button>`).join("") || `<div class="empty">没有匹配的命令</div>`;
}
async function runPaletteLine(line) {
  const typed = (line || $("paletteInput")?.value || "").trim();
  if (!typed) return;
  const out = $("paletteOut");
  out.hidden = false;
  out.textContent = "执行中…";
  try {
    const d = await api("/api/command", { method: "POST", body: JSON.stringify({ line: typed }) });
    out.textContent = (d.lines || []).join("\n") || "（没有输出）";
  } catch (e) {
    out.textContent = (e.hint ? e.hint + "\n\n" : "") + (e.message || "命令失败");
  }
}

/* ───────── 渲染 ───────── */

/* ── 阅读页的细顶栏 + 右侧抽屉 ──
   正文上面只留一行（⋯ 标题 · 位置），动作/元信息/节号全进抽屉：
   信息一条没少，但不再一行行往下挤正文。抽屉展开状态记在偏好里。 */
function readBarHtml(title, bits, open) {
  // open 由调用方给（文章视图里"改稿历史"会把抽屉撑开，那时 metaOpen 还是 false）；
  // 不给就按偏好算。图标要跟着**实际**开合状态走，否则面板开着、按钮还显示 ⋯。
  const isOpen = open === undefined ? !!state.metaOpen : !!open;
  return `<div class="rd-bar">
    <button class="ib" data-act="meta-toggle" title="${isOpen ? "收起详情与操作" : "详情与操作"}">${isOpen ? "✕" : "⋯"}</button>
    <span class="rd-title" title="${esc(title)}">${esc(title)}</span>
    ${bits || ""}
  </div>`;
}

function showReadDrawer(html, forceOpen) {
  const d = $("rdDrawer"), s = $("rdScrim");
  if (!d) return;
  const open = !!state.metaOpen || !!forceOpen;
  d.innerHTML = html;
  d.hidden = !open;
  if (s) s.hidden = !open;
}

function closeReadDrawer() {
  const d = $("rdDrawer"), s = $("rdScrim");
  if (d) d.hidden = true;
  if (s) s.hidden = true;
}

/** 抽屉现在是不是开着 —— 两种开法都算：用户展开的（metaOpen）、面板自己撑开的（改稿历史）。 */
function readDrawerOpen() {
  const d = $("rdDrawer");
  return !!(state.metaOpen || state.versions || (d && !d.hidden));
}

/** 真正把抽屉关上：**两种开法的理由一起清掉**。
    只置 metaOpen=false 是不够的 —— 面板那条 forceOpen 的理由（state.versions）还在，
    下一次 render 立刻又把它开回来，用户看到的就是"Esc 关不掉 / 收起没反应"。 */
function closeReadPanel() {
  state.metaOpen = false;
  state.versions = null;          // 「改稿历史」撑开的那条理由
  savePrefs({ metaOpen: false });
  render();
}

function setMetaOpen(on) {
  state.metaOpen = !!on;
  savePrefs({ metaOpen: state.metaOpen });
  render();
}

function notWired(heading, what, cli, note) {
  return `<h2 class="h2">${esc(heading)}</h2>
    <div class="note"><strong>这一页还没接入 Web 版。</strong>${esc(what)}
      ${cli ? `<div style="margin-top:8px">命令行可用：<span class="mono">${esc(cli)}</span></div>` : ""}
      ${note ? `<div style="margin-top:8px">${esc(note)}</div>` : ""}
    </div>`;
}

function syncChrome() {
  const isRead = state.view === "article" || state.view === "ebook";
  $("rtBar").hidden = !isRead;
  const pg = $("rtPager"); if (pg) pg.classList.toggle("on", !!state.paged);
  // 全屏阅读只在阅读类视图里成立；离开阅读就自动退出，免得回到列表时没有导航
  if (!isRead && state.immersive) setImmersive(false);
  // 悬浮球只在阅读页出现；顺带把"换书 = 换会话"的作废逻辑跑一遍
  aiSyncOrb();
}

/* ── 全屏阅读（沉浸）模式 ──
   侧栏、顶栏、进度条一起让位，把窗口交给正文。实现的全部就是 body 上的一个 class ——
   **不重渲染**，所以切换是瞬时的；退出用 Esc、顶栏或右下角那个 ⛶。
   刻意不做成浏览器的 F11 全屏：那是整个窗口的事，而这里要的是"应用内少点东西"。 */
function setImmersive(on) {
  state.immersive = !!on;
  document.body.classList.toggle("immersive", state.immersive);
  for (const id of ["btnFull", "rtFull"]) {
    const b = $(id); if (!b) continue;
    b.classList.toggle("on", state.immersive);
    b.textContent = state.immersive ? "⤡" : "⛶";
    b.title = state.immersive ? "退出全屏阅读（Esc）" : "全屏阅读";
  }
  // 宽度变了 → 重新分页（保位：进/退全屏不该把读到的地方弄丢）
  if (state.view === "article" || state.view === "ebook") repaginateKeepingPlace();
}

function navTo(view) {
  state.view = view;
  state.feedId = null; state.articleId = null; state.articleVersion = null; state.versions = null;
  state.editsId = null; state.dedupRep = null; state.dedupPair = null; state.jumpKw = "";
  document.querySelectorAll("#nav button").forEach((x) => x.classList.toggle("on", x.dataset.v === view));
  $("side").classList.remove("open");
  render();
}

async function render() {
  const v = $("view"); const title = $("title");
  if (!v) return;
  const V = state.view;
  syncChrome();
  // 「刚进这一篇」还是「同一篇重渲染」：前者才该套用**存下来的**阅读位置；
  // 后者要用锚点保位（存下来的位置是 400ms 前的，重渲染时套用会把画面往回拽 ——
  // 平板上尤其明显：一翻页就"跳回上一页"）。
  const entryKey = V + ":" + (V === "article" ? (state.articleId ?? "") : V === "ebook" ? (state.ebookId ?? "") : "");
  const freshEntry = state._entryKey !== entryKey;
  state._entryKey = entryKey;
  // 离开"可翻页"的视图就彻底关掉分页状态：否则 pager.on 会残留为 true，
  // 新页面上的滚轮事件还会被它接管（表现为"这一页滚不动"）。
  if (V !== "article" && V !== "ebook") { pagerReset(); closeReadDrawer(); }
  $("nToday").textContent = TODAY.length || "";
  $("nFeeds").textContent = FEEDS.filter((f) => !f.isImport).length || "";
  $("nLikes").textContent = LIKES.length || "";
  $("nImp").textContent = IMPORTS.length || "";
  $("nImp").style.display = IMPORTS.length ? "" : "none";
  // 阅读类视图才用「阅读宽度」；其余视图走固定的应用宽度（见 index.html 的 .wrap 注释）
  const reading = V === "article" || V === "ebook";
  v.className = "wrap" + (reading ? " narrow" : "");
  $("rtBar").querySelectorAll('.colbtn[data-cols="3"]').forEach((b) => {
    b.style.display = V === "ebook" ? "none" : "";   // 电子书最多两栏（窄屏由 CSS 压成一栏）
  });

  /* ── 今日哈汤 ── */
  if (V === "today") {
    title.textContent = "今日哈汤";
    const tm = TODAY_META;
    const prog = tm.tracking
      ? `目标 ${tm.target} 篇 · 已读 ${tm.done}`
      : `目标 ${tm.target} 篇 · 进度未跟踪（遥测默认关闭）`;
    const dg = tm.digest;
    v.innerHTML = `
      <p class="kicker">TODAY · ${new Date().toISOString().slice(0, 10)}${tm.generatedAt ? " · 生成于 " + esc(tm.generatedAt) : ""}</p>
      <h2 class="h2">今日哈汤</h2>
      <p class="sub">${esc(prog)} · 规则式选文，读完就关</p>
      <div class="grid3" style="margin-bottom:16px">
        <div class="stat"><b>${TODAY.length}</b><span>今日清单</span></div>
        <div class="stat"><b>${tm.tracking ? tm.done : "—"}</b><span>已读</span></div>
        <div class="stat"><b>${tm.target}</b><span>目标</span></div>
      </div>
      <div class="cards">${TODAY.length ? TODAY.map((a) => `
        <button class="card" data-article="${a.id}">
          <div class="t">${esc(a.t)}</div>
          <div class="m"><span>${esc(a.src)}</span><span class="b">${esc(a.reason)}</span><span>≈${a.min} 分</span></div>
        </button>`).join("") : `<div class="empty">今天还没有值得读的 —— 去「订阅源」更新一下</div>`}</div>
      <div class="row">
        <button class="btn" data-act="today-refresh">重新熬一碗</button>
        <button class="btn" data-act="today-digest">${dg ? "重算今日变化" : "看今日变化"}</button>
        <button class="btn" data-act="today-mark">已读说明</button>
        <button class="btn" data-act="nav" data-v="edits">改稿追踪</button>
      </div>
      ${dg ? renderDigest(dg) : `<div class="note">「今日变化」要扫一遍 48 小时窗口找重复簇，默认不自动算 —— 点上面的按钮按需生成。</div>`}`;
  }

  /* ── 订阅源 ── */
  else if (V === "feeds") {
    title.textContent = "全部订阅";
    const real = FEEDS.filter((f) => !f.isImport);
    v.innerHTML = `
      <p class="kicker">FEEDS</p><h2 class="h2">全部订阅</h2>
      <p class="sub">${real.length} 个源 · 更新计划 / 归档 / 删除 · 点进源看文章</p>
      <div class="cards">${real.length ? real.map((f) => `
        <button class="card" data-feed="${f.id}">
          <div class="t">${esc(f.title)}</div>
          <div class="m">
            <span class="b">${f.active} 在读</span>
            ${f.archived ? `<span class="b">${f.archived} 改稿</span>` : ""}
            <span class="b">${esc(f.schedule || "manual")}</span>
            ${f.last ? `<span>${esc(fmtDate(f.last))}</span>` : `<span>从未更新</span>`}
          </div>
        </button>`).join("") : `<div class="empty">还没有订阅源 —— 点左下角「＋ 订阅」加一个</div>`}</div>
      <div class="note">本地导入的文件在「本地导入」那一页，不算订阅源。</div>`;
  }

  /* ── 单个源 ── */
  else if (V === "feed") {
    const f = FEEDS.find((x) => x.id === state.feedId) || FEEDS[0];
    if (!f) { v.innerHTML = `<div class="empty">这个源不见了（可能已被删除）</div>`; return; }
    title.textContent = f.title;
    v.innerHTML = `<div class="empty">加载文章…</div>`;
    await loadFeedArticles(f.id);
    const list = ARTICLES[f.id] || [];
    const preset = ["", "30m", "1h", "6h", "12h", "daily@10:00", "weekly@Mon 08:00", "__custom"];
    const cur = (f.schedule || "").toLowerCase();
    v.innerHTML = `
      <p class="kicker">ARTICLES</p>
      <h2 class="h2">${esc(f.title)}</h2>
      <p class="sub mono">${esc(f.url)}</p>
      <div class="row">
        <button class="btn pri" data-update="${f.id}" data-act="feed-update" data-id="${f.id}">更新</button>
        <button class="btn" data-act="feed-archive" data-id="${f.id}">归档</button>
        <button class="btn" data-act="feed-unarchive" data-id="${f.id}">去归档</button>
        <button class="btn" data-act="feed-info" data-id="${f.id}">来源信息</button>
        <button class="btn dan" data-act="feed-delete" data-id="${f.id}">删除</button>
      </div>
      <div class="card" style="cursor:default">
        <div class="t">更新计划</div>
        <div class="row" style="align-items:center">
          <select data-sched="${f.id}" style="padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)">
            ${preset.map((p) => `<option value="${esc(p)}" ${p === cur ? "selected" : ""}>${p === "" ? "manual（只手动）" : p === "__custom" ? "自定义…" : esc(p)}</option>`).join("")}
          </select>
          <input data-sched-custom="${f.id}" placeholder="自定义：30m / 1h / daily@10:00 / weekly@Mon 08:00" style="flex:1;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)" />
          <button class="btn pri" data-act="feed-schedule" data-id="${f.id}">保存计划</button>
        </div>
        <div class="m">与终端 <span class="mono">sip --schedule ${f.num || f.id} 30m</span> 同一份实现；选 manual = 只手动更新。</div>
      </div>
      <div class="cards" style="margin-top:12px">${list.map((a) => `
        <button class="card" data-article="${a.id}">
          <div class="t">${a.liked ? "♥ " : ""}${a.ai ? "🤖 " : ""}${esc(a.t)}</div>
          <div class="m"><span class="b">${esc(a.q)}</span>${a.hist ? `<span class="b acc">有改稿</span>` : ""}${a.pub ? `<span>${esc(fmtDate(a.pub))}</span>` : ""}</div>
        </button>`).join("") || `<div class="empty">这个源还没有文章</div>`}</div>`;
  }

  /* ── 文章阅读 ── */
  else if (V === "article") {
    const id = state.articleId;
    title.textContent = "阅读 · " + id;
    v.innerHTML = `<div class="empty">加载文章…</div>`;
    let real = null;
    const vq = state.articleVersion ? `?version=${state.articleVersion}` : "";
    try { real = await api(`/api/articles/${id}${vq}`); } catch (e) { real = null; }
    if (!real) { v.innerHTML = `<div class="empty">这篇文章读不到（可能已被删除，或接口不可用）</div>`; return; }
    // 记下标题：AI 面板要拿它当标题（articleId -> 标题的映射这里最省事，
    // 见 aiTitleOf 的注释）。**不要**在这里顺手算"有没有正文"——那件事的判据归服务端，
    // 前端猜一份必然漂移（踩过：猜的字段名跟 bodyHtml 对不上，把好文章判成没正文）。
    state.articleTitle = real.title || "";

    // 本地导入的文件**一律交给阅读器**：那边按"节"分页（整本书一次排版会把浏览器卡死）、
    // 图片也改写好路径。文章视图对整本书分不了页（只能退回滚动，于是"左栏读到底、
    // 右栏还在下面"），早期版本还会把 file:// 的图整个丢掉 —— 同一个东西不该有两套体验。
    if (real.imported) { await openEbook(id); return; }

    const liked = !!real.liked || LIKES.some((x) => x.id === id);
    const isOld = !!state.articleVersion && state.articleVersion !== real.version;
    const jk = state.jumpKw || "";
    const rp = _rpMap[String(id)] || 0;
    // 重渲染会换掉整棵 DOM，锚点元素随之消失 —— 用"读到几成"把位置接力过去。
    // 没有它，读一半点个 ♥ 或「生成摘要」就会被打回第一页。
    const keepRatio = (pager.on && pager.pages > 1) ? pager.page / (pager.pages - 1) : 0;
    const keepAnchorIdx = (() => {
      const a = pagerAnchor();
      if (!a || !pager.art) return -1;
      return Array.prototype.indexOf.call(pager.art.children, a);
    })();

    v.innerHTML = `
      ${isOld ? `<div class="jump-bar" style="background:rgba(185,28,28,.12);border-color:rgba(185,28,28,.25);color:var(--bad)">
        <span>正在看历史版本</span><strong class="mono">v${esc(real.version)}</strong>
        <button class="btn" data-act="back-latest" data-id="${id}">回到最新</button>
      </div>` : ""}
      ${jk ? `<div class="jump-bar">
        <span>已定位关键词</span><strong class="mono">${esc(jk)}</strong>
        <button class="btn" data-act="jump-next-hit">下一个命中</button>
        <button class="btn" data-act="clear-hits">清除高亮</button>
      </div>` : ""}
      ${readBarHtml(real.title || "", `<span class="b">${esc(real.quality || "")}</span>
        <span class="b">v${esc(real.version || 1)}${real.versionCount > 1 ? "/" + esc(real.versionCount) : ""}</span>
        ${real.hasFulltext ? `<span class="b acc">全文</span>` : ""}
        <button class="btn ${liked ? "pri" : ""}" data-act="like" data-id="${id}" title="收藏">♥</button>
        ${real.link ? `<button class="btn" data-act="open-external" data-link="${esc(real.link)}" title="打开原文">↗</button>` : ""}`,
        !!state.metaOpen || !!state.versions)}
      <div class="pager" id="pager">
        <article class="prose" data-cols="${esc(effectiveCols(+getComputedStyle(document.documentElement).getPropertyValue("--cols").trim() || 1))}">${real.bodyHtml || "<p>（这篇没有正文）</p>"}</article>
        <div class="pager-bar" id="pagerBar" hidden>
          <button class="btn" data-page="-1" title="上一页">‹</button>
          <span class="n" id="pagerNum">1 / 1</span>
          <button class="btn" data-page="1" title="下一页">›</button>
          <span class="hint">← → 翻页</span>
        </div>
      </div>
      ${rp > 0 && !isOld ? `<div class="row"><button class="btn" data-act="restore-pos" data-pos="${rp}">跳回上次位置（${rp}px）</button></div>` : ""}`;

    // 动作、元信息、摘要、来源都进右侧抽屉（默认收起）—— 正文上面只留一行。
    // 改稿历史也在抽屉里：它是一张长表格，摆在正文上面同样是"挤压"。
    showReadDrawer(`
      <p class="kicker">${esc(real.feed || "")}</p>
      <h3>${esc(real.title || "")}</h3>
      <div class="meta">
        ${real.published ? `<span>${esc(fmtDate(real.published))}</span>` : ""}
        ${real.author ? `<span>${esc(real.author)}</span>` : ""}
        <span class="b">${esc(real.quality || "")}</span>
        <span class="b">v${esc(real.version || 1)}${real.versionCount > 1 ? "/" + esc(real.versionCount) : ""}</span>
        ${real.status && real.status !== "active" ? `<span class="b bad">${esc(real.status)}</span>` : ""}
        ${real.hasFulltext ? `<span class="b acc">已抓全文</span>` : ""}
        ${real.pageCount ? `<span class="b">${esc(real.pageCount)} 页</span>` : ""}
      </div>
      <div class="row">
        <button class="btn ${liked ? "pri" : ""}" data-act="like" data-id="${id}">♥ 收藏</button>
        <button class="btn" data-act="fulltext" data-id="${id}">抓全文</button>
        <button class="btn" data-act="summary" data-id="${id}">生成摘要</button>
        ${real.link ? `<button class="btn" data-act="open-external" data-link="${esc(real.link)}">原文</button>` : ""}
        <a class="btn" href="/api/articles/${id}/export" download>导出 MD</a>
        ${real.hasHistory ? `<button class="btn" data-act="versions" data-id="${id}">改稿历史</button>` : ""}
        ${real.feedId ? `<button class="btn" data-act="nav-feed" data-id="${real.feedId}">去这个源</button>` : ""}
      </div>
      ${real.summary ? `<div class="note">${esc(real.summary)}</div>` : `<div class="m">还没有摘要 —— 上面点「生成摘要」。</div>`}
      ${state.versions ? renderVersionPanel(state.versions, id) : ""}
      <div class="rule"></div>
      <div class="m">阅读宽度 / 字号 / 栏数在右下角；<span class="mono">Esc</span> 关掉本面板，<span class="mono">F</span> 切换全屏阅读。</div>`,
      !!state.versions);   // 主动点"改稿历史"时直接把抽屉打开

    setCols(+(getComputedStyle(document.documentElement).getPropertyValue("--cols").trim() || 1));
    // 位置接力（同一次会话内重渲染）：优先"同一段"，量不到再按比例
    const rs = readStateOf(id);
    if (keepRatio > 0 || keepAnchorIdx >= 0) {
      const kids = pager.art ? pager.art.children : null;
      const anchor = (kids && keepAnchorIdx >= 0 && keepAnchorIdx < kids.length) ? kids[keepAnchorIdx] : null;
      pagerRestoreTo(anchor, keepRatio);
    } else if (freshEntry && rs && typeof rs.p === "number" && pager.on) {
      // 上次是翻页读的 → 回到那一页
      pager.page = Math.min(pager.pages - 1, Math.max(0, rs.p));
      pagerApply();
    } else if (freshEntry && rs && typeof rs.y === "number" && rs.y > 0 && !pager.on) {
      // 上次是滚动读的 → 回到那个位置（服务端那份 rp 也还在，作为兜底）
      $("content").scrollTop = rs.y;
    }
    clearTimeout(window._pagerImgT);
    window._pagerImgT = setTimeout(() => { if (state.view === "article") pagerMeasureSoon(); }, 500);
    v.querySelectorAll(".pager img").forEach((im) => im.addEventListener("load", () => {
      if (state.view === "article") pagerMeasureSoon();
    }, { once: true }));
    // bodyHtml 已由**服务端**净化，这里直接注入 —— 前端不再判断 isHtml，也不再自己转义。
    if (jk) {
      const n = highlightIn(v.querySelector(".prose"), jk);
      toast(n ? `高亮 ${n} 处，已滚到第一处` : "正文里没扫到关键词");
      if (n) setTimeout(scrollToHit, 80);
    }
    state.jumpKw = "";
  }

  /* ── 收藏 ── */
  else if (V === "likes") {
    title.textContent = "收藏";
    v.innerHTML = `<p class="kicker">LIKES</p><h2 class="h2">收藏</h2><p class="sub">♥ 你标的 · 🤖 AI 标的</p>
      ${LIKES.length ? `<div class="cards">${LIKES.map((a) => `<button class="card" data-article="${a.id}">
        <div class="t">${a.ai ? "🤖 " : "♥ "}${esc(a.t)}</div>
        <div class="m"><span>${esc(a.feed)}</span>${a.ai ? `<span class="b acc">AI 标记</span>` : ""}</div></button>`).join("")}</div>`
        : `<div class="empty">还没有收藏 —— 读文章时点「♥ 收藏」</div>`}`;
  }

  /* ── 本地导入 ── */
  else if (V === "imported") {
    title.textContent = "本地导入";
    v.innerHTML = `
      <p class="kicker">IMPORT</p><h2 class="h2">本地导入</h2>
      <p class="sub">txt / md / pdf / epub / mobi / docx · 文件会<strong>复制进</strong>数据目录，原文件你随时可以删</p>
      <div class="drop" id="impDrop">
        <div>把文件拖到这里，或</div>
        <div class="row" style="justify-content:center">
          <label class="btn pri" style="cursor:pointer">选择文件
            <input type="file" id="impFile" multiple accept=".txt,.md,.markdown,.pdf,.epub,.mobi,.docx" hidden />
          </label>
        </div>
        <div class="m" style="justify-content:center">PDF 在网页里按页栅格化阅读；EPUB/DOCX/MOBI 抽出正文（含图片）</div>
      </div>
      <p class="sub" id="impMsg" hidden style="margin-top:10px"></p>
      <div class="cards">${IMPORTS.length ? IMPORTS.map((a) => `
        <div class="card" style="cursor:default">
          <div class="t">${esc(a.title)}</div>
          <div class="m">
            <span class="b">${esc((a.type || "?").toUpperCase())}</span>
            ${a.pages ? `<span class="b">${a.pages} 页</span>` : ""}
            <span class="b">${esc(fmtSize(a.size))}</span>
            <span>${a.chars} 字</span>
            <span>${esc(fmtDate(a.importedAt))}</span>
          </div>
          <div class="row" style="margin-bottom:0">
            <button class="btn pri" data-act="ebook-open" data-id="${a.itemId}">阅读</button>
            ${a.isPdf ? `<button class="btn" data-act="file-open" data-id="${a.itemId}">浏览器打开（原生阅读器）</button>` : ""}
            <button class="btn" data-act="file-link" data-id="${a.itemId}">临时链接</button>
            <button class="btn dan" data-act="import-del" data-id="${a.itemId}">删除</button>
          </div>
        </div>`).join("") : `<div class="empty">还没有导入任何文件</div>`}</div>
      <div class="note">命令行等价物：<span class="mono">sip --import &lt;file&gt;</span> · 删除 <span class="mono">sip --import-rm &lt;编号&gt;</span>。两边是同一套实现。</div>`;
    bindImportDrop();
  }

  /* ── 电子书阅读 ── */
  else if (V === "ebook") {
    const meta = state.ebookMeta || {};
    title.textContent = "阅读 · " + (meta.title || state.ebookId);
    if (!state.ebookPages && !meta.isPdf && !meta.type) {
      try { state.ebookMeta = await api(`/api/imports/${state.ebookId}`); state.ebookPages = state.ebookMeta.pages || null; } catch { }
    }
    const m = state.ebookMeta || {};
    // 导入项现在只在阅读器里打开（见文章视图那条转移），所以原本在文章视图能做的事
    // —— 收藏、导出 —— 在这里也得有，否则换个入口就少两个功能。
    const bookLiked = LIKES.some((x) => x.id === state.ebookId);
    const bookActions = `
          <button class="btn ${bookLiked ? "pri" : ""}" data-act="like" data-id="${state.ebookId}">♥ 收藏</button>
          <a class="btn" href="/api/articles/${state.ebookId}/export" download>导出 MD</a>`;
    if (m.isPdf) {
      const pages = state.ebookPages || 0;
      const cur = Math.min(Math.max(1, state.ebookPage), Math.max(1, pages));
      v.innerHTML = `
        ${readBarHtml(m.title || "", `<span class="b">PDF</span><span class="b acc">第 ${cur} / ${pages || "?"} 页</span>`)}
        <div class="card diffbox" style="cursor:default;text-align:center;padding:8px">
          <img src="/api/imports/${state.ebookId}/page/${cur}" alt="第 ${cur} 页" style="max-width:100%;border-radius:6px" />
        </div>
        <div class="row" style="justify-content:space-between">
          <button class="btn" ${cur <= 1 ? "disabled" : ""} data-act="ebook-page" data-page="${cur - 1}">← 上一页</button>
          <div class="row" style="margin:0;align-items:center">
            <button class="btn" data-act="ebook-page" data-page="1">首页</button>
            <input type="number" min="1" max="${pages || 1}" value="${cur}" data-act="ebook-goto"
                   style="width:72px;padding:6px;border-radius:8px;border:1px solid var(--line);background:var(--elev)" />
            <button class="btn" data-act="ebook-page" data-page="${pages}">末页</button>
          </div>
          <button class="btn pri" ${cur >= pages ? "disabled" : ""} data-act="ebook-page" data-page="${cur + 1}">下一页 →</button>
        </div>`;
      showReadDrawer(`
        <p class="kicker">EBOOK · 本地导入</p>
        <h3>${esc(m.title || "")}</h3>
        <div class="meta"><span class="b">PDF</span><span class="b">${esc(fmtSize(m.size))}</span>
          ${pages ? `<span class="b">${pages} 页</span>` : ""}</div>
        <div class="row">
          <button class="btn pri" data-act="file-open" data-id="${state.ebookId}">用浏览器打开（原生阅读器）</button>
          <button class="btn" data-act="file-link" data-id="${state.ebookId}">复制临时链接</button>
          ${bookActions}
          <button class="btn" data-act="nav" data-v="imported">← 书库</button>
        </div>
        <div class="note">PDF 没有文本层，所以网页只能**按页栅格化**（150 DPI，渲染过的页缓存在 <span class="mono">readwithhotsoup/temp/</span>）——不能选字、不能搜索、也读不出正文。<br/>
          要选字/搜索/批注，用上面的「用浏览器打开」：那是把**原文件**原样递给浏览器自带的 PDF 阅读器。<br/>
          「复制临时链接」给的是<b>进程内、只对这一份、10 分钟过期</b>的令牌地址，可以贴到别的标签页、别的 PDF 程序或手机上（普通接口地址在别的浏览器里会 401）。</div>`);
    } else {
      // 正文**取一次就缓存**：这本书可能几百 KB，每次 render 都重新拉一遍 + 重新解析，
      // 会让"点一下"变成"等半天"。
      if (!state.ebookText || state.ebookText.itemId !== state.ebookId) {
        v.innerHTML = `<div class="empty">加载正文…</div>`;
        let text = null, err = null;
        try { text = await api(`/api/imports/${state.ebookId}/text`); } catch (e) { err = e; }
        if (err) { v.innerHTML = `<div class="empty">读不到：${esc(err.message || "")}</div>`; return; }
        const sections = splitBookSections(text.bodyHtml);
        // 上次读到第几节（阅读位置对文本型电子书记的是**节号**；PDF 记的是页码）
        const saved = _rpMap[String(state.ebookId)] || 0;
        state.ebookText = { ...text, sections, section: Math.min(Math.max(0, saved), sections.length - 1) };
      }
      const text = state.ebookText;
      const secs = text.sections || [{ title: "", html: text.bodyHtml }];
      const idx = Math.min(Math.max(0, text.section || 0), secs.length - 1);
      text.section = idx;
      const sec = secs[idx];

      // 电子书正文和文章正文走**同一套分栏翻页**（.pager + .prose）。
      // 但只对**当前这一节**分页 —— 整本书一次排版会把浏览器卡死（见 splitBookSections）。
      const cols = getComputedStyle(document.documentElement).getPropertyValue("--cols").trim() || "1";
      // 首帧就用**有效栏数**（按可用宽度降过栏、并按电子书 2 栏上限收过），
      // 别先排 3 栏再纠正 —— 那会闪一下
      const useCols = effectiveCols(+cols || 1);
      v.innerHTML = `
        ${readBarHtml(text.title || "", `
          <span class="b">${esc((text.type || "").toUpperCase())}</span>
          ${secs.length > 1 ? `<span class="b">第 ${idx + 1} / ${secs.length} 节</span>` : ""}
          ${useCols > 1 ? `<span class="b acc">${useCols} 栏</span>` : ""}
          <button class="btn ${bookLiked ? "pri" : ""}" data-act="like" data-id="${state.ebookId}" title="收藏">♥</button>`)}
        <div class="pager" id="pager">
          <article class="prose narrow" data-cols="${esc(useCols)}">${sec.html || "<p>（这一节没有内容）</p>"}</article>
          <div class="pager-bar" id="pagerBar" hidden>
            <button class="btn" data-page="-1" title="上一页">‹</button>
            <span class="n" id="pagerNum">1 / 1</span>
            <button class="btn" data-page="1" title="下一页">›</button>
            <span class="hint">← → 翻页</span>
          </div>
        </div>`;
      // 目录、动作、说明全进右侧抽屉 —— 正文上面只留一行（书名 + 位置）
      showReadDrawer(`
        <p class="kicker">EBOOK · 本地导入</p>
        <h3>${esc(text.title || "")}</h3>
        <div class="meta"><span class="b">${esc((text.type || "").toUpperCase())}</span>
          ${secs.length > 1 ? `<span class="b">共 ${secs.length} 节</span>` : ""}
          ${secs.length > 1 ? `<span class="b acc">正在读第 ${idx + 1} 节</span>` : ""}</div>
        <div class="row">
          <button class="btn ${bookLiked ? "pri" : ""}" data-act="like" data-id="${state.ebookId}">♥ 收藏</button>
          <a class="btn" href="/api/articles/${state.ebookId}/export" download>导出 MD</a>
          <button class="btn" data-act="file-open" data-id="${state.ebookId}">用浏览器打开原文件</button>
          <button class="btn" data-act="file-link" data-id="${state.ebookId}">复制临时链接</button>
          <button class="btn" data-act="nav" data-v="imported">← 书库</button>
        </div>
        ${secs.length > 1 ? `<div class="row" style="margin-top:4px"><strong style="font-size:13px">目录</strong></div>
        <div class="cards">${secs.map((s, i) => `<button class="card" style="padding:8px 10px" data-act="book-sec" data-idx="${i}">
          <div class="t" style="font-size:13.5px;margin:0">${i + 1}. ${esc(s.title)}${i === idx ? " ← 在读" : ""}</div>
        </button>`).join("")}</div>` : ""}
        <div class="note">分栏 / 字号 / 行距在右下角调；多栏时 ← → 按页翻，翻到头用「下一节」。<br/>
          电子书最多两栏（手机上自动一栏）——每栏太窄反而难读。</div>`);
      // 先按当前偏好把栏数应用到这一节，再量页数（顺序反了会按单栏量）。
      // 传的是**用户偏好**（而不是压过栏数的值）—— setCols 会把它存回偏好，
      // 而打开一本书不该悄悄改掉你的全局栏数；电子书的 2 栏上限在 effectiveCols 里统一处理。
      setCols(+cols || 1);
      // 从"上一节"往回翻时落在这一节的**最后一页**，读起来才连续
      const edge = state._sectionEdge; state._sectionEdge = null;
      const st = readStateOf(state.ebookId);
      if (edge === "last" && pager.on) { pager.page = pager.pages - 1; pagerApply(); }
      else if (edge !== "first" && pager.on && st && st.s === idx && typeof st.p === "number") {
        // 上次就停在这一节的第 N 页 → 接着读（换节了就别乱跳，那会让人以为没翻动）
        pager.page = Math.min(pager.pages - 1, Math.max(0, st.p));
        pagerApply();
      }
      clearTimeout(window._pagerImgT);
      window._pagerImgT = setTimeout(() => { if (state.view === "ebook") repaginateKeepingPlace(); }, 300);
      // 图片是**一张一张**加载完的：每张都全量重排一次 = 把一次卡顿拆成几十次，所以合并到下一帧
      v.querySelectorAll(".pager img").forEach((im) => im.addEventListener("load", () => {
        if (state.view === "ebook") pagerMeasureSoon();
      }, { once: true }));
    }
  }

  /* ── 搜索 ── */
  else if (V === "search") {
    title.textContent = "搜索";
    v.innerHTML = `
      <p class="kicker">SEARCH</p><h2 class="h2">搜索</h2>
      <p class="sub">全文：子串命中（FTS5，中文可搜）· 语义：向量相似度（需要先 sip --init + 索引）</p>
      <div class="seg" style="margin-bottom:10px" id="smode2">
        <button data-mode="grep" class="on">全文</button>
        <button data-mode="sem">语义</button>
      </div>
      <div class="field"><input id="gq3" placeholder="关键词…（试试 trigram）" value="" /></div>
      <div class="row" style="align-items:center;margin-top:0">
        <button class="btn pri" data-act="search-run">检索</button>
        <label id="thrRow2" hidden style="font-size:12px;color:var(--muted);display:inline-flex;align-items:center;gap:6px">
          阈值
          <input type="range" id="semThr2" min="50" max="95" step="5" value="70" style="width:90px" />
          <span class="mono" id="semThrVal2">0.70</span>
          <span style="opacity:.7">仅语义</span>
        </label>
      </div>
      <div id="searchPageHits"><div class="empty">输入关键词后检索</div></div>
      <div class="note">全文没有「相似度」，故无阈值；只有语义检索用阈值滤低分结果。</div>`;
  }

  /* ── 改稿追踪 ── */
  else if (V === "edits") {
    title.textContent = "改稿追踪";
    if (!state.editsId) {
      v.innerHTML = `<div class="empty">读取改稿历史…</div>`;
      let err = null;
      try { if (!EDITS.length) await loadEdits(); } catch (e) { err = e; }
      if (err) { v.innerHTML = `<div class="empty">读不到：${esc(err.message || "")}</div>`; return; }
      v.innerHTML = `
        <p class="kicker">DIFF · 列表</p><h2 class="h2">改稿追踪</h2>
        <p class="sub">作者改过正文的文章 · 点一条看逐行差异（版本链按源隔离）</p>
        <div class="cards">${EDITS.length ? EDITS.map((d) => `
          <button class="card" data-act="edit-open" data-id="${d.itemId}">
            <div class="t">${esc(d.title)}</div>
            <div class="m">
              <span>${esc(d.feed)}</span>
              <span class="b">v${d.from} → v${d.to}</span>
              <span class="b acc">${d.versions} 版</span>
              ${d.lastChangedAt ? `<span>${esc(fmtDate(d.lastChangedAt))}</span>` : ""}
              ${d.stillActive ? "" : `<span class="b bad">已下架</span>`}
            </div>
          </button>`).join("") : `<div class="empty">还没有被改过稿的文章 —— 等作者偷偷改一次就有了</div>`}</div>
        <div class="note">与「跨源去重」不同：这里是<strong>同一个源里作者改稿</strong>，不是不同源的转载。</div>`;
      return;
    }

    v.innerHTML = `<div class="empty">读差异…</div>`;
    const cur = EDITS.find((x) => x.itemId === state.editsId);
    let versions = [], diff = null, err = null;
    try {
      versions = await loadVersions(state.editsId);
      if (versions.length >= 2) diff = await loadDiff(state.editsId, state.editFrom, state.editTo);
    } catch (e) { err = e; }
    state._lastDiff = diff;   // 展开折叠段落时要按同一份数据重画
    if (err) { v.innerHTML = `<div class="empty">读不到差异：${esc(err.message || "")}</div>`; return; }
    const active = versions.find((x) => x.current) || versions[0];
    const vList = versions.slice().sort((a, b) => a.version - b.version);
    v.innerHTML = `
      <div class="row" style="margin-top:0">
        <button class="btn" data-act="edit-back">← 返回列表</button>
        <button class="btn" data-article="${state.editsId}">打开最新原文</button>
      </div>
      <p class="kicker">DIFF · ${esc(cur?.feed || "")}</p>
      <h2 class="h2">${esc(cur?.title || active?.title || "")}</h2>
      <p class="sub">共 ${versions.length} 版 · 当前 v${esc(active?.version ?? "?")}</p>
      <div class="chips" style="margin-bottom:12px">
        ${vList.map((x) => `<button class="chip ${x.version === active?.version ? "on" : ""}"
            data-act="view-version" data-id="${state.editsId}" data-version="${x.version}">v${x.version}${x.status !== "active" ? " ·" + esc(x.status) : ""}</button>`).join("")}
      </div>
      ${!diff ? `<div class="note">这篇只有一版可比 —— 等下一次改稿。</div>` : `
        <div class="row" style="align-items:center">
          <label class="m">对比
            <select id="diffFrom" data-act="diff-pick">
              ${vList.map((x) => `<option value="${x.version}" ${x.version === diff.from ? "selected" : ""}>v${x.version}</option>`).join("")}
            </select> → 
            <select id="diffTo" data-act="diff-pick">
              ${vList.map((x) => `<option value="${x.version}" ${x.version === diff.to ? "selected" : ""}>v${x.version}</option>`).join("")}
            </select>
          </label>
          <span class="b bad">−${diff.removed}</span><span class="b good">+${diff.added}</span>
          ${diff.titleChanged ? `<span class="b acc">标题也变了</span>` : ""}
        </div>
        ${(diff.changes || []).length ? `<div class="card diffbox" style="cursor:default">${diffRowsHtml(diff.changes)}</div>`
      : `<div class="card" style="cursor:default"><div class="t">v${diff.from} → v${diff.to}</div>
            <p class="sub" style="margin:6px 0 0">两版<strong>正文文字完全一致</strong> —— 作者大概只动了排版、图片或摘要。</p></div>`}`}
      <div class="card" style="cursor:default;margin-top:12px">
        <div class="t">版本清单</div>
        <table class="dt"><thead><tr><th>版本</th><th>状态</th><th>归档时间</th><th>长度</th><th></th></tr></thead>
        <tbody>${versions.map((x) => `<tr>
          <td>v${x.version}${x.current ? "（当前）" : ""}</td>
          <td>${esc(x.status)}</td>
          <td>${x.archivedAt ? esc(fmtDate(x.archivedAt)) : "—"}</td>
          <td class="mono">${x.length} 字</td>
          <td><button class="btn" data-act="view-version" data-id="${state.editsId}" data-version="${x.version}">看正文</button></td>
        </tr>`).join("")}</tbody></table>
      </div>`;
  }

  /* ── 跨源去重 ── */
  else if (V === "dedup") {
    title.textContent = "跨源去重";
    if (!DEDUP.scanned && !DEDUP.clusters.length && !DEDUP.hidden.length) {
      v.innerHTML = `
        <p class="kicker">DEDUP</p><h2 class="h2">跨源去重</h2>
        <p class="sub">段落重合度 ≥ ${DEDUP.threshold}% 视为同一内容 · <strong>只隐藏，不删除</strong>，随时可撤销</p>
        <div class="row"><button class="btn pri" data-act="dedup-scan">开始扫描（48 小时窗口）</button></div>
        <div class="note">扫描要把窗口内上万篇正文读进来做段落比对，会跑几秒到几十秒。命令行等价物：<span class="mono">sip --dedup scan</span>。</div>`;
      return;
    }
    if (state.dedupRep) {
      const c = DEDUP.clusters.find((x) => x.representativeId === state.dedupRep);
      if (!c) { state.dedupRep = null; render(); return; }
      v.innerHTML = `<div class="empty">对比中…</div>`;
      const reps = c.members;
      const left = reps[0];
      const right = reps.find((m) => m.itemId === state.dedupPair) || reps[1] || reps[0];
      let d = null, derr = null;
      try { d = await api(`/api/dedup/diff?a=${left.itemId}&b=${right.itemId}`); } catch (e) { derr = e; }
      v.innerHTML = `
        <div class="row" style="margin-top:0">
          <button class="btn" data-act="dedup-back">← 返回簇列表</button>
          <button class="btn pri" data-act="dedup-hide-cluster" data-rep="${c.representativeId}">保留代表元 · 隐藏其余 ${reps.length - 1} 篇</button>
        </div>
        <p class="kicker">DEDUP · 对比</p>
        <h2 class="h2">${esc(c.title)}</h2>
        <p class="sub">重合度下限 <span class="mono">${c.minOverlap}%</span> · 阈值 <span class="mono">${DEDUP.threshold}%</span> · 代表元 = 编号最小的那篇</p>
        <div class="chips" style="margin-bottom:12px">
          ${reps.map((m, i) => i === 0
        ? `<span class="chip on">代表 · #${m.itemId} ${esc(m.feed)}</span>`
        : `<button class="chip ${m.itemId === right.itemId ? "on" : ""}" data-act="dedup-pair" data-rep="${c.representativeId}" data-id="${m.itemId}">对比 · #${m.itemId} ${esc(m.feed)}</button>`).join("")}
        </div>
        ${derr ? `<div class="empty">读不到差异：${esc(derr.message || "")}</div>` : `
        <div class="grid2" style="margin-bottom:12px">
          ${[left, right].map((m, i) => `
            <div class="card" style="cursor:default">
              <div class="t">${i === 0 ? "代表元" : "对比项"} · #${m.itemId}</div>
              <div class="m"><span>${esc(m.feed)}</span><span class="b">${m.length ?? 0} 字</span>${m.published ? `<span>${esc(fmtDate(m.published))}</span>` : ""}</div>
              <div style="margin-top:6px;font-size:13px;font-family:var(--serif)">${esc(m.title)}</div>
              <div class="row" style="margin-bottom:0">
                <button class="btn" data-article="${m.itemId}">读全文</button>
                ${i === 1 ? `<button class="btn dan" data-act="dedup-hide" data-id="${m.itemId}" data-canon="${left.itemId}">隐藏这篇</button>` : ""}
              </div>
            </div>`).join("")}
        </div>
        <div class="card diffbox" style="cursor:default">
          <div class="m" style="margin-bottom:10px">
            <span class="b">段落级 diff</span><span class="b">重合 ${d?.overlap ?? "?"}%</span>
            <span class="b bad">−${d?.removed ?? 0} 行</span><span class="b good">+${d?.added ?? 0} 行</span>
          </div>
          ${(d?.lines || []).map((l) => l.type === "Deleted" ? `<div class="diff-del">${esc(l.text)}</div>`
          : l.type === "Inserted" ? `<div class="diff-add">${esc(l.text)}</div>`
            : `<div>${esc(l.text) || "&nbsp;"}</div>`).join("") || `<div class="empty">两篇正文的段落完全一致</div>`}
        </div>`}`;
      return;
    }

    v.innerHTML = `
      <p class="kicker">DEDUP</p><h2 class="h2">跨源去重</h2>
      <p class="sub">段落重合度 ≥ ${DEDUP.threshold}% · 只隐藏，不删除 · 隐藏后可随时撤销</p>
      <div class="row">
        <button class="btn pri" data-act="dedup-scan">重新扫描</button>
        <span class="sub" style="align-self:center;margin:0">${DEDUP.clusters.length} 组 · 已隐藏 ${DEDUP.hidden.length} 篇</span>
      </div>
      <div class="cards">${DEDUP.clusters.length ? DEDUP.clusters.map((c) => `
        <div class="card" style="cursor:default">
          <div class="t">${esc(c.title)}</div>
          <div class="m">
            <span class="b">${c.size} 篇</span><span class="b acc">重合 ≥ ${c.minOverlap}%</span><span>${esc(c.source)}</span>
          </div>
          <div class="badge-list">${c.members.map((m) => `<span class="b">#${m.itemId} ${esc(m.feed)}</span>`).join("")}</div>
          <div class="row" style="margin-bottom:0">
            <button class="btn pri" data-act="dedup-open" data-rep="${c.representativeId}">查看对比</button>
            <button class="btn" data-act="dedup-hide-cluster" data-rep="${c.representativeId}">保留代表元 · 隐藏其余</button>
          </div>
        </div>`).join("") : `<div class="empty">窗口内没有发现跨源重复</div>`}</div>
      ${DEDUP.hidden.length ? `
      <div class="card" style="cursor:default;margin-top:12px">
        <div class="t">已隐藏（可撤销）</div>
        <table class="dt"><tbody>${DEDUP.hidden.map((h) => `<tr>
          <td>#${h.itemId}</td><td>${esc(h.title)}</td><td>${esc(h.source)}</td>
          <td><button class="btn" data-act="dedup-undo" data-key="${esc(h.key)}">撤销</button></td>
        </tr>`).join("")}</tbody></table>
      </div>` : ""}
      <div class="note">与「改稿追踪」不同：这里是<strong>跨源同一内容</strong>的转载/摘要差异。命令行：<span class="mono">sip --dedup scan | list | hide | undo</span>。</div>`;
  }

  /* ── 源规则 ── */
  else if (V === "policy") {
    title.textContent = "源规则";
    if (!POLICIES.rows.length && !POLICIES.actions.length) {
      v.innerHTML = `<div class="empty">读取规则…</div>`;
      try { await loadPolicies(); } catch (e) { v.innerHTML = `<div class="empty">读不到：${esc(e.message || "")}</div>`; return; }
    }
    const feeds = FEEDS.filter((f) => !f.isImport);
    const actLabel = {
      lower_frequency: "降频（设置更新计划）", archive: "归档（给标题打时间戳）",
      keep: "保留（只记一条理由）", tag: "打标签", unsubscribe: "退订备注",
    };
    v.innerHTML = `
      <p class="kicker">POLICY</p><h2 class="h2">源规则</h2>
      <p class="sub">你确认过的处理规则 · <strong>AI 永不自动写</strong>（createdBy 永远是 user）</p>
      <div class="card" style="cursor:default">
        <div class="t">新增 / 覆盖规则</div>
        <div class="row" style="align-items:center">
          <select id="polFeed" style="flex:1;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)">
            <option value="">选择订阅源…</option>
            ${feeds.map((f) => `<option value="${f.id}">${esc(f.title)}</option>`).join("")}
          </select>
          <select id="polAction" style="flex:1;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)">
            ${(POLICIES.actions.length ? POLICIES.actions : ["lower_frequency", "archive", "keep", "tag", "unsubscribe"])
        .map((a) => `<option value="${esc(a)}">${esc(actLabel[a] || a)}</option>`).join("")}
          </select>
        </div>
        <div class="row" style="align-items:center">
          <input id="polSchedule" placeholder="降频用：30m / 1h / daily@10:00 / weekly@Mon 08:00" style="flex:1;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)" />
          <input id="polTag" placeholder="标签（不带 #）" style="width:150px;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)" />
          <input id="polNote" placeholder="备注 / 理由" style="flex:1;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--elev)" />
          <button class="btn pri" id="polAdd" data-act="policy-add">保存</button>
        </div>
        <p class="sub" id="polMsg" hidden style="margin:8px 0 0"></p>
        <div class="m">「归档」会像终端里按 A 那样给源标题加时间戳；「降频」会真的写进 <span class="mono">Feeds.Schedule</span>。</div>
      </div>
      <table class="dt" style="margin-top:14px"><thead><tr><th>源</th><th>动作</th><th>参数</th><th>备注</th><th>当前计划</th><th></th></tr></thead>
      <tbody>${POLICIES.rows.length ? POLICIES.rows.map((p) => `<tr>
        <td>${esc(p.feed)}</td><td>${esc(p.action)}</td>
        <td>${esc(p.schedule || p.tag || "—")}</td><td>${esc(p.note || "—")}</td>
        <td class="mono">${esc(p.currentSchedule || "manual")}</td>
        <td><button class="btn dan" data-act="policy-del" data-id="${p.feedId}">删</button></td>
      </tr>`).join("") : `<tr><td colspan="6" class="empty">还没有规则</td></tr>`}</tbody></table>
      <div class="note">命令行等价物：<span class="mono">sip --policy list | set &lt;源&gt; &lt;动作&gt; … | remove &lt;源&gt;</span>。</div>`;
  }

  /* ── 阅读报告 ── */
  else if (V === "insights") {
    title.textContent = "阅读报告";
    v.innerHTML = `<div class="empty">读取报告…</div>`;
    let rep = null, rerr = null;
    try { rep = await api("/api/insights"); } catch (e) { rerr = e; }
    await loadGovernance();
    const interval = CONFIG.data?.insights?.interval || "off";
    const telOn = TELEMETRY.data?.enabled;
    const head = `
      <p class="kicker">INSIGHTS</p><h2 class="h2">阅读报告</h2>
      <div class="row">
        <span class="sub" style="margin:0;align-self:center">定时提醒：</span>
        ${["off", "7d", "30d"].map((x) => `<button class="btn ${interval === x ? "pri" : ""}" data-act="insights-interval" data-value="${x}">${x === "off" ? "关" : "每 " + x}</button>`).join("")}
        <button class="btn" data-act="telemetry-toggle" data-on="${telOn ? "0" : "1"}">${telOn ? "关闭遥测" : "开启遥测"}</button>
        <a class="btn" href="/api/telemetry/export" download>导出记录</a>
      </div>`;

    if (rerr && rerr.code === "TELEMETRY_OFF") {
      v.innerHTML = head + notWired("阅读报告",
        "这份报告全部来自本机遥测，而遥测默认是关的——所以现在没有任何数据可报。",
        "sip telemetry enable（或点上面的「开启遥测」）",
        "以前这一页显示的是一组编造的统计数字，已经删掉：报告可以没有，不能是假的。");
      return;
    }
    if (rerr || !rep) { v.innerHTML = head + `<div class="empty">报告读不到：${esc(rerr?.message || "接口不可用")}</div>`; return; }
    const feeds = rep.feeds || [];
    const sum = (k) => feeds.reduce((a, f) => a + (f[k] || 0), 0);
    v.innerHTML = head + `
      <p class="sub" style="margin-top:12px">${rep.windowDays} 天窗口 · 事实来自本机遥测（不发往任何地方）· 生成于 ${esc(fmtDate(rep.generatedAt))}</p>
      <div class="grid3">
        <div class="stat"><b>${sum("opened")}</b><span>打开</span></div>
        <div class="stat"><b>${sum("completed")}</b><span>读完</span></div>
        <div class="stat"><b>${sum("backlog")}</b><span>未读积压</span></div>
      </div>
      <p class="sub" style="margin-top:10px">♥ 你点赞 ${sum("userLikes")} · 🤖 AI 点赞 ${sum("aiLikes")} · AI 调用 ${rep.aiCalls?.total || 0} 次（摘要 ${rep.aiCalls?.llm || 0} / 嵌入 ${rep.aiCalls?.embedding || 0}）</p>
      <div class="card" style="cursor:default;margin-top:12px">
        <div class="t">按源</div>
        ${feeds.length ? feeds.map((f) => `
          <div style="margin-top:8px">
            <div style="display:flex;gap:8px;align-items:center"><span style="flex:1">${esc(f.title)}</span><span class="b">${esc(f.status || "")}</span>
              <button class="btn" data-feed="${f.id}">打开源</button></div>
            <div style="color:var(--muted);font-size:12px;margin-top:2px">订阅 ${f.active} · 积压 ${f.backlog} · 打开 ${f.opened} · 读完 ${f.completed} · 完成率 ${f.completionRate}%${(f.reasons && f.reasons.length) ? " · " + f.reasons.map((r) => esc(r)).join(" / ") : ""}</div>
          </div>`).join("") : `<div class="empty">还没有订阅源</div>`}
      </div>`;
  }

  /* ── 设置 ── */
  else if (V === "settings") {
    title.textContent = "设置";
    const th = document.documentElement.dataset.theme || "paper";
    const fs = getComputedStyle(document.documentElement).getPropertyValue("--reading-size").trim();
    const cols = getComputedStyle(document.documentElement).getPropertyValue("--cols").trim();
    const tab = state.tab || "reading";
    let body = "";

    if (tab === "reading") {
      body = `
        <div class="card" style="cursor:default">
          <div class="t">主题</div>
          <div class="seg" style="margin-top:8px">
            <button data-act="theme" data-th="day" class="${th === "day" ? "on" : ""}">${t("Day")}</button>
            <button data-act="theme" data-th="paper" class="${th === "paper" ? "on" : ""}">${t("Paper")}</button>
            <button data-act="theme" data-th="night" class="${th === "night" ? "on" : ""}">${t("Night")}</button>
          </div>
        </div>
        <div class="card" style="cursor:default;margin-top:8px">
          <div class="t">${t("Font")} ${fs} · ${t("Columns")} ${cols}</div>
          <div class="row">
            <button class="btn" data-act="font" data-d="-1">A−</button>
            <button class="btn" data-act="font" data-d="1">A+</button>
            <button class="btn" data-act="cols" data-cols="1">1</button>
            <button class="btn" data-act="cols" data-cols="2">2</button>
            <button class="btn" data-act="cols" data-cols="3">3</button>
          </div>
          <div class="m">偏好存在本浏览器的 localStorage（不上传、不进库）。多栏时左右箭头 / 滚轮翻页。</div>
        </div>
        <div class="card" style="cursor:default;margin-top:8px">
          <div class="t">${t("UI language")}</div>
          <div class="seg" style="margin-top:8px">
            <button data-act="lang" data-lang="zh-CN" class="${state.lang === "zh-CN" ? "on" : ""}">简体中文</button>
            <button data-act="lang" data-lang="en-US" class="${state.lang === "en-US" ? "on" : ""}">English</button>
            <button data-act="lang" data-lang="zh-Moe" class="${state.lang === "zh-Moe" ? "on" : ""}">卖萌</button>
          </div>
          <div class="m" style="margin-top:10px">词条来自 <span class="mono">/languages/${esc(state.lang)}.json</span>（与 sip Lang.T 同键）· 已载入 ${Object.keys(state.dict).length} 条</div>
        </div>`;
    } else if (tab === "system") {
      if (!CONFIG.data) { v.innerHTML = `<div class="empty">读取配置…</div>`; await loadGovernance(); }
      const c = CONFIG.data;
      body = c ? `
        <div class="card" style="cursor:default"><div class="t">运行</div>
          <div class="kv" style="margin-top:8px">
            <div><b>版本</b><span>sip v${esc(c.version)}</span></div>
            <div><b>数据目录</b><span class="mono">${esc(c.dataDir)}</span></div>
            <div><b>数据库</b><span class="mono">${esc(c.dbPath)}</span></div>
            <div><b>绑定</b><span>${esc(c.web.host)}:${esc(c.web.port)} ${c.web.networkReachable ? "（局域网可达）" : "（仅本机）"}</span></div>
            <div><b>Web 密码</b><span>${c.web.passwordSet ? "已设" : "未设（用终端里的引导链接）"}</span></div>
            <div><b>会话</b><span>进程内一次性密钥 · 重启即失效</span></div>
          </div>
        </div>
        <div class="card" style="cursor:default;margin-top:8px"><div class="t">内容</div>
          <div class="kv" style="margin-top:8px">
            <div><b>订阅源</b><span>${c.counts.feeds}</span></div>
            <div><b>文章</b><span>${c.counts.items}</span></div>
            <div><b>收藏</b><span>${c.counts.likes}</span></div>
            <div><b>全文缓存</b><span>${c.counts.fulltext} 篇</span></div>
          </div>
        </div>
        <div class="card" style="cursor:default;margin-top:8px"><div class="t">AI</div>
          <div class="kv" style="margin-top:8px">
            <div><b>状态</b><span>${c.ai.configured ? "已配置" : "未配置（先在终端跑 sip --init）"}</span></div>
            <div><b>嵌入模型</b><span>${esc(c.ai.embedding.model)} · ${c.ai.embedding.dimensions} 维</span></div>
            <div><b>嵌入端点</b><span class="mono">${esc(c.ai.embedding.endpoint)}</span></div>
            <div><b>嵌入 Key</b><span>${c.ai.embedding.keySet ? "已存（系统凭据库）" : "未存"}</span></div>
            <div><b>LLM</b><span>${esc(c.ai.llm.model)}</span></div>
            <div><b>LLM Key</b><span>${c.ai.llm.keySet ? "已存（系统凭据库）" : "未存"}</span></div>
            <div><b>检索阈值</b><span>${c.ai.embedding.searchThreshold}</span></div>
          </div>
          <div class="m" style="margin-top:8px">Key 只在真实终端里输入（<span class="mono">sip --init</span> / <span class="mono">sip aikey set</span>），网页只显示"存没存"。</div>
        </div>
        <div class="card" style="cursor:default;margin-top:8px"><div class="t">阈值</div>
          <div class="kv" style="margin-top:8px">
            <div><b>去重重合度</b><span>${c.thresholds.dedup}</span></div>
            <div><b>语义去重</b><span>${c.thresholds.dedupSemantic}</span></div>
            <div><b>改动分级</b><span>润色 &lt; ${c.thresholds.changeGradePolish} · 反转 ≥ ${c.thresholds.changeGradeReverse}</span></div>
            <div><b>主题归组</b><span>${c.thresholds.groupMatch}</span></div>
            <div><b>高频源阈值</b><span>${c.thresholds.floodPerDay ?? "自动"}</span></div>
          </div>
          <div class="m" style="margin-top:8px">改这些值请直接编辑 <span class="mono">readwithhotsoup/sip_settings.json</span>（网页不提供手滑入口）。</div>
        </div>` : `<div class="empty">读不到配置</div>`;
    } else {
      if (!CONFIG.data) { v.innerHTML = `<div class="empty">读取治理状态…</div>`; await loadGovernance(); }
      const simon = SIMON.data, tel = TELEMETRY.data, idx = INDEX.data;
      const lvl = simon?.level ?? 1;
      body = `
        <div class="card" style="cursor:default"><div class="t">孟思琳（simon）安全守护</div>
          <div class="m" style="margin:8px 0">默认开启、无法关闭；只能调挡位。挡位 2 起 CLI 与网页的一切写操作被拒，挡位 3 起连 CLI 调用都被拒。</div>
          <div class="row">
            ${[1, 2, 3].map((x) => `<button class="btn ${lvl === x ? "pri" : ""}" data-act="simon-up" data-level="${x}">挡位 ${x}</button>`).join("")}
          </div>
          <div class="m">升档（收紧）任意通道放行 —— 发现异常时能立刻收紧。<strong>降档（放宽）必须在真实终端</strong>：<span class="mono">sip simon level N</span>（真 TTY + Web 口令）。浏览器不是那个通道。</div>
          <div class="m" style="margin-top:6px">Agent 外部调用门：<strong>${simon?.agentGate ? "开" : "关"}</strong> · 开：<span class="mono">sip --agentok</span>（真实终端 + Web 口令）</div>
          ${simon?.events?.length ? `<div class="m" style="margin-top:6px">最近：${simon.events.slice(0, 5).map((e) => esc(e.type + " " + e.detail)).join(" · ")}</div>` : ""}
        </div>
        <div class="card" style="cursor:default;margin-top:8px"><div class="t">遥测（默认关）</div>
          <div class="m" style="margin:8px 0">${tel ? `当前：<strong>${esc(tel.consent)}</strong> · 本机记录 ${tel.events} 条${tel.first ? ` · ${esc(fmtDate(tel.first))} → ${esc(fmtDate(tel.last))}` : ""}` : "读不到"}</div>
          <div class="row">
            <button class="btn ${tel?.enabled ? "dan" : "pri"}" data-act="telemetry-toggle" data-on="${tel?.enabled ? "0" : "1"}">${tel?.enabled ? "关闭遥测" : "开启遥测"}</button>
            <a class="btn" href="/api/telemetry/export" download>导出 JSON</a>
            <button class="btn" data-act="purge-fulltext">清空全文缓存</button>
          </div>
          <div class="m">记录只写本机 <span class="mono">telemetry.db</span>，绝不自动上传。阅读报告依赖它 —— 关着就没有报告数据。</div>
        </div>
        <div class="card" style="cursor:default;margin-top:8px"><div class="t">向量索引</div>
          <div class="m" style="margin:8px 0">${idx ? `${esc(idx.model)}（${idx.dimensions} 维）@ <span class="mono">${esc(idx.endpoint)}</span><br/>
            向量 ${idx.vectors} 条 · 分块 ${idx.chunks} 条 · 活跃文章 ${idx.active} 篇 · ${idx.configured ? "AI 已配置" : "<strong>AI 未配置</strong>"}` : "读不到"}</div>
          <div class="row">
            <button class="btn pri" data-act="index-run" data-reindex="0">补索引（只处理缺的）</button>
            <button class="btn" data-act="index-run" data-reindex="1">重建索引（全量）</button>
            <button class="btn" data-act="summary-all">批量生成摘要</button>
          </div>
          <p class="sub" id="govMsg" hidden style="margin:8px 0 0"></p>
          <div class="m">与 CLI <span class="mono">sip --index / --reindex / --summary-all</span> 走同一套核心函数；网页只是不会卡在 stdin 上。</div>
        </div>`;
    }

    v.innerHTML = `
      <p class="kicker">SETTINGS</p><h2 class="h2">设置</h2>
      <div class="tabs">
        <button data-act="tab" data-tab="reading" class="${tab === "reading" ? "on" : ""}">阅读</button>
        <button data-act="tab" data-tab="system" class="${tab === "system" ? "on" : ""}">系统</button>
        <button data-act="tab" data-tab="governance" class="${tab === "governance" ? "on" : ""}">治理</button>
      </div>
      ${body}`;
  }

  /* ── 关于 ── */
  else if (V === "about") {
    title.textContent = "关于";
    if (!CONFIG.data) { try { await loadGovernance(); } catch { } }
    const c = CONFIG.data;
    v.innerHTML = `
      <p class="kicker">ABOUT</p><h2 class="h2">关于 sip</h2>
      <p class="sub">本地优先的个人信息库 · 品，你细品</p>
      <div class="grid2">
        <div class="card" style="cursor:default">
          <div class="t">版本</div>
          <div class="m" style="flex-direction:column;align-items:flex-start;gap:8px">
            <span>版本号：<span class="b acc">v${esc(c?.version || "?")}</span></span>
            <span>运行时：.NET 10 · 单文件</span>
            <span>界面：内置 Web（${esc(state.server?.auth || "本机")} 认证）</span>
          </div>
        </div>
        <div class="card" style="cursor:default">
          <div class="t">项目</div>
          <div class="m" style="flex-direction:column;align-items:flex-start;gap:8px">
            <span>作者：<strong>hahahotsoup</strong></span>
            <span>仓库：<a href="https://github.com/hahahotsoup/sipintui" target="_blank" rel="noopener">github.com/hahahotsoup/sipintui</a></span>
            <span>文档：<a href="https://sip.hotsouprealm.top/" target="_blank" rel="noopener">sip.hotsouprealm.top</a></span>
            <span>许可：GPL-3.0</span>
          </div>
        </div>
      </div>
      <div class="card" style="cursor:default;margin-top:10px">
        <div class="t">安全边界（说清楚，不含糊）</div>
        <div class="m" style="flex-direction:column;align-items:flex-start;gap:6px">
          <span>· 正文只在服务端净化后发给页面（标签/属性白名单）+ CSP <span class="mono">script-src 'self'</span>：订阅源作者写一篇文章，不能变成在你机器上运行的脚本</span>
          <span>· 会话是<strong>进程内一次性密钥</strong>：登录一次管到重启，换浏览器要重新登录</span>
          <span>· 无密码模式裸访问 <span class="mono">/</span> 被拒 —— 得用终端里那条 <span class="mono">?t=</span> 引导链接</span>
          <span>· 写操作受孟思琳挡位约束（与 CLI 同一策略源）；<strong>降档 / 开 Agent 门只能在终端</strong></span>
          <span>· API Key 只从真实终端输入，网页只显示"存没存"</span>
          <span>· 遥测默认关；开启后也只写本机 <span class="mono">telemetry.db</span>，可导出</span>
          <span>· 这道门挡得住被脚本/Agent 包装的调用；<strong>挡不住已以你身份运行、且知道口令的程序</strong> —— 那是应用层门，不是权限边界</span>
        </div>
      </div>
      <div class="note">数据全在 <span class="mono">readwithhotsoup/</span> · 拷走就是迁移 · 不注册、不云端。</div>`;
  }
}

function renderDigest(dg) {
  return `
    <div class="grid3" style="margin-top:16px">
      <div class="stat"><b>${dg.newTotal}</b><span>48h 新增</span></div>
      <div class="stat"><b>${dg.modified?.length || 0}</b><span>被改过</span></div>
      <div class="stat"><b>${dg.dedups?.length || 0}</b><span>可能同文</span></div>
    </div>
    ${dg.newBySource?.length ? `<div class="card" style="cursor:default;margin-top:10px">
      <div class="t">按源新增</div>
      <div class="badge-list">${dg.newBySource.map((s) => `<span class="b ${s.flood ? "bad" : ""}">${esc(s.source)} · ${s.count}${s.flood ? " · 高频" : ""}</span>`).join("")}</div>
    </div>` : ""}
    ${dg.modified?.length ? `<div class="card" style="cursor:default;margin-top:10px">
      <div class="t">被作者改过（点进去看差异）</div>
      <table class="dt"><tbody>${dg.modified.map((m) => `<tr>
        <td>#${m.itemId}</td><td>${esc(m.title)}</td><td>${esc(m.source)}</td>
        <td class="mono">−${m.removedLines} +${m.addedLines} · ${m.wordDelta >= 0 ? "+" : ""}${m.wordDelta} 字</td>
        <td><button class="btn" data-article="${m.itemId}">读</button></td>
      </tr>`).join("")}</tbody></table>
    </div>` : ""}
    ${dg.dedups?.length ? `<div class="card" style="cursor:default;margin-top:10px">
      <div class="t">可能同文（跨源重复）</div>
      <table class="dt"><tbody>${dg.dedups.map((c) => `<tr>
        <td>${c.size} 篇</td><td>${esc(c.title)}</td><td>${esc(c.source)}</td>
        <td class="mono">重合 ≥ ${c.minOverlap}%</td>
        <td><button class="btn" data-act="nav" data-v="dedup">去处理</button></td>
      </tr>`).join("")}</tbody></table>
    </div>` : ""}`;
}

function renderVersionPanel(versions, itemId) {
  const list = versions.slice().sort((a, b) => b.version - a.version);
  return `<div class="card" style="cursor:default;margin-bottom:16px">
    <div class="t">改稿历史（共 ${list.length} 版 · 版本链按源隔离）</div>
    <table class="dt"><thead><tr><th>版本</th><th>状态</th><th>归档时间</th><th>长度</th><th></th></tr></thead>
    <tbody>${list.map((v) => `<tr>
      <td>v${v.version}${v.current ? "（当前）" : ""}</td>
      <td>${esc(v.status)}</td>
      <td>${v.archivedAt ? esc(fmtDate(v.archivedAt)) : "—"}</td>
      <td class="mono">${v.length} 字</td>
      <td>${v.current ? `<span class="m">正在看</span>`
      : `<button class="btn" data-act="view-version" data-id="${itemId}" data-version="${v.version}">看这一版</button>`}</td>
    </tr>`).join("")}</tbody></table>
    <div class="row" style="margin-bottom:0">
      <button class="btn" data-act="versions" data-id="${itemId}">收起</button>
      <button class="btn" data-act="edit-open" data-id="${itemId}">逐行对比</button>
    </div>
  </div>`;
}

/* ───────── 面板 / 导入拖放 ───────── */
function bindImportDrop() {
  const drop = $("impDrop");
  const input = $("impFile");
  if (input) input.addEventListener("change", () => { realImportFiles(input.files); input.value = ""; });
  if (!drop) return;
  ["dragenter", "dragover"].forEach((ev) => drop.addEventListener(ev, (e) => {
    e.preventDefault(); drop.classList.add("over");
  }));
  ["dragleave", "drop"].forEach((ev) => drop.addEventListener(ev, (e) => {
    e.preventDefault(); drop.classList.remove("over");
  }));
  drop.addEventListener("drop", (e) => {
    const files = e.dataTransfer?.files;
    if (files && files.length) realImportFiles(files);
  });
}

/* ───────── 事件委托：全站唯一的点击入口 ───────── */
document.addEventListener("click", async (e) => {
  if (e.target.matches("[data-close]") || e.target.classList.contains("panel")) {
    const p = e.target.closest(".panel");
    if (p) { p.hidden = true; return; }
  }
  if (e.target.classList.contains("palette")) { closePalette(); return; }

  const pg = e.target.closest("[data-page]");
  if (pg) { pagerGo(+pg.dataset.page); return; }

  const hit = e.target.closest("[data-act]");
  const fa = e.target.closest("[data-feed]");
  const ar = e.target.closest("[data-article]");
  const loc = e.target.closest("[data-loc]");

  if (loc) {
    state.view = "article"; state.articleId = +loc.dataset.loc;
    state.jumpKw = loc.dataset.kw || ""; state.articleVersion = null; state.versions = null;
    render(); return;
  }
  if (ar && !hit) {
    state.view = "article"; state.articleId = +ar.dataset.article;
    state.articleVersion = null; state.versions = null; state.jumpKw = "";
    render(); return;
  }
  if (fa && !hit) { state.view = "feed"; state.feedId = +fa.dataset.feed; render(); return; }
  if (!hit) return;

  const act = hit.dataset.act;
  const id = +hit.dataset.id || 0;
  switch (act) {
    /* AI 阅读助手（划词问 AI）—— 见文件末尾那一节 */
    case "ai-open": aiOpen(); return;
    case "ai-close": aiClose(); return;
    case "ai-send": aiSend(); return;
    case "ai-new":
      AI.messages = []; AI.sessionId = null; AI.lastCites = []; AI.sel = ""; aiSyncSel(); aiRenderPanel(); return;
    case "ai-del": aiDelete(); return;
    case "ai-cfg": aiCfgOpen(); return;
    case "ai-cfg-close": aiCfgClose(); return;
    case "ai-cfg-save": await aiCfgSave(hit); return;
    case "ai-sess-toggle":
      AI.sessionListOpen = !AI.sessionListOpen; aiRenderPanel(); return;
    case "ai-switch":
      await aiSwitchSession(hit.dataset.sid || ""); return;
    case "ai-sel-clear":
      AI.sel = ""; aiSyncSel(); $("aiOrb")?.classList.remove("has", "pulse"); return;
    case "ai-cite":
      aiJumpTo(hit.dataset.id || "", +hit.dataset.page || 0); return;
    case "nav": navTo(hit.dataset.v); return;
    case "nav-feed": state.view = "feed"; state.feedId = id; render(); return;
    case "tab": state.tab = hit.dataset.tab; render(); return;
    case "theme": setTheme(hit.dataset.th); render(); return;
    case "lang": setLang(hit.dataset.lang); return;
    case "font": bumpFont(+hit.dataset.d); render(); return;
    case "cols": setCols(+hit.dataset.cols, true); render(); return;
    case "today-refresh": realTodayRefresh(); return;
    case "today-digest": realTodayDigest(); return;
    case "today-mark": realTodayMarkRead(); return;
    case "feed-update": realFeedUpdate(id); return;
    case "feed-archive": realFeedArch(id, true); return;
    case "feed-unarchive": realFeedArch(id, false); return;
    case "feed-info": realFeedInfo(id); return;
    case "feed-delete": realFeedDel(id); return;
    case "feed-schedule": realFeedSchedule(id); return;
    case "like": realLike(id); return;
    case "fulltext": realFulltext(id); return;
    case "summary": realSummary(id); return;
    case "open-external": {
      const url = hit.dataset.link || "";
      if (!/^https?:\/\//i.test(url)) { toast("没有可打开的原文链接"); return; }
      window.open(url, "_blank", "noopener,noreferrer"); return;
    }
    case "meta-toggle": if (readDrawerOpen()) closeReadPanel(); else setMetaOpen(true); return;
    case "meta-close": closeReadPanel(); return;
    case "versions": {
      if (state.versions) { state.versions = null; render(); return; }
      try { await loadVersions(id); render(); } catch (er) { toast(er.message || "读不到版本"); }
      return;
    }
    case "view-version":
      state.view = "article"; state.articleId = id; state.articleVersion = +hit.dataset.version;
      render(); $("content").scrollTop = 0; return;
    case "back-latest": state.articleVersion = null; render(); return;
    case "jump-next-hit": jumpNextHit(); return;
    case "clear-hits": state.jumpKw = ""; render(); return;
    case "restore-pos": $("content").scrollTop = +hit.dataset.pos; toast("已跳回上次位置"); return;
    case "edit-open": {
      state.view = "edits"; state.editsId = id; state.editFrom = null; state.editTo = null;
      state.diffOpen = {};            // 换了文章，折叠状态重来
      EDITS.length = 0; render(); return;
    }
    case "diff-more": {
      // 就地展开这一段未改动的段落：只重画 diff 框，不整页重渲染（别把滚动位置弄丢）
      const run = +hit.dataset.run;
      const open = state.diffOpen || (state.diffOpen = {});
      if (open[run]) delete open[run]; else open[run] = true;
      const box = hit.closest(".diffbox");
      if (box && state._lastDiff) box.innerHTML = diffRowsHtml(state._lastDiff.changes || []);
      return;
    }
    case "edit-back": state.editsId = null; render(); return;
    case "dedup-scan": {
      setBusy(hit, true, "扫描中…");
      try { await loadDedup(true); toast(`扫描完成：${DEDUP.clusters.length} 组`); render(); }
      catch (er) { toast(er.message || "扫描失败"); setBusy(hit, false); }
      return;
    }
    case "dedup-open": state.dedupRep = +hit.dataset.rep; state.dedupPair = null; render(); return;
    case "dedup-pair": state.dedupRep = +hit.dataset.rep; state.dedupPair = id; render(); return;
    case "dedup-back": state.dedupRep = null; state.dedupPair = null; render(); return;
    case "dedup-hide": realDedupHide(id, +hit.dataset.canon); return;
    case "dedup-hide-cluster": realDedupHideCluster(+hit.dataset.rep); return;
    case "dedup-undo": realDedupUndo(hit.dataset.key); return;
    case "policy-add": realPolicyAdd(); return;
    case "policy-del": realPolicyDel(id); return;
    case "ebook-open": await openEbook(id); return;
    case "ebook-page": ebookGo(+hit.dataset.page); return;
    case "book-sec": {
      await gotoSection(+hit.dataset.idx, "first");
      return;
    }
    case "file-open": openImportedFile(id); return;
    case "file-link": copyImportedLink(id, hit); return;
    case "import-del": realImportDel(id); return;
    case "insights-interval": realInsightsInterval(hit.dataset.value); return;
    case "telemetry-toggle": realTelemetry(hit.dataset.on === "1"); return;
    case "purge-fulltext": realPurgeFulltext(); return;
    case "index-run": realIndexRun(hit.dataset.reindex === "1"); return;
    case "summary-all": realSummaryAll(); return;
    case "simon-up": realSimonUp(+hit.dataset.level); return;
    case "palette-open": openPalette(); return;
    case "palette-run": runPaletteLine(hit.dataset.line); return;
    case "search-run": {
      const sem = $("smode2")?.querySelector("button[data-mode=sem]")?.classList.contains("on");
      const q = ($("gq3")?.value || "").trim();
      const box = $("searchPageHits");
      if (!box) return;
      if (!q) { box.innerHTML = `<div class="empty">请输入关键词</div>`; return; }
      box.innerHTML = `<div class="empty">检索中…</div>`;
      const thr = +($("semThr2")?.value || 70) / 100;
      try {
        const d = await runSearch(sem ? "sem" : "grep", q, thr);
        const hits = (d.hits || []).filter((h) => !sem || (h.score ?? 1) >= thr);
        box.innerHTML = `<p class="sub">${sem ? "语义 · 阈值 " + thr.toFixed(2) : "全文"} · ${hits.length} ${sem ? "条" : "篇"}</p>
          <div class="cards">${hits.map((h) => sem
          ? `<button class="card" data-article="${h.itemId}"><div class="t">${esc(h.title)}</div>
              <div class="m"><span>${esc(h.feed || "")}</span><span class="b acc">${(h.score || 0).toFixed(2)}</span></div></button>`
          : `<div class="card" style="cursor:default">
              <div class="t">${esc(h.title)}</div>
              <div class="m"><span>${esc(h.feed || "")}</span></div>
              ${h.snippet ? `<div style="margin-top:6px;font-size:12.5px;color:var(--muted)">${esc(h.snippet)}</div>` : ""}
              <button class="loc-btn" data-loc="${h.itemId}" data-kw="${esc(q)}">打开原文</button>
            </div>`).join("") || `<div class="empty">无结果</div>`}</div>`;
      } catch (er) {
        box.innerHTML = `<div class="empty">${esc(er.code === "SEARCH_FAILED" ? "语义检索不可用（先在终端跑 sip --init 并建索引）" : (er.message || "检索失败"))}</div>`;
      }
      return;
    }
    default: return;
  }
});

/* 下拉框 / 数字框变化：diff 选版本、电子书跳页、语义阈值 */
document.addEventListener("input", (e) => {
  const el = e.target;
  if (el.id === "semThr2") {
    const v = $("semThrVal2"); if (v) v.textContent = (+el.value / 100).toFixed(2);
  }
});
document.addEventListener("change", (e) => {
  const el = e.target.closest("[data-act]");
  if (!el) return;
  const act = el.dataset.act;
  if (act === "diff-pick") {
    state.editFrom = +($("diffFrom")?.value || 0);
    state.editTo = +($("diffTo")?.value || 0);
    state.diffOpen = {};            // 换了对比版本，折叠状态重来
    render();
    return;
  }
  if (act === "ebook-goto") { ebookGo(+el.value); return; }
});

/* 命令面板键盘：↑↓ 选择、Enter 执行、Esc 关闭；Ctrl/Cmd+K 打开 */
document.addEventListener("keydown", (e) => {
  // ── AI 对话面板优先接管键盘 ──
  // 面板开着时，用户在**打字**：Esc 该关面板、Enter 该发送，
  // 而"F 切全屏 / Esc 关抽屉"这些阅读快捷键**不能**顺手触发 ——
  // 否则打一句问句就会被切掉半屏。所以这里先处理、并直接 return。
  if ($("aiPanel")?.classList.contains("on")) {
    if (e.key === "Escape") { aiClose(); e.preventDefault(); return; }
    if (e.target && e.target.id === "aiQ") {
      if (e.key === "Enter" && !e.shiftKey) { aiSend(); e.preventDefault(); return; }
    }
  }
  const pal = $("pPalette");
  if (pal && !pal.hidden) {
    if (e.key === "Escape") { closePalette(); e.preventDefault(); return; }
    if (e.key === "Enter") { runPaletteLine($("paletteInput")?.value || ""); e.preventDefault(); return; }
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      const btns = Array.from(document.querySelectorAll("#paletteList .palette-item"));
      if (!btns.length) return;
      const cur = btns.findIndex((b) => b.classList.contains("on"));
      const next = Math.min(btns.length - 1, Math.max(0, cur + (e.key === "ArrowDown" ? 1 : -1)));
      btns.forEach((b, i) => b.classList.toggle("on", i === next));
      btns[next].scrollIntoView({ block: "nearest" });
      e.preventDefault();
      return;
    }
  }
  // Esc：先关阅读抽屉（**包括"改稿历史"自己撑开的那种**），再退全屏阅读。
  // 只管阅读视图：在列表视图里按 Esc 不该顺手把"抽屉默认展开"这个偏好改掉。
  if (e.key === "Escape" && (state.view === "article" || state.view === "ebook") && readDrawerOpen()) {
    closeReadPanel(); e.preventDefault(); return;
  }
  if (e.key === "Escape" && state.immersive) { setImmersive(false); e.preventDefault(); return; }
  // F：切换全屏阅读（阅读视图内）
  if ((e.key === "f" || e.key === "F") && !e.ctrlKey && !e.metaKey && !e.altKey
      && (state.view === "article" || state.view === "ebook")
      && !(e.target instanceof HTMLInputElement) && !(e.target instanceof HTMLTextAreaElement)) {
    setImmersive(!state.immersive); e.preventDefault(); return;
  }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { openPalette(); e.preventDefault(); return; }

  if ((state.view === "article" || state.view === "ebook") && pager.on) {
    if (e.key === "ArrowRight" || e.key === "PageDown" || e.key === " ") { if (pagerGo(1)) { e.preventDefault(); return; } }
    if (e.key === "ArrowLeft" || e.key === "PageUp") { if (pagerGo(-1)) { e.preventDefault(); return; } }
  }
  if (state.view !== "ebook") return;
  // 只有 PDF 是"按页"的：← → 翻页。文本型电子书已经在上面由分栏翻页接管，
  // 没有分页时就是普通纵向滚动，方向键不该再触发一次重渲染（那会把翻页重置回第一页）。
  if (!state.ebookMeta?.isPdf) return;
  if (e.key === "ArrowLeft") ebookGo((state.ebookPage || 1) - 1);
  if (e.key === "ArrowRight") ebookGo((state.ebookPage || 1) + 1);
});

/* ───────── 静态控件 ───────── */
on("nav", "click", (e) => {
  const b = e.target.closest("button[data-v]");
  if (b) navTo(b.dataset.v);
});
on("btnMenu", "click", () => {
  const app = $("app");
  if (app.classList.contains("side-collapsed")) {
    app.classList.remove("side-collapsed");
    $("side").classList.remove("open");
  } else if (window.matchMedia("(max-width:900px)").matches) {
    $("side").classList.toggle("open");
  } else {
    app.classList.add("side-collapsed");
  }
});
on("btnHideSide", "click", () => {
  $("app").classList.add("side-collapsed");
  $("side").classList.remove("open");
});
on("btnPalette", "click", openPalette);
on("rdScrim", "click", () => closeReadPanel());
on("btnFull", "click", () => setImmersive(!state.immersive));
on("rtFull", "click", () => setImmersive(!state.immersive));

/** 翻页 / 滚动 切换：有人喜欢"一屏一屏"，有人习惯滚。两者都留着，偏好记在本机。
 *  切换时**把位置带过去** —— 同一条"别把我打回开头"的原则。 */
function setPaged(on, userInitiated) {
  const el = $("content");
  let y = Math.round(el?.scrollTop || 0);
  if (pager.on && pager.art) {
    const a = pagerAnchor() || pager.art;
    // 竖向翻页时每页有精确起始 y；横向翻页拿不到"全文 y"，用锚点在本栏内的 y 近似
    y = pager.axis === "y" ? (pager.offsets[pager.page] || 0) : (a.offsetTop || 0);
  }
  state.paged = !!on;
  savePrefs({ paged: state.paged });
  const b = $("rtPager"); if (b) b.classList.toggle("on", state.paged);
  repaginateKeepingPlace();
  if (!state.paged) {
    if (el) el.scrollTop = y;                 // 翻页 → 滚动：滚到刚才那一段
  } else if (pager.on && pager.axis === "y") {
    let p = 0;                                 // 滚动 → 翻页：翻到含刚才那段的那一页
    for (let i = 0; i < pager.offsets.length; i++) if (pager.offsets[i] <= y + 2) p = i;
    pager.page = Math.min(pager.pages - 1, Math.max(0, p));
    pagerApply();
  }
  if (userInitiated) toast(state.paged ? "翻页模式：← → 或右下角 ‹ › 翻页" : "滚动模式：滚轮 / 滚动条");
}
on("rtPager", "click", () => setPaged(!state.paged, true));
on("paletteInput", "input", (e) => filterPalette(e.target.value));
on("btnAdd", "click", () => { $("pAdd").hidden = false; $("addUrl")?.focus(); });
on("btnSync", "click", async () => {
  const btn = $("btnSync");
  setBusy(btn, true, "同步中…");
  toast("同步中…（完成后会报新增篇数）");
  try {
    const d = await api("/api/feeds/sync", { method: "POST" });
    toast(`同步完成：${d.ok ?? 0} 成功 / ${d.fail ?? 0} 失败 · 新增 ${d.added ?? 0} 篇`);
    await loadRealData();
  } catch (e) {
    toast(e.code === "ALREADY_RUNNING" ? "已经在同步了，等这轮跑完" : (e.message || "同步失败"));
  } finally { setBusy($("btnSync"), false); }
});
on("doAdd", "click", async () => {
  const u = ($("addUrl")?.value || "").trim();
  if (!u) { setMsg("addMsg", "请先填订阅源 URL", "bad"); return; }
  const btn = $("doAdd");
  setBusy(btn, true, "抓取中…");
  setMsg("addMsg", `正在抓取 ${u} —— 首次抓取要把该源的文章全部下回来，请稍候（别关这个页面）`);
  try {
    const d = await api("/api/feeds", { method: "POST", body: JSON.stringify({ url: u }) });
    setMsg("addMsg", `已添加，抓到 ${d.items ?? 0} 篇文章`, "ok");
    const el = $("addUrl"); if (el) el.value = "";
    await loadRealData();
    navTo("feeds");
  } catch (e) {
    setMsg("addMsg", e.code === "ALREADY_RUNNING" ? "这个源已经在抓取了，等它结束再看"
      : "添加失败：" + (e.message || "未知错误"), "bad");
  } finally { setBusy(btn, false); }
});
on("doOpml", "click", async () => {
  const f = $("opmlFile")?.files?.[0];
  if (!f) { setMsg("addMsg", "请先选择一个 .opml 文件", "bad"); return; }
  const btn = $("doOpml");
  setBusy(btn, true, "导入中…");
  setMsg("addMsg", `正在导入 ${f.name} —— 逐个抓取订阅源，源多时可能要几分钟，别关这个页面`);
  const timer = setInterval(async () => {
    try {
      const p = await api("/api/progress");
      if (p && p.active) {
        const pos = p.total ? `${Math.min(p.done + 1, p.total)}/${p.total}` : "";
        setMsg("addMsg", `导入中 ${pos}${p.current ? " · 正在抓 " + p.current : ""}`);
      }
    } catch { }
  }, 700);
  try {
    const text = await f.text();
    const d = await api("/api/feeds/opml", { method: "POST", body: text, headers: { "Content-Type": "text/xml" } });
    setMsg("addMsg", `导入完成：新增 ${d.imported ?? 0} · 已存在跳过 ${d.skipped ?? 0} · 失败 ${d.failed ?? 0}`, d.failed > 0 ? "bad" : "ok");
    await loadRealData();
  } catch (e) {
    setMsg("addMsg", e.code === "ALREADY_RUNNING" ? "已经有一个导入在跑了，等它结束" : "导入失败：" + (e.message || "未知错误"), "bad");
  } finally { clearInterval(timer); setBusy(btn, false); }
});
on("doConsent", "click", async () => {
  $("pConsent").hidden = true;
  const id = pendingFulltext; pendingFulltext = 0;
  if (id) await realFulltext(id, true);
});
on("rtTheme", "click", cycleTheme);
on("rtMinus", "click", () => bumpFont(-1));
on("rtPlus", "click", () => bumpFont(1));
on("rtLh", "input", (e) => setLeading(+e.target.value));
on("rtW", "input", (e) => setContentWidth(+e.target.value));
on("rtBar", "click", (e) => {
  const b = e.target.closest(".colbtn"); if (!b) return;
  setCols(+b.dataset.cols, true);
});
on("content", "scroll", () => {
  const el = $("content"); const max = el.scrollHeight - el.clientHeight;
  $("prog").style.width = (max <= 0 ? 100 : (el.scrollTop / max) * 100) + "%";
  // 文章记滚动位置；PDF 记页码（走 ebookGo）；文本型电子书记**节号**（走 book-sec）——
  // 同一个 itemId 只会属于其中一种，不会互相覆盖。
  if (state.view === "article" && state.articleId) saveReadingPosition(state.articleId, Math.round(el.scrollTop));
  if (!pager.on) rememberReadState();   // 翻页模式下没有滚动，位置由 pagerApply 记
}, { passive: true });
on("content", "wheel", () => {
  // 滚轮**不再翻页**（用户要求）：分栏模式下它会把"往下滚一点看看"变成"跳一整页"，误触太容易。
  // 翻页交给 ← → 键与右下角的 ‹ › 按钮；滚轮恢复成最朴素的滚动（分页时页面本身没有可滚内容，
  // 所以它自然什么都不做）。这个空监听保留在案，是为了让"为什么滚轮不翻页"有处可查。
}, { passive: true });
let _pagerRz = 0;
window.addEventListener("resize", () => {
  clearTimeout(_pagerRz);
  _pagerRz = setTimeout(() => { if (state.view === "article" || state.view === "ebook") repaginateKeepingPlace(); }, 150);
});

/* PWA：装得上就行，**不缓存任何东西**（见 web/sw.js）—— 离线显示的旧库比没有更糟。 */
if ("serviceWorker" in navigator && location.protocol !== "file:") {
  navigator.serviceWorker.register("/sw.js").catch(() => { });
}

/* ───────── 启动 ───────── */
document.documentElement.dataset.theme = "paper";
try {
  const saved = localStorage.getItem("sip-lang");
  if (saved) state.lang = saved;
} catch { }

// index.html 里的 `<div class="app" id="app" hidden>` 是"登录壳时代"的残留：
// 那时页面自己画一张登录界面，验证完再 enterApp() 把 hidden 摘掉。
// 现在登录由**服务端**把关（未认证根本拿不到这一页），所以只需在这里摘掉它 ——
// ⚠️ 漏了这一步的后果就是**整页全白**：标记在、脚本在、render() 也跑了，
// 只是所有内容都画在一个 hidden 容器里。
$("app")?.removeAttribute("hidden");

applyLang().then(() => {
  restorePrefs();
  loadReadingProgress();
  render();
  aiInitOrb();          // 悬浮球：拖动位置 + 划词监听（见文件末尾那一节）
});
loadRealData();

/* ══════════════════════════════════════════════════════════════════
   AI 阅读助手（划词问 AI）· 前端
   ══════════════════════════════════════════════════════════════════
   交互基准：docs/草稿-AI阅读悬浮球.html（用户点过并认可的那张草图）。
   服务端：AiReading.cs（路由注册在 Web.cs 的 imports 段）。

   三条纪律，和站内其它地方同源：
   · **无内联脚本 / 无内联事件属性** —— CSP 是 script-src 'self'，
     所有动作走 data-act + 文件中部那个唯一的委托监听器。
   · **划词段只活一轮**（契约 §7）：每次发送取"当下"的选区快照带走；
     发完就清，绝不让上一轮的选区被下一轮复用 —— 否则会出现
     "拿旧段落答新问题"这种最典型的答非所问。
   · **引用跳不回去就不显示**（契约 I4）：服务端已经保证 chapterId 可回查，
     前端只负责把它变成一次真实的跳转。
   ══════════════════════════════════════════════════════════════════ */

/* 会话按书隔离：换书 = 换会话（服务端也会用 SESSION_BOOK_MISMATCH 拦） */
const AI = {
  itemId: null,        // 当前阅读的 item（文章或电子书都算；契约 §12.1-A40①）
  isArticle: false,    // 这一项是 RSS 文章（不是本地导入的书）—— 决定引用指向什么
  sessionId: null,
  messages: [],        // { role:"user"|"assistant", text, cites:[], snapshot:{} }
  sel: "",             // 本轮的划词快照
  anchor: null,        // 本轮的位置锚点
  busy: false,
  deleting: false,     // 删除进行中：防重复点击（服务端是硬删，点两次没意义）
  lastCites: [],
  sessions: [],        // 当前阅读项下的全部会话（换会话用；服务端 /chat 一直返回，之前被丢掉了）
  sessionListOpen: false,
};

/* ───────── 位置：悬浮球 ─────────
   位置存 localStorage：刷新后还在原地（草稿里的行为）。
   拖动阈值 4px 用来区分"点一下"和"拖一下" —— 不然想拖动就总会误开面板。 */
function aiInitOrb() {
  const orb = $("aiOrb");
  if (!orb) return;
  aiSyncText();                    // 界面文案跟当前语言对齐
  try {
    const p = JSON.parse(localStorage.getItem("sip-ai-orb") || "null");
    if (p && typeof p.r === "number") { orb.style.right = p.r + "px"; orb.style.bottom = p.b + "px"; }
  } catch { }

  let drag = null;
  orb.addEventListener("pointerdown", (e) => {
    const cs = getComputedStyle(orb);
    drag = { x: e.clientX, y: e.clientY, r: parseFloat(cs.right) || 26, b: parseFloat(cs.bottom) || 26, moved: false };
    try { orb.setPointerCapture(e.pointerId); } catch { }
  });
  orb.addEventListener("pointermove", (e) => {
    if (!drag) return;
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (Math.abs(dx) + Math.abs(dy) > 4) drag.moved = true;
    const r = Math.min(Math.max(6, drag.r - dx), window.innerWidth - 62);
    const b = Math.min(Math.max(6, drag.b - dy), window.innerHeight - 62);
    orb.style.right = r + "px"; orb.style.bottom = b + "px";
  });
  orb.addEventListener("pointerup", () => {
    if (!drag) return;
    const moved = drag.moved; drag = null;
    try {
      localStorage.setItem("sip-ai-orb", JSON.stringify({
        r: parseFloat(orb.style.right) || 26, b: parseFloat(orb.style.bottom) || 26,
      }));
    } catch { }
    // 拖动只是移动它，不该顺手打开面板 —— 这是"抓起来挪个地方"的常见误触
    if (!moved) aiOpen();
  });

  // 划词：只在正文里认（.pager .prose，与分页器同一个选择器），太短的不算
  document.addEventListener("selectionchange", () => {
    if (!aiOrbVisible()) return;
    const s = String(window.getSelection() || "").trim();
    const n = window.getSelection()?.anchorNode;
    const inProse = !!(n && n.parentElement && n.parentElement.closest(".pager .prose"));
    if (!inProse || s.length < 2) return;
    AI.sel = s.replace(/\s+/g, " ").slice(0, 1500);   // 与服务端 AskSelectionChars 对齐
    const tip = $("aiTip"), orbEl = $("aiOrb");
    if (orbEl) { orbEl.classList.add("has", "pulse"); const d = $("aiOrbDot"); if (d) d.textContent = "问"; }
    if (tip) {
      tip.classList.add("on");
      const r = $("aiOrb")?.getBoundingClientRect();
      if (r) tip.style.bottom = (window.innerHeight - r.top + 10) + "px";
      clearTimeout(tip._t); tip._t = setTimeout(() => tip.classList.remove("on"), 2600);
    }
    aiSyncSel();
  });
}

/** 悬浮球只在阅读页出现 —— 别的时候没有"这段"可指。 */
function aiOrbVisible() {
  const id = AI.itemId;
  return !!id && (state.view === "article" || state.view === "ebook");
}

/** 界面文案走 t()（键=英文原文，与 Lang.T 同一批键）。
    理由：站内**只有一个**语言开关，新界面若把中文写死，切到英文/zh-Moe 时
    它会单独掉队 —— 这正是"同一句话在两处各写一份"的变体。
    缺键时 t() 回落成键本身（英文），所以这里传空串让它保持元素里已有的中文。 */
function aiSyncText() {
  const T = (k) => { const v = t(k); return v === k ? "" : v; };
  const set = (id, v) => { const el = $(id); if (el && v) el.textContent = v; };
  const orb = $("aiOrb");
  if (orb) { const v = T("Ask AI (draggable)"); if (v) { orb.title = v; orb.setAttribute("aria-label", v); } }
  set("aiTip", T("Select text and ask AI →"));
  set("aiSend", T("Send") || "发送");
  const panel = $("aiPanel");
  if (panel) { const v = T("AI reading assistant"); if (v) panel.setAttribute("aria-label", v); }
  const q = $("aiQ");
  if (q) { const v = T("Ask about this passage… (selecting text first fills it in)"); if (v) q.placeholder = v; }
  const hint = $("aiHint");
  if (hint) { const v = T("Enter to send · Shift+Enter for a new line · citations are clickable"); if (v) hint.textContent = v; }
  // 面板里的三个图标按钮靠 title 表达含义。
  // ⚠ 必须按 data-act 找，不能用下标 —— 下标会在加/减按钮时静静错位（把"关闭"的
  // 文案贴到"删除"上，而且不报错）。这里就是踩过一次的地方。
  const hv = (act, k) => { const el = $("aiPanel")?.querySelector(`.ai-h .ib[data-act="${act}"]`); if (el) { const v = T(k); if (v) el.title = v; } };
  hv("ai-new", "New chat");
  hv("ai-del", "Delete this book's chat history (cannot be undone)");
  hv("ai-cfg", "AI settings");
  hv("ai-close", "Close (Esc)");
  const clr = $("aiPanel")?.querySelector(".ai-sel button");
  if (clr) { const v = T("Drop this passage"); if (v) clr.title = v; }
}

/** 每次 render 之后调用：同步球的显隐、标题、位置标签。 */
function aiSyncOrb() {
  // 换书/换文章就换会话：AI.sessionId 属于上一项时直接作废（服务端也会拦）
  // 契约 §12.1-A40①：会话的粒度是**阅读项** —— 本地导入的书和 RSS 文章都算，
  // 所以文章视图下 itemId 就是 articleId（这与"文章不是导入项"曾经冲突，
  // 那段历史见 app.js 里 aiSend 的注释：曾经这里把文章 id 发给 /ask 换回 404）。
  const id = (state.view === "article") ? state.articleId
    : (state.view === "ebook") ? state.ebookId : null;
  AI.isArticle = state.view === "article";
  if (id !== AI.itemId) { AI.sessionId = null; AI.messages = []; AI.lastCites = []; AI.sel = ""; }
  AI.itemId = id;
  const orb = $("aiOrb");
  if (orb) orb.hidden = !aiOrbVisible();
  if (!aiOrbVisible()) aiClose();
  aiSyncSel();
  if ($("aiPanel")?.classList.contains("on")) aiRenderPanel();
}

/** 当前位置（给面板标题用）：电子书给"第 N 章 · 章名"或"第 N 页"，文章给"本文"。 */
function aiWhereNow() {
  if (state.view === "ebook") {
    const m = state.ebookMeta || {};
    if (m.isPdf) return `第 ${state.ebookPage || 1} 页`;
    const secs = state.ebookText?.sections || [];
    const i = state.ebookText?.section ?? 0;
    const s = secs[i];
    // ⚠ 单位是「节」不是「章」，这是刻意的（§12.1-A45③）。
    // 这里的序号来自**前端自己的切节**（按 h1~h3 / 每 12000 字），
    // 与数据库 `Chapters` 的 `Ord` **不是一回事**。以前显示成"第 N 章"，
    // 于是出现"面板说第 1 章、你其实在读第 3 章（正文一）"这种自相矛盾的读数 ——
    // 位置标签不能把"切割序号"冒充成"章节号"。
    return s?.title ? `第 ${i + 1} 节 · ${s.title}` : `第 ${i + 1} 节`;
  }
  return "本文";
}

/** 当前阅读项的标题。文章用加载时存下的 real.title，书用 ebookMeta.title。
    为什么不从 ARTICLES 里找：那样要先知道 feedId，而文章视图手上只有 articleId
    （§2.1 那套"编号双轨"的坑就在这儿）。加载文章时顺手存一份最省事也最不容易错。 */
function aiTitleOf() {
  if (state.view === "ebook") return state.ebookMeta?.title || "";
  if (state.view === "article") return state.articleTitle || "";
  return "";
}

/* ───────── 面板 ───────── */
function aiOpen() {
  if (!aiOrbVisible()) return;
  const p = $("aiPanel"), orb = $("aiOrb");
  if (!p) return;
  p.classList.add("on");
  if (orb) orb.classList.remove("has", "pulse");
  aiSyncText();
  aiSyncSel();
  aiRenderPanel();
  // 已有历史就拉回来（按书保存 —— 刷新/重开浏览器不该丢）
  if (!AI.messages.length) aiLoadHistory().then(() => aiRenderPanel()).catch(() => { });
  setTimeout(() => $("aiQ")?.focus(), 260);
}

function aiClose() { $("aiPanel")?.classList.remove("on"); }

function aiSyncSel() {
  const box = $("aiSel"), txt = $("aiSelText");
  if (!box || !txt) return;
  const has = !!AI.sel;
  box.hidden = !has;
  if (has) txt.textContent = AI.sel;
}

/** 本轮的锚点：把"我现在读到哪儿 + 我划了哪段"打包给服务端（契约 §5.1 anchor）。
    文章的 anchor **不带 chapterId** —— 它没有章节模型（§12.1-A40①：扩的是问答，不是章节），
    硬塞一个章节 id 只会让服务端 FindChapter 落空、白记一个 `chapter:none` 降级。

    ⚠ 契约 §12.1-A45 修的就是这里。原来送的是 `secs[i].title` —— 而前端切节在无标题时
    会兜底成「第 N 节」，服务端的 FindChapter 只认「精确 ChapterId」和「#<ord>」，
    两者都不是 → 本章层整层为空，AI 只能回"没有可依据的资料"。
    实测（《咸的玩笑》）：划的是 ord 3「正文一」(epub:5) 的正文，面板却报"第 1 章"。
    现在改成送**服务端认得出的两种线索**：
      · `chapterId = "#<节序号>"` —— FindChapter 已支持的形式（A45② 的第 2 级）
      · `sectionLead` = 当前节开头的一段纯文本 —— 兜底定位（A45② 的第 4 级）
    两样都不依赖"前端切节 == 服务端章节"这个不成立的假设。
    `sel` 参数是**必须显式传进来**的划词段：调用点已经先把 `AI.sel` 清空了
    （"划词只活一轮"），所以这个函数不能指望从全局读到它。 */
function aiBuildAnchor(sel) {
  const a = { locType: state.view === "ebook" ? (state.ebookMeta?.isPdf ? "page" : "chapter") : "article", ord: 0 };
  const s = (sel !== undefined ? sel : AI.sel);
  if (s) a.selection = s;
  if (state.view === "ebook" && !state.ebookMeta?.isPdf) {
    const secs = state.ebookText?.sections || [];
    const i = state.ebookText?.section ?? 0;
    const sec = secs[i];
    a.chapterId = "#" + (i + 1);
    a.ord = i + 1;
    const lead = aiSectionLead(sec);
    if (lead) a.sectionLead = lead;
  }
  return a;
}

/** 当前节开头的一段纯文本，给服务端当"内容定位"的线索（A45② 第 4 级）。
    取的是**正文第一个有实义的文本节点之后**的 200 字，不是节首 200 字符 ——
    节首往往是 h1 标题或图片，拿它去比正文会定位不到。 */
function aiSectionLead(sec) {
  if (!sec) return "";
  try {
    const d = document.createElement("div");
    d.innerHTML = sec.html || "";
    // 跳过 heading/media 这类"不是正文"的开头
    for (const n of Array.from(d.children || [])) {
      const tag = (n.tagName || "").toLowerCase();
      if (/^h[1-6]$/.test(tag) || tag === "img" || tag === "figure") continue;
      const txt = (n.textContent || "").replace(/\s+/g, " ").trim();
      if (txt.length >= 12) return txt.slice(0, 200);
    }
    const all = (d.textContent || "").replace(/\s+/g, " ").trim();
    return all.length >= 12 ? all.slice(0, 200) : "";
  } catch { return ""; }
}

/** 把服务端返回的消息数组转成面板内部形状。
    抽出来是因为它现在有**两个**消费者：载入当前会话、以及切换到别的会话 ——
    两处各写一遍必然漂移（这个文件里已经踩过一次同类坑）。 */
function aiMsgsFrom(raw) {
  const msgs = [];
  (raw || []).forEach((m) => msgs.push({
    role: m.role === "user" ? "user" : "assistant",
    text: m.text || m.content || "",
    cites: m.cites || [], snapshot: m.snapshot || null,
    persisted: m.persisted,
  }));
  return msgs;
}

async function aiLoadHistory() {
  if (!AI.itemId) return;
  try {
    const d = await api(`/api/imports/${AI.itemId}/chat`);
    // 会话列表要一直记着：面板顶部的"会话已保存"点开就是它（见 aiSessionListHtml）。
    // 之前这个返回值里的 sessions 被丢掉了，于是"换会话"无从谈起。
    AI.sessions = (d?.sessions || []).map((s) => ({
      id: s.id, title: s.title || "", turnCount: s.turnCount || 0, updatedAt: s.updatedAt || "",
    }));
    if (d?.activeSessionId) AI.sessionId = d.activeSessionId;
    const msgs = aiMsgsFrom(d?.messages);
    if (msgs.length) AI.messages = msgs;
  } catch { /* 没有历史不是错误 */ }
}

/** 切到指定会话：**只换消息，不新建**。
    与服务端的分工：服务端会按 sessionId 校验归属（SESSION_BOOK_MISMATCH），
    所以这里不做归属判断 —— 判断只能有一处，多了必然漂移。 */
async function aiSwitchSession(sid) {
  if (!sid || !AI.itemId || AI.busy) return;
  try {
    const d = await api(`/api/imports/${AI.itemId}/chat/${encodeURIComponent(sid)}`);
    AI.sessionId = sid;
    AI.messages = aiMsgsFrom(d?.messages);
    AI.lastCites = [];
    AI.sel = ""; aiSyncSel();
    AI.sessionListOpen = false;
    aiRenderPanel();
  } catch (e) {
    toast(e?.message || t("Could not load that chat"));
  }
}

/** 会话列表（折叠在"会话已保存"那个徽标里）。
    为什么要这个：同一个阅读项下会有多个会话（换一次话题就是一个），
    而面板一次只显示一个 —— 没有切换入口的话，看起来就像"别的记录丢了"。
    这正是用户报障时的体感（实测他的库里 itemId=2 下有 4 个会话）。 */
function aiSessionListHtml() {
  const list = AI.sessions || [];
  if (!list.length) return `<div class="ai-sess-empty">${esc(t("No saved chats yet"))}</div>`;
  return list.map((s) => {
    const cur = s.id === AI.sessionId;
    const when = (s.updatedAt || "").replace("T", " ").slice(5, 16);
    return `<button class="ai-sess${cur ? " on" : ""}" data-act="ai-switch" data-sid="${esc(s.id)}"
      title="${esc(s.title || s.id)}">
      <span class="t">${esc(s.title || t("(untitled)"))}</span>
      <span class="m">${s.turnCount} ${esc(t("turns"))} · ${esc(when)}</span>
    </button>`;
  }).join("");
}

/* 删除这本书的聊天记录。
   契约要点（§4.5 + AiReading.cs:446）：DELETE 必须带 `?yes=1`，否则 400 CONFIRM_REQUIRED
   ——「不可恢复」的动作不能靠一个手滑的点击完成。所以：先弹确认，再带上 yes=1。
   删除的是**当前会话**；没有当前会话才退化成清空整本（服务端另有 DELETE /chat 端点）。
   删完**重新拉一次**服务端状态，而不是只清本地：这样界面上剩几条就是真的剩几条。 */
async function aiDelete() {
  if (!AI.itemId || AI.deleting) return;
  const sid = AI.sessionId;
  const what = sid ? t("this chat") : t("all chats for this book");
  if (!window.confirm(`${t("Delete")} ${what}？\n${t("This cannot be undone.")}`)) return;
  AI.deleting = true;
  const btn = $("aiDel"); if (btn) btn.disabled = true;
  try {
    const url = sid
      ? `/api/imports/${AI.itemId}/chat/${encodeURIComponent(sid)}?yes=1`
      : `/api/imports/${AI.itemId}/chat?yes=1`;
    await api(url, { method: "DELETE" });
    AI.messages = []; AI.sessionId = null; AI.lastCites = []; AI.sel = ""; aiSyncSel();
    await aiLoadHistory();          // 以服务端为准（可能还有别的会话）
    aiRenderPanel();
    toast(t("Chat history deleted"));
  } catch (e) {
    // 403 = 挡位 ≥2 被孟思琳拦下；把服务端的话原样带出来，别自己编一句
    toast(e?.message || t("Could not delete the chat history"));
    if (btn) btn.disabled = false;
  } finally { AI.deleting = false; }
}

function aiRenderPanel() {
  const meta = $("aiMeta"), msgs = $("aiMsgs"), hint = $("aiHint");
  const title = aiTitleOf();
  if ($("aiTitle")) $("aiTitle").textContent = title || t("AI reading assistant");
  if ($("aiPos")) $("aiPos").textContent = aiWhereNow();

  if (meta) {
    const bits = [];
    // 载体徽标：文章/EPUB/PDF 三种。§12.1-A40① 之后文章也能问，所以这三个不是装饰 ——
    // 它决定了回答里的"引用"会指向章节、页码，还是只是一篇文章。
    const kind = state.view === "ebook" ? (state.ebookMeta?.isPdf ? "PDF" : "EPUB") : "文章";
    bits.push(`<span class="b">${esc(kind)}</span>`);
    bits.push(`<span class="b acc">${esc(aiWhereNow())}</span>`);
    // 会话徽标是个**按钮**：点开就是会话列表（只有一个会话时不点也不碍事）
    if (AI.sessionId) {
      const n = (AI.sessions || []).length;
      bits.push(`<button class="b ai-sessbtn${AI.sessionListOpen ? " on" : ""}" data-act="ai-sess-toggle"
        title="${esc(t("Switch chat"))}">${esc(t("chat saved"))}${n > 1 ? ` (${n})` : ""} ▾</button>`);
    }
    meta.innerHTML = bits.join("");
  }

  // 会话列表：展开时插在消息区之上
  const sessBox = $("aiSess");
  if (sessBox) {
    const show = !!AI.sessionListOpen && (AI.sessions || []).length > 0;
    sessBox.hidden = !show;
    if (show) sessBox.innerHTML = aiSessionListHtml();
  }

  if (msgs) {
    if (!AI.messages.length) {
      msgs.innerHTML = `<div class="ai-empty">
        ${esc(t("Ask away: the whole book and the current chapter are both fair game."))}<br />
        ${esc(t("Or select a passage first — the ball brings it in for you."))}<br />
        ${esc(t("Citations are clickable and jump straight to the chapter."))}</div>`;
    } else {
      msgs.innerHTML = AI.messages.map((m, i) => aiMsgHtml(m, i)).join("");
      msgs.scrollTop = msgs.scrollHeight;
    }
  }
  if (hint) hint.textContent = t("Enter to send · Shift+Enter for a new line · citations are clickable");
  const ta = $("aiQ"), send = $("aiSend");
  if (ta) ta.placeholder = t("Ask about this passage… (selecting text first fills it in)");
  if (send) send.disabled = AI.busy;
  // 没有会话就没有"这条记录"可删 —— 别给一个点了报错的按钮
  const del = $("aiDel");
  if (del) del.disabled = AI.busy || AI.deleting || !AI.sessionId;
}

function aiMsgHtml(m, idx) {
  if (m.role === "user") {
    return `<div class="ai-m u">
      ${m.sel ? `<div class="ai-why">引用：${esc(m.sel.slice(0, 46))}${m.sel.length > 46 ? "…" : ""}</div>` : ""}
      <div class="ai-q">${esc(m.text)}</div></div>`;
  }
  const cites = (m.cites || []).filter((c) => c && (c.chapterId || c.page));
  return `<div class="ai-m" data-mi="${idx}">
    <div class="ai-a">${aiMd(m.text)}${m.typing ? '<span class="ai-cur"></span>' : ""}</div>
    ${cites.length && !m.typing ? `<div class="ai-cites">${cites.map((c) =>
      `<button class="ai-chip" data-act="ai-cite" data-id="${esc(c.chapterId || "")}" data-page="${c.page || 0}"
        title="${esc(c.title || c.label || "")}">${esc(c.label || c.title || c.chapterId || ("第 " + c.page + " 页"))}</button>`).join("")}</div>` : ""}
    ${m.snapshot && !m.typing ? aiWhyHtml(m.snapshot, m.persisted) : ""}
  </div>`;
}

/** 检索详情：默认收起，但**必须存在** —— 回答不准时要能区分
    "没检索到"和"检索到了但答歪了"（契约 §5 的可解释性要求）。 */
function aiWhyHtml(s, persisted) {
  const layers = (s.layers || []).join(" → ") || "—";
  const deg = (s.degraded || []).length ? `\n降级：${(s.degraded || []).join(", ")}` : "";
  const info = `层：${layers}\n预算：${s.usedTokens || 0}/${s.budgetTokens || 0} token`
    + (s.chapterIdUsed ? `\n本章：${s.chapterIdUsed}` : "")
    + `\n书内命中：${(s.bookHits || []).length} · 全库命中：${(s.libraryHits || []).length}`
    + (s.vectorModel ? `\n向量模型：${s.vectorModel}` : "")
    + deg
    + (persisted === false ? "\n⚠ 这轮对话没有存进历史" : "");
  return `<details class="ai-why"><summary>检索详情</summary><pre>${esc(info)}</pre></details>`;
}

/** 极小的 Markdown：只认 **粗体** 与 `> 引用`。
    先转义再套标签 —— 模型输出不可信，绝不能让它的 < > 变成 HTML。 */
function aiInline(s) { return s.replace(/\*\*(.+?)\*\*/g, "<b>$1</b>"); }
function aiMd(src) {
  return String(src || "").split(/\n{2,}/).map((block) => {
    const lines = block.split("\n");
    if (lines.length && lines.every((l) => /^>\s?/.test(l))) {
      return `<blockquote>${lines.map((l) => aiInline(esc(l.replace(/^>\s?/, "")))).join("<br />")}</blockquote>`;
    }
    return `<p>${lines.map((l) => aiInline(esc(l))).join("<br />")}</p>`;
  }).join("");
}

/* ───────── 发送 + SSE 流式 ───────── */
async function aiSend() {
  if (AI.busy || !AI.itemId) return;
  const ta = $("aiQ");
  const q = (ta?.value || "").trim() || (AI.sel ? "这段在讲什么？" : "");
  if (!q) { toast(t("Type a question first")); return; }
  // ⚠ 这里**故意不做**"文章有没有正文"的前端预检。
  // 曾经做过一次，猜的字段名（content/body/fulltext）跟接口实际的 `bodyHtml` 对不上，
  // 于是把有正文的文章判成"没有可读正文"，白白拦住了一个好问题 —— 而服务端自己
  // 的判据是对的（`Items.Content` 为空才回 409 `ARTICLE_NO_TEXT`，见 AiReading.cs）。
  // 教训：同一件事的判据**只能有一份**。前端猜一份 = 迟早漂移，且漂移时症状是
  // "明明有正文却问不了"，比不做预检更糟。判据归服务端，前端只负责把服务端的话显示好。

  const selSnapshot = AI.sel;          // ⚠ 划词只活这一轮
  AI.sel = ""; aiSyncSel();
  if (ta) ta.value = "";
  AI.busy = true;

  const um = { role: "user", text: q, sel: selSnapshot };
  AI.messages.push(um);
  const am = { role: "assistant", text: "", cites: [], snapshot: null, typing: true };
  AI.messages.push(am);
  aiRenderPanel();
  const orb = $("aiOrb"); orb?.classList.remove("has", "pulse");

  // anchor：把"我现在读到哪儿 + 我划了哪段"一起带给服务端（契约 §5.1）。
  // 没有划词、也不在电子书里 → 不带锚点（服务端会退化成整篇/整库检索）。
  //
  // ⚠ 必须把 selSnapshot **显式传进去**：AI.sel 在上面已经被清空（那是"划词只活一轮"的
  // 实现方式，也是面板里引用框消失的原因），而 aiBuildAnchor() 读的正是 AI.sel ——
  // 于是它算出来的锚点里 selection 恒为空。曾经的症状：**面板里明明显示着引用，
  // AI 却回"本轮划词段为空"**（引用框走的是 um.sel 这条渲染路径，与发出去的锚点不是同一条）。
  const anchor = aiBuildAnchor(selSnapshot);
  const body = { question: q, sessionId: AI.sessionId || undefined, library: false };
  // 带锚点的条件里要有 `#`：A45 之后 chapterId 是 `#<节序号>`（不是章名），
  // 漏了它会让"当前节"这条线索根本不发出去 —— 那正是 A45 要修的症状。
  if (selSnapshot || (anchor && (anchor.chapterId || anchor.sectionLead || anchor.locType === "page"))) body.anchor = anchor;

  try {
    const res = await fetch(`/api/imports/${AI.itemId}/ask`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "Accept": "text/event-stream" },
      body: JSON.stringify(body),
    });

    if (!res.ok || !(res.headers.get("Content-Type") || "").includes("text/event-stream")) {
      // 挡位 403 / 没配 AI / body 过大 —— 服务端在**开流之前**回 JSON，
      // 所以这里读得到结构化的错误（不会出现"半截流"）。
      let err = null; try { err = (await res.json())?.error; } catch { }
      aiFail(am, err?.message || res.statusText || "请求失败", err?.code);
      return;
    }

    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      // SSE 帧以空行分隔
      let i;
      while ((i = buf.indexOf("\n\n")) >= 0) {
        const frame = buf.slice(0, i); buf = buf.slice(i + 2);
        aiHandleFrame(frame, am);
      }
    }
    aiFinish(am);
  } catch (e) {
    aiFail(am, e.message || "网络错误");
  }
}

function aiHandleFrame(frame, am) {
  let ev = "message", data = "";
  frame.split("\n").forEach((line) => {
    if (line.startsWith("event:")) ev = line.slice(6).trim();
    else if (line.startsWith("data:")) data += line.slice(5).trim();
  });
  if (!data) return;
  let obj = null; try { obj = JSON.parse(data); } catch { return; }

  if (ev === "session") {
    AI.sessionId = obj.sessionId || AI.sessionId;
  } else if (ev === "delta") {
    am.text += obj.text || "";
    aiPatchStreaming(am);          // 逐字：只改最后一屏，不整块重渲染
  } else if (ev === "cites") {
    am.cites = obj.cites || [];
    AI.lastCites = am.cites;
  } else if (ev === "done") {
    AI.persisted = obj.persisted;
    am.persisted = obj.persisted;
  } else if (ev === "error") {
    am.errCode = obj.code || "";
    if (obj.message) am.text += `\n\n（${obj.message}）`;
  }
}

/** 流式过程中只更新最后一条助手消息的正文 —— 整块重渲染会闪、也会打断滚动。 */
function aiPatchStreaming(am) {
  const box = $("aiMsgs");
  if (!box) return;
  const el = box.querySelector(`.ai-m[data-mi="${AI.messages.length - 1}"] .ai-a`);
  if (el) { el.innerHTML = aiMd(am.text) + '<span class="ai-cur"></span>'; box.scrollTop = box.scrollHeight; }
}

function aiFinish(am) {
  am.typing = false;
  AI.busy = false;
  aiRenderPanel();
  if (am.persisted === false) toast("回答出来了，但这轮对话没存进历史");
}

function aiFail(am, msg, code) {
  am.typing = false;
  AI.busy = false;
  // 错误码要翻译成人能读懂、且知道下一步做什么的话（契约 §5.5 / §8.3）。
  // 文案走 t()：键=英文原文，缺键时回落成英文原文而不是空白。
  const HINT_KEY = {
    SIMON_BLOCKED: "Simon blocked this web write (question history is written to disk) — lower the level from a real terminal first.",
    AI_NOT_CONFIGURED: "AI is not configured yet — set it up from the AI panel, or run sip --init in a real terminal.",
    SSE_BUSY: "Too many questions at once — try again in a moment.",
    EMPTY_QUERY: "先写个问题",
    ITEM_NOT_FOUND: "This item is not in the library.",
    SESSION_BOOK_MISMATCH: "That chat belongs to another book — a new one was started.",
    // 契约 §12.1-A40① 新增的两个：它们以前会掉进"显示原始 message"那条路，
    // 而原始 message 是英文字面量，用户看到的就是一句没法行动的英文。
    ARTICLE_NO_TEXT: "This article has no readable text — the feed only gave a title or summary.",
    API_KEY_INVALID: "The AI endpoint refused the key (401) — check the key and endpoint in the AI panel.",
    MODEL_UNAVAILABLE: "The AI endpoint could not be reached — check the endpoint address in the AI panel.",
  }[code];
  const hint = HINT_KEY ? t(HINT_KEY) : "";
  if (!am.text) am.text = hint || msg;
  else am.text += `\n\n（${hint || msg}）`;
  if (code === "SESSION_BOOK_MISMATCH") AI.sessionId = null;
  aiRenderPanel();
}

/* ───────── AI 配置面板（契约 §12.1-A40②）─────────
   三级结构对应契约里的三条硬要求：
     1. 开关关着（默认）→ 只显示"去终端配"的说明，不给一个点了就报错的表单
     2. 开着 → 表单；**key 永不回显**，留空 = 不改，输入 = 覆盖
     3. 改端点要二次确认 —— 它决定"你的问题和文段被发到哪儿"
   注意 AI.cfgYesOnce：确认只对**那一次提交**有效，不留在任何地方。 */
async function aiCfgOpen() {
  const box = $("aiCfgBox"), body = $("aiCfgBody"), panel = $("aiPanel");
  if (!box || !body) return;
  panel?.classList.add("on");
  box.hidden = false;
  if ($("aiCfgTitle")) $("aiCfgTitle").textContent = t("AI settings");
  body.innerHTML = `<div class="ai-empty">${esc(t("Loading…"))}</div>`;
  try {
    const d = await api("/api/ai/config");
    AI.cfg = d || {};
    aiCfgRender();
  } catch (e) {
    body.innerHTML = `<div class="ai-empty">${esc(t("Could not read the AI settings"))}：${esc(e.message || "")}</div>`;
  }
}

function aiCfgClose() {
  const box = $("aiCfgBox");
  if (box) box.hidden = true;
}

function aiCfgRender() {
  const body = $("aiCfgBody");
  if (!body) return;
  const c = AI.cfg || {};
  const on = !!c.webWriteEnabled;

  // ① 开关关着：说清"为什么"和"怎么打开"，而不是禁用一堆输入框
  if (!on) {
    body.innerHTML = `
      <div class="ai-cfg-warn">${esc(t("Configuring AI from the web is turned off."))}</div>
      <div class="ai-f"><div class="sub">${esc(t("Turn it on in sip_settings.json (AiConfigWebWrite: true), or run sip --init in a real terminal."))}</div></div>
      <div class="ai-f"><label>${esc(t("Current LLM endpoint"))}</label>
        <input type="text" value="${esc(c.llm?.apiEndpoint || "")}" readonly /></div>
      <div class="ai-f"><label>${esc(t("Current model"))}</label>
        <input type="text" value="${esc(c.llm?.model || "")}" readonly /></div>
      <div class="ai-f"><label>${esc(t("API key"))}</label>
        <input type="text" value="${esc(c.llmApiKeySet ? t("set") : t("not set"))}" readonly /></div>`;
    return;
  }

  const local = !!c.llmEndpointIsLocal;
  body.innerHTML = `
    <div class="ai-f">
      <label>${esc(t("LLM endpoint"))}</label>
      <input type="text" id="cfgLlmEp" value="${esc(c.llm?.apiEndpoint || "")}" placeholder="https://api.deepseek.com/v1" />
      <div class="sub">${esc(t("Your questions and the passages they quote are sent to this address."))}${local ? " " + esc(t("(looks local)")) : ""}</div>
    </div>
    <div class="ai-f">
      <label>${esc(t("Model"))}</label>
      <input type="text" id="cfgLlmModel" value="${esc(c.llm?.model || "")}" placeholder="deepseek-chat" />
    </div>
    <div class="ai-f">
      <label>${esc(t("API key"))}</label>
      <input type="password" id="cfgLlmKey" value="" autocomplete="off"
        placeholder="${c.llmApiKeySet ? esc(t("(already set — leave empty to keep it)")) : esc(t("paste your key"))}" />
      <div class="sub">${esc(t("Stored in the OS credential store. It is never sent back to this page."))}</div>
    </div>
    <div class="ai-f">
      <label>${esc(t("Embedding endpoint"))} <span style="color:var(--faint)">${esc(t("(optional — needed for semantic search)"))}</span></label>
      <input type="text" id="cfgEmbEp" value="${esc(c.embedding?.apiEndpoint || "")}" placeholder="http://localhost:11434/v1" />
    </div>
    <div class="ai-f">
      <label>${esc(t("Embedding model"))}</label>
      <input type="text" id="cfgEmbModel" value="${esc(c.embedding?.model || "")}" placeholder="nomic-embed-text" />
    </div>
    <div class="ai-cfg-acts">
      <button class="btn pri" data-act="ai-cfg-save" id="cfgSave">${esc(t("Save"))}</button>
    </div>
    <div class="ai-f" style="margin-top:12px">
      <div class="sub" id="cfgMsg"></div>
    </div>`;
}

async function aiCfgSave(btn) {
  const c = AI.cfg || {};
  const v = (id) => ($(id)?.value ?? "").trim();
  const payload = {};
  const newLlmEp = v("cfgLlmEp"), newEmbEp = v("cfgEmbEp");
  const newLlmModel = v("cfgLlmModel"), newEmbModel = v("cfgEmbModel"), newKey = v("cfgLlmKey");
  if (newLlmEp && newLlmEp !== (c.llm?.apiEndpoint || "")) payload.llmEndpoint = newLlmEp;
  if (newEmbEp && newEmbEp !== (c.embedding?.apiEndpoint || "")) payload.embeddingEndpoint = newEmbEp;
  if (newLlmModel && newLlmModel !== (c.llm?.model || "")) payload.llmModel = newLlmModel;
  if (newEmbModel && newEmbModel !== (c.embedding?.model || "")) payload.embeddingModel = newEmbModel;
  if (newKey) payload.llmApiKey = newKey;

  const msg = $("cfgMsg");
  if (!Object.keys(payload).length) { if (msg) msg.textContent = t("nothing to change"); return; }

  // ③ 改端点要确认 —— 这是"你的问题和文段会被发到哪儿"的那个决定，不能靠一次手滑完成
  const touchesEndpoint = !!(payload.llmEndpoint || payload.embeddingEndpoint);
  if (touchesEndpoint) {
    const to = payload.llmEndpoint || payload.embeddingEndpoint;
    const ok = window.confirm(
      `${t("Changing the endpoint sends your questions and the text they quote to that address.")}\n\n${to}\n\n${t("Continue?")}`);
    if (!ok) return;
  }

  setBusy(btn, true, t("Saving…"));
  try {
    // ?yes=1 只在"这一次确实改了端点"时带 —— 与服务端 CONFIRM_REQUIRED 对齐
    const url = "/api/ai/config" + (touchesEndpoint ? "?yes=1" : "");
    const r = await api(url, { method: "POST", body: JSON.stringify(payload) });
    const done = (r?.changed || []).length;
    toast(done ? t("Saved") : t("nothing to change"));
    // key 输入框必须清掉：留着就等于把它挂在 DOM 上（下一个截图/插件就能读到）
    if ($("cfgLlmKey")) $("cfgLlmKey").value = "";
    await aiCfgOpen();          // 重新拉一次，以服务端为准
  } catch (e) {
    if (msg) msg.textContent = e.message || t("Could not save the AI settings");
    setBusy(btn, false);
  }
}

/* ───────── 引用跳转 ───────── */
/** 点引用胶囊 → 跳到对应章节/页码。**跳不过去就不该出现这个胶囊**（契约 I4），
    所以这里遇到找不到的情况要明确说一句，而不是静静地什么都不发生。 */
async function aiJumpTo(chapterId, page) {
  if (!AI.itemId) return;
  if (page > 0) {
    // PDF：走既有的分页器（它按 `p<N>` / 页码跳）
    if (state.view !== "ebook") { await openEbook(AI.itemId); }
    state.ebookPage = page; ebookGo(page);
    return;
  }
  if (!chapterId) return;
  try {
    if (state.view !== "ebook") await openEbook(AI.itemId);
    const d = await api(`/api/imports/${AI.itemId}/chapters/${encodeURIComponent(chapterId)}`);
    // 章节正文拿到手 → 用它替换当前节的显示，并把位置记进阅读进度
    const html = d?.html || d?.bodyHtml || "";
    if (!html) { toast(t("That chapter no longer exists.")); return; }
    state.ebookText = { ...(state.ebookText || {}), itemId: AI.itemId,
      sections: [{ title: d.title || chapterId, html }], section: 0 };
    saveReadingPosition(AI.itemId, 0);
    render();
    toast(d.title ? `已跳到「${d.title}」` : "已跳转");
  } catch (e) {
    toast(e.code === "CHAPTER_NOT_FOUND" ? t("That chapter no longer exists.") : (e.message || "跳转失败"));
  }
}

