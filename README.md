# hahahotsoup's rss reader

一个本地 RSS 订阅源管理工具，支持**版本追踪**、**快照归档**和**文章变化检测**。

服务于 hahahotsoup's rssreader，可以快速管理自己的 RSS 订阅源并归档化。

## 功能

- **订阅管理**：添加、更新、删除 RSS 订阅源，所有数据存储在本地 SQLite 数据库中
- **文章追踪**：自动检测文章的新增、修改和删除，修改/删除的文章会保留历史版本（带时间戳归档标记），不会丢失
- **文章管理**：查看指定源的所有文章，支持永久删除（物理删除，不可恢复）
- **快照归档**：对订阅源加时间戳归档，保留某一时刻的完整快照，归档后的源不会被后续更新覆盖
- **CLI 参数**：支持命令行参数一键操作，也支持交互式菜单
- **跨平台**：基于 .NET，数据存储为单个 `.db` 文件，Mac/Linux/Windows 均可运行

## 快速开始

### 环境要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### 编译运行

```bash
git clone https://github.com/hahahotsoup/rssreader-core.git
cd rssreader-core
dotnet build
dotnet run
```

## 使用说明

### CLI 模式

```bash
rssreader -l                  # 列出所有订阅源
rssreader -d https://xxx/rss  # 下载新 RSS 源
rssreader -u 1                # 更新第 1 个源
rssreader -a 1                # 归档（加时间戳）
rssreader -una 1              # 去归档
rssreader -r 1                # 删除订阅源
rssreader -h                  # 帮助
```

| 短参数 | 长参数 | 说明 |
|--------|--------|------|
| `-l` | `--list` | 列出所有订阅源 |
| `-d` | `--download` | 下载新的 RSS 源 |
| `-u` | `--update` | 更新指定订阅源（编号） |
| `-a` | `--archive` | 归档当前快照（加时间戳） |
| `-una` | `--unarchive` | 去归档（检查同名冲突） |
| `-r` | `--remove` | 删除订阅源及其全部文章 |
| `-h` | `--help` | 显示帮助 |

不带参数运行时进入交互式菜单（在菜单内输入订阅源编号即可查看该源的文章列表）。

### AI 命令（语义搜索 / 智能摘要）

内置 AI 能力：**Embedding 向量化 + 语义搜索**（RAG）与 **LLM 文章摘要**，供 AI Agent 或人类通过同一套 CLI 使用。

```bash
rssreader --init                          # 首次配置 AI（模型 + API Key，交互式）
rssreader --config                        # 查看/修改 AI 配置
rssreader --index                         # 对文章做 Embedding 向量化（交互式选择源）
rssreader --reindex                       # 更换 Embedding 模型后重新向量化
rssreader --search "LLM Agent"            # 语义搜索（返回命中文章 + 相似度）
rssreader --search "RAG" --feed 1 --json  # 限定订阅源搜索，JSON 输出
rssreader --summary 12                    # 为文章 12 生成摘要（保存到数据库）
rssreader --summary feed:3                # 为订阅源 3 的全部文章生成摘要
rssreader --summary-all                   # 为所有未生成摘要的文章生成摘要
```

#### AI 命令详解

| 命令 | 说明 |
|------|------|
| `--init` | 交互式首次配置：选择 Embedding 提供方（ollama/openai）、LLM 提供方（deepseek/openai），并录入 API Key |
| `--config` | 打印当前 AI 配置（不含密钥）及配置文件路径 |
| `--index` | 为选中订阅源的文章批量生成 Embedding 向量，写入 SQLite 的 `Vectors` 表 |
| `--reindex` | 更换 Embedding 模型（维度变化）后，清除旧向量并全量重建 |
| `--search <查询>` | 对查询做 Embedding，与库中向量计算余弦相似度，按阈值过滤并排序输出；可选 `--feed 编号`、`--threshold 0.7`、`--json` |
| `--summary <编号>` | 为单篇文章调用 LLM 生成摘要；`feed:<编号>` 为该源全部文章逐个生成 |
| `--summary-all` | 为所有 `Summary` 为空的文章生成摘要 |

