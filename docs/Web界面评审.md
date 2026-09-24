# sip 内嵌 Web 界面评审

> 评审对象：`WebServer.cs`、`WebAuth.cs`、`web/index.html`、`web/login.html`（以及为接入 Web 而改动的 `simon.cs` / `sip.csproj` / `sipcore.cs` / `languages/*`）
> ⚠️ 2026-09-12：`WebServer.cs` + `WebAuth.cs` 已合并为 **`Web.cs`**（Web 的服务端全在这一个文件里）。本文保留评审当时的状态与行号。
> 评审日期：2026-09-12
> 状态：**第一～七节为评审当天原貌（仅评审、未实测）**；实际落地与运行时实测见文末「八、落地与实测」

---

## 一、评审范围与方法

**通读全文**：`WebServer.cs`(1132 行)、`WebAuth.cs`(468 行)、`web/login.html`(451 行)、`sip.csproj`、`README.md`、`CHANGELOG.md`
**关键路径精读 + 全量检索**：`web/index.html`(1693 行，精读全部 API 调用点与 `innerHTML` 注入点)、`simon.cs`、`sipcore.cs` 的 schema/迁移/清洗相关段落
**编译验证**：`dotnet build sip.csproj` → **0 error**，13 个 warning 全部为既有的 Terminal.Gui 弃用告警（与本次改动无关）
**端到端 HTTP 冒烟**：尝试启动 `sip.exe --start` 实测，但无头后台会话中 `HttpListener` 绑定失败（"句柄无效"），**HTTP 层未实测**。下文所有结论均来自静态阅读与编译，不含运行时实测。

---

## 二、现状总览

`web/` 目前是**高保真设计稿 + 部分接真后端**的混合体，同一条渲染路径里真假两套数据并存。

| 视图 | 状态 | 说明 |
|---|---|---|
| 今日哈汤 | 部分接真 | 列表条目取自 `/api/today`，但目标数/已读数/统计卡写死 |
| 订阅源列表、源详情 | 接真 | `/api/feeds`、`/api/feeds/{id}/articles` |
| 文章阅读 | 部分接真 | 正文接真，头部元信息（日期/质量/摘要）写死 |
| 收藏 | 接真 | `/api/likes`、`/api/articles/{id}/like` |
| 搜索（全文/语义） | 接真 | `/api/grep`、`/api/search` |
| **改稿追踪** | **纯假数据** | 后端 `/versions`、`/diff` 已就绪却未接 |
| 跨源去重 | 纯假数据 | 后端无对应路由 |
| 源规则 | 纯假数据 | 后端无对应路由 |
| 阅读报告 | 纯假数据 | 后端无对应路由 |
| 本地导入 | 纯假数据 | 后端无对应路由 |
| 电子书阅读 | 纯假数据 | 后端无对应路由 |

后端 `WebServer.cs` 已实现的接口中，**`/api/articles/{id}/versions` 与 `/api/articles/{id}/diff`（含 DiffPlex 行级 diff）完全就绪但界面未调用**——「改稿追踪」是本项目最核心的差异化能力，也是唯一"后端已备好却空着"的功能。

---

## 三、必修问题

### P0 — 安全

#### 1. 存储型 XSS：订阅源正文原样注入 DOM

**位置**：`web/index.html:1313`

```js
prose.innerHTML = isHtml ? body : `<p>${esc(body)}…</p>`;
```

`body` 取自 `/api/articles/{id}` 的 `content` / `fulltext`，而 `Items.Content` 是 RSS 原文**原样入库**的 HTML。全代码库**不存在任何 HTML sanitizer**：

- `HtmlAgilityPack` 仅用于 `HtmlToMarkdown` 转换（`sipcore.cs:3560-3736`），服务于 TUI 显示
- `StripControlChars`（`sipcore.cs:3743-3754`）只在 **CLI 打印**与 **TUI markdown 生成**时调用，属于**输出期**防护，入库不经过它
- 已检索确认：无 `Sanitize`、无标签白名单、无 `script/style` 剥离入库逻辑

**影响**：任意订阅源（或订阅源中引用的任何第三方内容）可在本机网页面内执行任意 JS。配合已认证的会话 cookie，脚本可读取整个订阅库、调用写接口删除订阅源、导出数据。

