# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

- **Web 也能导出 OPML**（`GET /api/feeds/opml`，入口在「添加订阅源」面板里、与导入同一处）：`Content-Disposition: attachment; filename="sip-feeds.opml"`，前端一个 `<a download>` 直接指向它。生成逻辑与 CLI **共用 `BuildOpml`**（不写第二套，否则两边导出的文件迟早有细微差别）。实测：200 + 正确响应头 + 21 个源全部在内
- **`--start` 横幅提醒遥测未开启**：阅读报告全部来自本机遥测、而遥测默认关闭，不提醒的话用户打开网页只看到"没有数据"却不知道该做什么。终端是唯一能给出可执行下一步的地方。实测输出：`Telemetry : off · the reading report will have no data (turn on: sip telemetry enable)`

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
