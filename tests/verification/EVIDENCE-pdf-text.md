# 取证：PDF 逐页文本路径（t10 的输入，非最终验收判定）

快照：`tests\Sip.Tests\bin\Release\net10.0\sip\sip.dll` = `73cc4e6a9ce6…`，mtime **09-25 11:56:14**
（产品树在动；换快照后需重跑。）
复现：`python tests/verification/probe_pdf_toc.py "<pdf>" [关键词]`
（驱动 `sip_driver.py`：拷隔离实例 → Agent 门 → 真跑 CLI → 直查 sqlite；不碰真实库）

## 0) 独立真值（PyMuPDF，不经 sip）

`C:\Users\hahahotsoup\Downloads\1123.pdf`：3 页、**有文本层**、字数 **1051 / 1002 / 467 = 合计 2520**、书签 **0 条**。

## 1) 实测：`--toc` 在 PDF 上**是通的**（收窄 anchor-eng 的"PDF 分支可疑"）

| 命令 | rc | 实际输出（节选） |
|---|---|---|
| `--import 1123.pdf --title PDFProbe` | 0 | `Imported: PDFProbe → #1`；`Items.PageCount=3` |
| `--toc 1`（人类可读） | 0 | `PDFProbe · PDF · 没有书签 —— 只能按页码走` + `1 第 1 页 / 2 第 2 页 / 3 第 3 页` |
| `--toc 1 --json` | 0 | `chaptersSource="page"`, `chapterCount=3`, `pageCount=3`, `backfilled=false`, `chapters[].chapterId = pdf:p1/p2/p3`，`PageStart=PageEnd=1/2/3` |
| `--chapterize 1` | 0 | `重建章节：成功 1，失败 0` |

→ **stdout 不空**（`--toc --json` 1174 字节）。anchor-eng 报的"stdout 为空"在本快照上**无法复现**，
更像他那层 PowerShell 包装（他自述脚本有 bug）或更早快照；**"PDF 分支坏了"这条假设不成立**。
页级目录（无书签 → `chaptersSource='page'`）工作正常，符合 A2/A29 的形状。

## 2) 实测：缺的是**逐页文本**，且诊断话术与事实相反

| 命令 | rc | 实际输出 |
|---|---|---|
| `--toc 1 --json` 同一份 payload | 0 | `"textAvailable": false, "textLayer": true` |
| `--page 1` | 0 | `第 1 页` |
| `--page 1 --json` | 0 | `textAvailable:false`，**没有 `text` 字段** |
| `--locate 第 --book 1` | 0 | **`这份 PDF 没有文本层，关键词定位不可用`** |
| 库直查 | — | `SELECT COUNT(*) FROM PdfPages` → **0**（`--toc` 与 `--chapterize` 均已跑过，两次机会都没写） |
| 库直查 `Chapters` | — | 3 行，`Source='page'`、`PageStart/PageEnd` 正确；`CharCount=0`、`CharStart/CharEnd=NULL` |

**期望（契约）**：§1.5.1 边界②③ + §1.6「`PdfPages` 无该书行 → **懒重抽**，与章节同一趟」→ 跑过 `--toc`/`--chapterize` 后
`PdfPages` 应覆盖 `[1,3]`、`CharCount` 合计 ≈ 2520；`--page 1 --json` 应带 `text`（§3.4）；
`--locate` 应反查到**页**（§3.5 / A34）。**实际：0 行。**

**两条性质不同的问题，别合并成一个 bug**：
1. **功能缺口**：抽取未实现（captain 收口 t3 时已记：「`PdfPages` 表已建、抽取逻辑未实现」）—— t10 在做。
2. **话术违约（独立问题，改起来是几行）**：`--locate` 对**有文本层**的文件说「**这份 PDF 没有文本层**」。
   同一份 payload 里 `textLayer` 就是 `true`，PyMuPDF 也实测 2520 字 —— 这是**对格式的错误断言**，
   正是 §1.5「措辞纪律」/A14 禁止的写法（只允许「本轮 sip 不抽 PDF 正文」或「这一份没有文本层（实测）」）。
   两者**降级路径不同**：把"本轮不抽"说成"读不到"，AI/用户会去重试一个永远不会变的事实。
   → 抽取落地后，这里应改成「本轮不抽 / 尚未抽取（`pdf:not-extracted`）」类话术。

## 4) 给 t10 的断言清单（可直接用）

- `PdfPages` 行数 == `pageCount`，`Page` 覆盖 `[1,3]`，`sum(CharCount)` ≈ 2520（±空白规范化）；
- 幂等：第二次 `--toc` 后行数与各页文本不变（A32）；
- `--page 1 --json` 的 `text` 非空且与 `PdfPages.Text` 一致（A35 四处同页的一部分）；
- `--locate 第 --book 1` 返回 `page`（1 基）+ `chapterId` + `snippet`，**且不产生任何 embedding 调用**（A33/A34）；
- 扫描件（如 `## 找骨架…(1).pdf`，逐页 0 字）：`PdfPages` 行齐全但 `CharCount=0`、`textAvailable:false`、**`success:true`**（正常结果，不是错误）。
