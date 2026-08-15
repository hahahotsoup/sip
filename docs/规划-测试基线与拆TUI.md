# 规划:测试基线 + 只拆 TUI

> 状态:已定稿 · 2026 年(项目 v1.1.4 阶段)
> 受众:作者本人(执行细节)+ 未来接手者(契约说明)

## 0. 目标与铁律

### 目标

把「能跑的个人工具」变成「敢改、能接手」的项目,分两步走:

1. **P0 测试基线** —— 先织回归网,再动代码
2. **P1 只拆 TUI** —— 把 TUI 交互代码从 RssReader.cs 挪到独立文件

**明确不做的事**:不按 Domain/Storage/CLI 分层拆 RssReader.cs 的文件组织——CLI 与核心必须粘在一起(CLI 本质是核心的门面,拆开成本高收益低)。

### 铁律:契约冻结清单(任何阶段不得破坏)

| # | 契约 | 说明 |
|---|---|---|
| 1 | CLI 命令名 / 参数 / `--json` 输出结构 | 脚本与 Agent 依赖 |
| 2 | 退出码 0=成功 / 1=通用错误 / 2=网络 / 3=资源未就绪 | 脚本用 exit code 判断成败 |
| 3 | `--ignoresafeannouncement` 行为 | Agent 调用用 |
| 4 | 数据目录布局 `readwithhotsoup/`(rss.db + JSON + fulltext/) | 用户数据 = 契约 |
| 5 | 语言文件格式(外置可编辑、键=英文原文) | 用户可定制 |

## 1. 百万级文章可行性(规划前置结论)

**有戏。瓶颈与代码组织无关,是算法与存储结构问题;拆不拆文件不影响性能。**

| # | 瓶颈 | 现状(已核实) | 改造 | 百万级目标 |
|---|---|---|---|---|
| 1 | `--grep` 全文搜 | `LIKE '%kw%'` 四字段全表扫 | **FTS5 + trigram 分词**(中文子串可搜) | <1s |
| 2 | `--search` 语义搜 | 全量向量载入 + 全量余弦 + 拉 Content 列 | **sqlite-vec ANN** + 粗筛后回表 | <2s |
| 3 | 去重 / 今日哈汤 | 窗口内全文载内存 + 段落对比较 | minhash/simhash 指纹 + 窗口抽样 | 分钟 → 秒 |
| 4 | 导入 | 逐条 INSERT 无事务 | 整源更新包一个事务 | 10 万篇分钟 → 秒 |
| 5 | TUI 启动 | 每源全量 `LoadArticleNodes().ToList()` | 懒加载 / 分页 | 启动即用 |

