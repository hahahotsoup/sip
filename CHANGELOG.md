# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

> **一次导入一批书，PDF 能像纸质书那样摊开读。**

### Added

- **CLI 批量导入**：`sip --import a.pdf b.epub c.md` 一条命令吃完多个文件。
  - 单个文件的输出形状**逐字段不变**（`{success,id,title,file,feed}`）；多个文件才变成 `{success,counts,items[]}`，`items` 每本一行、带真实 id 与逐条 error。
  - 失败**不中断**后面的文件，退出码取最严重的那一个 —— 批量里 9 成 1 败必须非 0，否则 agent 会以为整批都进去了。
  - `--title` 只对单个文件成立，批量时当场拒绝（一次给 N 本书起同一个名字没有意义）。
  - 非 JSON 模式逐条打印 `Importing i/n: <文件>`；**JSON 模式一行都不打**（见 Fixed）。
- **PDF 对开双页**（`web/app.js` + `web/index.html`）：阅读器里新增「对开」开关（正文上方按钮，或按 `2`），左右并排两页。
  - 页码对齐：第 1 页当封面**单独一屏**，之后按 2-3 / 4-5 / 6-7 成对；`←` `→` 与「上一屏/下一屏」一次翻**一屏**（步长跟着模式走，不再是固定 ±1）。
  - 末页是奇数时右半页留一个**同宽空位**，有内容的左页不会跑到屏幕中央、翻到最后一屏也不会横跳。
  - 位置读数变成 `第 4-5 页`；选项记在本地偏好里，下次打开还是对开。
  - 窄屏（≤900px）自动降级成**纵向叠放**（并排只会让每页缩到看不清），翻页仍按跨页走。
- **Web 批量导入的总进度与失败清单**（`web/app.js`）：进度行写「导入中 3/12 · 成功 2 · 失败 1」，结束时逐条列出哪个文件、为什么失败（格式不支持 / 文件为空 / 太大 / 锁冲突），并附用时。

### Fixed

- **`--import --json` 的输出一直不是合法 JSON**：`ImportCli` 里那行 "Import done: …" 总结漏在了 `if (json)` 块的 else 分支之外，于是它紧跟在 JSON 后面打进 stdout，任何 `JSON.parse` 都会以 `'0xE5' is invalid after a single JSON value` 失败。README 早就写明「提示走 stderr，`--json` 的输出保持干净」，这行违背的正是那条契约。
- **批量导入的失败提示会「粘住」**（`web/app.js`）：原先只在失败时写那句红字、成功时不写，于是最后一个文件失败时它一直留在框里，看着像整批都失败了。现在每个文件都重写一次状态行，结束时给出成功/失败与失败清单。
- **在页码输入框里打字会误触新增的 `2` 快捷键**：输 `12` 跳页会顺手把对开模式切两次。加了 `typingInField()` 守卫（`TEXTAREA` / `SELECT` / `contenteditable` / 各类文字型 `INPUT`），数字与字母类快捷键先问一句「现在是不是在打字」。
  - 既有的 `F`（全屏）本来就有 `instanceof HTMLInputElement` 检查，不受影响 —— 所以没有改它，只是把重复的判据去掉了。

## [2.0.0] - 2026-09-25

> **Web 功能全部补齐。** 1.3.0 时内置 Web 只是"能读"：今日哈汤、订阅源、收藏、搜索、阅读报告接了真接口，
> 改稿追踪 / 跨源去重 / 源规则 / 本地导入 / 电子书 / 治理面要么是假数据、要么干脆没有入口。
> 这一版把这些全部接真，**并且顺手拆掉了那条假数据与 XSS 的隐患路径**（CSP + 外置脚本）。

### Added

- **改稿追踪接真**（`web/app.js` + `Web.cs`）。后端 `/api/articles/{id}/versions` 与 `/diff` 从 1.3.0 起就就绪，界面却一直读假数据；现在列表 + 逐行差异 + 历史版本正文全部来自你的库。
  - 新增 `GET /api/edits`：列出"有历史版本的文章"（按 (FeedId, Guid) 分组，`COUNT(*) > 1`）。
  - `GET /api/articles/{id}?version=N`：读**历史版本**的正文（TUI 里按 V 选一版的等价物）。返回里带 `version` / `versionCount` / `hasHistory` / `status`，界面据此决定要不要显示"改稿历史"。
  - `GET /api/articles/{id}/diff?from=vA&to=vB`：改成**明确比较指定的两版**，并回 `added` / `removed` / `titleChanged`。原先的实现（1.3.0）在"两个源转载同一篇"时会拿 B 源的 v1 去比 A 源的 v2 —— 因为版本号在两个源里会重复。
- **跨源去重接真**（`GET /api/dedup`、`POST /api/dedup/scan|hide|hide-cluster|undo`、`GET /api/dedup/diff`）。复用 CLI 的 `FindDuplicateClusters` / `HideAsDedup` / `UndoDedup` / `ListHiddenDedup`，不另写一套算法。
  - 对比用**段落级** diff（`NormalizeParagraphs` + DiffPlex），显示的就是算法实际在比的东西，而不是另一套"看起来像"的文本处理。
  - 隐藏**可撤销**（写 `dedup.json` + `Items.Status='dedup'`），界面上「已隐藏」列表每条都有撤销按钮。删除是不可逆的，不做。
  - 扫描加并发护栏：连点几下就是几倍的正文载入与段落比对（同"同步"按钮的道理）。
- **源规则接真**（`GET|POST /api/policies`、`DELETE /api/policies/{feedId}`）。`source_policy.json` 的五个动作（降频 / 归档 / 保留 / 标签 / 退订备注）都能在网页里增删。
  - 与 CLI **同一个校验器**：`lower_frequency` 走同一个 `TryParseSchedule`，网页收不下的表达式终端也读不懂，不如当场拒。
  - 「归档」直接调 CLI/TUI 用的 `AddTimestampForRealId`（给源标题加时间戳），「降频」调 `SetFeedSchedule` —— 不是"网页自己写一遍"。
  - `createdBy` 永远是 `user`：**AI 永不自动写规则**，网页也不破例。
- **本地导入 / 电子书接真**（`GET|POST /api/imports`、`GET /api/imports/{id}`、`/text`、`/asset`、`/page/{n}`、`DELETE /api/imports/{id}`）。
  - 上传走"字节 → 临时文件 → **CLI 同一条 `ImportFileCore`**"（`ImportCli` 也改成调它）。文件被**复制进** `readwithhotsoup/imported/`，原文件你随时可以删。
  - EPUB/DOCX/MOBI 抽出的图是 `file://` 绝对路径，而净化器只放行绝对 http(s) —— 原先这些图会被整个丢掉。现在服务端把 `<img src="file://…">` 改写成 `/api/imports/{id}/asset?path=…`，**该接口把路径围在 `imported/` 之内**（越界一律 403）。
  - PDF：`ReadPdfFile` 从来不解析文本（只写一句占位），所以网页也不假装能读 —— 改为按页栅格化（复用 `RenderPdfPages`，150 DPI，渲染过的页命中磁盘缓存），`/api/imports/{id}/page/{n}` 直接给 PNG。`?page=` 越界返回 404 而不是空白图。
  - 删除复用新抽出的 `ImportItemDelete`（CLI 的 `--import-rm` 也改调它）：删库行 + 删落地文件。
- **治理面接真**：
  - `GET /api/simon` + `POST /api/simon/level`：**升档（收紧）任意通道放行**，降档返回 `SIMON_LOOSEN_REQUIRES_TERMINAL` + 去终端的命令。
  - `GET|POST /api/telemetry` + `GET /api/telemetry/export`：遥测开关与导出（导出是只读，不需要挡位放行）。
  - `GET /api/config`：数据目录 / 绑定 / 会话范围 / AI 配置 / 阈值 / 计数 / 挡位 / 遥测，一次看全。**只读** —— 改配置的路仍然只在终端。
  - `POST /api/insights/interval`：报告定时提醒（`off` / `7d` / `30d`），与 CLI 同一个解析器。
  - `POST /api/summaries`：批量摘要（`sip --summary-all` 的等价物，但**不会卡在 stdin 上** —— CLI 那条会问序号与确认，在服务器里读到的是 EOF）。
  - `POST /api/purge-fulltext`：清全文缓存（单篇或全部）。
  - `GET|POST /api/index`：向量索引状态与补索引 / 重建索引。没配 AI 时**先拒**（`AI_NOT_CONFIGURED` + `sip --init`），而不是跑一遍注定超时的循环。
- **`POST /api/feeds/{id}/schedule`**：更新计划（`sip --schedule` 的等价物，共用 `SetFeedSchedule`）。
- **`GET|POST /api/reading-progress`**：阅读位置记忆（与 TUI 共用 `reading_progress.json`）。刻意**不过**挡位门 —— 这是界面状态（读到哪儿了），不是对库的改动。
- **`GET /api/imports` 之外的导出**：`GET /api/articles/{id}/export` 直接下载 Markdown（复用 CLI 的 `BuildArticleMarkdown`），文件名带 RFC 5987 的 `filename*`，中文标题不再乱码或把响应头弄坏。
- **命令面板**（`Ctrl/Cmd+K` 或顶栏 ⌘，`POST /api/command`）：等价于 TUI 命令栏的"我知道命令，但懒得点"，但**只认只读白名单**（`status / list / today / edits / dedup / hidden / policy / simon / telemetry / index / config / grep <q> / show <id> / versions <id>`）。
  - 刻意**不做通用 CLI 执行器**：网页里开一条任意命令通道，等于把 CLI 的全部写能力塞进浏览器。
  - **参数个数也管**：`simon level 1`、`telemetry enable` 这类"拿只读命令名当挡箭牌、后面跟写意图"的写法整条拒掉，而不是丢掉多余参数后照样返回一份只读结果（那样用户会以为写命令成功了）。