#### AI 架构说明

- **搜索**：`--search` 与前端/AI Agent 共用同一个接口，无需专门做 Agent 翻译层；结果含文章源、文章 ID、相似度分数
- **摘要**：仅在用户请求时生成（不自动调用），结果写入 `Items.Summary / SummaryAt` 字段，可反复使用
- **模型健康检查**：调用时先检测模型可用性，模型不可用则报错并停止使用
- **Embedding 切换**：更换模型后维度变化会使旧向量失效，需执行 `--reindex`，程序会提醒
- **安全提醒**：首次调用 AI 功能时输出安全提示，提醒妥善保管 API Key

#### 配置与密钥存储

| 内容 | 存储位置 | 说明 |
|------|----------|------|
| 非敏感配置（提供方/模型/端点/阈值） | `ai_config.json`（与 `rss.db` 同目录） | 可提交、可共享 |
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

### 交互模式

启动后显示主菜单：

```
A 看看已有订阅 | B 下载新RSS源 | Q 退出
```

#### 订阅管理（A 菜单）

| 命令 | 示例 | 说明 |
|------|------|------|
| 输入编号 | `2` | 更新该订阅源，检测文章变化 |
| `T 编号` | `T 1` | 归档当前快照（加时间戳） |
| `R 编号` | `R 1` | 去归档（检查同名冲突） |
| `D 编号` | `D 2` | 删除订阅源及其全部文章 |
| `L 编号` | `L 1` | 查看订阅源的所有文章 |

#### 文章管理（L 子菜单）

进入后会列出该源所有文章，每篇有显示编号和状态标签：

```
── [1] xxx 的文章列表 ──
  [1] [现] v1 | 如何学习 C#
  [2] [旧] v1 | SQLite 入门指南
  [3] [删] v1 | 已删除的文章
```

| 命令 | 示例 | 说明 |
|------|------|------|
| `D 编号` | `D 2` | 删除指定文章 |
| `Q` | `Q` | 返回 A 菜单 |

注意：
- 文章编号在删除后**自动继位**（删掉 #2 后原来的 #3 变成 #2）
- **物理删除不可恢复**，作者自动标记的 `[删]` 文章（软删除）也会被一并清掉

#### 下载新源（B 菜单）

直接输入 RSS 链接，自动下载、解析、存入数据库。

### 文章归档机制

程序对每篇文章维护三种状态：

| 状态 | 含义 |
|------|------|
| `active` | 当前有效 |
| `archived` | 作者修改后的旧版本（保留原文） |
| `deleted` | 作者删除了此文（标记但不物理删除） |

更新 RSS 时，程序会：

- 比对新旧 Content，**仅正文变化才触发归档**
- 修改的文章：旧版 → `archived`，新版 → `active`
- 删除的文章：`active` → `deleted`，附时间戳标记
- 列表显示各状态文章数量：`现行 5 篇, 其中有 2 篇发生了更改, 1 篇被作者删掉了`

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
- [ktsu.CredentialCache](https://www.nuget.org/packages/ktsu.CredentialCache)（系统原生凭据库存取 API Key）
- Embedding / LLM：兼容 OpenAI 接口（本地 Ollama、DeepSeek、OpenAI 等）

## 项目结构

```
├── Hahahotsoup.RssReader.Core.csproj
├── RssReader.cs          # 全部代码（单文件）
├── ai_config.json        # AI 非敏感配置（运行时生成）
└── README.md
```

## AI 相关

- 使用 deepseek-pro 生成部分代码和注释
- 内置 Embedding 语义搜索与 LLM 摘要（详见上方「AI 命令」小节）

## 许可证

遵循 GNU General Public License v3.0 (GPL-3.0)