- **A 类场景**(百万历史文章、日常只看近期):现在就能扛,`--today` 窗口天然限制在近期,只需 TUI 懒加载(#5)
- **B 类场景**(百万全部在线、随时全文/语义可搜):需做 1-5,合计约 1~2 周

存储层本身无瓶颈:SQLite 单文件承载 10~50GB、百万行可行,WAL + 完整性自愈已在。

## 2. P0:测试基线(1~2 周,必须最先)

### 为什么是进程级黑盒测试

主程序是顶层语句,所有函数是 `Program` 类的 private 方法,单元测试够不着;而 CLI 才是「Agent 一等公民」的契约面,黑盒测试正好验证真实用户视角。

### 方案

- 新建 `tests/Sip.Tests`(xunit + Microsoft.NET.Test.Sdk)
- 测试框架:构建后把 `sip.exe` + `languages/` **复制到临时目录运行** → 数据目录天然隔离(主程序数据目录固定在 exe 同级,不可配置,复制 exe 即隔离),测完即删,不碰真实数据
- fixture 库:测试内用 Microsoft.Data.Sqlite 按已知 schema 构造(Feeds/Items/Models/Vectors)

### 首批用例

| 用例 | 断言 |
|---|---|
| CLI 契约快照 | fixture 库上跑全命令,`--json` 结构 + 退出码 |
| SSRF 矩阵 | fixture 插入 Link 指向 `127.0.0.1` / `169.254.169.254` / 私网段的 item,`--fulltext --yes` 必须拒绝 + 正确退出码 |
| dedup 不变量 | hide 自己必须失败;重复 hide 幂等;环的预防 |
| 版本归档 | 同 Guid 内容变化 → 旧版归档 + 新版本插入 |

### CI

- `.github/workflows/ci.yml`:build → test → 三平台 publish 冒烟

### 验收

- CI 绿;全命令快照覆盖
- **任何重构代码合入前必须全绿**

## 3. P1:只拆 TUI(纯搬移,只搬不动)

### 边界判定原则

**递归调用图只被 TUI 路径触达的函数 = TUI 专属,搬走**。共享函数(CLI 也在用)留在核心,哪怕名字像 TUI。

### 技术方案

新建 `TuiApp.cs` 定义 `public partial class Program`(顶层语句生成的 Program 类是 partial,C# 官方支持):

- 搬入函数原样搬移,唯一机械修改:函数签名加 `static`(类成员需要)
- 入口文件顶层语句与 CLI 区调用点(`await RunTui(dbPath)`、`RunFullscreenReader(...)`)跨文件依然有效(同类成员)
- **partial 类无法访问入口文件的 Main 局部变量(`dataDir` 等)** —— 已核实 TUI 区无 `dataDir` 引用(一律通过 `dbPath` 参数),方案无坑
- 缩进原样保留(类内 0 缩进方法合法);后续可 `dotnet format` 统一

### 搬移清单(调用图判定,11 个区域,约 2380 行)

**整棵搬(闭包容器,不能拆内部)**:
- `RunTui`(原 L2866-4442,约 1577 行)—— 内含 ~50 个闭包局部函数:`ToggleSidebar`、`Telemetry*`(4 个)、`UpdateStats`、`RebuildTree`、`ShowSelectedContent`、`ShowVersionHistory`、`ArchiveSelectedFeed`、`DeleteSelected`、`RefreshAllFeeds`、`RunNetworkOp`、`AddFeedDialog`、`SearchDialog`、`SummarizeSelected`、`ShowHelpDialog`、`ShowAboutDialog`、`Ask`、`OpenUrl`、`ToggleLinkNav`、`ToggleContentMode`、`OpenCurrentLink`、`CycleLink`、`ShowCmdBar`、`RunCommand`、`SwitchLanguage`、`RebuildStatusBar`、`DoTuiSearch`、`DoTuiGrep`、`InitConfigDialog`、`IndexSelectedFeed`、`ReindexAll`、`SyncDueFeeds` 等
- `ShowTodayPage`(含局部 `MarkRead`/`RefreshList`/`ShowContent`/`SelectAndShow`)

**顶层函数搬走**:
- `RunFullscreenReader`、`ShowFullscreenReader`(含局部 `OnKey`)
- `DashboardStats`、`ShowFeedManager`(含局部 `Rebuild`)、`ScheduleDisplayName`、`AdjustHour`、`SchedulePickerDialog`、`ScheduleCustomDialog`、`FeedEditDialog`、`AddFeedManagerDialog`
- `TodayStartScreenLines`、`ShowStartScreen`、`CreateMarkdownView`、`LoadArticleNodes`、`MarkdownImageLoader`
- `ShowInsightsPage`、`ShowTextDialog`、`RunCliCommandInTui`、`StrikeText`

**必须留在核心的「疑似 TUI」函数(调用图证据)**:
- `BuildArticleMarkdown` —— CLI `--export` 在用
- `HtmlToMarkdown` / `WalkHtml` —— 被 BuildArticleMarkdown 用
- `StripHtml` / `StripControlChars` / `CjkSpace` —— CLI 大量在用
- `GetDisplayNum` —— CLI 在用
- `TrimFulltextCache` —— 核心 `DoFetchCore` 在用

### 搬完后的文件结构

```text
RssReader.cs   ← CLI + 核心(约 6400 行,不再增长 TUI 代码)
TuiApp.cs      ← 新增:RunTui 及全部 TUI 动作(约 2400 行)
Tui.cs         ← 自绘组件(不变)
Sumenia.cs     ← 遥测(不变)
```

### 验收

- `dotnet build` 0 错误
- CLI 全命令冒烟(临时目录)+ P0 测试全绿
- TUI 手动走查(启动 / 侧栏展开 / 今日哈汤 / 报告页)

## 4. 后续(P2-P4,不在本轮范围)

| 阶段 | 内容 | 验收 |
|---|---|---|
| P2 领域硬化 | Status 枚举 + DB CHECK;dedup 关系入表(环/幂等由结构保证);整源更新包事务 | 评审 dedup 缺陷各配一条自动化测试 |
| P3 性能改造 | 上表 1-5:FTS5 → sqlite-vec → 去重指纹 → 批量事务 → TUI 懒加载 | 基准脚本:grep <1s、search <2s @100 万 |
| P4 安全收尾 | UrlPolicy 统一(打开/保存/导出/sanitize);响应体大小上限;发布脚本排除运行时数据 | 安全清单全关 |

## 5. 风险与对策

| 风险 | 对策 |
|---|---|
| 搬移中误搬共享函数 | 编译能发现;调用图先行判定(本规划已做) |
| 行为漂移(编译不报) | 只搬不动;P0 快照测试兜底;CLI 冒烟 |
| RunTui 闭包拆坏 | 整棵搬,不拆内部;局部函数与局部变量绑定 |
| pragma CS0618 配对错乱 | 按区域整块搬(disable/restore 同区域);对编译无碍,仅影响 warning 范围 |
| 编码问题(BOM/CRLF) | 已核实原文件 BOM+CRLF,写回保持一致,git diff 无假阳性 |

## 6. 时间估计

- P0:1~2 周
- P1:2~3 周(实际为机械搬移,脚本辅助可压缩到 1~2 天搬完,余量在验证)
- P2-P4:另行规划

每个阶段可独立交付、可中断。