- **`/api/today?digest=1`**：今日变化摘要（新增 / 被改过 / 可能同文）。默认**不算** —— 它要跑一次 48 小时窗口的跨源重复检测（上万篇正文），首屏不该为它等几秒。
- **静态资源路由**（`Web.cs` 的 `TryServeStatic`）：`/app.js`、`/login.js`、`/sw.js`、`/manifest.webmanifest`、`/icon.svg`，以及**`/languages/<code>.json`**。
  - 最后一条修的是"多语言形同虚设"：前端一直请求 `./languages/xx.json`，而服务端此前**没有任何静态路由** → 永远 404 → 永远回落到 20 个键的内置表。现在三种语言都是完整词典。
  - 文件名只接受 `字母/数字/连接符`，服务端**不做路径拼接** —— 目录穿越没有落点（有回归用例）。
- **PWA 可安装**：`manifest.webmanifest` + 一个**刻意什么都不缓存**的 `sw.js`。离线显示的旧库比没有更糟，所以 service worker 只做同源透传。
- **移动端**：侧栏抽屉、按钮/正文尺寸、浮动阅读条在窄屏下重排；`viewport-fit=cover` + `theme-color`。
- **原文件直出 + 临时链接**（`GET /api/imports/{id}/file`、`POST /api/imports/{id}/link`）。
  - 起因：PDF 在网页里只能**逐页栅格化**看（`ReadPdfFile` 从不解析文本），于是不能选字、不能搜索、读不出正文。与其硬做一套自己的 PDF 阅读器，不如把**原文件原样**递给浏览器自带的那个 —— `Content-Disposition: inline`，并支持**单段 Range**（浏览器的 PDF 阅读器会按需拉区间，几十 MB 的扫描件不必整份下完才能翻第一页）。
  - 「临时链接」解决的是**会话**问题：网页的钥匙是"一个浏览器一把、活到进程结束"，所以页面里点开没问题，但把链接贴到别的标签页 / 别的 PDF 程序 / 手机上会 401。令牌是**进程内、只对一份文件、10 分钟过期**的（`webFileTickets`），比会话 cookie 窄得多：只读、只一份、会过期、重启即失效。接口同时认令牌与会话，所以"登录后在页面里直接打开"和"贴到别处打开"是同一个地址。
  - 校验：令牌必须**同时**对得上文件与时效 —— 拿 A 的令牌去读 B 也会被拒。
  - 入口：PDF 阅读页与「本地导入」列表都有「用浏览器打开（原生阅读器）」与「临时链接」。

### Security

- **上 CSP，并把内联脚本整块拆出去**（`Web.cs` 的 `HandleWebContext` + `web/app.js` + `web/login.js`）。
  - `default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: http: https:; media-src http: https:; font-src 'self'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'`。
  - 为什么现在才上：`script-src 'self'` 要求页面里**没有内联脚本、也没有 `onclick=`**，所以前端先拆成 `index.html`（只有标记）+ `app.js`（全部逻辑，用 `data-act` + 一个委托监听器）。
  - 收益是双份的：**「订阅源正文里混进 `<script>`」这条路径在浏览器层面也死了** —— 净化器哪天漏掉一个标签，浏览器也不会执行它。CSP 是第二道防线，第一道仍然是服务端净化。
  - 回归用例钉着：`Content-Security-Policy` 头必须含 `script-src 'self'` / `frame-ancestors 'none'`，且 `index.html` 里不许再出现 `<script>` 或 `onclick=`。
- **导入资产接口的路径围栏**：`/api/imports/{id}/asset?path=…` 只服务 `ImportedDir()` 之内的文件，越界 403。没有这道围栏，正文里一个 `src` 就能让浏览器读走机器上任意文件。
- **命令面板的只读白名单**：见上，连同"参数个数"一起收。

### Fixed

- **`ShowDiff` 缺 `FeedId` 条件 → 跨源误归档**（`sipcore.cs`）。Guid 是文章级标识，不同源完全可能转载同一篇（Guid 相同）；原先 `WHERE Guid = @guid AND Status='active'` 会在 A 源改稿时**顺手把 B 源那份 active 副本一起归档**，B 源看起来就像文章凭空消失。归档与版本链现在都按 `(Guid, FeedId)` 隔离。
  - 连带修 `--versions` / `--diff` / TUI 的版本历史 / `Web.cs` 的 versions+diff：版本号在两个源里会重复，只按 Guid 取会让"看 v1→v2"变成**跨源比较**。
  - 回归用例：`Versions_AreScopedToOwnFeed`（跨源转载的同一 Guid 只该有 1 条历史）、`Edits_ListsSameFeedRevisions_ButNotCrossFeedReposits`。
- **登录页的外置脚本**（`web/login.js`）：登录页不再是内联 `<script>`，同样受 `script-src 'self'` 约束。
- **电子书正文没有走分栏翻页**（`web/app.js`）。文本型电子书（epub/docx/mobi/txt/md）的正文以前写死 `data-cols="1"` 且**没有 `#pager` 容器** —— 右下角的分栏按钮按了没反应、多栏永远只有一栏，用户看到的就是"分栏模式没有按页去读"。现在它与文章正文**共用同一套 `.pager` + `.prose`**：跟随全局栏数、多栏时 ← → / 滚轮翻页、按窗口尺寸重新分页、图片加载完重排、阅读位置一并记忆（PDF 记页码、文本记节号，同一个 itemId 不会两者兼有）。
  - 顺带修：离开可翻页的视图时彻底关掉分页状态。否则 `pager.on` 残留为 `true`，新页面的滚轮事件还会被它接管（表现为"这一页滚不动"）。
- **⚠️ 上面那条的第一版让"读书"变成了"页面无响应"**（同一次改动内的自纠）。原因不是分栏错了，而是**分页的单位错了**：分栏要求浏览器一次性把内容排版出来再读 `scrollWidth`/高度，代价与文档长度成正比；把**整本书**（几十万字）塞进一个 `.prose` 里做这件事，主线程直接卡死。
  - 修法是把超长正文切成"节"（`splitBookSections` / `sectionsFromNodes`：按 h1~h3 或每 ~12000 字切一刀），**只对当前这一节分页**，并给节点加了上一节/下一节与跳节按钮 —— 排版量有界、翻页灵敏，读起来也更像一本书。
  - 另加三道防护：分页高度上限（`PAGER_MAX_HEIGHT`，超了退回滚动）、页数上限、以及把"图片一张张加载完各自触发一次全量重排"合并到下一帧（`pagerMeasureSoon`）。正文与切分结果**按书缓存**，重渲染不再重复拉取与解析整本。
  - 回归：`tools/webui-smoke.mjs` 直接验证"32000 字会被切成 ≥3 节、最长一节 ≤14000 字、内容不丢"，并在电子书视图上断言出现「共 N 节」与节点按钮 —— 把切分去掉，冒烟立刻红。
- **导入的文件点进去一律落到「阅读器」，并且阅读器里有图**（`Web.cs` + `web/app.js`）。
  - `GET /api/articles/{id}` 现在会回 `imported`，并**带上 importItemId 净化**：导入项的图片是本机 `file://` 路径，不改写成资产接口就会被净化器整个丢掉 —— 用户看到的就是"电子书里没有图"。
  - 界面据 `imported` 直接转给阅读器：文章视图对整本书**分不了页**（只能退回滚动，于是"左栏读到底、右栏还在下面"），而阅读器按节分页、图片路径也是改写好的。同一个东西不该有两套体验。
  - `openEbook` 现在 `await render()`，调用方等这一屏真的画完 —— 否则会先闪一下"加载正文…"再跳走。
- **分不了页就不再装作分栏**（`pagerFallbackToSingle`）：多栏 + 纵向滚动读起来正是"左栏读到底，右栏还在下面"，比单栏更糟。现在一旦触发长度/页数上限，就把**当前这一篇**退回单栏（不动用户的全局偏好）并提示原因。
- **版面比例：按可用宽度降栏、图片不得高过一页、节号收成一行**（`web/index.html` + `web/app.js`）。用户反馈"显示比例很奇怪"，三处原因叠在一起：
  - **栏太窄**：阅读区 820px 里塞 3 栏，每栏只剩 266px —— 21px 的中文一行十来个字，看着就是"被拉伸的手机版"。现在 `minColumnWidth()` 按**字号**算一栏至少要多宽（约 18 字/行，下限 300px），`effectiveCols()` 再按实际可用宽度决定真排几栏：装不下就降栏，**偏好本身不改**（窗口拉宽、栏宽拉大、字号调小都会自动恢复），用户手动点栏数时才会提示一句为什么。
  - **图比页还高**：封面/整页插图按原始尺寸铺满整栏，在分栏模式里会**溢出到页外**（翻页翻不到、滚也滚不到）。现在正文图统一 `max-height`：分页时按页高（`--page-h`，由 `pagerMeasure` 写进 CSS 变量）再收一层，单栏滚动时最多占一屏；并且居中 + 留白，不贴着正文。
  - **按钮墙**：43 个节号按钮折成三行，喧宾夺主。现在是一行可横向滚动，节标题另起一行（"第 N / M 节 · 标题"）。
  - 首帧就用**有效栏数**渲染（而不是先排 3 栏再由 `setCols` 纠正），免得闪一下。
  - 回归：冒烟桩里 `--cols` 报成 3、阅读区 800px，断言渲染结果必须是 `data-cols="2"` —— 把 `effectiveCols` 改成恒等函数，冒烟立刻红。
- **滚轮不再翻页**（`web/app.js`）。分栏模式下滚轮把"往下滚一点看看"变成"跳一整页"，误触太容易；翻页交给 ← → 键与右下角 ‹ › 按钮，滚轮恢复成最朴素的滚动。
- **应用布局宽度与阅读宽度分开**（`web/index.html`）。`--content-w`（右下角那条「宽」滑块）本是为读长文准备的，可以拉到 2400px 铺满窗口；但它此前作用在**所有**视图上，于是把滑块拉宽之后，「今日哈汤」那三张统计卡被抻成三条横幅——用户反馈"正常 UI 的显示宽度都怪怪的"。现在 `.wrap`（列表/仪表盘）走固定的 1180px，`.wrap.narrow`（文章/电子书）才跟随阅读宽度。
- **全屏阅读（沉浸）模式**：顶栏 ⛶ / 右下角 ⛶ / 快捷键 `F` 进入，`Esc` 退出。侧栏、顶栏、进度条一起让位，正文吃满窗口（上限 1500px 防止行长失控）。实现只是 `body` 上的一个 class —— **不重渲染**，切换是瞬时的；离开阅读类视图会自动退出（否则回到列表就没有导航了）。
  - 刻意不做成浏览器 F11：那是整个窗口的事，这里要的是"应用内少点东西"。
