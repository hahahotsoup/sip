---
name: sip-rss
description: 调用 sip（RSS 阅读器）CLI 进行订阅管理、全文搜索与语义搜索。用于执行 AI 任务时检索 RSS 文章、管理订阅源。当用户要求搜索/检索 RSS 内容、查看订阅源、下载或管理文章时使用。
---

# sip — RSS 阅读器 CLI 使用指南

`sip` 是一个本地 RSS 阅读器，所有数据存于 SQLite（`rss.db`）。AI 通过命令行调用它来完成两类任务。

## ⚠️ 先初始化，别默认模型已就绪

**AI 很容易犯的错**：默认 Embedding / LLM 模型已经配置好、文章已经向量化。**事实是：默认什么都没配置、什么都没索引。** 必须按下面流程先检查，缺什么补什么：

```bash
sip --config                                   # ① 检查 AI 是否已配置（无输出/提示未配置 → 需 --init）
sip --search "test" --ignoresafeannouncement   # ② 试探搜索：报「尚无向量索引 / run --index」→ 需先 --index
```

- **未配置 AI**（`--config` 无有效配置）：先跑 `sip --init`（交互式向导，会提示用户录入 API Key；若用户不在场，**告诉用户需要先手动执行 `sip --init` 或配置 ai_config.json**，不要假装已配置）
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

**编码**：sip 一律输出 **UTF-8**。若调用环境的终端是 GBK/其他代码页（Windows cmd/PowerShell 默认 GBK），把输出按 UTF-8 解码即可，或在 PowerShell 里先执行 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`；不要用 GBK 解码，否则中文乱码。

## 命令速查

| 命令 | 说明 |
|------|------|
| `sip -l` | 列出所有订阅源（编号、标题、文章统计） |
| `sip -l <编号>` | 列出某源的文章 |
| `sip -d <url>` | 下载/添加新 RSS 源（URL 可省略协议前缀） |
| `sip -u <编号>` | 更新某源 |
| `sip -a <编号>` | 归档（加时间戳） |
| `sip -una <编号>` | 去归档 |
| `sip -r <编号>` | 删除源及其全部文章与向量 |
| `sip --config` | 查看 AI 配置 |
| `sip --index` | 对文章做 Embedding 向量化（需先 `--init`） |
| `sip --reindex` | 更换 Embedding 模型后重新向量化 |
| `sip --search <查询> [--feed 编号] [--threshold 0.7] [--json]` | 语义搜索（Embedding） |
| `sip --grep <关键词>` | 全文搜索（标题/正文/摘要关键字匹配，不依赖 AI） |
| `sip --show <编号>` | 原文直出：文章标题/来源/链接 + 原始正文打到标准输出（**读全文用这个**） |
| `sip --summary <编号>` | 为文章生成 LLM 摘要 |
| `sip --summary feed:<编号>` | 为某源全部文章生成摘要 |
| `sip --summary-all` | 为所有未生成摘要的文章生成摘要 |

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

1. **先用全文搜索确认命中**：`--grep` 是精确关键字匹配（标题/正文/摘要），不依赖 AI、无阈值问题，最适合先跑。
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

#### 合并结果

多轮检索后合并去重，按出现频次/相关度排序，向用户呈现「标题 + 来源 + 链接 + 命中位置」。如果语义搜索返回的相似度普遍偏低但内容明显相关，应主动说明并降低阈值重试，不要因为默认阈值就漏掉相关文章。

#### 读取全文

搜索结果里的 `[编号]` 即文章 ID。**需要看某篇全文时**（总结、问答、引用），用 `sip --show <编号>` 把原始正文打到标准输出，例如：

```bash
sip --show 42 --ignoresafeannouncement        # 读 42 号文章全文（标题/来源/链接 + 原始 HTML 正文）
sip --show 42 --lang en-US --ignoresafeannouncement
```

- `--show` 输出的是**未渲染的原始正文**（Content 原文，可能是 HTML），不需要再进 TUI 或调 `--preview`
- 优先读 `--show` 拿到的正文来回答用户，而不是只依赖 `--grep`/`--search` 的摘要片段

## 常见问题

- **「尚无向量索引」**：还没 `--index`。先 `sip --index`（或提示用户）。
- **「模型维度变化」**：换了模型，需 `sip --reindex`。
- **语义搜索结果少**：阈值调低 + 换更多关键词。
- **`--grep` 永远可用**：全文搜索不需要 AI，是语义搜索出问题时的可靠兜底。

## 交互说明

无参数运行 `sip` 会进入 TUI（三键键盘导航），AI 场景一律走 CLI（带参数），不要进 TUI。
