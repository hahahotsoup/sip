---
name: sip-rss
description: 调用 sip（本地个人信息库）CLI 进行订阅管理、全文/语义搜索、证据收集（ingest）与证据问答（retrieve/ask）。当用户要求搜索/检索内容、管理订阅源、沉淀证据、追问变化或让 AI 从可信库回答时使用。
---

# sip — 个人信息库 CLI 使用指南

> 📘 **完整契约与安全纪律见同目录《高级用户手册》**：[高级用户手册.md](./高级用户手册.md)（Agent 必读：命令契约 / simon 挡位 / 故障排查 / 安全纪律）。
> 👤 用户侧白话版：`docs/用户快速手册.md`。

## ⚡ 思维链：Agent 执行前必读

**每次操作前，按这个顺序想一遍：**

1. **用户要什么？** → 明确目标（搜索？管理？收集？问答？）
2. **我该用什么命令？** → 查下面的命令速查表
3. **需要先检查什么？** → AI 配置？向量索引？simon 挡位？
4. **执行后怎么判断成败？** → 看退出码（0=成功）+ 输出文本/JSON
5. **失败了怎么办？** → 查常见问题，不要盲目重试

**禁止：**
- ❌ 不检查就假设 AI 已配置
- ❌ 不看退出码就假设成功
- ❌ 命令被拒后反复重试（先查 simon 挡位）
- ❌ 用裸 `sip --show <id>`（会进 TUI，必须加 `--json`）

## ⚠️ 先初始化，别默认模型已就绪

**AI 很容易犯的错**：默认 Embedding / LLM 模型已经配置好、文章已经向量化。**事实是：默认什么都没配置、什么都没索引。** 必须按下面流程先检查，缺什么补什么：

```bash
sip --config                                   # ① 检查 AI 是否已配置（无输出/提示未配置 → 需 --init）
sip --search "test" --ignoresafeannouncement   # ② 试探搜索：报「尚无向量索引 / run --index」→ 需先 --index
```

- **未配置 AI**（`--config` 无有效配置）：`--init` 是**交互式向导，仅在真实终端手动运行**（安全考虑，不接受管道/脚本输入；AI/非交互调用会被拒绝）。AI 不要代跑 `--init`——直接**告诉用户需先在真实终端手动执行 `sip --init`**（或让用户手动编辑 `ai_config.json`），不要假装已配置。
- **「尚无向量索引」/ 搜索为空但该有内容**：先跑 `sip --index`（交互式选择源，或先 `sip -l` 看有哪些源）
- **换了 Embedding 模型报「模型维度变化」**：跑 `sip --reindex`
- **`--grep` 不依赖 AI**，永远可用；AI 未配置时用它做全文检索是可靠的兜底

> 初始化涉及录入 API Key / 交互选择，**需要用户在终端配合时不要代跑**，明确告知用户执行哪条命令。

## 如何调用

```bash
# 已构建的程序（exe 所在目录）
sip <命令> [参数]

# 开发环境（本项目内）
dotnet bin/Release/net10.0/sip.dll <命令> [参数]
```

建议统一追加 `--ignoresafeannouncement` 跳过安全横幅（脚本/AI 专用），例如：

```bash
sip --search "关键词" --json --ignoresafeannouncement
```

> 注意：`--ignoresafeannouncement` 只屏蔽安全横幅，**不屏蔽到期提醒**。非 `--json` 输出末尾可能出现「N 个订阅源已到期，运行 sip --sync 可更新」——这是有用信息，可据此建议用户执行 `--sync`；`--json` 模式会自动抑制该提醒（避免污染结构化输出）。