- **电子书最多两栏**（`effectiveCols` 里按视图封顶；手机上 CSS 仍会压成一栏），并且电子书视图里隐藏「3 栏」按钮——给你一个按了没用的按钮，比不给更糟。
- **阅读页改成「细顶栏 + 右侧抽屉」**（`web/index.html` + `web/app.js`）。起因是用户反馈"上层挤压得太狠"：文章/电子书的头部原本是一摞（书库条 / 类型 / 书名 / 说明 / 节号列表 / 节标题），一行行往下挤，正文只剩一半高。
  - 现在正文上面只有**一行**：`⋯`（展开/收起）+ 标题 + 位置（质量/版本/第 N 节）+ 收藏与原文这两个最常用的图标。
  - 动作、元信息、摘要、来源、**目录**、以及 PDF 的「用浏览器打开 / 临时链接」全部挪进**右侧抽屉**：点开也不占正文宽度（抽屉浮在上面，只盖住它自己那块）。默认收起，展开状态记在偏好里（`metaOpen`）。
  - `Esc` 先关抽屉、再退全屏；点遮罩也关；主动点「改稿历史」时抽屉自动打开（那张长表格摆在正文上方同样是挤压）。
  - 回归：冒烟测试断言阅读页必须有 `.rd-bar` + `meta-toggle`，**动作按钮不许再出现在正文上方**，且抽屉默认收起、能开能关 —— 把动作搬回顶栏，冒烟立刻红。
- **顶栏更细 + 去掉底部节按钮 + 翻到头自动接下一节**（`web/index.html` + `web/app.js`）。
  - `.rd-bar` 压到 30px 高（图标 26px、标题 13.5px）——它只是"我在读什么、读到哪"的一条提示。
  - 底部那排「← 上一节 / 下一节 →」拿掉（目录已经在抽屉里），改成**翻页翻到头自然接下一节**：最后一页时右下角的 `›` 直接变成「下一节 ›」，往回翻到第一页则变成「‹ 上一节」并落在上一节的**最后一页**（往前读才连贯）。
- **调字号/行距/栏宽/栏数不再被打回第一页**（`repaginateKeepingPlace` / `pagerRestoreTo`）。重排后**页码没有可比性**（新字号下的第 3 页完全是别的内容），有可比性的是**内容**：先抓住当前页开头的那一段，重排后回到包含它的那一页；抓不到段落时退回"按比例"。RSS 文章与电子书共用这套逻辑；窗口缩放同理。
  - 顺带把"读一半点 ♥ / 生成摘要后跳回第一页"也一起修了：重渲染会换掉整棵 DOM，所以用**子元素序号 + 比例**把位置接力过去。
  - 回归：冒烟测试在阅读页里跑一遍 `bumpFont / setLeading / setContentWidth / setCols`，任何异常或正文容器丢失都会红。
- **单栏也能翻页 + 记住上次读到哪**（`web/app.js`）。
  - **单栏纵向翻页**：以前只有多栏才分页（单栏退回滚动）。现在单栏也按**段落边界**分页（`pager.offsets`，每页从某一段开始）——刻意不按像素硬切，那会把一行字切成两半（上半页看得见、下半页看不见），比滚动更难受。多栏仍是横向翻页；`setCols` 在 1↔2 栏之间切换时靠"锚点段落"把位置带过去。
  - **翻页/滚动可切换**（右下角 `⇄`，偏好记在本机）：有人喜欢"一屏一屏"，有人习惯滚。切换时位置也带过去（翻页→滚动滚到那一段，滚动→翻页翻到含那一段的那一页）。另外加了两道保险：内容超过 5000 页或单页高度超限时自动退回滚动。
  - **阅读状态**（`sip-web-read-state-v1`，按 itemId 存在本机）：翻页模式**没有滚动**，而服务端那份 `reading_progress.json` 记的是滚动像素（TUI 也用）——翻页时它恒为 0，等于没记。所以页码/节号/滚动位置单独记一份，**下次打开自动回到原处**（同一条目按字段合并更新，只留最近 500 篇）。文章与电子书都适用。
  - 回归：冒烟测试钉住"存进去读得出来、局部更新不冲掉别的字段、没读过的不该有记录、非阅读视图调用不抛异常、翻页/滚动切换不抛异常"——把局部合并改成整体覆盖，冒烟立刻红。
- **平板上一翻页就"跳回上一页"**（`web/app.js`）。根因是**重排时只保住了"第几页"这个序号，没保住内容**：正文里的图是 `loading="lazy"`（净化器给每张图都加），往下翻时新一屏的图开始加载 → 每次 `load` 都触发重新分页 → 图撑高版面、页边界整体后移 → "第 3 页"在新边界下对应**更靠前**的内容。平板比桌面明显：图更慢，且地址栏一隐一现反复触发 `resize`。
  - 修法：把**所有**重排路径都接到"锚点保位"上（图片加载、定时重排、resize、进/退全屏）——锚点是一个**段落元素**，重排后按它**新的**位置找页，所以上方图片撑高多少都不影响；此前只有"调字号/栏宽"走了这条路。
  - 顺带修两个同类问题：① **滚动 → 翻页**的切换（内容变长使分页突然成立）原本会落到第 1 页，现在按当前滚动位置算出该在哪一页；② 存下来的阅读位置原本**每次重渲染都会被套用**，而它是 400ms 前的值 —— 现在只在**真正进入这一篇**时套用，同一篇重渲染改用锚点保位。
  - 回归：冒烟测试里阅读页的 `bumpFont / setCols / …` 会走一遍保位路径（不抛异常）；重排路径只剩 `repaginateKeepingPlace` 一处直调 `pagerMeasure`，用 grep 即可复核。
- **「比对」把整篇正文报成"全删 + 全插"**（`sipcore.cs` 的 `DiffNormalize` + `Web.cs`）。改稿追踪里看到的是一大块红接一大块绿，看着像作者把全文重写了一遍 —— 其实只是比错了单位。
  - RSS 的 `Content` 常常**一整篇只有一行 HTML**（段落靠 `<p>` 分，行里没有换行）。`InlineDiffBuilder` 是**按行**比的，"一行变一行"自然就是整篇删除 + 整篇插入。跨源去重一直是先 `NormalizeParagraphs`（剥标签、按段切）再比，文章/版本比对漏了这一步。
  - 现在四处比对的**输入**统一走 `DiffNormalize`：网页改稿追踪（`/api/articles/{id}/diff` 直接复用去重那套段落级 diff）、CLI `sip --diff`、TUI 的左右分栏 diff、以及今日摘要里的"改了几行"（`ChangeOverview` 的 ±行数与字数以前也是拿 HTML 标记在算）。
  - 顺带修界面：**大段没改的段落折起来**（只留变更点上下各 3 段，点一下就地展开），并把"只有一版"与"两版文字一致（只改了排版/摘要）"这两种空差异分开说。
  - 回归：`Diff_OneLineHtml_ComparesParagraphs_NotWholeArticle`（接口必须给出 Unchanged 段落、删/插只涉及改动的那一段）、`Diff_OneLineHtml_PrintsOnlyTheChangedParagraph`（CLI 只打印一条 `-` 一条 `+`，差异文本里不许出现 `<p>`）—— 把归一化去掉，两条立刻红；冒烟测试另钉折叠/展开。
- **阅读抽屉的「Esc 关不掉 / 收起没反应」**（`web/app.js`）。抽屉有两种开法：用户自己展开的（`metaOpen`）和**面板自己撑开的**（点「改稿历史」时 `forceOpen = state.versions`）。而 Esc / 收起 / 点遮罩此前都只把 `metaOpen` 置 false —— 撑开它的那条理由还在，`render()` 立刻又把它开回来，用户看到的就是"Esc 没反应"（面板还杵在那儿，提示语里却写着"Esc 关掉本面板"）。
  - 改成统一的 `closeReadPanel()`：**两种理由一起清**（`metaOpen` + `state.versions`），Esc / 收起 / 点遮罩 / 顶栏按钮全走它；顶栏那个按钮也按**实际**开合状态显示 `⋯` / `✕`（此前面板开着、按钮还显示 `⋯`）。
  - 回归：冒烟测试里点开「改稿历史」后**真的按一次 Esc**（把注册过的 keydown 监听器跑一遍），断言抽屉收起、`state.versions` 被清空、按钮变回 `⋯` —— 换回旧写法立刻红。
- **删掉 `web/index.html.bak`**（28 KB 的多余备份，`git add web/` 会一并提交）与 `index.html` 里那个**永不显示的登录壳**（`#login` / `#loginForm` / `#skipLogin` —— 真实登录在 `login.html`，留着只让人分不清真假），以及两个死面板（从未有入口的搜索面板、纯模拟的 Web 密码面板）。
### Changed

