# 🍲 sip

> ——「品，你细品。」
>
> 一个让你站着把信息喝了的 RSS 阅读器核心。
>
> 同时厚脸皮一下：[https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
> 关注热汤茶馆喵 关注热汤茶馆谢谢喵 🐾

一个本地 RSS 订阅源管理工具，支持**版本追踪**、**快照归档**和**文章变化检测**，提供**文件夹视图 TUI** 和**全功能 CLI**两种用法，并支持**多语言（语言文件可定制翻译）**。

## 功能

- **TUI 文件夹视图**：订阅源 + 文章合并为可展开的树形视图，选中即看正文，底部状态栏一键操作
- **CLI 全功能**：订阅、更新、归档、删除、AI 语义搜索与摘要全部支持命令行调用
- **订阅管理**：添加、更新、删除 RSS 订阅源，所有数据存储在本地 SQLite 数据库中
- **文章追踪**：自动检测文章的新增和修改，修改的文章会保留历史版本（带时间戳归档标记），不会丢失
- **快照归档**：对订阅源加时间戳归档，保留某一时刻的完整快照，归档后的源不会被后续更新覆盖
- **AI 能力**：Embedding 语义搜索（RAG）与 LLM 文章摘要，OpenAI 兼容接口（Ollama / DeepSeek / OpenAI 等）
- **多语言**：用户界面文案全部外置到 `languages/*.json`，用 `--lang <代码>` 或 `LANG` 环境变量切换，可自行定制/翻译
- **跨平台**：基于 .NET，数据存储为单个 `.db` 文件，Mac/Linux/Windows 均可运行

## 快速开始

### 环境要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### 编译运行

```bash
git clone https://github.com/hahahotsoup/rssreader-core.git
cd rssreader-core
dotnet build -c Release
dotnet bin/Release/net10.0/sip.dll          # 进入 TUI 界面
dotnet bin/Release/net10.0/sip.dll --help   # 或直接用 CLI
```

程序名已从 `rssreader` 更名为 **`sip`**（输出为 `sip.exe`）。

> **打包发布（单文件）**：需要给别人用、不想带一堆 dll 时，执行 `powershell -ExecutionPolicy Bypass -File publish.ps1`，会为 Windows x64 / Linux x64 / macOS Intel / macOS Apple Silicon 各生成一个**单文件可执行程序**（框架依赖，目标机需预装 [.NET 10 运行时](https://dotnet.microsoft.com/download)，单文件体积很小）。只发布单个平台也可手动执行，例如：
>
> ```bash
> dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
> ```
>
> 发布产物 = `sip.exe`（或 `sip`）+ 一个 `languages/` 文件夹（默认翻译，用于复制到数据目录）。首次运行单文件版时，程序会在 exe 同级自动创建 **`readwithhotsoup/`** 文件夹，并把 `languages/` 复制进去；数据库、AI 配置、语言文件等**所有数据文件都放在 `readwithhotsoup/` 里**，备份/迁移时整个文件夹拷走即可。

## 使用说明

### TUI 模式（无参数启动）

直接运行 `sip`（不带任何参数）进入文件夹式 TUI：

```
┌───────────────┬──────────────────────────────┐
│  订阅源        │  正文                        │
│  ▾ Hacker News│  …文章内容纯文本预览…          │
│    [现] Apple… │                              │
│    [现] New D…│                              │
│  ▸ BBC 科技    │                              │
│  ▸ 开源周报    │                              │
├───────────────┴──────────────────────────────┤
│  H 帮助 | U 更新 | F6 全部 | A 归档 | R 去归档 │
│  X 删除 | D 加源 | S 搜索 | Y 摘要 | Q 退出     │
└──────────────────────────────────────────────┘
```

启动后先显示**开始界面**（slogan + 功能简介），回车进入、`Q` 退出。

左侧是**订阅源 + 文章合并的树形视图**：订阅源是父节点（`▶`/`▼` 展开收起），展开后看到该源的所有文章，像浏览文件夹一样。**文章标题过长会自动换行**（不会截断），方便一眼看全。每篇文章只显示**最新一版**，不再显示 `[现] v1` 之类的前缀；若该文被作者改过、有旧版本，标题右侧会有 **`✎`** 标记，选中后按 **`V`** 可查看全部版本（输入编号即可读旧版正文）。选中文章时右侧**用 Markdown 渲染正文**——HTML 自动转为 Markdown，标题/粗体/斜体/删除线/分隔线/列表/链接/代码块都能正确显示样式。

按 **`C`** 可折叠左侧栏，正文区随即扩张到全宽（再按 `C` 恢复），适合专注阅读长文。

| 操作 | 说明 |
|------|------|
| `↑` / `↓` | 在侧栏中上下选择（标题过长自动换行） |
| `Enter` | 在订阅源上：折叠/展开；在文章上：右侧显示正文 |
| `←` / `→` | 在「侧栏」与「正文」栏之间切换焦点 |
| `PageUp` / `PageDown` | 上下翻页（侧栏内翻页；正文栏内滚动） |
| `U` | 下载更新当前订阅源（同 CLI `-u`） |
| `F6` | 更新所有订阅源 |
| `A` | 归档当前源（标题加时间戳，同 CLI `-a`） |
| `R` | 去归档（同 CLI `-una`） |
| `X` | 删除选中源 / 单篇文章（同 CLI `-r`） |
| `D` | 添加新订阅源（同 CLI `-d`） |
| `S` | 语义搜索（同 CLI `--search`） |
| `Y` | 给当前文章生成摘要（同 CLI `--summary`） |
| `G` | 切换「完整正文 / 文章概要」（概要含 RSS 摘要 + AI 摘要） |
| `V` | 查看文章版本/变更历史（标题带 `✎` 标记的文章才有；输入编号可看旧版正文） |
| `C` | 折叠/展开左侧栏（折叠后正文区占据全宽） |
| `H` | 快捷键帮助 |
| `F2` | 关于页（About，含项目介绍与博客链接） |
| `Esc` | 唤出底部命令行，输入指令后 `Enter` 执行、再按 `Esc` 关闭 |
| `Ctrl+O` | 正文栏进入/退出「链接导航模式」，`Tab`/`Shift+Tab` 或 `↑↓` 切换链接，`Enter` 打开，`Esc` 退出 |
| `Q` | 退出程序 |

> 正文区用 **Markdown 渲染**：HTML 自动转成 Markdown，标题/粗体/斜体/删除线/分隔线/列表/链接/代码都带真实样式；`<br>` 正确换行，代码块保留缩进（段落不做自动缩进，避免与原文排版打架）。图片（`<img>`）会转成可点击链接 `[🖼️ 图片](url)`（Windows Terminal 不支持终端内嵌图片，可用链接导航或鼠标点击在浏览器打开）；鼠标点击正文中的链接，或按 `Ctrl+O` 进入链接导航模式后按 `Enter`，即可在浏览器中打开。

**底部命令行**：按 `Esc` 唤出（平时隐藏），可直接输入与 CLI 相同的命令，例如：

```
u 2             # 更新 2 号源
d https://xxx   # 下载添加新源
a 2             # 归档 2 号源
r 2             # 去归档 2 号源
s 关键词          # 语义搜索
g 关键词          # 全文搜索（不依赖 AI）
y               # 给当前选中文章生成摘要
init            # AI 配置向导（对话框版）
index           # 向量化当前选中源
reindex         # 清空全部向量并重新向量化
q               # 退出
```

> 更新 / 加源 / 向量化 / 摘要等**带输出的操作会弹出居中进度对话框**，运行日志实时显示在对话框内，不会污染正文区，完成后自动关闭。
>
> TUI 快捷键与外部 CLI 命令一一对应：`U/A/R/X/D` 分别对应用 `-u/-a/-una/-r/-d`，`S/Y` 对应 `--search/--summary`。对话框内同样全键盘操作：`Tab` 切换焦点，`Enter` 确认，`Esc` 取消。

### CLI 模式

```bash
sip -l                  # 列出所有订阅源
sip -l 1                # 列出 1 号源的文章
sip -d https://xxx/rss  # 下载新 RSS 源
sip -u 1                # 更新第 1 个源
sip -a 1                # 归档（加时间戳）
sip -una 1              # 去归档
sip -r 1                # 删除订阅源
sip -h                  # 帮助
sip --lang en-US -l     # 切换英文界面
```

**文章预览**：`sip --preview <文章编号>` 打开一个独立的全屏预览界面（Markdown 渲染正文），底部提示 **「按 W 进入完整 TUI 程序 · Q 返回」**——按 `W` 直接无缝切换到完整 TUI，按 `Q`/`Esc` 返回命令行。文章编号用 `sip --grep 关键词` 或 `sip --search` 的结果里的 `[编号]`。

| 短参数 | 长参数 | 说明 |
|--------|--------|------|
| `-l` | `--list` | 列出所有订阅源；带编号则列出该源的文章（如 `-l 1`） |
| `-d` | `--download` | 下载新的 RSS 源（URL 可省略 http/https 前缀，自动补全） |
| `-u` | `--update` | 更新指定订阅源（编号） |
| `-a` | `--archive` | 归档当前快照（加时间戳） |
| `-una` | `--unarchive` | 去归档（检查同名冲突） |
| `-r` | `--remove` | 删除订阅源及其全部文章与向量 |
| `-p` | `--preview` | 预览单篇文章（`--grep`/`--search` 输出的文章编号），底部按 `W` 进入完整 TUI |
| `--sync` | | 只更新「到期」的订阅源（可选 `--feed 编号` 限定单个），输出每个源的 上次/下次 |
| `--update-all` | | 强制更新所有订阅源（等价 TUI 的 `F6`） |
| `--schedule` | | 设置某源更新计划：`--schedule <编号> <表达式>` |
| `-h` | `--help` | 显示帮助 |

### 更新调度（按源自动更新，不浪费资源）

每个订阅源可以单独设一条**更新计划**，程序只在「到期」时才去拉取——高频源（如 5 分钟）和低频源（如一个月、或每天固定时刻）互不干扰，不会频繁白刷。

**计划表达式**：

| 类型 | 示例 | 含义 |
|------|------|------|
| 间隔 | `5m` / `30m` / `1h` / `7d` / `30d` | 距上次拉取这么多时间后到期 |
| 固定时刻 | `daily@10:00` | 每天 10:00 到期 |
| 固定时刻 | `weekly@Mon 08:00` | 每周一 08:00 到期 |
| 手动 | `manual` 或留空 | 不自动更新（默认） |

```bash
sip --schedule 1 30m            # 1 号源每 30 分钟更新
sip --schedule 2 daily@10:00    # 2 号源每天 10 点更新
sip --schedule 3 manual         # 3 号源改为手动
sip -l                          # 每个源显示「频率 · 上次 · 下次」
```

**什么时候自动更新**：
- **打开程序时**：自动静默同步所有到期的源（弹进度框列出「源名 · 上次 X 前」），无需确认
- **程序开着时**：每 15 分钟后台检查一次，有到期的才更新（没到期不发请求，几乎零开销）
- 也可以随时手动：`sip --sync`（只更到期的）、`sip --update-all`（全更）、`U`/`F6`（TUI 内）

**到期判定**：`now >= 上次拉取时间 + 计划到期点`。首次设置计划（从未拉取过）会立即到期更新一次。**每次成功拉取都会重写「上次拉取时间」**，所以提前手动更新（`U` / `-u`）后会重新计时，不会刚更完又被判到期；拉取失败则保持到期状态，下次继续重试。间隔型按「最近一次拉取 + 间隔」重置；`daily@`/`weekly@` 固定时刻天然按钟表对齐。

> 归档源（标题带时间戳）不参与自动调度；自动同步时也不会弹出向量化询问（有需要再手动 `--index`）。

### 多语言（语言文件）

所有用户可见文案都从 `languages/<代码>.json` 读取，键为中文原文、值为译文，缺失时回退原文。

- 选择方式：`--lang <代码>` 参数 > `LANG` 环境变量 > 默认 `zh-CN`
- **存储位置**：所有语言文件都在 `readwithhotsoup/languages/` 里——首次启动程序会把 exe 旁的 `languages/`（zh-CN.json / en-US.json 等默认翻译）自动复制进去，之后**直接编辑数据目录里的文件即可**，改完即生效
- **定制翻译**：在 `readwithhotsoup/languages/` 里放一个语言文件，命名为 `你的代码.json`（把键值改成你的语言），用 `--lang 你的代码` 加载即可，例如放 `fr-FR.json` 后 `--lang fr-FR`
- 查找顺序：`readwithhotsoup/languages/<代码>.json` → 中文原文（原样返回）

### AI 命令（语义搜索 / 智能摘要）

内置 AI 能力：**Embedding 向量化 + 语义搜索**（RAG）与 **LLM 文章摘要**，供 AI Agent 或人类通过同一套 CLI 使用。

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

#### AI 命令详解

| 命令 | 说明 |
|------|------|
| `--init` | 交互式首次配置：选择 Embedding 提供方（ollama/openai）、LLM 提供方（deepseek/openai），并录入 API Key |
| `--config` | 打印当前 AI 配置（不含密钥）及配置文件路径 |
| `--index` | 为选中订阅源的文章批量生成 Embedding 向量，写入 SQLite 的 `Vectors` 表 |
| `--reindex` | 更换 Embedding 模型（维度变化）后，清除旧向量并全量重建 |
| `--search <查询>` | 对查询做 Embedding，与库中向量计算余弦相似度，按阈值过滤并排序输出；可选 `--feed 编号`、`--threshold 0.7`、`--json` |
| `--grep <关键词>` | 全文搜索：在标题/正文/摘要做关键字匹配（SQL LIKE），不依赖 AI，适合精确查找与兜底 |
| `--summary <编号>` | 为单篇文章调用 LLM 生成摘要；`feed:<编号>` 为该源全部文章逐个生成 |
| `--summary-all` | 为所有 `Summary` 为空的文章生成摘要 |

#### AI 架构说明

- **搜索**：`--search` 与前端/AI Agent 共用同一个接口，无需专门做 Agent 翻译层；结果含文章源、文章 ID、相似度分数
- **摘要**：仅在用户请求时生成（不自动调用），结果写入 `Items.Summary / SummaryAt` 字段，可反复使用
- **模型健康检查**：调用时先检测模型可用性，模型不可用则报错并停止使用
- **Embedding 切换**：更换模型后维度变化会使旧向量失效，需执行 `--reindex`，程序会提醒
- **安全提醒**：首次调用 AI 功能时输出安全提示，提醒妥善保管 API Key

> 全局选项 `--ignoresafeannouncement`：加在任何 CLI 调用末尾，跳过安全横幅等提示，仅输出数据（供脚本 / AI Agent 使用），例如 `sip --search "AI" --json --ignoresafeannouncement`。

#### 配置与密钥存储

| 内容 | 存储位置 | 说明 |
|------|----------|------|
| 非敏感配置（提供方/模型/端点/阈值） | `readwithhotsoup/ai_config.json`（与 `rss.db` 同目录） | 可提交、可共享 |
| API Key | 操作系统原生凭据库 | Windows 凭据管理器 / macOS 钥匙串 / Linux Secret Service，不写入任何文件 |

> 安全提示：请勿泄露 API Key，不要截图或上传含密钥的界面；如怀疑泄露，请立即更换密钥。

#### AI 使用要点

- **新增文章不会自动向量化**：新下载/更新的文章要先执行 `--index` 才会进入语义搜索；搜索只会命中当前模型（`Models.IsCurrent = 1`）的向量
- **索引幂等**：`--index` 只处理「还没有向量」的 active 文章（`Vectors` 表对 `(ItemId, ModelId)` 有唯一约束），重复执行不会重复生成
- **搜索阈值**：默认 0.7（`ai_config.json` 的 `SearchThreshold`），可按需用 `--threshold` 覆盖；本地 bge-m3 的命中分数通常落在 0.5~0.6，建议设 0.5 左右
- **摘要缓存**：`Items.Summary` 非空即视为已生成并跳过，不会重复调用 LLM；想重新生成需先清空该字段（`--summary-all` 同理）
- **模型健康检查**：Embedding / LLM 不可用时返回明确错误码（`MODEL_UNAVAILABLE` / `API_KEY_MISSING` / `API_KEY_INVALID` 等），不会静默失败

#### 常见问题

| 现象 | 原因 / 解决 |
|------|------------|
| 搜索提示「尚无向量索引」 | 还没跑 `--index`，或更换 Embedding 模型后需 `--reindex` |
| 搜索结果太少/为空 | 阈值偏高，调低 `--threshold`（本地 bge-m3 建议 0.5） |
| 搜索报「模型维度变化」 | 换了模型，旧向量失效，执行 `--reindex` 重建 |
| 摘要报「缺少 LLM API Key」 | 先 `--init` 录入密钥（存系统凭据库），或检查 `--config` |
| 想让某篇重新摘要 | 清空该行 `Summary` 字段后再执行 `--summary` |

### 下载新源

TUI 模式下先用 `Q` 退出，再通过 CLI 添加订阅源：

```bash
sip -d https://example.com/rss
```

CLI 会输出文章差异（新增/修改），并把源与文章写入数据库。URL 可省略 `http(s)://` 前缀，程序会自动补全；若补全的 https 连不上，会自动回退 http 重试一次。不检测删除：RSS 通常只推最新 N 篇，老文章下架不代表被删，因此只跟踪新增与修改。

下载/更新后若已配置 AI，会询问是否把该源的新文章加入语义搜索（可用 `sip --index` 稍后补做）。

### 文章归档机制

程序对每篇文章维护状态：

| 状态 | 含义 |
|------|------|
| `active` | 当前有效 |
| `archived` | 作者修改后的旧版本（保留原文） |

> 不再检测「删除」：很多站点的 RSS 只推最近 N 篇，老文章不在列表里不代表被删，因此只跟踪**新增**与**修改**，避免把正常下架的文章误标为 `deleted`。

更新 RSS 时，程序会：

- 比对新旧 Content，**仅正文变化才触发归档**
- 修改的文章：旧版 → `archived`，新版 → `active`
- 新增的文章直接写入 `active`
- 列表显示各状态文章数量：`现行 5 篇, 其中有 2 篇发生了更改`

### 归档订阅源快照

`T 1` 后标题变为 `博客名_20260712_143000`，此后：

- 下载同名源会被当作**全新源**，不覆盖旧数据
- 归档源**禁止更新**，去归档需先执行 `R 1`
- 新下载的同名源获得独立的文章副本

## 技术栈

- C# / .NET 10.0
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite)
- [CodeHollow.FeedReader](https://github.com/arminreiter/FeedReader)（RSS/Atom 解析）
- [DiffPlex](https://github.com/mmanela/diffplex)（文本差异比较）
- [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)（文件夹视图 TUI）
- [HtmlAgilityPack](https://html-agility-pack.net/)（正文 HTML → 纯文本）
- [ktsu.CredentialCache](https://www.nuget.org/packages/ktsu.CredentialCache)（系统原生凭据库存取 API Key）
- Embedding / LLM：兼容 OpenAI 接口（本地 Ollama、DeepSeek、OpenAI 等）

## 项目结构

```
├── sip.csproj          # 项目文件（程序名 sip）
├── RssReader.cs        # 全部代码（单文件）
├── publish.ps1         # 单文件打包脚本（win/linux/mac 各平台）
├── languages/          # 默认语言文件（发布时随 exe 一起，首次启动复制到数据目录）
│   ├── zh-CN.json
│   └── en-US.json
├── .opencode/skills/   # AI Agent 使用 CLI 的 skill（教 AI 调用 sip）
│   └── sip-rss/SKILL.md
├── readwithhotsoup/    # 运行时数据目录（首次启动在 exe 同级自动创建）
│   ├── rss.db          # SQLite 数据库
│   ├── ai_config.json  # AI 非敏感配置（运行时生成）
│   └── languages/      # 语言文件（默认翻译复制到此处，可直接编辑）
└── README.md
```

## AI 相关

- 使用 deepseek-pro 生成部分代码和注释
- 内置 Embedding 语义搜索与 LLM 摘要（详见上方「AI 命令」小节）

## 许可证

遵循 GNU General Public License v3.0 (GPL-3.0)