**这是 Web 层新引入的攻击面**：TUI 天然免疫（终端只把文本画成单元格），项目已有 `tests/Sip.Tests/TerminalInjectionTests.cs` 专门守护这类"呈现层注入"，**Web 侧缺对应防线**。

#### 2. 搜索高亮把文本当 HTML 重新解析

**位置**：`web/index.html:1029`

```js
span.innerHTML = node.nodeValue.replace(re, m => `<mark class="hit">${m}</mark>`);
```

`highlightIn()` 遍历文本节点做关键词高亮，却把**文本内容当 HTML 字符串重新解析**。后果有二：

- 正文中出现字面文本 `<img src=x onerror=…>`（对 RSS 完全合法）时会被激活为真实元素 → 又一条 XSS 路径
- 正文中的 `<`、`&` 等字符被 HTML 解析器吞掉或转义，**高亮后正文显示错乱**

正确做法是用 `document.createTextNode` + `document.createElement("mark")` 构建，而非 `innerHTML`。

#### 3. 孟思琳挡位 3 的契约被绕开

**位置**：`simon.cs:114-118`

```csharp
// --start / webpass / aikey：本机 Web 与凭据管理，不依赖 TUI
bool isCredAdmin = cmd is "webpass" or "aikey" or "--start";
bool isSimonStatus = cmd == "simon" && (sub is "" or "status" or "show" or "list" or "--json");
if (isCredAdmin || isSimonStatus)
    return null;                    // ← 在 level >= 3 判断之前放行
if (level >= 3)
    return Lang.T("挡位 3(极致):CLI 调用已全部拒绝({0});只允许通过 TUI 使用。", cmd);
```

该豁免插在 `level >= 3` 判断**之前**，而紧邻其上的 `simon.cs:106-108` 正是它要维护的契约：

> 挡位 2 = CLI 写操作一律拒绝；挡位 3 = CLI 所有调用一律拒绝。
> **CLI 本身（含交互终端）是不可信通道**；TUI 命令栏不经此检查，永远是真人通道。

即：设计意图是**只有真人 TUI 通道可信，CLI 一律视为不可信**。新增的 `--start` 恰恰是一条**不需要真实 TTY** 的 CLI 通道（`FirstRunWebPasswordSetup()` 在非交互环境直接 return），等于在挡位 3 下重新打开了一条被判定为"不可信"的通道。

**可复现的读取路径**（挡位 3 下）：

1. 本机任意进程执行 `sip --start`（无 TTY 要求，放行）
2. 若未设 Web 密码（用户当初跳过了，或挡位较低时执行过 `webpass clear`）→ 服务以无密码模式启动
3. `GET /` → 响应下发 `sip_local` cookie（`WebServer.cs:135-145`、`WebAuth.cs:193-202`）
4. 携带该 cookie 请求 `/api/feeds`、`/api/articles/{id}` → **读走整个订阅库**

> **2026-09-12 更新**：挡位 3 的数据加密（SQLCipher + AES）已整体移除，故本条原先引用的「加密全部数据、其他软件读不到你的数据」这一承诺**已不存在**。
> 但下面的路径**依然成立且危害不变**——它原本就绕过了加密（经 HTTP 读明文，根本不碰数据库文件），
> 所以移除加密既没有修复它，也没有加重它。结论改为：挡位 3 下，本机任意进程仍可经 `GET /` 拿到会话 cookie 读走整个订阅库；
> 这正是"回环地址不是鉴权边界"的直接体现（见 `docs/` 设计讨论：安全门是减少暴露面，不是权限边界）。

**写操作本身是安全的**：`WebWriteAllowed()`（`WebServer.cs:233-254`）确实复用 CLI 同一套挡位语义（≥2 拦破坏性写、≥3 拦全部写），并 `SimonRecord("blocked_cmd", …)` 留痕。**缺口仅在"读"**。

**附带问题**：`WebWriteAllowed` 的拒绝文案为 `"Simon blocked this web write (level {0}). Use TUI or lower the level."`——在挡位 3 下建议用户"改用 TUI"，而此刻 TUI 通往 Web 的路已被 `--start` 打开，提示语与威胁模型自相矛盾。