- **不带参数启动 = 内置 Web（双击就能用）；TUI 改为显式命令 `sip tui`**（`sipcore.cs` 入口 + `Web.cs`）。
  - 起因很朴素：**双击 exe 的人要的是"能用"**。与其用一条弃用横幅去劝他改用 Web，不如让默认值就等于我们推荐的那个界面。旧行为是「无参 → 问一句『TUI 要弃用了，你确定吗』→ 进 TUI」：劝住了他也没更好走，没劝住他还是在用要弃用的那个。
  - `StartWebFromCli`：逃生口 + 首次向导 + 起服务，**无参与 `--start` 共用同一条路径**（两处各写一遍，迟早只剩一处被改到）。
  - **自动打开浏览器**，且直接开到**那条带引导令牌的链接**上。无密码模式尤其需要 —— 令牌只印在终端里，不自动开浏览器的话，双击的人还得自己复制粘贴一次。关闭方式：`--no-open`，或 `sip_settings.json` 的 `WebOpenBrowser: false`（新增字段，默认 `true`）。只在**真终端**下做：测试/脚本环境 stdout 被重定向，不会弹浏览器。
  - **非交互（管道/脚本/无控制台）下不带参数启动不起服务**：打印帮助、退出码 1。否则"无参启动一个前台阻塞的服务器"会把脚本挂死，而终端里那条带令牌的链接也没人看得见。
  - `sip tui`（别名 `--tui`）：显式进终端界面，**不再问"你确定吗"** —— 敲出来本身就是答案。它进了挡位 3 的只读白名单：挡位 3 的提示语是"只允许通过 TUI 使用"，把自己的入口拦掉就等于死胡同。（Agent 门照样拦程序调用 —— 那条轴管的是调用者，不是命令。）
  - 横幅加了 `Terminal UI: sip tui` 一行；`--help` 写明"无参数 = Web、`--no-open` 关自动开浏览器"；README / 用户快速手册 / 业务逻辑梳理同步。
- **`ImportCli` / `ImportRmCli` 拆出共用核心**（`ImportFileCore` / `ImportItemDelete`）：CLI 那层负责打印，Web 那层要结构化结果，但"复制文件、抽正文、算页数、写 Items"与"删库行 + 删落地文件"各只有一份实现。终端导入和网页导入不会对同一个文件给出不同结果。
- **`index.html` 尺寸**：103 KB → 25 KB（逻辑搬到 98 KB 的 `app.js`）。改前端仍然要重新构建（都内嵌进 exe）。
- **测试夹具**（`tests/Sip.Tests/WebServerHarness.cs`）：`SipWebServer` 暴露 `Instance`，用例可以直接塞 fixture 数据并断言库状态；新增 `WebFeaturesTests`（30 条）与 `WebSimonTests`（挡位这一轴单独一个实例 —— 升档会改变该进程的写策略，而降档是设计上做不到的）。

---

> 下面这一段是 1.3.0 之后、本次 Web 补齐**之前**就已积累但一直未发布的改动（内置 Web 界面 + 会话改为进程内一次性密钥 + Agent 门 + 主数据库等）。它们随 2.0.0 一起发布，故并入本节。

### Security

- **无密码模式下 `GET /` 不再无条件下发凭据**（`WebServer.cs`）。原先**本机任意进程 GET 一次 `/` 就能拿到 `sip_local` cookie**，读走整个订阅库。现在改为带外发放：
  - 启动时生成一次性**引导令牌**，打印在 `Web UI : http://…/?t=…` 那一行
  - 裸访问 `/` 只得到一张说明页，**页面上绝不回显令牌** —— 回显就等于把它交给任何能 GET 本页的进程，带外信道白设
  - 令牌换到 cookie 后立刻 302 回干净 URL，**令牌不进地址栏与浏览历史**
  - 已有 cookie 时刷新/收藏照常；`--json` 与 `/api/*` 的 401 行为不变
  - 无密码模式仍有价值：本机浏览器 + 终端里的链接，零密码负担；想省这一步就 `sip webpass`
- **Web 会话改成「进程内一次性密钥」：登录一次管到重启，换浏览器必须重新登录**（`Web.cs`）。原实现是 HMAC 签名的 `exp.MAC` + 系统凭据库里的**长期**签名密钥——于是那把 token **跨重启照样有效、抄进别的浏览器也照样有效**，24 小时后才过期；三条都与预期相反（要的是「一次登录管到重启，换浏览器就得重新来」）。
  - **会话表只在内存里**（`WebSession` / `webSessions`）：重启 sip = 表是空的 = 所有浏览器回到登录页。**没有第二份持久副本**，所以「重启后还能免密码进去」在结构上不可能发生。顺带：会话不再往系统凭据库写东西（那东西写满会让安全设置静默失效，见 Fixed 一节）
  - **每个浏览器一把钥匙**：登录成功时现场生成 32 字节随机值，并绑这次请求的浏览器指纹（UA 的 SHA-256）。换浏览器 = 手里没有这把钥匙；**就算把 cookie 值抄过去也过不了指纹**，而且对不上时**当场吊销**——这把钥匙已经泄漏，原浏览器也得重新登录
  - **一次一换**：同一浏览器再登录一次，旧钥匙立即作废、cookie 换成新的。`/api/logout` 也改成**服务端吊销 + 清 cookie**（原先只清 cookie，抄走那串值的人照样能用；审计 §9.5 只提了 `sip_local`，这是同一件事的两半）
  - **无密码模式走同一套**（`sip_local` 同样入表、同样绑指纹）：换浏览器或重启进程后必须重新打开终端里那条 `?t=` 引导链接。`POST /api/login` 在无密码模式下**刻意什么都不发**——否则本机任意进程 POST 一个空 body 就进了门，引导令牌这道带外信道等于白设
  - cookie 仍在浏览器里存 30 天：它只是一串**进程内**的钥匙，进程一退就作废；做成会话 cookie 反而会变成「关掉浏览器也要再登一次」，与需求不符
  - **把话说明白**：终端横幅新增一行 `Session`；重启后带着旧 cookie 打开页面，登录页会插一句「sip 重启过了…」（`<!--SIP_LOGIN_NOTICE-->` 占位，服务端注入）；无密码模式的说明页会区分「重启过 / 换浏览器」；前端 `api()` 拿到 401 直接把人送回 `/`，而不是弹一句看不懂的 `UNAUTHORIZED`
  - **失败限流一并修正**：原先是**只增不减**的进程级计数器（只在登录成功时清零），长期运行的进程里累计错 20 次之后就变成「错一次锁一次」；现在改为 **5 分钟滑动窗口内错 20 次**才拦，并回 `Retry-After`
  - 回归用例（真起进程 + 真 HTTP + 真 cookie 罐；`tests/Sip.Tests/WebLoginTests.cs` + `WebServerHarness.cs`）：`Restart_ForcesLoginAgain_EvenWithTheSameCookie`（重启后同串 cookie 必须 401，且登录页要说明原因）、`FreshBrowser_MustLogInAgain`、`CookieCopiedToAnotherBrowser_IsRefused_AndRevoked`、`ReloginInTheSameBrowser_RotatesTheKey`、`Logout_RevokesTheKeyOnTheServer`、`TooManyBadPasswords_BlocksEvenTheRightPassword`，外加无密码模式两条（换浏览器仍要引导链接 / 登录接口不发凭据）
- **Origin 校验改为绑定感知**（`WebOriginOk`）。原先白名单只认回环，导致**局域网/手机访问的所有写请求一律 403** —— 直接废掉移动端。
  - 现在：回环（`localhost`/`127.0.0.1`/`::1`）一律认；非回环**只认 IP 字面量**，且需等于配置的绑定地址（绑 `0.0.0.0`/`*` 时放宽为任意 IP 字面量）
  - ⚠️ 那个回环白名单**不是随手写的，它防的是 DNS rebinding**：攻击者把 `evil.com` 解析到 `127.0.0.1`，浏览器同源请求时 Host 与 Origin 都是 `evil.com`，只比对"两者相等"会放行。所以改法是"域名一律拒"而不是删条件。实测 rebinding 模拟仍 403
- **Web 挡位判定与 CLI 统一**（`WebWriteAllowed`）。原先只拦 `delete/sync/update/update-all/add`，于是挡位 2 下 Web 仍可 `archive/unarchive/like/fulltext/summary` —— **同一个操作仅因走的通道不同就裁决相反**，实际是绕过 CLI 限制的写通道。现在挡位 2 起拦一切写，读不受影响
- **`X-Frame-Options: DENY`**：本机 UI 不该被任何页面套进 iframe
- **`/api/logout` 同时清 `sip_local`**：原先只清 `sip_web`，无密码模式下"登出"等于没登出
- **`GET /summary` 不再被当写操作拦掉**：只读取缓存摘要本就该放行
- **非回环绑定的失败提示改为可操作**：Windows 上非管理员绑局域网地址会「拒绝访问」（http.sys 需要 urlacl），原先提示"端口可能被占用"会把人带偏。现在直接给出 `netsh http add urlacl url=http://<ip>:<port>/ user=Everyone` 以及"以管理员运行"的替代方案
- **登录页不再请求外网「一言」**（`web/login.html`）。原来首屏 + 之后每 20 秒都会去 `v1.hitokoto.cn` 拉一句 —— 等于持续把「这台机器正在打开 sip 登录页」告诉第三方，与本项目「不收集、不上传」的立场直接冲突，**登录页尤其不该有出网请求**。改为本地句子池（取自 sip 自己的文案），按钮也从「一言」改成「换一句」
- **正文净化移到服务端，接口不再返回原始 HTML**（`WebServer.cs`）——本轮最重要的一条。原先 `GET /api/articles/{id}` 把 `content` / `fulltext` 的**原始 HTML** 交给前端，前端 `innerHTML` 直接注入：任何订阅源（或导入的网页/md）都能在本机页面上执行脚本，而该页面同源、带 cookie，脚本可以直接把整个库经 `/api/*` 读走——**订阅源作者写一篇文章就等于拿到你的阅读器**。
  - 策略是**标签白名单 + 属性白名单**，不是黑名单（`on*` 一大串、新标签层出不穷，黑名单永远漏）。`script/style/iframe/object/embed/form/input/svg/math/link/meta/base/template` 等**连内容一起丢**；属性只留 `href/src/alt/title/colspan/rowspan/datetime/cite`
  - 不保留 `style`/`class`/`id`：它们能做 CSS 数据外泄，也能把正文排版成"删除订阅源"按钮的样子骗点击
  - URL 只放行**绝对**地址——相对路径会以本机服务为基准，等于让正文拿我们的接口当图床/探针。`href` 只认 `http(s)`/`mailto` 并补 `rel="noopener noreferrer"`；`src` 只认 `http(s)` 或 `data:image/`，`img` 补 `loading="lazy"`，**`src` 被删掉的 `img` 整个去掉**（否则正文里撒一地破图）
  - 接口只返回净化后的 `data.bodyHtml`（`content`/`fulltext` 字段删除），纯文本正文统一编码后包 `<p>`——前端不再判断 `isHtml`，**判断点越少，漏点越少**
  - 净化本身抛异常时返回「正文无法安全显示」：**宁可什么都不显示，也不放未净化的 HTML 出去**
  - 端到端实测（真实二进制 + 真实 HTTP + 21 条载荷）：`onclick=`/`onerror=`/`onload=`/`style=`/`href="javascript:` 全消失，`<script>/<iframe>/<svg>/<form>/<object>/<marquee>/<input>/<button>` 连内容全消失，`src="x"` 与 `src="/api/feeds"` 被删；`<figure>/<figcaption>/<table>/colspan/<pre>/<code>` 与转义实体（`&lt;script&gt;`）原样存活
