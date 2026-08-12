# 🍲 sip

> **简体中文** | [**English**](./README.en.md)

> ——「品，你细品。」
>
> **读文如喝汤，眼睛别总是往碗里瞟，闭上眼睛享受为先。**

sip 是一堵信息防火墙：让你和你关心的 AI，只看到你信任的内容。

它不是算法推荐阅读器，也不是让你"刷更多"的资讯流，而是一个**本地优先的透明信息过滤器和阅读辅助器**——你指定信源，sip 守护并辅助筛选信源，同时提升阅读体验。你和你的 AI 代理从一份干净、可追溯的数据中获取答案。

📖 **完整文档见 [sip Wiki](https://sip.hotsouprealm.top/)** —— 安装、CLI/TUI、AI 命令、隐私等全部细节都在这里。

同时厚脸皮一下：[https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
关注热汤茶馆喵 关注热汤茶馆谢谢喵 🐾

---

## 快速开始

> ✅ **经过完整测试（v1.1.4，综合 8.4/10）**：功能、安全、性能、稳定性、并发、故障注入均已实测，见 [完整测试报告](./sip-完整测试报告-2026-08-12-最终版.md)。sip 的数据文件是互通、开放的标准格式（SQLite + 明文 JSON），可随时更换软件核心以迁移，不会被锁死。

去 [Releases](https://github.com/hahahotsoup/sipintui/releases) 下载**单文件可执行程序**，直接运行：

```bash
./sip.exe            # Windows：进入 TUI（首次启动自动创建 readwithhotsoup/ 数据目录）
./sip.exe --help     # 或直接用 CLI
./sip.exe --version  # 查看版本号
```

- **单文件 + 自带官方翻译**：语言文件内嵌 exe，只拷一个 exe 也能跑
- **框架依赖**：需预装 [.NET 10 运行时](https://dotnet.microsoft.com/download)
- **数据目录**：`readwithhotsoup/`（SQLite + 文件缓存，全本地，拷走即迁移）

从源码构建、`publish.ps1` 全平台打包等方法，见 [Wiki · 快速开始](https://sip.hotsouprealm.top/指南/快速开始.html)。

---

## 核心能力

| 模块 | 一句话 |
|------|--------|
| 📚 **智能归档** | 版本追踪、内容 Diff、快照归档、阅读进度记忆、来源健康 |
| 📖 **辅助阅读** | TUI 文件夹视图、沉浸阅读、全文抓取、Markdown 渲染、今日哈汤 |
| 🤖 **AI 友好** | 语义搜索（RAG）、LLM 摘要、统一 JSON、结构化退出码 |
| 🕊️ **隐私** | 本地遥测苏暖泉（Sumenia）默认关闭、仅本地保存、绝不自动上传；开启后 `search` 事件记录**完整查询词**（仅本机，可 `telemetry export/clear`，不会上传） |
| 📈 **阅读情况报告** | `sip --insights` 按源呈现阅读事实（打开/读完/完成率/♥🤖点赞/AI调用次数/健康），**决定在你**；可设定时提醒 |

> 完整功能清单与每项细节，见 [Wiki · 功能](https://sip.hotsouprealm.top/功能/)。

---

## 快速上手示例

```bash
sip -l                  # 列出订阅源
sip -d https://xxx/rss  # 添加 RSS 源
sip -u 1                # 更新 1 号源
sip --show 12           # 全屏阅读
sip --search "RAG"      # 语义搜索（先 sip --init 配置 AI）
sip --today             # 今日哈汤
sip telemetry enable    # 开启本地遥测（仅本地；开启后才能用 --insights）
sip --insights          # 阅读情况报告（按源事实，决定在你）
sip --insights-interval 7d   # 每 7 天提醒一次报告
```

> 🔒 `sip --init` 涉及录入 API Key，**仅在真实交互式终端手动运行**（安全考虑，不接受管道/脚本输入）；AI 无法代跑，需用户手动配置。

完整 CLI 参数、TUI 快捷键、AI 命令与错误码，见 [Wiki · 使用说明](https://sip.hotsouprealm.top/使用说明/命令行.html)。

---

## 接入 QQ / 微信 / Discord / Telegram 机器人

用本地 Agent（**OpenClaw** 或 **Cherry Studio**）把 sip 挂到消息渠道，你在群里 @ 它，它用 sip 的检索/摘要能力，只从你信任的源回答。

把三样东西喂给 Agent 即可：

1. **独立 `sip.exe`**（[Releases](https://github.com/hahahotsoup/sipintui/releases) 下载单文件）
2. **`sip-rss` skill**（`.opencode/skills/sip-rss/SKILL.md` 或 `sip-skill.zip`）
3. **系统提示词**（见 Wiki，让 Agent 只用 `sip --search/--grep/--show/--summary` 检索）

> Telegram、Discord 由 OpenClaw 原生支持；QQ、微信需第三方桥接（OneBot/go-cqhttp、Wechaty 等）。完整步骤、提示词与示例见 [Wiki · Bot 接入](https://sip.hotsouprealm.top/使用说明/Bot接入.html)。

---

## 具体场景

### 场景一：给 AI 装「护栏」，查资料只信白名单

AI 写东西时总引用搜狐、百家号？让它只查你订阅的源：

```bash
sip --search "LLM Agent 综述" --json     # 语义检索，返回可信命中
sip --search "RAG" --feed 1 --json        # 限定单一订阅源
```

AI 只会拿到你订阅源里的内容，从此告别垃圾引用。

### 场景二：验证一篇「被改过」的文章

作者偷偷改观点？看它怎么变的：

```bash
sip --versions 12        # 看 12 号文章的全部历史版本
sip --diff 12 v1 v3      # 对比 v1 与 v3 的正文差异
```

### 场景三：每天读点值得读的（今日哈汤）

不想大海捞针，让 sip 先选 5 篇：

```bash
sip --today              # 顶部=今日变化摘要（按源新增数 / ⚠高频源 / 被改过+改动概览）+ 今日推荐
sip --today --json       # 结构化输出（digest 里的被改过 itemId 可直接 sip --diff 看作者改了啥）
```

### 场景四：给父母配置好「看得安心」的源

配好白名单（央视新闻、本地气象局、你信任的医学号）后，他们只需：

```bash
./sip.exe                # 打开即用，只见你筛过的源
```

垃圾源已被挡在外面，不用自己分辨真假。

### 场景五：慢读长文

RSS 摘要太短，抓全文慢慢读：

```bash
sip --fulltext 12        # 抓取 12 号文章原文到本地缓存
sip --show 12            # 全屏阅读
```

### 场景六：找回很久以前看过的一篇

记不得标题，只记得关键词？不依赖 AI 的全文搜索：

```bash
sip --grep "量子纠缠"     # SQL LIKE 精确匹配标题/正文/摘要
```

### 场景七：让 bot 在群里帮你查

接 OpenClaw / Cherry Studio 后，群里 @ 它：

```
@bot 最近两天有什么值得读的更新？
@bot 帮我查一下"LLM Agent"相关文章
```

Agent 自动调 `sip --today` / `sip --search`，只从你信任的源回答。见 [Wiki · Bot 接入](https://sip.hotsouprealm.top/使用说明/Bot接入.html)。

---

## 为什么有 sip？

现在的信息环境有三个残酷事实：

1. **大模型引用垃圾信源**——DeepSeek、豆包、ChatGPT 随手引用搜狐、百家号、低质自媒体，而你没法告诉它"不要用这些"。
2. **算法把你关进茧房**——让你不停刷，却从不告诉你"为什么推这个"。
3. **你最在乎的人缺乏分辨能力**——父母、朋友面对标题党和谣言，没有技术手段保护自己。

sip 只做两件事——**确定性的规则** 与 **本地事实的存储**；凡需要判断、解释、对话的，都交给 agent 与用户。它不会让你刷更多，但会让你读得更安心。

> **当你打开 sip 时，你知道你今天读到的东西是可信的；当你的 AI 调用 sip 时，你知道它引用的来源是可靠的。**

愿下次相见，别来无恙 🍲

---

## 路线图：从「信息仓库」走向「信息摄入管理系统」

sip 的核心定位是**事实优先**——只做确定性的规则与本地事实的存储，凡需要判断、解释、对话的都交给用户与 agent。以下规划都围绕这一原则展开，**宁可慢、不可把判断包装成事实**。

### 已落地：阅读情况报告（Insights）

- `sip --insights` 按源呈现**阅读事实**（打开/读完/完成率/♥🤖点赞/AI 调用次数/订阅与积压），并给出**可解释的原因**（如「30 天打开 0 篇」「完读率 3%」），**决定在你**。
- 遥测已把 AI 调用（摘要/搜索/嵌入）按**文章与源**归因，报告可按源统计。
- 入口：CLI `--insights` / TUI 报告页（`P` 键 / 命令 `report`）；可 `--insights-interval` 定时提醒。

### 已落地：今日变化摘要（diff 招牌）

- `sip --today` 顶部是**今日变化摘要**：每个源各新增多少、⚠ 高频源（腹泻式更新）单独折叠、**被作者改过**带改动概览（标题改没改/增删行数/约±字数，纯 diff 计数、零 LLM）、⚠ 可能同文（跨源重复）。
- 「被作者改过」每篇给 `sip --diff <id>` 入口，点开看作者改了啥——把版本追踪的 **diff 能力做成招牌**，让「帮我看到变化」落到实处。

### 已落地：跨源去重（用户确认版）

- `sip --dedup scan` 检测**可能同文**（段落重合度，跨源、零 LLM）；`hide` 把重复篇标 `Status='dedup'` 并记规则，之后**从搜索/全文/摘要/计数自动排除**，且 `--sync` 导入时跳过（防卷土重来）。
- **隐藏 ≠ 删除**：数据保留、非破坏可恢复（`--dedup undo`）；manage 界面按 `i` 查看/撤销已隐藏。
- 检测是事实、决定在你、规则持久——正是 Source Policy「记住你的决定」的雏形。

### 已落地：报告术语/事实重构 + Source Policy 闭环

1. **术语与事实分离**——报告区分「状态」（仅技术故障：拉取失败/长期未更新）与「活跃度」（关于你的行为），**低阅读 ≠ 低价值**；删除了「建议退订/可考虑精简」这类把行为包装成价值判断的措辞，改为 `reasons` 事实原因列表。
2. **不引入黑盒评分**——不产出 `source_score = 0.72`；每条 `reasons` 都是可解释的（指标 + 数值），判断留给用户。
3. **Source Policy 闭环**——`sip --policy` 把「你的决定」存成规则并应用（`lower_frequency` 改更新频率 / `archive` 归档 / `tag` 打标签 / `keep` / `unsubscribe` 记录备注），`-l` 显示规则标记。**createdBy 永远是 `user`，AI 永不自动写**。

   ```
   读 → 分析(Insights) → 你确认 → source rule → 信息流调整
   ```

### 已落地：推荐源模板（Onboarding）

- `sip --onboarding` 按领域（AI/开发/科技公司）列出推荐源，`add <分类> <索引|all>` 一键添加；`templates.json` 可编辑自定义。

> 这些步骤的目标是把 sip 从「信息仓库」变成「信息摄入管理系统」——Insights 是分析层，Source Policy 让「你的决定」持久生效，onboarding 降低首次使用门槛。

### 工程：项目拆解（单文件 → 多模块）

`sip` 早期是单文件 `RssReader.cs`。已拆出 `Sumenia.cs`（遥测）与 `Tui.cs`（视图类）；**拆解到此为止，不再继续**（其余代码保持单文件，降低维护复杂度）。

---

## 许可证

遵循 GNU General Public License v3.0 (GPL-3.0)