---

### P1 — 界面功能性缺陷

#### 4. `renderFeedSidebar` 未定义，导致每次数据加载都误报 API 不可用

**位置**：`web/index.html:799`（调用）、`782-821`（函数体）

```js
if(feeds?.feeds){
  FEEDS.length=0;
  …
  $("nFeeds").textContent=FEEDS.length;
  renderFeedSidebar?.();          // ← 该函数从未定义
}
…
}catch(e){
  toast("API 不可用（请用 sip --start 打开）");
}
```

`renderFeedSidebar` 在整个文件中**从未定义**（已全量检索确认）。关键点：**未声明的标识符配上可选链仍会抛 `ReferenceError`**——可选链只对"已声明但为 null/undefined"的值生效。因此：

- `/api/feeds` 成功返回（正常情况）→ 第 799 行抛 `ReferenceError` → 被 782 行的 `try` 捕获 → 误报 toast「API 不可用（请用 sip --start 打开）」
- **第 816 行的 `render()` 不会执行** → 界面不重绘

**用户可见后果**：添加订阅源、同步、归档、去归档、删除、收藏之后，列表**不刷新**，且每次都提示 API 不可用（而 API 其实是好的）。首屏之所以能正常显示，只是因为 `index.html:1689` 另有一条 `render()` 调用兜住了；`loadRealData()` 自身这条路径是坏的。

顺带说明：左侧栏 HTML（`index.html:283-307`）中**不存在订阅源列表容器**，该函数应是早期设计的残留——侧栏只有 today/feeds/likes/... 导航按钮，订阅源卡片实际渲染在主视图内。

#### 5. 登录页脚本中断，密码泄漏进 URL

**位置**：`web/login.html:415`

```js
$("quoteMore").addEventListener("click", loadQuote);
$("forgot").addEventListener("click", (e) => { … });   // ← #forgot 不存在
…
$("loginForm").addEventListener("submit", …);           // ← 因此从未注册
```

`login.html` 全文的 id 只有 `pass`、`loginForm`、`quote`、`quoteText`、`quoteAuthor`、`quoteMore`、`toast`，**没有 `forgot`**。于是第 415 行 `null.addEventListener` 抛 `TypeError`，脚本在此中断，**第 420 行起的登录提交处理器根本没被注册**。

**用户可见后果**：设了密码时，点「登录」走浏览器**原生表单提交**（无 `action`/`method` → `GET` 当前 URL）：

- 密码以明文出现在地址栏、浏览器历史、以及 `Referer` 头中
- 页面只是重新加载同页（服务端见未认证又发回登录页），**永远登不进去**

即：**在设置了 Web 密码的前提下，登录页当前完全不可用。**

#### 6. 语言文件永远 404，多语言形同虚设

**位置**：`web/index.html:697` 请求 `./languages/${code}.json`；`WebServer.cs` 路由

`HandleWebContext()` 只路由三类路径：`/`（与 `/index.html`）、`/login-bg.png`、`/api/*`。**没有任何静态文件路由**，因此 `/languages/zh-CN.json` 一律落到 404。

后果：`loadLangDict()` 永远走 catch 分支，永远回落到 `FALLBACK`（`index.html:676-691`，仅约 20 个键）。切换语言基本看不出效果，而界面文案却写着"词条来自 ./languages/xx.json（与 sip Lang.T 同键）"（`index.html:1598`），与实际行为不符。

注：`sip.csproj:40-52` 确实把 `languages/**/*.json` 复制到了输出/发布目录，但**服务端不提供这些文件**，所以磁盘上有也没用。

---

### P2 — 后端已就绪却仍用假数据