- **挡位不再能被「换个凭据作用域」降下来**（`simon.cs` `SimonLevelGet`）。`SIP_SIMON_KEY_NAME` 本意是测试隔离，但原实现**只读**该作用域的挡位、把真实作用域丢了：设一个环境变量就能让凭据库里的挡位失效、退回 `sip_settings.json` 里那个可能过期的低挡位——一道纯收紧的门被一行环境变量放宽。现在两个作用域都读、取**更高**的一档：环境变量只能收紧，不能放宽（与 Agent 门同一套规矩）

### Added

- **主数据库：跨位置「哪一份才是真的」+ 提示 + 更改/合并**（`PrimaryDb.cs`、`sipcore.cs`）。sip 是绿色的——exe 拷到哪儿都能跑、数据目录（`readwithhotsoup/`）跟着 exe 走，于是同一个用户很容易攒出好几份库（桌面一份、U 盘一份、发布目录一份），谁也说不清哪份是"真的"。
  - **第一次运行即认领**：把当前数据目录写进**系统凭据库**（键名 `primary_db`）。这条记录**刻意不带 `SimonScopeHash()` 作用域** —— 挡位/Agent 门/AI Key 都是"每个副本互不影响"，而这里要的恰恰相反：跟着用户走、跨所有副本可见。测试隔离沿用同一套手法（`SIP_SIMON_KEY_NAME` 换命名空间），并已纳入 `TestHost` 的凭据清理
  - **在别的位置打开就提示**：主库在哪、当前打开的是哪一份、怎么改怎么合。提示走 **stderr** —— `--json` 的 stdout 必须干净（脚本要解析），这是给人看的诊断
  - **只提示，不当门**：在别处照样能用那份库（绿色程序最常见的用法就是"拷一份出去试"）；真正的门仍是挡位与 Agent 门
  - 命令：`sip db status | set [<目录>|--here] | merge [<目录>] [--yes]`（`status` 只读；`set`/`merge` 是写，挡位 2 起拦）
  - **合并的三条规矩**：① 只读来源、只写目标（两个连接，不踩 `ATTACH` 跨库事务的语义坑）② 订阅源按 `FeedUrl` 去重、文章按 `Guid`（无 Guid 退 `Link`，再无退 标题+发布时间）③ **幂等** —— 同一份合两次，第二次全是"已存在跳过"
  - `article_signals.json`（收藏）与 `reading_progress.json` 按**新编号**映射搬过去（目标已有的以目标为准）；向量索引、源规则、去重规则、健康状态、导入资产、全文缓存**明确不合并**，并在输出里逐项说明该用什么补（如主库重跑 `sip --index`）
  - 回归用例 `PrimaryDbTests`（9 条；靠**两个实例共享同一凭据命名空间**来模拟"跨位置"）：第一次运行认领、同目录不再唠叨、异地提示含主库路径与指令、`--json` 的 stdout 保持纯 JSON、`set --here` 改指与改回、目录不存在被拒、非交互缺 `--yes` 被拒、自己合自己被拒、合并计数 + 侧挂文件按新 id 落地 + 再合一次幂等
- **Web 也能导出 OPML**（`GET /api/feeds/opml`，入口在「添加订阅源」面板里、与导入同一处）：`Content-Disposition: attachment; filename="sip-feeds.opml"`，前端一个 `<a download>` 直接指向它。生成逻辑与 CLI **共用 `BuildOpml`**（不写第二套，否则两边导出的文件迟早有细微差别）。实测：200 + 正确响应头 + 21 个源全部在内
- **`--start` 横幅提醒遥测未开启**：阅读报告全部来自本机遥测、而遥测默认关闭，不提醒的话用户打开网页只看到"没有数据"却不知道该做什么。终端是唯一能给出可执行下一步的地方。实测输出：`Telemetry : off · the reading report will have no data (turn on: sip telemetry enable)`

### Added — AI 阅读助手（划词问 AI）

> 界面右下角一个可拖动的悬浮球：**选中一段文字 → 点球 → 带着选区问 AI**。
> AI 能读上下文并给出**可点击的出处**（EPUB 到章节、PDF 到页码），点一下就跳过去。
> 契约与全部变更记录见 `docs/AI阅读定位-契约.md`。

- **本地导入项真正有了章节模型**（`sipcore.cs` + 新增 `AiReading.cs`）。
  - **EPUB 按 `navPoint` 建行，不是按 spine 文件**。目录项与正文文件是**多对一**（实测《中国哲学简史》200 条目录只有 33 个文件、《毛泽东选集》410/416），按文件建行会把 200 节压成 33 章，"第 3 章第 2 节"这类引用直接说不出来 —— 而那正是这个功能的目的。优先级 `ncx → nav → spine`（真实语料 100% 是 EPUB2 + `toc.ncx`）。
  - `chapterId` 编码：`epub:<spine>`（全契约唯一的 0 基例外）、`epub:<spine>~<锚点>`、撞键时 `~n<k>` 消歧、`pdf:p<N>`（1 基）。
  - **修了两个让导入整个失效的 bug**：① manifest 正则要求 `id` 在 `href` 之前，而真实 EPUB 常写成 `href=… id=…` → 一个 item 都匹配不到 → 书导入成空；② `DtdProcessing = Prohibit` 会拒掉所有带 DOCTYPE 的 XML，而真实 EPUB2 的 `toc.ncx` **总是**带 DOCTYPE → 整份目录静默丢弃（实测 200 个 navPoint 掉到 33 个 spine 章节，且一声不响）。
  - **PDF 书签走 PdfPig**（纯托管、零传递依赖、离线可还原）。用 `GetNodes()` 展平而不是 `Roots.Count`（实测 28/28/L0、79/31/L1，`Roots.Count` 根本不是章节数）；四分类：容器节点不建行 / 子树有目标则继承首个后代页 / 整棵无目标则丢弃并计 `tocDropped` / 有目标则成章。
- **PDF 逐页文本**（`PdfPages` 表，独立于 `Items.Content`）。
  - 抽进独立页表是为了不改 `Items.Content` —— 那会动 `SourceHash` 并触发全库块回填。
  - **"能不能读正文"是逐份、逐页的事实**：整本 `pdfHasTextLayer` + 逐页 `textAvailable`，扫描件逐页 0 字是**正常结果**而不是错误。措辞纪律：永不说"PDF 没有文本层"这种格式断言，只能说"这一份/这一页抽不出文字"。
  - 大 PDF 两段式：`≥300 页或 ≥30 万字` 先 2 秒内返回书签章 + `pdfTextState='extracting'`，文本在同进程继续抽；带时长/内存/磁盘/并发四条上界。
- **四层上下文梯度**（§6.5）：划词段 → 本章（PDF 为当前页）→ 本书 → 全库（默认关）。总预算 16000 token，**划词段是最后被丢弃的一层**；预算不够时从最外层往里丢，并把降级原因写进快照。
- **SSE 逐字流式**，带非流式回落。
  - 帧序固定 `session → delta… → cites → done`；`session` 必须是第一帧（前端要在回答开始前拿到 sessionId）。
  - 15 秒心跳写一行 SSE 注释：既是"连接还活着"，也是**按期出现一个写点** —— 上游静默十几秒时，没有写点就察觉不到客户端已经走了，取消也就传不到上游。
  - **「用了 SSE」与「真的是流式」是两件事**，分开证：`tests/verification/sse_mini_probe.mjs` 与 `SseStreamingTests.cs` 记录逐帧到达时刻（服务端 2/315/628/939/1253/1564ms vs 客户端 27/339/653/964/1277/1589ms）。
  - 一个真实的坑：测试夹具里写 `for (k = 0; k < FrameGapMs / 100; k++) Thread.Sleep(100)`，而 `FrameGapMs = 50` → 整数除法得 0 → 服务端瞬间写完 40 帧，客户端看起来"不是流式"。**是夹具的错，不是产品的错。**
- **会话与消息落独立的 `chat.db`**（不建在 `rss.db` 里）。
  - 目的是让 `rss.db` 保持**只读可哈希证明**：提问前后对 `rss.db`(+`-wal`/`-shm`) 取哈希必须完全相同，而 `chat.db` 必须变了 —— 单向断言分不清"正确地只写了 chat.db"与"什么都没发生"。
  - 硬删、无 `DeletedAt`；删会话要从界面确认（服务端要求 `?yes=1`，否则 400 `CONFIRM_REQUIRED`）。
- **多轮读物协议**（§12.1-A48）：模型可以**自己决定读到哪儿**。
  - 文章默认只给相关片段（3000 字符）；模型在回答末尾**独占一行**写 `【读全文】`，服务端截住这条指令（不进回答、不推给前端）、放宽到 8000 字符、**再问一次**。最多 3 轮（每轮都是一次真金白银的上游调用）。
  - **EPUB 绝不整本进资料区**：上限就是**一章**。第 2 层带 `行号│` 前缀，模型可点名 `【读本章】120-260`。
  - 为什么用文本指令而不是 function calling：本地模型大多不支持它，用了这能力就变成"只有云端大模型能用"。代价是流式下必须截住指令，已由 `DirectiveFilter` 处理（有专门的探针）。
