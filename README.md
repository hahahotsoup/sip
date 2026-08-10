# 🍲 sip

> ——「品，你细品。」
>
> **读文如喝汤，眼睛别总是往碗里瞟，闭上眼睛享受为先。**

sip 是一堵信息防火墙：让你和你关心的 AI，只看到你信任的内容。

它不是算法推荐阅读器，也不是让你“刷更多”的资讯流。它是一个**本地优先的透明信息过滤器和阅读辅助器**——你指定信源，sip 守护并辅助筛选信源，同时提升阅读体验，你和你的 AI 代理从一份干净、可追溯的数据中获取答案。

同时厚脸皮一下：[https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
关注热汤茶馆喵 关注热汤茶馆谢谢喵 🐾

---

## 为什么会有 sip？

现在的信息环境有三个残酷事实：

1. **大模型引用垃圾信源**——DeepSeek、豆包、ChatGPT 随手给你引用搜狐、百家号、低质自媒体，而你甚至没法告诉它“不要用这些”。
2. **算法把你关进茧房**——抖音/头条让你不停刷，但从不告诉你“为什么推这个”，更不让你选“我不想看什么”。
3. **你最在乎的人缺乏分辨能力**——你的父母、朋友，面对标题党和谣言时，没有技术手段保护自己。

sip 对这三个问题的回答很简单：

> **我既要站着，还要把信息读了**

---

## 核心设计原则

| 原则 | 说明 |
|------|------|
| **本地优先** | 数据在你自己手里（SQLite + 文件缓存），不需要账号，不上传阅读记录 |
| **透明决策** | 只看你订阅的源，没有算法黑箱；过滤规则就是你维护的订阅源列表 |
| **版本即事实** | 作者改了什么？什么时候改的？sip 帮你全程记录，不丢失任何历史 |
| **AI 只读白名单** | AI 摘要和语义搜索，只基于你信任的订阅源，杜绝低质引用 |
| **开箱即用，极轻量** | 单文件 exe，零依赖，启动即用；AI 能力按需调用，不预跑 |

---

## 它能解决什么具体问题？

### 1. 给 AI 装上一道“护栏”

当你或你的 AI Agent 需要查资料时：

- 让 AI 调用 `sip --search "xxx" --json`
- AI 只从你订阅的源中检索信息
- 从此告别“AI 张口就是搜狐和百家号”

### 2. 让信息变化“可见”

普通的 RSS 阅读器只告诉你“有篇文章”。

sip 会告诉你：

- “这篇文章在 8 月 1 日被作者修改过”
- “修改前它是这样说的，修改后它变成了那样”（`sip --diff 123 v1 v3`）
- “这个博客在过去一年里改了 12 次关键观点”

**你看的不再是静态页面，而是信息的演变轨迹。**

### 3. 帮你在乎的人远离信息过载

给你的父母配置好白名单（比如：央视新闻、本地气象局、你信任的医学公众号）。

他们打开 sip 后：

- 只看得到你筛选过的来源
- 摘要过短的文章会自动提示抓取全文，慢慢读
- 不用分辨真假，因为垃圾源已经被挡在外面了

**hahahotsoup注：我很清楚，tui的门槛太高了，所以等到这个程序成熟以后，avalonia也一并提上日程**

---

## 功能

### 📚 智能归档

- **版本追踪**：自动检测文章的每一次修改，保存 v1、v2、v3……
- **内容 Diff**：`sip --diff 123 v1 v3` 清晰展示变化
- **快照归档**：给整个订阅源打时间戳快照，永久保存某一时刻的完整状态
- **阅读进度记忆**：退出 TUI 后再回来，从上次读到的地方继续

### 📖 辅助阅读

- **TUI 文件夹视图**：订阅源 + 文章树形展开，键盘驱动（Vim 风格快捷键）
- **沉浸阅读模式**：一键隐藏所有侧栏，全屏读正文
- **全文抓取**：RSS 摘要过短时，`sip --fulltext <id>` 抓取原文到本地缓存（零改表）
- **Markdown 渲染**：HTML 自动转 Markdown，代码块/列表/链接完美呈现

### 🤖 AI 友好

- **全功能 CLI**：所有操作均可命令行调用
- **统一 JSON 输出**：`--json` 让 AI 稳定解析，无需写正则
- **Embedding 语义搜索**：基于向量检索，支持本地 Ollama / OpenAI / DeepSeek
- **LLM 摘要**：按需生成，结果缓存复用
- **结构化退出码**：`0` 成功 / `1` 通用 / `2` 网络错误 / `3` 资源未就绪，适合脚本编排

---

## 规划中（尚未实现）

以下能力已在设计里，但**当前版本还没有**，不会虚假宣传：

- 🔒 **来源身份 / 健康状态**：展示每个来源的名称/类型/作者/官网/更新时间，标记“多久没更新”
- 🔒 **白名单 / 黑名单过滤**（域名级、关键词级）+ **过滤日志**
- 🔒 **跨源文章去重**：同一内容被多个源重复推送时自动识别
- 📖 **系统 TTS 朗读**（Windows/macOS/Linux 原生语音）+ **作者音频优先**（检测 RSS 音频附件，优先播原声）
- 📖 **每日少量阅读（Sip Today）**：每天只给你 5~10 条真正值得看的内容

---

## 快速开始
### ai skill
代码里的https://github.com/hahahotsoup/sipintui/tree/main/.opencode/skills/sip-rss 内含一份skill，直接喂给ai即可

年年说，老有人忘，干脆放文首（无语/(ㄒoㄒ)/~~）

### 环境要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### 编译运行（单文件）

把源码直接发布成**一个单文件可执行程序**（`sip.exe` / `sip`），带上语言文件就能跑，不需要带一堆 dll：

```bash
git clone https://github.com/hahahotsoup/rssreader-core.git
cd rssreader-core
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false -o publish/win-x64
./publish/win-x64/sip.exe          # 进入 TUI 界面（Windows）
./publish/win-x64/sip.exe --help   # 或直接用 CLI
```

发布产物只有**一个 `sip.exe`**（单文件可执行程序，框架依赖，目标机需预装 [.NET 10 运行时](https://dotnet.microsoft.com/download)，体积很小）。

> **语言文件已内嵌**：`zh-CN.json` / `en-US.json` 等官方翻译会打进 exe 内部——就算你把整个发布目录只拷走一个 exe，首次（或每次数据目录缺失时）启动都会**自动恢复**默认语言，界面仍是中文。发布目录里的外置 `languages/` 文件夹是**给用户定制翻译用的**（改完即生效，内置副本不会覆盖你的修改）。

`-r win-x64` 换成目标平台即可，常见 RID：

| 平台 | RID |
|------|-----|
| Windows x64 / ARM64 | `win-x64` / `win-arm64` |
| Linux x64 / ARM64 | `linux-x64` / `linux-arm64` |
| macOS Intel / Apple Silicon | `osx-x64` / `osx-arm64` |

> **免装运行时版（self-contained）**：想发布给别人「拷走即跑」、不要求对方装 .NET 运行时，把 `--self-contained false` 改成 `--self-contained true` 再发布即可（体积约几十 MB，程序更独立）。

> **一次性发布全部平台**：执行 `powershell -ExecutionPolicy Bypass -File publish.ps1`，会为 Windows x64 / Linux x64 / macOS Intel / macOS Apple Silicon 各生成一个单文件可执行程序，输出到 `publish/<平台>/`。

> **数据目录**：无论哪种方式编译，**首次运行**都会在可执行文件旁自动创建 **`readwithhotsoup/`** 文件夹——SQLite 数据库 `rss.db`、AI 配置 `ai_config.json`、语言文件 `languages/`、全文缓存 `fulltext/`、阅读进度 `reading_progress.json` 等**所有数据都放在这里**。备份/迁移时整个文件夹拷走即可。

---

## 使用说明

### TUI 模式（无参数启动）

直接运行 `sip`（不带任何参数）进入文件夹式 TUI，启动后先显示**开始界面**（slogan + Dashboard 数据面板），回车进入、`Q` 退出。

左侧是**订阅源 + 文章合并的树形视图**：订阅源是父节点（`▶`/`▼` 展开收起），展开后看到该源的所有文章，像浏览文件夹一样。**文章标题过长会自动换行**（不会截断），方便一眼看全。每篇文章只显示**最新一版**；若该文被作者改过、有旧版本，标题右侧会有 **`✎`** 标记，选中后按 **`V`** 可查看全部版本。选中文章时右侧**用 Markdown 渲染正文**。

| 操作 | 说明 |
|------|------|
| `j` / `k`（或 `↑` / `↓`） | 在侧栏中上下选择（标题过长自动换行） |
| `l` / `Enter` | 在订阅源上：折叠/展开；在文章上：右侧显示正文 |
| `←` | 在正文栏时返回侧栏 |
| `Space` / `b`（或 `PageDown` / `PageUp`） | 上下翻页（侧栏内翻页；正文栏内滚动） |
| `Ctrl+D` / `Ctrl+U` | 正文栏内半页向下 / 半页向上（vim 习惯） |
| `i` | 沉浸阅读：隐藏侧栏/状态栏/状态行，正文占满全屏（再按 `i` 恢复） |
| `U` | 下载更新当前订阅源（同 CLI `-u`） |
| `F6` | 更新所有订阅源 |
| `A` | 归档当前源（标题加时间戳，同 CLI `-a`） |
| `R` | 去归档（同 CLI `-una`） |
| `X` | 删除选中源 / 单篇文章（同 CLI `-r`） |
| `D` | 添加新订阅源（同 CLI `-d`） |
| `S` | 语义搜索（同 CLI `--search`） |
| `Y` | 给当前文章生成摘要（同 CLI `--summary`） |
| `G` | 切换「完整正文 / 文章概要」 |
| `V` | 查看文章版本/变更历史（标题带 `✎` 标记的文章才有；输入编号可看旧版正文） |
| `M`（或命令行 `manage`） | 打开「订阅源管理页」：全屏列出所有源，`j/k` 移动、`u` 更新、`a` 归档、`r` 去归档、`x` 删除、`s` 设计划、`d` 加源 |
| `C` | 折叠/展开左侧栏 |
| `H` | 快捷键帮助 |
| `F2` | 关于页 |
| `Esc` | 唤出底部命令行，输入指令后 `Enter` 执行、再按 `Esc` 关闭 |
| `Ctrl+O` | 链接导航模式 |
| `Q` | 退出程序 |

> **阅读进度记忆**：每篇文章的滚动位置会记住（存在 `readwithhotsoup/reading_progress.json`，不改数据库）——退出 TUI 后再回来，会从上次读到的地方继续。

**底部命令行**：按 `Esc` 唤出（平时隐藏），可直接输入与 CLI 相同的命令，例如：

```
u 2             # 更新 2 号源
d https://xxx   # 下载添加新源
a 2             # 归档 2 号源
r 2             # 去归档 2 号源
s 关键词          # 语义搜索
g 关键词          # 全文搜索（不依赖 AI）
fetch           # 抓取当前文章的全文（首次需输入同意短语；摘要过短时正文会提示）
manage          # 打开订阅源管理页（同 M 键）
y               # 给当前选中文章生成摘要
init            # AI 配置向导（对话框版）
index           # 向量化当前选中源
reindex         # 清空全部向量并重新向量化
q               # 退出
```

### CLI 模式

```bash
sip -l                  # 列出所有订阅源
sip -l 1                # 列出 1 号源的文章（编号格式 [列表序号/真实ID]）
sip -d https://xxx/rss  # 下载新 RSS 源
sip -u 1                # 更新第 1 个源
sip -a 1                # 归档（加时间戳）
sip -una 1              # 去归档
sip -r 1                # 删除订阅源
sip -h                  # 帮助
sip --lang en-US -l     # 切换英文界面
```

**全屏阅读**：`sip --show <文章编号>` 打开一个无侧栏的全屏阅读界面（Markdown 渲染正文），底部提示 **「按 W 进入完整阅读器 · 按 Esc 退出」**——按 `W` 无缝切入完整 TUI（并定位到当前文章），按 `Esc`/`Q` 返回命令行。

**AI 读原文**：`sip --show <文章编号> --json` 把文章的标题/来源/链接/发布时间/作者 + **原始正文**（不做任何渲染）以 JSON 打到标准输出，供 AI 或脚本读取，例如 `sip --show 42 --json --lang en-US --ignoresafeannouncement`。

| 短参数 | 长参数 | 说明 |
|--------|--------|------|
| `-l` | `--list` | 列出所有订阅源；带编号则列出该源的文章。编号格式 `[列表序号/真实ID]`，`--show/--versions/--summary` 等命令用右边的真实 ID |
| `-d` | `--download` | 下载新的 RSS 源（URL 可省略 http/https 前缀，自动补全） |
| `-u` | `--update` | 更新指定订阅源（编号） |
| `-a` | `--archive` | 归档当前快照（加时间戳） |
| `-una` | `--unarchive` | 去归档（检查同名冲突） |
| `-r` | `--remove` | 删除订阅源及其全部文章与向量（加 `--yes`/`-y` 跳过确认，供脚本/AI 非交互使用） |
| `--show <编号>` | | 全屏阅读（无侧栏，`W` 进完整 TUI、`Esc` 退出）；加 `--json` 输出未渲染原文 JSON 给 AI/脚本 |
| `--versions <编号>` | | 列出文章的全部历史版本（含状态与时间）；想看某版原文用 `--show <该版本的编号>` |
| `--diff <编号> [vA vB]` | | 对比文章两个版本的正文（默认最近两版）；`--json` 结构化输出给 AI |
| `--export <编号 | feed:N | all> [out.md|目录]` | | 把文章导出为 Markdown（`--export-all` 前会确认，`--yes` 跳过） |
| `--fulltext <编号>` | | 抓取文章全文到本地缓存（首次需同意；`--yes` 跳过同意/确认，`--json` 结构化）；`--purge-fulltext [编号]` 清缓存 |
| `--sync` | | 只更新「到期」的订阅源（可选 `--feed 编号` 限定单个） |
| `--update-all` | | 强制更新所有订阅源（等价 TUI 的 `F6`） |
| `--schedule` | | 设置某源更新计划：`--schedule <编号> <表达式>` |
| `-h` | `--help` | 显示帮助 |

### 更新调度（按源自动更新，不浪费资源）

每个订阅源可以单独设一条**更新计划**，程序只在「到期」时才去拉取。表达式：间隔型 `5m` / `30m` / `1h` / `7d`、固定时刻 `daily@10:00`、`weekly@Mon 08:00`、手动 `manual`。

```bash
sip --schedule 1 30m            # 1 号源每 30 分钟更新
sip --schedule 2 daily@10:00    # 2 号源每天 10 点更新
sip --schedule 3 manual         # 3 号源改为手动
sip -l                          # 每个源显示「频率 · 上次 · 下次」
```

- **打开程序时**：自动静默同步所有到期的源
- **程序开着时**：每 15 分钟后台检查一次，有到期的才更新
- **CLI 模式下不会自动同步**，但会提醒你有到期源（`--ignoresafeannouncement` 可关）
- **到期判定**：`now >= 上次拉取时间 + 计划到期点`；每次成功拉取都重写「上次拉取时间」

### 多语言（语言文件）

所有用户可见文案都从 `readwithhotsoup/languages/<代码>.json` 读取，**源码原文是英文**，语言文件为「英文键 → 译文」，缺失时回退英文原文。文件支持**嵌套分组结构**（`Lang.Init` 会自动展平，兼容旧扁平格式）。

- 选择方式：`--lang <代码>` 参数 > `LANG` 环境变量 > 默认 `zh-CN`
- 首次启动自动复制/合并默认翻译到数据目录；**直接编辑数据目录里的文件即可**，改完即生效
- 新增翻译 key 会自动合并进已有文件，**不覆盖用户改过的 key**
- 定制翻译：复制 `en-US.json` 为 `你的代码.json`，改值后用 `--lang 你的代码` 加载

### AI 命令（语义搜索 / 智能摘要）

内置 AI 能力：**Embedding 向量化 + 语义搜索**（RAG）与 **LLM 文章摘要**，供 AI Agent 或人类通过同一套 CLI 使用。

> **给 AI Agent / 脚本的初始化提醒**：默认**未配置模型、未做向量化**——直接用 `--search` 会报「AI 未配置」或「尚无向量索引」。AI 应先 `sip --config` 确认已初始化，缺配置跑 `sip --init`、缺索引跑 `sip --index`、换过模型跑 `sip --reindex`。输出一律 **UTF-8**。

```bash
sip --init                          # 首次配置 AI（模型 + API Key，交互式）
sip --config                        # 查看/修改 AI 配置
sip --index                         # 对文章做 Embedding 向量化（交互式选择源）
sip --reindex                       # 更换 Embedding 模型后重新向量化
sip --search "LLM Agent"            # 语义搜索（返回命中文章 + 相似度）
sip --search "RAG" --feed 1 --json  # 限定订阅源搜索，JSON 输出
sip --grep "关键词"                  # 全文搜索（标题/正文/摘要，不依赖 AI）
sip --summary 12                    # 为文章 12 生成摘要（保存到数据库）
sip --summary feed:3                # 为订阅源 3 的全部文章生成摘要
sip --summary-all                   # 为所有未生成摘要的文章生成摘要
```

| 命令 | 说明 |
|------|------|
| `--init` | 交互式首次配置：选择 Embedding 提供方、LLM 提供方，并录入 API Key |
| `--config` | 打印当前 AI 配置（不含密钥）及配置文件路径 |
| `--index` | 为选中订阅源的文章批量生成 Embedding 向量 |
| `--reindex` | 更换 Embedding 模型（维度变化）后，清除旧向量并全量重建 |
| `--search <查询>` | 语义搜索；可选 `--feed 编号`、`--threshold 0.7`、`--json` |
| `--grep <关键词>` | 全文搜索（SQL LIKE，不依赖 AI）；默认输出「编号+标题+出现次数+±50 字符片段」，有上限（`--limit N` / `--max-snippets N` / `--json` / `--full`） |
| `--summary <编号>` | 为单篇文章调用 LLM 生成摘要；`feed:<编号>` 为该源全部文章逐个生成 |
| `--summary-all` | 为所有 `Summary` 为空的文章生成摘要 |

**API Key** 存操作系统原生凭据库（Windows 凭据管理器 / macOS 钥匙串 / Linux Secret Service），不写入任何文件；非敏感配置存 `readwithhotsoup/ai_config.json`。

#### 错误码说明

AI 命令失败时统一上报结构化错误码，`--json` 模式下错误以 `{"error": {"code": "...", ...}}` 形式返回：`MODEL_UNAVAILABLE` / `INVALID_RESPONSE` / `INVALID_JSON` / `EMPTY_RESPONSE` / `API_KEY_INVALID` / `NETWORK_ERROR` / `NO_INDEX` / `FEED_NOT_FOUND` / `ITEM_NOT_FOUND` / `EMPTY_QUERY`。

#### 退出码（脚本 / AI 判断成败）

CLI 命令成功时退出码为 `0`，失败时按类别返回非零退出码：

| 退出码 | 含义 |
|--------|------|
| `0` | 成功（含正常取消，如 `-r` 确认时回答 n） |
| `1` | 通用错误（参数/用法错误、未知命令、数据库错误、部分更新失败） |
| `2` | 网络 / 服务不可达（`NETWORK_ERROR`、`MODEL_UNAVAILABLE`、下载超时） |
| `3` | 资源未就绪（AI 未配置、API Key 缺失/无效、`NO_INDEX`、源/文章不存在、空查询） |

> `--json` 模式下错误仍会先输出结构化 `{"success": false, "error": {...}}`，再以对应的非零退出码退出。

### 文章归档机制

程序对每篇文章维护状态：`active`（当前有效）/ `archived`（作者修改后的旧版本）。更新 RSS 时：

- 比对新旧 Content，**仅正文变化才触发归档**
- 修改的文章：旧版 → `archived`，新版 → `active`；新增的文章直接写入 `active`
- 不再检测「删除」（很多站点 RSS 只推最近 N 篇，老文章下架不代表被删）

### 全文抓取

RSS 摘要过短（<100 字符）时，可抓取原文到本地缓存：

```bash
sip --fulltext <编号>            # 抓取全文（首次需输入同意短语；--yes 跳过同意/确认）
sip --fulltext <编号> --json     # 结构化输出 {itemId, cached, content}
sip --purge-fulltext [编号]      # 清缓存（不传编号 = 全清）
```

- 全文存 `readwithhotsoup/fulltext/<itemId>.md`（文件缓存，**不改数据库**）；该源已索引时，全文向量存 `vecs.json` 并并入语义搜索
- **Content 永远是主内容**，全文只做补充；显示时原文在上、全文在下，中间分界
- 抓取不产生新版本、不参与 diff/更新

---

## 技术栈

- C# / .NET 10.0
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite)
- [CodeHollow.FeedReader](https://github.com/arminreiter/FeedReader)（RSS/Atom 解析）
- [DiffPlex](https://github.com/mmanela/diffplex)（文本差异比较）
- [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)（文件夹视图 TUI）
- [HtmlAgilityPack](https://html-agility-pack.net/)（正文 HTML → 纯文本 / 全文抽取）
- [ktsu.CredentialCache](https://www.nuget.org/packages/ktsu.CredentialCache)（系统原生凭据库存取 API Key）
- Embedding / LLM：兼容 OpenAI 接口（本地 Ollama、DeepSeek、OpenAI 等）

---

## 项目结构

```
├── sip.csproj          # 项目文件（程序名 sip）
├── RssReader.cs        # 全部代码（单文件）
├── publish.ps1         # 单文件打包脚本（win/linux/mac 各平台）
├── languages/          # 默认语言文件（编译/发布时复制到 exe 旁，同时内嵌进 exe 兜底）
│   ├── zh-CN.json
│   └── en-US.json
├── .opencode/skills/   # AI Agent 使用 CLI 的 skill（教 AI 调用 sip）
│   └── sip-rss/SKILL.md
├── readwithhotsoup/    # 运行时数据目录（首次启动在 exe 同级自动创建）
│   ├── rss.db          # SQLite 数据库
│   ├── ai_config.json  # AI 非敏感配置（运行时生成）
│   ├── fulltext/       # 全文抓取缓存（<itemId>.md + vecs.json）
│   ├── reading_progress.json  # 阅读进度记忆
│   └── languages/      # 语言文件（默认翻译复制到此处，可直接编辑）
└── README.md
```

---

## AI 相关

- 使用 AI（deepseek / opencode / chatgpt）生成部分代码和注释
- 内置 Embedding 语义搜索与 LLM 摘要（详见上方「AI 命令」小节）

---

## 写在最后

sip 不是一个追求“日活”和“停留时长”的产品。

它追求的是：

> **当你打开 sip 时，你知道你今天读到的东西是可信的；当你的 AI 调用 sip 时，你知道它引用的来源是可靠的。**

它不会让你刷更多，但它会让你读得更安心。

愿下次相见，别来无恙

---

## 许可证

遵循 GNU General Public License v3.0 (GPL-3.0)