| # | 位置 | 问题 |
|---|---|---|
| 7 | `index.html:1475,1488` | 「改稿追踪」列表与详情全部读 `DIFF_LIST` 假数据；`openDiff`/`openVersion` 纯 mock。后端 `/versions`、`/diff` 已就绪 |
| 8 | `index.html:1214,1216-1218` | 今日哈汤写死「目标 5 篇 · 已读 0」与统计卡 `5/12/1`；后端已返回真实 `target`/`done`/`tracking` 却未使用 |
| 9 | `index.html:1301` | 阅读页写死一行「模拟摘要：…」；真实 `summary`/`author`/`published`/`quality` 已取到但未渲染 |
| 10 | `index.html:1291-1292` | 文章头部写死日期 `2026-09-10` 与质量徽章 `full` |
| 11 | `index.html:1271` | `ARTICLES[1]?.find(…)` 硬编码 feedId=1 查询收藏态，读其他源的文章时判断错误 |
| 12 | `index.html:795` | 后端 `/api/feeds` 不返回 `health`，`f.health\|\|"ok"` 使健康徽章**恒为绿** |
| 13 | `index.html:796` | 后端 `lastChecked` 是 ISO 时间串，直接显示为 `2026-09-11T21:30:00`，而假数据格式是「2 小时前」 |
| 14 | 全局 | 去重 / 源规则 / 阅读报告 / 本地导入 / 电子书阅读均为 mock，且后端**尚无** `/api/dedup`、`/api/insights`、`/api/policy`、`/api/import` 路由 |
| 15 | 全局 | 后端 `/api/logout`、`/api/auth/status` 已实现但界面未接——**没有登出入口** |
| 16 | 全局 | 左侧栏导航按钮 `data-v="dedup"` 等指向纯 mock 视图，但外观与接真视图无差别，用户无法分辨真假 |

#### 关于「已读状态」的事实澄清

界面若要实现阅读进度/已读标记，需注意**数据库层没有这个概念**：`Items` 表（`sipcore.cs:4144-4159`）只有 `Status`（`active/archived/deleted`）、`Version`、`ArchivedAt`，**没有 read/unread 列**。已读状态目前散落在三处：

- `reading_progress.json`：`itemId → 滚动位置`（`sipcore.cs:1183-1204`）
- `sip_today_cache.json` 的 `read[]` 数组：驱动今日哈汤的 `✓`（`sipcore.cs:1705-1755`）
- `telemetry.db`：`article_open`/`article_progress`/`article_complete`/`article_skip` 事件，**且遥测默认关闭**

Web 端要做阅读进度，需要新设计一个接口，不能指望现有字段。

---

### P3 — 杂项与工程卫生

| # | 问题 |
|---|---|
| 17 | `web/index.html.bak`（28 KB）是多余备份，`git add web/` 会一并提交 |
| 18 | `web/login-bg.png` 为 **1.6 MB**，经 `sip.csproj:33-35` 内嵌进程序集——单文件 exe 每次发布因此增大约 1.6 MB。对主打「下载一个 exe」的项目值得权衡（可外置 + 渐变兜底，或压缩到数百 KB） |
| 19 | **缺 CSP 响应头**。已有 `X-Content-Type-Options`/`Referrer-Policy`/`Cache-Control`（`WebServer.cs:89-91`），但一条严格 CSP 可直接掐死 P0 第 1、2 号问题，性价比极高 |
| 20 | `web/login.html:385` 引用外网 `https://v1.hitokoto.cn` 取一言——与「本地优先 / 不注册 / 不云端 / 遥测默认关」的项目调性冲突，且被 CSP 拦掉后即失效。建议改本地句子池 |
| 21 | 静态资源查找路径不一致：`LoadWebHtml()` 只找 `BaseDirectory/web/index.html`（`WebServer.cs:289`），而 `LoadLoginHtml()` 还额外找了 `dataDir/../web/login.html`（`WebServer.cs:303-307`）。开发态可能出现一个走内嵌、一个走磁盘的错配 |
| 22 | `web/index.html:257` 的登录壳带 `hidden` 属性且 `enterApp()` 再次隐藏，属于永不显示的残留标记 |
| 23 | `HandleLogin` 的失败限流计数器 `WebLoginFailCount` 是**进程级**且从不随时间衰减（仅在成功登录时清零），长期运行后可能误伤（`WebServer.cs:409-415`） |

---

## 四、已经做对的地方

为免只报问题，以下为本次评审确认**质量良好**的部分：

**编译与集成**
- `dotnet build` **0 error**；13 个 warning 全部是既有的 Terminal.Gui 弃用告警
- `--start` / `webpass` / `aikey` 的 CLI 分派、`SipSettings.WebHost/WebPort/WebSetupDone`、i18n 词条、csproj 内嵌资源均接线完整
- `WebServer.cs` 直读数据库与内部方法，**不 shell out CLI、不解析子进程 stdout**——设计取舍正确