- **Web 端可配置 AI**（§12.1-A40②，开关 `AiConfigWebWrite`，**默认关**）。
  - 默认关是保留原加强：**key 只在真终端输入**。打开后网页可配端点/模型/key。
  - `GET /api/ai/config` **永不回显 key**，只回 `llmApiKeySet: true/false`；改端点要 `?yes=1` 确认并落 `simon_events` 审计（`detail` 只记主机 —— 有的兼容服务把 token 塞在查询串里）；挡位 ≥2 时改配置过写闸门。
  - 端点校验**故意不套抓正文那套 SSRF 规则**：那套拦 loopback 与私网，而本地 Ollama/LM Studio（`http://localhost:11434/v1`，正是 `EmbeddingCfg.ApiEndpoint` 的默认值）是核心用例。抓正文面对的是文章里的 URL（不可信），配置端点面对的是已认证用户亲手填的地址（可信）—— 不是同一类信任假设。
- **RSS 文章也能问**（§12.1-A40①）。书与文章同在 `Items` 表、靠 `Feeds.FeedUrl` 区分；会话粒度是"**阅读项**"而不是"书"。文章的"整篇"就是第 3 层。
- **会话切换**：同一个阅读项下会累积多个会话，面板顶部可列出并切换（没有这个入口时，"别的记录不见了"与"记录丢了"看起来一模一样）。
- **Web 端配置的边界**【必须】：目录、跳章、跳页、关键词搜索**不依赖 AI 配置**（没配 AI 也必须能用）。

### Fixed — AI 阅读助手

- **重启后聊天记录看起来消失**。数据其实一直在 `chat.db` 里，是 `GET /api/imports/{id}/chat` 用只认本地导入的判据做存在性检查，对**文章**的 id 直接回 404。同一个坑犯了两次（先修了 `POST /chat`、漏了 `GET /chat`），现在判据只留一处。
- **`snapshot` 恒为 `{}`**：`Snapshot`/`Hit`/`Cite` 是**字段**型类，而 `WriteJson` 与落库都用默认序列化选项 —— 默认不序列化字段。后果是前端「检索详情」永远空白，而"这一轮为什么答不准"只能从快照看出来。用 `IncludeFields` 单独序列化，未动全局选项。
- **划词段没送达服务端**：`aiSend` 先清空 `AI.sel` 再取锚点，于是锚点里的 selection 恒为空。症状极具误导性 —— **面板里引用框显示着那段原文，AI 却回"本轮划词段为空"**（引用框走渲染路径，与发出去的锚点是两条路）。
- **悬浮球在文章上必然失败**：前端把文章 id 发给只认导入项的端点，服务端在 AI 配置检查之前就回 `ITEM_NOT_FOUND`，前端翻成"这本书不在本地导入里"。修法是让服务端按载体分支，前端**不再自己判**"文章有没有正文"（猜的字段名与接口实际的 `bodyHtml` 对不上，把有正文的文章判成没正文）。
- **「本章」整层为空**：`FindChapter` 只认精确 `chapterId` 与 `#<ord>`，而 Web 端的"当前节"是**前端自己切的**（按 `h1~h3` / 每 12000 字），与 `Chapters.Ord` 不是一回事。实测：用户划的是 ord 3「正文一」，系统按第 1 章（front，正文为空）处理。现在扩成四级回查（精确 id → `#ord` → 标题相等 → 节首文本定位），四级都不中才降级、**不许猜**。
- **控制指令漏给用户**：过滤器第一版在**第一个** `@` 上就判"不是指令前缀"直接放行 —— 判据写成"是否等于标记"而不是"是否仍是标记的前缀"，等于形同虚设。标记也改成中文方括号（`@@` 会被模型当正文照抄）。


- **`GET /api/insights`：阅读报告接真**（`WebServer.cs`）。直接调 CLI 用的同一个 `BuildInsights`，**不另起一套算法**——同一个数字在终端和网页上不一致，比没有这个功能更糟。支持 `?window=<天>`（1–365，越界夹紧）；遥测关闭时明确回 `409 TELEMETRY_OFF` + 可执行提示（`sip telemetry enable`）。
  - 与 CLI 的唯一区别：Web 端**不记** `settings.LastInsightsAt`。那个时间戳是给 TUI 判断"报告到期提醒"用的，GET 请求不该顺手写配置（挡位 2 起写操作本来也会被拦）。
  - 端到端实测：遥测关 → `409 TELEMETRY_OFF`；遥测开 → `200`，返回真实的 `windowDays`/`generatedAt` 与按源的「订阅·积压·打开·读完·完成率」；`?window=7` → 7、`?window=99999` → 365。
- **前端：假数据默认不再渲染**（`web/index.html`，`PREVIEW_MOCKS = false`）。7 组设计稿预览数据里「阅读报告」已接真，其余（改稿追踪 / 跨源去重 / 源规则 / 本地导入 / 电子书）默认显示"**这一页还没接入 Web 版**"并给出对应的 CLI 命令。
  - 为什么这么做：**假数据冒充真数据是最坏的一种 bug**——「阅读报告」原先在渲染一组编造的统计数字，看起来就是你的阅读情况；去重页的"扫描完成：发现 2 组"、规则页的"已删除规则"同样是假成功提示。
  - **预览数据一个字都没删**：接一个后端就搬走一个。要审阅设计稿把开关改成 `true` 即可（`web/` 当时还没提交，顺手删除等于把设计稿永久丢掉）。
  - 顺带修掉两处**会漏进真实界面**的假数据：侧栏「本地导入」角标原先是写死的 `3`（HTML 硬编码 + JS 用预览数组长度刷新），现在没有真实接口时**隐藏而不是显示 0**——显示 0 一样是在替你断言"你没导入过东西"；`titleOf()` 里 `id===901` 返回预览书名，真实条目恰好是 901 时会显示成假书名。
  - 前端 `api()` 现在把后端的 `error.code` / `hint` 带进异常：页面需要区分"真的出错"和"这项功能本来就没数据"（TELEMETRY_OFF 该显示说明，不该弹一行红字）。
  - 回归用例 `WebInsightsTests.TelemetryOff_ReturnsTelemetryOff_NotAFabricatedReport`：遥测关时不许出现 `feeds`/`opened`/`completionRate`/`windowDays` 任一字段。

- **Agent 外部调用门：默认关闭**（`simon.cs` 的 `AgentModeBlock` / `AgentModeCli`）——OpenClaw 那类事故的共同点是**默认开放**，这道门把它反过来。
  - 判据是「有没有真交互终端」而不是「命令是什么」：真终端放行；管道 / 重定向 / 无控制台一律拒绝。**只拦程序，不影响你自己的终端**
  - 与孟思琳挡位是**两条独立的轴**：挡位是命令级策略（读/写/全部，连你自己的终端一起拦），Agent 门是调用者级策略（人/程序）
  - 命令：`sip --agentok`（开，走人工通道：真 TTY + Web 口令）、`sip --agentoff`（关，收紧所以任意通道放行）、`sip --agentstatus`；状态同时显示在 `sip simon status`
  - 豁免：`--agent*` 自身、`--help`/`--version`（纯信息）、`--start`/`webpass`/`aikey`（各有自己的门）。状态只认凭据库里那一条值，**没有环境变量旁路**（`SIP_SIMON_KEY_NAME` 只换作用域名，换了名字查不到值就是关）
  - 拒绝时 `--json` 返回 `{ code: "AGENT_BLOCKED" }`，并记 `blocked_cmd`（`agent-off:` 前缀）
  - 副作用已写进 README：`sip -l | grep x` 这种自己接了管道的用法也算程序调用

### Changed

- **源码按「一个文件 = 一件事」重新归置**（不含任何行为改动）。起因是 `WebAuth.cs` 这个名字下面的内容其实横跨四件不相干的事，找东西得先猜文件名：
  - `WebServer.cs` + `WebAuth.cs` → 合并为 **`Web.cs`**（1,954 行）：HTTP 服务、路由、各接口处理器、正文净化、网页登录（口令/会话/引导令牌）、`sip webpass`、首次向导
  - **AI 密钥存取 + `sip aikey`** → 挪进 `sipcore.cs` 的「凭据存储」段旁边（它与 web 无关，是"AI Key 存哪、怎么读"）
  - **人工通道**（`HasInteractiveConsole` / `RequireInteractiveTty` / 失败锁定 / `SensitiveActionAllowed`）→ 挪进 **`simon.cs`**：它定义的是"谁有资格放宽保护"（降挡、开 Agent 门都走这里），口令复用 Web 密码——放在孟思琳身边才找得到
  - `Web.cs` 头部写明这是 Web 的全部、前端在 `web/`；README 新增「源码结构」表，一行一个文件说清是什么
  - 迁移方式：纯行区间搬迁（同一 `partial class Program`，零语义变化），编译器验证无遗漏/无重复，全部 42 个用例通过。`docs/` 里引用旧文件名与旧行号的地方保留原貌（那是当时版本的记录），新名字以本文件与 README 为准

- **Agent 开关只存系统凭据库，没有兜底文件**（2026-09-12 当天收回「文件兜底」的设计）。原设计是：凭据库写不进去时退回 `agent_mode.json` 并报警。实测发现这个兜底的**触发条件写错了**——代码实际是「凭据库没有值就信文件」，而「没有值」正是**全新安装的默认状态**。也就是说默认状态下任何程序写一个 JSON 文件就能把这道门打开，与「只有 `--agentok` 能开」直接矛盾；**一道能被它拦的对象自己打开的门只是装饰**。至于当初担心的「凭据库坏了就永远打不开」：这道门拦的只是**非交互调用**，人坐在真实终端前从不受影响（见 `AgentModeBlock`），凭据库坏掉时丢的只是「程序可以调用 sip」这一项授权——属于收紧方向的失败，应当响亮地失败，而不是悄悄换一条更弱的路。现在 `CredWrite` 失败会明确报错且**不改变**开关状态
- **测试宿主自己开这道门**（`TestHost.cs`）。测试套件正是「非交互调用」（`CreatePsi` 重定向 stdin/stdout），不处理的话全部用例都会被拦。做法是往本实例独享的作用域（`SIP_SIMON_KEY_NAME`）写**与 `sip --agentok` 完全相同的那条凭据**，用完即删：产品里不留旁路，也不再写任何兜底文件。同一次事故的教训——测试往凭据库写东西必须自己清理
  - **写完回读确认，失败就当场炸**（2026-09-12 补）：一开始只是 `catch { }`，结果凭据库偶发写不进去时，症状是**几十个用例各自报 `AGENT_BLOCKED`**，真正的原因（门没打开）埋在噪音里——实测撞上过一次。现在建实例时回读校验，不通过就抛一条说明白的异常
  - 代价要说清楚：**测试套件现在要求系统凭据库可写**（以前是写文件）。这是删掉文件兜底的必然结果；无凭据库的环境（如没有 Secret Service 的 headless Linux）会在建实例时明确报错，而不是给出一堆看不懂的 `AGENT_BLOCKED`
  - **删除也改成回查 + 重删**：清理原来是"删一遍就算"，而 `cmdkey` 失败是被吞掉的——实测确实漏下过一条。后果不轻（746 条垃圾顶满凭据库那次就是这么来的），所以现在删完用 `cmdkey /list:<目标>` 独立回查，还在就再删一遍（仍失败也不抛，避免 Dispose 异常污染测试结论）