**编码**：sip 一律输出 **UTF-8**。若调用环境的终端是 GBK/其他代码页（Windows cmd/PowerShell 默认 GBK），把输出按 UTF-8 解码即可，或在 PowerShell 里先执行 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`；不要用 GBK 解码，否则中文乱码。

## 命令速查

| 命令 | 说明 |
|------|------|
| **订阅管理** | |
| `sip -l` | 列出所有订阅源（编号、标题、文章统计、健康标记）；`--json` 结构化输出 |
| `sip -l <编号>` | 列出某源的文章（`--json` 结构化）。编号格式 `[列表序号/真实ID]`，`--show/--versions/--summary` 等命令用**右边**的真实 ID；行尾可能有 `[摘要]`/`[无正文]` 质量标记 |
| `sip -d <url>` | 下载/添加新 RSS 源（URL 可省略协议前缀） |
| `sip -u <编号>` | 更新某源 |
| `sip --sync [--feed N] [--json]` | 只更新「到期」的订阅源（可选 `--feed 编号` 限定单个；`--json` 结构化） |
| `sip --update-all` | 强制更新所有订阅源 |
| `sip --schedule <编号> <表达式>` | 设置某源更新计划：`30m` / `1h` / `7d` / `daily@10:00` / `weekly@Mon 08:00` / `manual`（手动） |
| `sip -a <编号>` | 归档（加时间戳） |
| `sip -una <编号>` | 去归档 |
| `sip -r <编号>` | 删除源及其全部文章与向量（加 `--yes` 跳过确认，供脚本/AI 非交互删除） |
| **AI 功能** | |
| `sip --config` | 查看 AI 配置 |
| `sip --index` | 对文章做 Embedding 向量化（需先 `--init`） |
| `sip --reindex` | 更换 Embedding 模型后重新向量化 |
| `sip --search <查询> [--feed 编号] [--threshold 0.7] [--json]` | 语义搜索（Embedding） |
| `sip --grep <关键词> [--limit N] [--max-snippets N] [--json] [--full]` | 全文搜索（标题/正文/摘要关键字匹配，不依赖 AI）；默认输出「编号+标题+出现次数+±50 字符片段」，有上限不刷屏；`--json` 结构化、`--full` 输出整篇摘要 |
| `sip --show <编号> --json` | 原文 JSON 直出：标题/来源/链接/时间/作者 + 原始正文（未渲染）打到标准输出（**AI 读全文用这个**）；已抓取全文时额外带 `fulltext` 字段（读全文优先用该字段，比 DB 里的 RSS 摘要完整）。⚠️ 裸 `sip --show <编号>` 是全屏阅读界面（给人读的，会占用终端），**AI 一律加 `--json`** |
| `sip --versions <编号>` | 列出文章的全部历史版本（含状态与时间）；用 `--show <版本编号> --json` 查看某版原文 |
| `sip --diff <编号> [vA vB] [--semantic] [--json]` | 对比同一文章两个版本的正文（默认最近两版）；`--json` 给结构化 diff；`--semantic` 显示语义距离和改动分级 |
| `sip --export <编号 \| feed:N \| all> [out.md\|目录] --yes` | 把文章导出为 Markdown；`--yes` 跳过全部导出的确认 |
| `sip --fulltext <编号> --yes --json` | 抓取文章**全文**到本地缓存并输出（RSS 摘要过短时用）；`--yes` 跳过同意/确认。⚠️ 全文抓取涉及抓取源站页面，需显式同意；不改数据库、不参与版本比对。⚠️ **安全边界**：只抓 http/https；回环/链路本地/私网段默认拒绝（SSRF 防护），确需内网源时让用户在 `ai_config.json` 设 `"allowPrivateNet": true` |
| `sip --purge-fulltext [编号]` | 清除全文缓存 |
| `sip --feed-info <编号> [--json]` | 来源身份与健康：类型/作者/官网/更新时间/最近文章/状态（正常/⚠ 长期未更新/✗ 失败 N 次） |
| `sip --export-opml [file]` | 导出全部订阅源为 OPML（默认 feeds.opml） |
| `sip --import-opml <file>` | 从 OPML 批量导入订阅源（按 FeedUrl 跳过已存在） |
| `sip --like <编号> [--ai [理由]]` | 标记文章：默认用户点赞（♥），`--ai` 表示 AI 判断用户会喜欢（🤖）；再执行 = 取消 |
| `sip --likes [--json]` | 列出所有标记文章 |
| `sip --today [--json] [--refresh] [--quick N]` | 今日阅读清单（规则式选文，上限=目标 5 篇；含预估时长与理由）。**一天固定一碗**（当日缓存，新文章当天不自动进清单）；`--refresh` 显式重新生成；要当天新内容可直接 `--grep`/`--show`；开启 telemetry 后可跟踪完成进度。**顶部是「今日变化摘要」**：按源各新增多少、⚠ 高频源（腹泻式更新）单独折叠、被作者改过带「改动概览」（标题改没改/增删行数/约±字数，纯 diff 计数、无 LLM）、⚠ 可能同文（跨源重复）——`--json` 里 `digest.modified[].itemId` 可接 `sip --diff`，`digest.dedups[].itemA/B` 可接 `sip --dedup` |
| `sip --dedup scan\|hide-cluster <代表Id>\|hide <hiddenId> <canonicalId>\|list\|undo <key> [--json]` | 跨源重复检测与隐藏（v1.1.4 起为**簇检测**）：`scan` 列可能同文（段落重合度，无 LLM），输出**重复簇**（代表 + 成员，JSON 里 `clusters[].representativeId` / `members`）；`hide-cluster <代表Id>` 一键隐藏整簇（保留代表、隐藏其余成员），`hide` 单篇隐藏，标 `Status='dedup'` 并记 `dedup.json` 规则（此后搜索/全文/摘要/计数自动排除，且 `--sync` 导入时跳过，防卷土重来）；`list` 看已隐藏；`undo <key>` 撤销恢复。**隐藏 = 忽略不删除，非破坏可恢复**；manage 界面按 `i` 查看/撤销已隐藏 |
| `sip telemetry status/show/enable/disable/clear/export` | 本地阅读遥测（**默认关闭**；仅本地、不上传；可查看/关闭/删除/导出） |
| `sip --insights [--window N d] [--json]` | 阅读情况报告（**需遥测开启**）：每源卡片展示 打开/读完/完成率/跳过/♥🤖点赞/AI调用次数/**状态（仅技术故障）**/**reasons（事实原因，无价值结论）**；**AI 不替你决定**，只列事实，决定在你 |
| `sip --policy list \| set <feedId> <archive\|keep\|lower_frequency\|tag\|unsubscribe> [args] \| remove <feedId>` | **Source Policy 闭环**：把「你的决定」存成规则并应用。`lower_frequency <expr>` 直接设该源更新频率；`archive` 归档；`tag <名>` 打标签（`-l` 显示 `#tag`）；`keep`/`unsubscribe` 记录备注。**createdBy 永远是 user，AI 永不自动写**；`-l` 会显示规则标记 |
| `sip --onboarding [list \| <category>] \| add <category> <index\|all>` | 推荐源模板（认知门槛）：按领域分组一键添加订阅源；`templates.json` 可编辑自定义清单 |
| `sip --insights-interval <7d\|30d\|off>` | 设定报告定时提醒间隔（存 sip_settings.json，默认 off）；到期且遥测开启时 CLI 末尾提醒、TUI 启动自动弹出报告页；TUI 里按 `P` 或命令 `report` 随时打开 |
| `sip --summary <编号>` | 为文章生成 LLM 摘要（`--json` 结构化输出） |
| `sip --summary feed:<编号>` | 为某源全部文章生成摘要 |
| `sip --summary-all` | 为所有未生成摘要的文章生成摘要 |
| `sip simon status [--json] \| level <1\|2\|3> \| export-key <file> \| import-key <file>` | 孟思琳(simon)安全守护（**默认开启、无法关闭**，只有挡位 1/2/3）：`status` 查当前挡位与加密状态（`--json` 结构化）；`level` 调挡位（**降挡只能在 TUI 命令栏**，CLI 降挡报 `SIMON_LOCKED`，AI 不要代跑）；`export-key`/`import-key` 换机迁移加密密钥 |
| **证据收集（ingest）** | |
| `sip ingest --stdin [--origin <url>] [--producer <名>] [--yes] [--json]` | **收集证据**：把管道输入（如 Argo/搜索结果原文）存进本地证据库；`--origin` 记来源 URL、`--producer` 记生产者（如 argo）。**查完即存**的钥匙 |
| `sip ingest --url <url> [--yes]` | **URL 直存**：抓网页存为证据（SSRF 防护，回环/内网/云元数据一律拒绝） |
| `sip ingest --evidence <file>` | 导入 sip-evidence-v1 证据包（schema 校验；缺 schema/content 报错退出 1） |
| `sip ingest list [--stale] [--group N] [--tag <标签>] [--json]` / `show <id>` | 浏览证据；`--stale` 只看过期、`--group N` 按主题、`--tag` 按标签 |
| `sip ingest refresh [id\|--stale\|--all] [--json]` | **追踪变化**：重查原文→哈希比对→没变/分级变化(⚪润色/🟡调整/🔴反转)/失效(标 invalid 不覆盖)；默认只刷过期的 watch 目标 |
| `sip ingest confirm <id>` / `rm <id> [--yes]` | 你核实过（Verified=1+重算共识）/ 遗忘删除 |
| `sip ingest group add <标签> [--seed <查询>]\|rename\|rm\|groups` | **主题分组**（你定义主题；存证据自动归组；需要先 `--init` 配 AI） |
| `sip ingest retrieve <查询> [--top N] [--group N] [--json]` | **RAG 就绪**：检索证据，命中带原文片段/来源URL/版本/新鲜度/分级/反转/**hasDiff(被改过)**——给 Agent 引用 |
| `sip ingest ask "<问题>" [--json] [--ignoresafeannouncement]` | **只摘录不转述**的问答：答案只能由库里证据的原文片段逐字摘录；库里没有 → 直接说"不知道"；需要 LLM（`--init` 配置） |
| **数据体检（v1.2.2）** | |
| `sip ingest stats [--json]` | 一行总览：证据数/版本数/改动数/反转数/主题数/标签数/今日新增 |
| `sip ingest cleanup --stale [--min-views N] [--recent-days N] [--dry-run] [--yes] [--json]` | 清理过期证据（保留 ViewCount ≥ 3 或最近查看过的） |
| `sip ingest tag list\|add <id> <name>\|rm <id> <name> [--json]` | 标签管理 |
| `sip ingest tree <id> [--depth N] [--json]` | 树状评论 |
| `sip ingest watch add <id> [--interval <min>]\|rm <id>\|list\|refresh [id] [--all] [--json]` | 网页监控（**手动刷新，不支持自动抓取**） |

> ⚠️ **ingest 纪律**：只存"会再用/会变且你在意/你确认过的"（三问判据）；去重命中(cos≥0.92)时非交互自动跳过并返回 `duplicateOf`（不替你删），确需强存加 `--force`；挡位 2 下 ingest 写子命令被拦（只读的 list/show/retrieve/groups/ask 可用）。

## ⚠️ 安全守护：孟思琳(simon)——命令可能被拦，先查挡位

`sip simon status` 返回的挡位**决定哪些 CLI 命令可用**。默认开启、无法关闭，只有 1/2/3 三挡；命令被拒**不是 bug，是守护在拦截**：

| 挡位 | 名称 | CLI 行为 |
|------|------|----------|
| 1 | 基础 | 不拦截，行为与以往一致 |
| 2 | 严格 | **CLI 写操作一律拒绝**（增删改源 `-d`/`-r`/`-u`/`--sync`/`--update-all`、归档 `-a`、`--fulltext`、`--dedup hide*`、`--policy set`、`telemetry enable` 等非只读命令）；只读命令可用 |
| 3 | 极致 | **CLI 全部拒绝，唯一例外 `sip simon status`**；全部数据加密（SQLCipher + AES，密钥只存系统凭据库，按数据目录隔离） |

- **命令被拒时先查挡位**：跑 `sip simon status --json` 确认当前挡位，再向用户解释——挡位 2 时改用只读命令（`--grep`/`--show`/`-l`/`--diff`/`--today`/`--dedup scan` 等）或建议用户去 TUI 操作；挡位 3 时 CLI 只剩 `sip simon status`，一切操作都要用户在 TUI 里做。**不要反复重试被拒的命令。**
- **降挡只能在 TUI 命令栏进行**（真人坐在键盘前）；CLI 调用 `sip simon level` 降挡报 `SIMON_LOCKED`——AI 永远不能代跑降挡，告知用户自己在 TUI 里调。
- 挡位 3 的加密密钥自动生成、按数据目录隔离（多副本互不影响）；换机迁移用 `sip simon export-key` 导出密钥。

## 退出码（脚本/AI 判断成败）

所有 CLI 命令成功退出码为 `0`，失败按类别返回非零码（shell 可用 `sip -u 1 && echo OK` 这类惯用法）：

| 退出码 | 含义 |
|--------|------|
| `0` | 成功（含正常取消，如 `-r` 确认时回 n） |
| `1` | 通用错误：参数/用法错误、未知命令、数据库错误、`--sync`/`--update-all` 部分失败 |
| `2` | 网络 / 服务不可达：`NETWORK_ERROR`、`MODEL_UNAVAILABLE`、下载超时 |
| `3` | 资源未就绪：AI 未配置、API Key 缺失/无效、`NO_INDEX`、源/文章不存在、空查询 |

错误文本格式：`Error [错误码] 消息` + `Suggestion:` + `Details:` 三行式；`--json` 模式错误为 `{"success": false, "error": {"code": ...}}`。**用退出码 + 文本/JSON 双重判断成败**，别只靠解析文本。simon 相关错误码：`SIMON_LOCKED`（降挡被拒/守护拦截）、`ENCRYPT_FAILED`（挡位 3 加密失败，数据仍为明文，可重试）、`USAGE`（参数错误）。

## 两种场景

### 场景一：普通操作（订阅/管理）

用户要增删改查订阅源、归档、更新时使用。示例：

```bash
sip -l                                    # 先看有哪些源
sip -u 1 --ignoresafeannouncement         # 更新 1 号源
sip -a 1 --ignoresafeannouncement         # 归档 1 号源
sip -d https://example.com/rss            # 添加新源
sip --summary 12 --ignoresafeannouncement # 生成摘要
```

注意：`--summary`/`--index` 等依赖 AI 的命令，若未配置（`--config` 检查）需先提示用户执行 `sip --init`。

### 场景二：检索（全文 + 语义搜索）

当用户要「找文章」「了解某主题」「在某源里查内容」时，这是重点场景。**策略：多轮、多样关键词、结合两种搜索**。

#### 原则

1. **先用全文搜索确认命中**：`--grep` 是精确关键字匹配（标题/正文/摘要），不依赖 AI、无阈值问题，最适合先跑。**默认就是安全的片段模式**：每篇只出「编号+标题+出现次数+±50 字符片段」（上限 20 篇 × 10 段），不会把大源正文灌进上下文；命中太多可加 `--limit N`，需要结构化结果用 `--json`，要某篇完整正文用 `sip --show <编号> --json`。
   > ⚠️ **别轻易跨全源 `--search`**：跨源 `--search` 是向量全量扫描，数据量大时会明显变慢。先 `--grep` 确认精确命中；确需语义扩展再用 `--search`，并配合 `--feed 编号` 限源、`--threshold` 调阈值；避免在大库上高频跑无谓的跨源搜索。
2. **再用语义搜索扩展**：`--search` 按语义相似度找「意思相近但字面不同」的文章，能补全文搜索漏掉的。
3. **多次换关键词**：不要只搜一次。围绕主题拆出 3~6 个不同的关键词/短语/同义词/英文原文，逐个检索，合并去重。
4. **留意阈值**：`--search` 的 `--threshold`（默认 0.7）控制返回底线。阈值太高结果太少、太低噪声多，要**根据结果数量动态调整**。

#### 检索步骤模板

```bash
# 1) 全文搜索几个关键词（精确命中）
sip --grep "关键词A" --ignoresafeannouncement
sip --grep "关键词B" --ignoresafeannouncement
sip --grep "英文原文" --ignoresafeannouncement

# 2) 语义搜索，先用默认阈值看覆盖
sip --search "关键词A" --ignoresafeannouncement
sip --search "一句话描述主题" --ignoresafeannouncement

# 3) 根据结果量调整阈值：
#    结果太少（0~2 条）→ 降阈值，如 --threshold 0.5
#    结果噪声多（>20 条且相关度低）→ 升阈值，如 --threshold 0.8
sip --search "关键词A" --threshold 0.5 --ignoresafeannouncement
```

#### 阈值判断

| 现象 | 建议 |
|------|------|
| 0~2 条结果 | 阈值过高 → 降到 0.5~0.6 再试 |
| 结果很多但都不相关 | 阈值过低 → 升到 0.75~0.8 |
| 本地 bge-m3 模型 | 相似度常落在 0.5~0.6，建议 0.5 |
| 云端模型（text-embedding-3 等） | 0.7 附近合理 |
| 命中来自抓取全文（`--fulltext` 过的文章） | 全文向量命中分普遍比标题向量**低 0.1~0.2**，搜「正文独有概念」偏少属正常，可降阈值重试 |

#### 合并结果

多轮检索后合并去重，按出现频次/相关度排序，向用户呈现「标题 + 来源 + 链接 + 命中位置」。如果语义搜索返回的相似度普遍偏低但内容明显相关，应主动说明并降低阈值重试，不要因为默认阈值就漏掉相关文章。

#### 读取全文

搜索结果里的 `[编号]` 即文章真实 ID。**需要看某篇全文时**（总结、问答、引用），用 `sip --show <编号> --json` 把原始正文打到标准输出，例如：

```bash
sip --show 42 --json --ignoresafeannouncement        # 读 42 号文章全文（JSON：标题/来源/链接/时间/作者 + 原始 HTML 正文）
sip --show 42 --json --lang en-US --ignoresafeannouncement
```

- `--show <编号> --json` 输出的是**未渲染的原始正文**（`content` 字段是 HTML 原文）；若该文抓取过全文，还会带 `fulltext` 字段（纯文本正文，优先用它回答，比 RSS 摘要完整），可直接给 AI 读
- ⚠️ **裸 `sip --show <编号>`（不带 `--json`）会进入全屏阅读界面**，占用终端等待按键——AI 场景一律带 `--json`，不要裸跑
- 优先用 `--show ... --json` 拿到的正文来回答用户，而不是只依赖 `--grep`/`--search` 的摘要片段

#### 查看历史版本

文章作者改过内容时会保留多个版本（TUI 里标题带 ✎）。CLI 用 `--versions <编号>` 列出全部版本（版本号/状态/时间/标题，`←` 标记当前版），想读某版原文用 `--show <该版本的编号> --json`：

```bash
sip --versions 42 --ignoresafeannouncement   # 列出 42 号文章的所有历史版本
sip --show 87 --json --ignoresafeannouncement   # 读 87 号（可能是某个历史版本）的全文
```

注意：`--versions` 传的是 `--show`/`--grep` 结果里的**全局文章 ID**；每个版本是独立的数据库行、各有自己的 ID，只有 `--show <版本ID> --json` 才能看到旧版正文。文章只有一版时输出提示（退出码 0，不算错误）。`sip -l <源编号>` 列表里的编号是 `[列表序号/真实ID]` 双格式，用 `--show`/`--versions` 等命令时取**右边**的真实 ID。

## 常见问题

- **「尚无向量索引」**：还没 `--index`。先 `sip --index`（或提示用户）。
- **「模型维度变化」**：换了模型，需 `sip --reindex`。
- **语义搜索结果少**：阈值调低 + 换更多关键词。
- **`--grep` 永远可用**：全文搜索不需要 AI，是语义搜索出问题时的可靠兜底。
- **命令被拒**：先查 `sip simon status` 看挡位，不要盲目重试。

## 数据积累行为（长期使用才会出现，注意别被误导）

- **来源健康**：`-l`/`--feed-info` 可能显示「⚠ 长期未更新」「✗ 失败 N 次」——这是**长期数据**，不代表命令出错；源刚加/刚更新过通常显示正常。
- **内容质量**：`-l N` 里 `[摘要]`/`[无正文]` 标记、JSON 的 `quality` 字段（`full`/`short`/`empty`）是**长期积累**的结果；`short` 的文章可以建议用户用 `--fulltext` 抓全文。
- **阅读进度**：`reading_progress.json` 记录滚动位置，重开文章可能提示「按 Space 跳回」——TUI 行为，AI 用 `--show ... --json` 读取不受影响。
- **阅读报告（`--insights`）**：只有在遥测开启（`sip telemetry enable`）后才可用；报告里的打开/读完/完成率/AI 调用次数来自遥测，♥🤖 点赞来自 signals（无需遥测）。**报告只呈现事实与规则观察，AI/程序不替用户做决定**；用户可据此在报告页按 a/x 归档/删除订阅源，或邀请 Agent 协助讨论。遥测会把 AI 调用（摘要/搜索/嵌入）按文章/源归因，报告可按源统计。报告间隔用 `--insights-interval` 设定，到期自动提醒。
- **全文/向量缓存**：`fulltext/`、`vecs.json` 会随使用增长，超出阈值自动清理；如需强制清理用 `--purge-fulltext`。
- **跨源去重**：`--dedup`/`--today` 的「可能同文」依赖段落重合度，v1.1.4 起 `--dedup scan` 输出**重复簇**（JSON 里 `clusters[].representativeId`/`members`）；`hide-cluster <代表Id>` 一键隐藏整簇（保留代表、隐藏其余成员），隐藏后文章 `Status='dedup'`，从搜索/全文/摘要/计数里消失（数据仍在，`--dedup undo` 可恢复）。`dedup.json` 里规则会阻止该文章被 `--sync` 重新导入（除非它已被作者改成不同内容）。
- **遥测记录**：遥测开启时，`search` 事件会记录**完整查询词**（`--grep`/`--search` 的 query），仅存本机 `telemetry.db`，可 `telemetry show/export/clear` 查看、导出、删除，**不会上传**。介意可在开启遥测前知晓此点。

## 交互说明

无参数运行 `sip` 会进入 TUI（三键键盘导航），AI 场景一律走 CLI（带参数），不要进 TUI。

- TUI 里按 `M`（或命令行 `manage`）是**订阅源管理页**（给人用的，AI 不需要）；manage 里 `Enter` 打开单源编辑面板、`s` 用方向键选更新计划、`i` 查看已隐藏文章。
- **TUI 命令行与 CLI 能力对齐**：TUI 内按 Esc 呼出的命令行支持 `diff / export / export-opml / import-opml / feed-info / like / likes / purge-fulltext / dedup / insights-interval / telemetry / config / simon` 等（输出在 TUI 对话框里展示）。AI 场景仍建议走 CLI（`--json` + 退出码更利于脚本），但在 TUI 里人也能做到和 CLI 一样的事；**降挡（`simon level` 调低）只能在这里做**。
- TUI 命令行 `fetch` = 抓当前文章全文；**首次使用全文抓取需要用户输入同意短语**（一次性，交互式）。AI 一律用 `sip --fulltext <id> --yes` 跳过同意与二次确认——**不要代用户在 TUI 里操作同意流程**，需要抓全文时告诉用户或直接跑 CLI 带 `--yes`。
- **Telemetry 默认关闭**（遥测妹纸叫**苏暖泉 / Sumenia**）：AI/非交互场景不会询问、保持关闭，无需处理；即使用户开启了遥测，也只在本地记录（`telemetry.db`），不会自动上传。