**认证与凭据**
- PBKDF2-SHA256 / 100,000 次迭代 / 16 字节随机盐（`WebAuth.cs:100-119`）
- `CryptographicOperations.FixedTimeEquals` 定时安全比较（密码与会话 token 均是）
- 会话 cookie 为 HMAC-SHA256 签名的 `exp.MAC`，24 小时过期，`HttpOnly` + `SameSite=Lax`
- 会话密钥存系统凭据库，名带 `SimonScopeHash()` 实现**多副本隔离**；AI Key 亦按数据目录 scope 存储，并保留 legacy 全局名**只读回退**
- 忘记密码有逃生口（`web_auth.reset`），且该文件处理在启动链路中（`sipcore.cs:3110`）

**无密码模式并非"全开"**
- 必须先在浏览器打开 `/` 获取进程内 32 字节随机 `sip_local` cookie，纯 curl 扫描拿不到（`WebAuth.cs:173-213`）

**Web 写操作复用同一套挡位语义**
- `WebWriteAllowed()` 与 CLI 挡位语义一致，并 `SimonRecord` 留痕（`WebServer.cs:233-254`）

**CSRF 与路径防护**
- 非 GET/HEAD/OPTIONS 请求校验 `Origin`/`Referer` 必须回环（`WebServer.cs:379-391`）
- 路径含 `..` 或 `\` 直接 400（`WebServer.cs:97-101`）

**SQL 与 schema 一致性**
- WebServer 查询的 `Feeds.Schedule`、`Feeds.LastCheckedAt`、`Items.Summary`、`Items.PageCount` **均确认存在**（分别由 `sipcore.cs:4244`、`4246`、`4220`、`4249` 的迁移添加），不存在列缺失风险
- `FILTER (WHERE …)` 与 `ROW_NUMBER() OVER (…)` 需要 SQLite ≥3.30 / ≥3.25，Microsoft.Data.Sqlite 9.0 捆绑版本满足

**前端转义纪律（部分到位）**
- 搜索结果渲染全部正确使用了 `esc()`（`index.html:1126-1129`）——说明作者有转义意识，**仅正文那条路径漏了**

**优雅降级**
- `HttpListener` 绑定失败时打印原因与提示后 `SetExit()` 退出，不静默失败（`WebServer.cs:30-40`）
- 登录页在背景图缺失时回落到最小可用壳（`WebServer.cs:317-338`）

---

## 五、建议修复顺序

按"改动量 / 收益"排序：

1. **加 CSP 头** —— 一处改动（`WebServer.cs:89-91` 附近），可同时缓解 P0 第 1、2 号两条 XSS 路径
2. **正文 HTML 净化** —— 引入白名单 sanitizer（复用已有 HtmlAgilityPack），或在 WebServer 侧改为输出已转换的 Markdown/纯文本
3. **`highlightIn` 改用 DOM 构建**（`index.html:1019-1034`）
4. **删掉 `renderFeedSidebar?.()` 死调用**（`index.html:799`）—— 一行删除，直接修复"列表不刷新 + 误报 API 不可用"
5. **修登录页 `forgot`**（`login.html:415`）—— 删除该行或补上元素；顺带把外网一言换成本地句子池
6. **补 `/languages/*.json` 静态路由**（`WebServer.cs`），并在 CSP 中放行同源脚本/样式
7. **重审挡位 3 豁免**（`simon.cs:114-118`）—— 建议方向：`--start` 在挡位 3 下仍要求已设密码，或对无密码模式的 `sip_local` 下发增加一次性人工确认；同时修正 `WebWriteAllowed` 的提示文案
8. **接改稿追踪真接口**（`/versions`、`/diff` 已就绪，收益最大）
9. 清理 P2 各残留写死项、P3 工程卫生项

---

## 六、Web 功能对齐清单（规划用）

TUI 已具备、Web 端尚无对应能力的交互面（供后续排期参考）：

1. **两栏书库壳**：按源计数（`{n} current / {n} changed / {n} deleted`）、按需展开文章（懒加载）、同 `Guid` 仅取最新版、行标记 `♥ / 🤖 / ✎`、CJK 感知换行与截断、`article cur/total` 位置读数
2. **正文渲染等价物**：H1 标题、作者/日期/来源元信息块、`---` 分隔、Content-vs-Description 回退、AI 摘要概览模式、`## Fetched full text` 段、"摘要过短→请抓全文"引导、以及不可信内容的控制字符清洗
3. **阅读控制**：Content/Overview 切换、滚动与翻页两种模式、半页翻动、沉浸/全宽、侧栏折叠、按文章记忆滚动位置 + "跳回上次位置"
4. **链接处理**：正文内链接抽取成可导航列表、`[i/n]` 指示、仅允许 http/https
5. **版本与 diff**：按 `Guid` 的版本列表（含状态与归档时间）、查看历史版本全文、两版对比、去重并排比较与"保留 A / 保留 B / 放弃"
6. **今日哈汤**：5 篇、理由标签、分钟估算、批次计数、每日缓存、`✓` 已读、目标进度（依赖遥测）
7. **阅读报告**：按源卡片（活跃/积压/打开/读完/完成率/点赞/AI 调用/健康状态/理由）、30 天窗口、归档与删除动作
8. **源管理**：更新计划预设（含整点调整与自定义表达式）、归档/去归档、删除确认、添加源带进度日志、本地文件导入（txt/md/pdf/epub/docx）、OPML 导入导出
9. **AI / 配置**：init 向导、`index`/`reindex` 带进度、摘要、语义检索与全文 grep
10. **治理面**：遥测同意（默认关、显式开启）、全文抓取同意短语门禁、`purge-fulltext`、去重命令、`insights-interval`、源规则、onboarding、挡位调整（降挡确认）、运行时切换语言、快捷键帮助
11. **后台行为**：打开时同步到期源 + 每 15 分钟一次，带可见进度
12. **命令面板**：等价于 TUI 命令栏（命令名大小写不敏感 + 自由参数），结果在只读面板中呈现

**以下为终端专属，Web 无需对齐**：Sixel 整条链路（DCS 编码、像素→单元格换算、`ESC[2J` 清屏、`sip pic` 开关）；ANSI 配色角色与单元格换行；原始按键处理与 `StatusBar` 组件。浏览器只需普通 `<img>` + CSS 即可覆盖其**用途**（相对 URL 解析、预取、占位留白、导入电子书抑制图片）。

---

## 七、附：评审中确认的两个易误判点

1. **不要把 `Items` 缺少 `Summary`/`PageCount`、`Feeds` 缺少 `Schedule`/`LastCheckedAt` 当作 bug** —— 这些列由 `sipcore.cs:4220/4226/4244/4246/4249` 的 `ALTER TABLE` 迁移添加，WebServer 的查询是安全的。
2. **`languages/zh-CN.json` 中 `"Cancelled"` 词条并未丢失** —— 本次改动的 diff 删除的是一条**重复项**，规范词条仍在第 124 行。

---

## 八、2026-09-12 落地与实测（第 1 刀）

> 第一～七节保持评审当天的原貌（当时"仅评审、未实测"）。本节记录之后实际落地了什么，以及**运行时实测**的结果。

### 已完成
1. **正文净化上移到服务端**（原建议第 2 条，对应 P0 第 1 号）——`WebServer.cs` 新增 `ToSafeBodyHtml` / `SanitizeList` / `IsSafeUrl`：**标签白名单 + 属性白名单**。接口改为只返回 `data.bodyHtml`，`content`/`fulltext` 字段删除。
2. **`highlightIn` 改用 DOM 构建**（原建议第 3 条）：不再把文本节点当标记重新解析。
3. **删掉 `renderFeedSidebar?.()`**（原建议第 4 条）：未声明标识符即使带 `?.` 也会抛 `ReferenceError`，原先表现为"列表不刷新 + 误报 API 不可用"。
4. **引导令牌下发 + `X-Frame-Options: DENY` + 绑定感知的 Origin 校验**（原建议第 1 条的前置）：裸访问 `/` 只给说明页，令牌只存在于终端输出里。
5. **Agent 门默认关闭**：`simon.cs` 的 `AgentModeBlock`，判据是"有没有真交互终端"，只拦程序调用、不影响你自己的终端。
6. **登录页两处修复**（原建议第 5 条）：① 删掉对不存在的 `#forgot` 元素的 `addEventListener` —— 它抛的 `TypeError` 发生在注册登录表单**之前**，导致设了密码后**根本登不进去**（点「登录」无反应）；② 外网一言（`v1.hitokoto.cn`，首屏 + 每 20 秒一次）换成**本地句子池**，登录页不再有任何出网请求。

### 运行时实测（把评审时的欠账还上）
评审时写的是"无头后台会话中 `HttpListener` 绑定失败，HTTP 层未实测"。本次用真实二进制 + 真实 HTTP + 真实 cookie 流程实测：

| 项 | 实测结果 |
|---|---|
| 裸访问 `/` | 只得到说明页；`/api/feeds` 无 cookie → **401** |
| `/?t=<令牌>` | **302** + `Set-Cookie: sip_local=…; Path=/; HttpOnly; SameSite=Lax; Max-Age=2592000` |
| `sip -l`（程序调用） | **被拦**，`AGENT_BLOCKED`，退出码 3 |
| `agent_mode.json` = `{"Ok":true}` | **完全无效**（见下"一个被收回的设计"） |
| `GET /api/articles/{id}` 净化 | 见下表（21 条载荷） |

**载荷 → 输出**（均为实测，非推断）：

| 输入 | 输出 |
|---|---|
| `<script>alert()</script>`、`<iframe>`、`<svg onload>`、`<style>`、`<form><input><button>`、`<object>` | 连内容一起消失 |
| `<p onclick=…>` | 属性删掉，文字留下 |
| `<img src=x onerror=…>`、`<img src="/api/feeds">` | 属性删 + **节点整个去掉**（否则正文里撒一地破图） |
| `<a href="javascript:…">` | `href` 删掉，文字留下 |
| `<a href="http://ok.example/fine" onclick=…>` | 保留，并补 `rel="noopener noreferrer"` |
| `<div style="position:fixed">` | `style` 删掉，文字留下 |
| `<marquee>`（未知标签） | 脱壳，文字留下 |
| `<figure>`/`<figcaption>`/`<table>`/`colspan`/`<pre>`/`<code>` | 原样保留 |
| `&lt;script&gt;`、`&amp;amp;`、`&gt;` | 原样存活（作为文本） |

### 一个被收回的设计（值得单独记）
Agent 开关最初是"凭据库为主 + `agent_mode.json` 兜底"，理由是"凭据库写不进去时开关永远打不开"。实测发现**触发条件写错了**：代码实际是"凭据库没有值就信文件"，而"没有值"正是**全新安装的默认状态**——于是默认状态下任何程序写一个 JSON 文件就能把这道门打开，与"只有 `--agentok` 能开"直接矛盾。**一道能被它拦的对象自己打开的门只是装饰。** 兜底已删除：凭据库是唯一权威，读不到值 = 关；凭据库真坏掉时人照样能用 CLI/TUI，丢的只是"程序可以调用 sip"，属于收紧方向的失败，就该响亮地失败。

### 仍未做（下一刀）
- 7 组预览数据里 **「阅读报告」已接真**（`GET /api/insights`，复用 CLI 的同一个 `BuildInsights`），其余（改稿追踪 / 跨源去重 / 源规则 / 本地导入 / 电子书）**默认不再渲染**：`PREVIEW_MOCKS = false`，页面显示"这一页还没接入 Web 版" + 对应 CLI 命令。预览数据保留在源码里——`web/` 当时还没提交，删掉就等于把设计稿永久丢掉。
- **改稿追踪**仍未接后端的 `/versions`、`/diff`（后端已就绪，收益最大的一项，原建议第 8 条）。它同时卡着 P0-1：`ShowDiff` 缺 `FeedId` 条件会跨源误归档，接 Web 之前应先修这个。
- **CSP 尚未加**（原建议第 1 条）：`script-src 'self'` 要求先把 `index.html` 里的内联 `onclick=` 改成 `data-*` + `addEventListener`。
- 移动端响应式与 PWA（需要 HTTPS 或 Tailscale）。