- **人工通道：真 TTY + 口令**（`SensitiveActionAllowed`，`WebAuth.cs`）——「放宽保护」类操作的统一入口。降挡已改走这条通道。
  - **TTY**：`HasInteractiveConsole()` 为假（管道、脚本、无控制台）一律拒绝
  - **口令**：复用 Web 密码（`VerifyWebPassword`，PBKDF2-SHA256/100k）；未设密码时退化为**逐字短语**并明说「这更弱，去跑 `sip webpass`」，记 `auth_weak` 事件
  - **失败计数落盘**（`human_auth.json`）：连续 5 次错误锁 15 分钟。落盘是必须的——每次 `sip` 都是新进程，进程内计数器对脚本毫无意义
  - **不回显读取** `PromptSecret()`：复用 `ReadSecret` 的机制，并修掉它的一个坑——原实现无条件 `Append(key.KeyChar)`，而方向键/F 键的 `KeyChar` 是 `'\0'`，手滑按一下就会往口令里塞 NUL，**永远校验不过且看不出原因**
  - **TUI 不再是旁路**：TUI 用掩码输入框（`AskSecret`）收口令后传给同一个校验函数；原先的 `fromTui` 信任标志删除
  - 强度写进 README：挡得住被脚本/Agent 包装的调用；**挡不住已以你身份运行且知道口令的程序**——那是权限边界的事，应用层无解

- **降挡不再依赖 TUI**（`simon.cs`）
  - 关键修正：原先挡位 3 下 `sip simon level` 会被 `SimonCheckBlock` 提前拦掉（level 3 = 拒绝一切 CLI，豁免里只有 `simon status`）——**降挡的唯一通道就是 TUI**。只改 `SimonCli` 在挡位 3 下根本走不到。现在 `simon level` 一并放行，由口令门把关
  - 修掉一处把降挡确认建在**缓存值**上的问题：TUI 侧改用 `CurrentSimonLevel()`（凭据库权威值）而非 `LoadSettings().SimonLevel`
  - 升档（收紧）保持任意通道放行：Agent 发现异常要能立刻收紧
- `--json` 下的降档请求直接返回结构化 `SIMON_LOCKED`，不再把人类提示文字混进 JSON 流

### Fixed

- **多栏阅读读不了：左右两栏内容对不上，读左栏要滚到底、再翻回右上角**（`web/index.html`）。原因是多栏只是一句 CSS `column-count`，整篇仍是一条纵向长滚动。现在多栏时**横向翻页**：一页 = 视口高的 N 栏，用 `transform` 平移翻页；←/→、PageUp/PageDown、空格、滚轮、右下角 ‹ › 都能翻，并显示 `3 / 12` 页码。
  - **安全优先的两条**：页数按「自然高度摊到页高」与「横向栏条宽度」两种估算取**较大值**——宁可多一页空白，也不能少算一页（少算会让后面的内容被裁掉且无法到达）；**量不出多于 1 页就退回原来的滚动**，绝不把内容裁没。任何异常也回退成能滚动的老样子。
  - 图片是懒加载的，加载完后高度会变 → 自动重新分页；字号/行距/栏宽/窗口尺寸变化同样重新分页。**单栏阅读完全不受影响**。
- **抓全文把网页拍平成一坨：没有段落、没有图片，连页面按钮符号都收了进来**（`sipcore.cs` `FetchAndExtract`、`Web.cs`）。原来用的是 `InnerText`——它把整页文字**无分隔地拼起来**（标题与正文粘成一行、图片与链接全丢）。现在改为先取 `<article>/<main>/<body>`，再交给**已有的 `HtmlToMarkdown`**（保留标题/段落/列表/引用，并把图片相对地址按原文 URL 转成绝对）；Web 端则必须**先把 Markdown 渲染成 HTML 再净化**——少了这一步，`#`/`**`/`![]()` 会以纯文本显示，那正是"排版很乱、没图片"的直接原因。TUI 同步去掉 `EscapeMd`（缓存已是 Markdown，转义反而会显示成 `\#`、`\*\*`）。
  - 实测（你博客里正文最长的一篇）：落盘由"一整坨"变成 **278 行 / 138 处段落分隔**，标题与链接保留；图片路径实测可渲染，而 Markdown 里嵌的 `<script>` 仍被净化器剥掉（顺序正确：**先渲染、后净化**）。
  - 注意：**已缓存的旧全文仍是旧的平铺格式**（当初就是这么存进去的）；重新抓一次即可得到新格式。
- **导入 OPML 时看不到进度**（`Web.cs`、`sipcore.cs`、`web/index.html`）。新增 `GET /api/progress`，`ImportOpmlCore` 多一个可选回调（CLI 传 null，仍然是同一份实现），前端 700ms 轮询显示「导入中 3/21 · 正在抓 …」——原先只有一条永远不变的提示，用户没法判断是在跑还是卡住了。

- **Web 抓全文缺少免责声明同意门，而且替你写下了同意**（`Web.cs`、`sipcore.cs`）。CLI 有一道一次性同意门（要手打短语），Web 端却直接传 `yes: true` —— **既跳过门，又替用户把同意写进 `fulltext_consent.txt`**：一次点击 = 永久同意，而用户从没见过那段声明。现在两端共用同一份同意标记与**同一段声明原文**（`FulltextDisclaimer()`，避免两边措辞不一致）：未同意 → `428 FULLTEXT_NEEDS_CONSENT` + 声明，前端弹确认面板，点「同意并继续」带 `consent:true` 再来一次才记录。实测：`{}` → 428 且**不写标记**；`consent:true` → 过门并写标记
- **每次 `--start` 都问"要不要设 Web 密码"**（`Web.cs` `FirstRunWebPasswordSetup`）。原先的提前返回条件是 `setupDone && 已设密码` —— 于是"设过一次、选了跳过"的库**每次启动都被问一遍**。现在：已设密码不问；没设密码但**仅本机绑定**也不问（裸链接 + 终端里的引导令牌本来就是设计好的用法）；只有**非本机绑定 + 无口令**才提醒——那时 OpenClaw 的教训才适用
- **zh-Moe 缺 41 条译文，界面里成片冒出英文键**（`languages/zh-Moe.json`）。因为 `Lang.T` 查不到键就回落显示英文原文，漏译不报错。缺的全是 `ingest`（证据/标签/监控）那批，已补齐（736 → 777 键，0 缺失、0 重复，换行风格与另两个文件一致）。**并加了常驻用例 `LangParityTests`**：`zh-CN` 有而 `zh-Moe` 没有的键 → 测试直接红并列出键名——漏译不再靠人肉发现

- **测试套件泄漏系统凭据，把用户的 Windows 凭据库顶满**（`tests/Sip.Tests/TestHost.cs`）——这是本次最隐蔽的一个，值得单独记。
  - **现象**：`sip simon level` 改不动、`sip aikey` 存不进、Web 登录会话建不出来。三者都**只表现为"改不动"，不报任何错**
  - **根因**：旧版加密路径每个测试实例都往凭据库写一把随机密钥，用 `SIP_SIMON_KEY_NAME` 做了**名字隔离但从不清理**。502 个 Debug + 25 个 Release 实例之后，实测 **756 条 sip 条目里 746 条是测试垃圾（占整个凭据库 842 条的 88%）**，`CredWrite` / `cmdkey` 开始返回 `ERROR_NOT_ENOUGH_MEMORY`
  - **处置**：删除 746 条测试条目（保留 9 条真实条目），**删掉第一条之后 `cmdkey` 写入就立刻恢复**——因果确认。`SipInstance.Dispose` 现在会清掉本实例写的凭据（本体 + `_level`），名字隔离之外补上清理
  - **排查方法写进 README**：`cmdkey` 写探针的**错误码是关键**——8 = 存储满，5 = 权限。两者症状都是"写不进"，但修法完全不同
  - 注意：`ERROR_NOT_ENOUGH_MEMORY` 与凭据内容大小无关（实测那条凭据只有几十字节），是**条目数量**触顶
- **`SimonLevelSet` 缺 try/catch 导致进程未捕获崩溃**（`simon.cs`）。凭据库写入失败时（实测 Windows 返回 `ERROR_NOT_ENOUGH_MEMORY`，`CredWrite failed`）整个进程崩掉，且崩在安全函数里。现在返回是否写入成功，调用方明确报「系统凭据库写入失败，挡位未改变」并置非零退出码；**不回退去信 `sip_settings.json`**——那样「改 JSON 无法降挡」这条不变量就破了。本次测试当场复现了这个崩溃（审计清单第 28 条）。
- **`sip -l --limit N` 把 N 当成了源编号**（`sipcore.cs`）。位置参数取的是"第一个不以 `--` 开头的参数"，而 `--limit` 的值正好符合——于是 `sip -l --limit 2` 静默列出**2 号源的文章**，而不是源清单。现在先把被 flag 吃掉的下标记下来，再挑位置参数。回归用例 `ListAll_WithLimit_DoesNotUseTheLimitValueAsFeedNumber`
- **登录页设了密码后登不进去**（`web/login.html:415`）——本轮最"要命"的一个。页面里**没有 `id="forgot"` 的元素**，却对它调 `addEventListener`：这行抛 `TypeError`，而它位于**注册登录表单 submit 处理器之前**，于是后面全部不执行，点「登录」没有任何反应（表单退化成浏览器原生提交）。这行是设计稿遗留的死链，直接删除——密码重置本来就只能走 `sip webpass`（需要真实终端），补一个点不动的按钮只会更糟
  - 顺带把两个页面的 `<script>` 抽出来过了 `node --check`：语法均有效（这类"少一行就整页失效"的问题，光读 diff 很难发现）
- **连点「添加 / 更新 / 同步」会真的重复下载，而且不给在途提示**（`WebServer.cs`、`web/index.html`）。三处一起补：
  - 服务端**同键在途护栏**：第二次请求直接 `409 ALREADY_RUNNING`，不排队、不重复下（实测 5 个并发只有 1 个真的跑）。客户端禁用按钮只是礼貌，服务端这道才是保证
  - 客户端**常驻状态行 + 按钮禁用**：原先只有 1.6 秒就消失的 toast，而下载要几秒到几分钟——用户看到的正好是"点完就没动静"，于是只能连点试探，而连点又会重复下载
  - **真实进度**：更新/同步/添加现在返回 `added`/`items`，界面说"新增 N 篇"或"没有新内容"，而不是笼统的"已更新"
- **文章列表按入库顺序排，而不是发布时间**（`WebServer.cs` `HandleFeedArticles`）。`ORDER BY Id DESC` 让网页把**最旧**的一篇排在最前（实测源 1 首条是 2025-06 的文章），而 CLI 用 `ORDER BY i.PublishDate DESC`——同一个库、两个通道、两种顺序。现在与 CLI 一致（`PublishDate DESC, Id DESC`，显示序号同步改）
- **OPML 导入只能走 CLI，入口还和「添加订阅源」分家**。现在合并成同一个入口：添加面板里多了「导入 OPML」，新增 `POST /api/feeds/opml`（请求体就是文件内容），**与 CLI 共用同一个 `ImportOpmlCore`**（不写第二套解析，否则迟早出现"终端说导入 3 个、网页说 0 个"）
  - 顺手堵 XXE：OPML 是不可信 XML，解析改为 `DtdProcessing.Prohibit` + `XmlResolver = null`；实测带外部实体的 OPML 返回 `400 OPML_PARSE_FAILED`。请求体上限 2 MB
  - 导入期间同样受在途护栏保护（并发只跑一个）；导入失败逐个源给出原因（只回前 20 条）
- **首次 Web 向导在 stdin 到 EOF 时会替你设一个没人知道的密码**（`WebAuth.cs`、`sipcore.cs` `ReadSecret`）。`Console.ReadLine()` 在 EOF 返回 `null`，而代码里 `null != "skip"` 会走"设密码"分支。实测一次自动化启动后，数据目录里出现了未知口令的 `web_auth.json`，此后每次打开网页都被登录页挡住——**静默锁死**。现在：
  - 三个提问点（设密码？/ skip 确认 / I-UNDERSTAND 确认）在 EOF 时分别按"跳过设密码""保持安全的默认绑定"处理，绝不替用户做危险选择
  - `ReadSecret` 补上 `'\0'` 过滤——方向键/F 键的 `KeyChar` 是 `'\0'`，无条件 `Append` 会往口令里塞 NUL，**永远校验不过且看不出原因**（`PromptSecret` 早修过这个坑，这里漏了）；读不到控制台时返回空串，长度校验会拒绝它，**不会**设出未知口令

### Added

- **本地滚动备份（每天一份）**：每天首次启动自动用 `VACUUM INTO` 给 `rss.db` 留一份**一致性快照**，放在 `readwithhotsoup/backups/`，保留最近 7 天（`BackupKeepDays`）；备份目录总量超 2 GiB 时从最旧的开始淘汰，至少保留 1 份。
  - 用 `VACUUM INTO` 而非裸文件复制：WAL 模式下直接拷 `.db` 可能拿到不一致的状态
  - 先写 `.tmp` 再改名：中途失败不会留下一个「看起来像当日备份」的残file
  - 失败静默降级，只打一行原因，**绝不阻塞启动**
  - `sip simon status` 显示概况（`本地备份: N 份 · X MB · 最新 …`），JSON 对应 `backups` / `backupLatest`——**备份找不到就等于没有备份**，所以让它可见
  - 恢复：把 `backups/rss-YYYYMMDD.db` 复制回 `readwithhotsoup/rss.db`
  - 为什么是备份而不是加密：个人库最现实的丢失原因是程序自身或误操作（2026-08-30 那两次自愈就是例子），不是被偷
  - 验证：真实库生成 4.58 MB 备份，文件头为合法 SQLite；同日二次运行正确跳过；塞 11 份后正确淘汰到最新 7 份

### Fixed

- **完整性自愈不再删除数据库**（`sipcore.cs` 的 `CheckMainDbIntegrity`）。原先 `File.Move` 失败时的 catch 分支里有一句 `try { File.Delete(dbPath); }`，而**紧挨其上的注释写的是「本次跳过自愈，下次启动再试」——注释与代码完全相反**。触发条件是「文件被其他进程占用（并发启动）」，而这恰恰**不是损坏**：库是好的，只是被另一个进程拿着。换言之，双击两次或并发启动，健康库可能被直接删掉。
  - 现在 `rss.db` 在任何路径下**只改名、永不删除**
  - 健康判定从 `bool` 改为三态 `DbHealth { Ok, Corrupt, Unavailable }`：「被占用/读不到」与「明确损坏」彻底分开。只有 `SQLITE_CORRUPT(11)` / `SQLITE_NOTADB(26)` 算损坏；`CANTOPEN / PERM / IOERR / READONLY / FULL` 一律判 `Unavailable`，**什么都不动**
  - `repair_db` 改为**改名成功之后**才记账。原先先记账再改名，改名失败时日志会谎称「已保留现场」——2026-08-30 那两条记录就是这么来的（仓库里找不到任何 `.corrupt-*` 文件，证明两次 `Move` 都失败）
  - 顺带移除 `IsSqliteFile` 在本路径的使用：它读不到文件头时返回 `false`，会直接把「被占用」变成「损坏」，是三态化要解决的同一个病根
  - 隔离验证：造一个非 SQLite 的假库 → 被改名为 `.corrupt-*` 且**内容逐字节保留**（哨兵字符串完好），新库正常重建
  - 未验证：`Unavailable` 分支在沙箱里模拟不出「被占用/读不到」（管理员账户会绕过 `icacls` deny，只读属性也不阻塞读取），仅经代码审查。但**危险的那一行已结构性消失**——全项目已无删除主库的代码

### Removed

- **挡位 3 的数据加密（SQLCipher + AES）整体移除**。它付了密钥管理与丢库风险的全部成本，却只防「目录被拷走」一种场景：密钥存在系统凭据库，对以本机用户身份运行的程序是透明的，**挡不住本机其他程序读取**——而那才是本地应用最现实的威胁。而且升级加密时会在同目录留下**永久保留**的 `rss.db.plaintext.bak` 明文副本（`SimonEncryptTests` 还断言它必须存在），等于连那半个收益也没拿到。要挡住本机进程，应用层无解，只能靠全盘加密或独立用户账户。
  - **保留**：挡位 1/2/3、完整性自愈、SSRF 与终端注入防护。挡位 3 现在只表示「非交互禁全部写」
  - **移除**：`sip simon export-key` / `import-key`。后者写的是 `CredSet("simon_db_key", ...)`，缺作用域后缀（`SimonKeyName()` 返回的是 `simon_db_key_<数据目录哈希>`），**从未真正生效过**
  - **移除依赖**：`SQLitePCLRaw.provider.e_sqlcipher`、`SQLitePCLRaw.lib.e_sqlcipher`，以及入口的 `SetProvider(e_sqlcipher)`。`Microsoft.Data.Sqlite` 等版本本次不动（升 10.x 属独立改动）
  - **新增启动护栏**：检测到旧版遗留的 `.db-encrypted` 标记时明确报错并退出——不会静默变成 `file is not a database`，更不会被当成「损坏」而改名/删除
  - **删除测试**：`tests/Sip.Tests/SimonEncryptTests.cs`

### Changed

- README：去掉「挡位 3 加密全部数据」「其他软件读不到你的数据」等已不成立的表述；顺带修正中英 README 测试用例数不一致（38 vs 97）——准确数字待重新跑测试后回填

## [1.3.0] - 2026-09-11

### Added

- **PDF PageCount**: Imported PDFs store page count in `Items.PageCount` (old DBs migrate automatically)
- **Selective PDF vision**: `sip --show <id> --json --vision --pages 3-7` (also `1,5,9`, `-10`, `50-`) rasterizes only the requested pages for agents / vision models
- **PDFtoImage** dependency for page rasterization (replaces ad-hoc image scrap for PDFs)

### Changed

- **publish.ps1**: Stage to a temp path first, then copy into `publish/` — fixes MSB3094 when the project path contains an apostrophe (`hahahotsoup's`)
- **README**: Dropped the DeepSeek pricing notice

### Deprecated

- **TUI**: Still available, but will be phased out. New investment is CLI + Web (sip-web). Prefer `sip` flags and the local web UI going forward; TUI removal timing will be announced in a future Release Note.

## [1.1.0] - 2026-08-01

### Added

- **RSS Feed Management**: Add, remove, and update RSS subscriptions
- **Article Reading**: TUI interface for reading articles
- **Full-text Search**: Search articles by keywords
- **Semantic Search**: AI-powered similarity search
- **Today's Soup**: Daily recommendations
- **Telemetry**: Optional local reading behavior tracking

### Changed

- Initial release with core RSS reading functionality

## [1.0.0] - 2026-07-15

### Added

- **Core Architecture**: SQLite-based local-first design
- **Feed Management**: RSS feed subscription and management
- **Article Storage**: Article content and metadata storage
- **Basic Search**: Simple text search functionality

### Changed

- Initial release
