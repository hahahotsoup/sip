# AI 阅读定位 · 契约（v1 · 冻结给实现/验证/评审）

> **这份文档是什么**：t3（章节锚点）/ t4（悬浮球 harness）要实现的东西的**唯一契约**，
> 也是 t5（验证）/ t6（审计）/ t7（评审）/ t8（整合）判断"对不对"的依据。
> 它是**规格**，不是实现建议：凡标【必须】的都是可验收的硬约束；标【建议】的允许实现方按现场调整，
> 但调整必须在本文件里留一行修改记录（见 §12 变更流程）。
>
> **权威顺序**（冲突时按此裁决，不按"谁写得更新"）：
> 1. `docs/草稿-AI阅读悬浮球.html` —— 用户看过并认可的**交互**规格（界面与手感以它为准）；
> 2. 本文件 —— **数据/接口/语义**的契约（草稿里的假数据不许当数据契约用）；
> 3. 现有代码的既有行为（`--show`/`--pages`/`--vision`/`/api/imports/*` 一律**不得**改变语义）。
>
> **本轮范围**：本地导入的 EPUB / PDF（含无书签、扫描件）/ 其它导入格式的降级；
> Web 阅读页右下角悬浮球 + SSE 流式问答；四层上下文供给；按书可删的对话历史。
> **不做的**见 §11（那里列的东西**同样**是契约：评审按"有没有偷偷做"来判）。
>
> ✅ **本轮已拍板项（一眼看清，别再回头问；r1 修订 A33）** —— 这几条都**已落进本文件**，
> 不存在"等某人裁决"的悬置状态；若谁要推翻，按 §12 记一笔偏离再说：
> 1. **PDF 逐页文本抽取 = 做**（用户拍板：「pdf需要embedding」＋ 被问"要不要顺手抽文本层"时选"做"）
>    → `PdfPages` **一页一行**；`--page` 给该页文本、`--locate` 支持**页级**关键词反查。**见 §1.5.1 / §12.1-A15。**
> 2. **PDF 可以进块表，但只能"按需"** → `--index`/`--reindex` 是唯一调用点；
>    **导入与懒回填永不产生 embedding 调用**（成本可见、可取消）。**见 §6.2 R5 / §12.1-A17。**
> 3. **草稿对用户的承诺已改**：从"读不到 PDF 句子"改成"**文字层已逐页抽出**；抽取**逐份而定**，
>    扫描件仍然抽不出任何字"。**见 §1.5 措辞纪律 / §12.1-A26。**
> 4. **挡位 ≥2 拦「问 AI」（403 `SIMON_BLOCKED`）**；**纯读端点仍须 200**；闸门在**写响应头之前**判。
>    **见 §5.4 / §5.3.1 / §12.1-A19+A22+A27。**
> 5. **PDF 仍然不做**：OCR、章节猜测、段/行级精度（**做**的是：逐页文本、页级关键词、按需语义索引）。
>    **见 §11-2/3/4。**
>
> ⚠️ **报"契约说 X"之前，先做 30 秒自查（r1 修订 A24）** —— 本文件改得快，旧副本与当前版**长得不像**，
> 团队里已经出现过多次"基于旧版下判断"（`§1.5 没有 PdfPig`、`P/Invoke 是主路径`、`§5.4 的挡位口径`…），
> 那几条分别早在 A1 / A13 / A19 就改掉了。**三步**：
> 1. **读 §12.1 变更记录，以其中最大的 `A<n>` 编号为"当前版"标志**。凡被改过的结论都在那里留了一行，
>    含"谁、用什么方法、什么样本"，并会注明**撤销/替换**了哪一条；没在 §12.1 出现过的结论，才算"原始至今未变"。
>    ⚠️ **行号（`:NNN`）不是版本标志** —— 它每次修订都漂。看到"契约 :973-974 说…"这种引用，
>    先问一句：**那是本文件，还是别处（如审计报告）**里的**旧版摘录**？本文件现在只有你正打开的这份
>    `docs/AI阅读定位-契约.md`。
> 2. **关键字 grep 本文件**（例：`PdfPig`、`PdfPages`、`AnchorState`、`persisted`、`SIMON_BLOCKED`）。
>    ⚠️ **别把命中数当判定标准** —— 它每改一次就变。只认"**量级**"：这些关键字在本文件里都是**几十次**级别的
>    （`PdfPig`/`PdfPages` 三十次以上）；如果你只搜到零到几次，你手上就是旧版。
> 3. **引用时带上原句 + §章节号**（如"§5.4：'写库即过闸…'"），对方就能立刻判断你看的是哪一版。
>    ⚠️ **§12.1 里的"绝对化措辞"要回读原文**（r1 修订 A38，captain 提）：**摘要容易把带条件的规则压成无条件** ——
>    凡在变更记录里看到「任何情况 / 永不 / 一律 / 绝不 / 只有」，都去 § 正文回读一遍（正文才是规范）。
>    反过来，**写变更记录的人也【必须】给绝对化表述带上条件**（"一律"要写清"对谁一律"）。
>    ⚠️ **通信也会过期**（r1 修订 A31）：**邮件/消息里引用的"契约说 X"同样要回文件核** ——
>    以 §12.1 的**最大 `A<n>` 编号**为准。本轮出现过"照着旧邮件里的契约结论开工"的案例
>    （那封邮件写于用户拍板**之前**，结论已被 A15/A17 取代）。**消息不是契约，文件才是。**
>    **推论：遇到"两处说法相反"时不要停下工作去等拍板** —— 先回文件核一遍；
>    本轮两次"等你收敛"其实都是邮件旧版，文件里从来没有冲突（A32）。
> 4. **正文里不会出现被撤销的旧表述的原文**（r1 修订 A25/A26）：§0~§11 只写**现行**说法，旧文一律只在 §12.1 变更记录里
>    出现。所以你 grep 到某句"陈旧说法"时，两种可能：**在 §12.1 里 = 历史记录**（合法）；
>    **在正文里 = 要么是被"不许/永远不许"显式标记的禁止对象**（合法，例如"永远不许写「PDF 没有文本层」"），
>    **要么就是真残留**（该改）。判据：那句**是不是在陈述事实**。
>    （这条规则是被两次误判逼出来的：§5.4/§9/§1.5 曾**逐字引用**旧文来说明"这已被改掉"，
>    结果 grep 自查把它们当成"契约还是旧版"的证据。）

---

## 0. 术语与全局不变量

### 0.1 术语表

| 术语 | 含义 | 出处/落点 |
|---|---|---|
| **阅读项 / book** | `Items` 里属于「本地导入」源（`Feeds.FeedUrl = 'local://import'`）的一行 | `Web.cs:3319 ImportItemInfo`、`sipcore.cs:1045 ImportFileCore` |
| **章节（chapter）** | 阅读项内部的一个定位单位。EPUB 是书里的章，PDF 是书签章，无书签 PDF 退回"页" | 本文件 §1 |
| **chapterId** | 章节的稳定编码（**不是**行号），引用、跳转、对话锚点全用它 | §1.3 |
| **锚点（anchor）** | 一次提问附带的阅读位置：`chapterId` +（可选）划词原文 | §4.3 |
| **引用（cite）** | 回答里可点击跳回原文的胶囊：`chapterId` + 结构化位置 | §5.3 / §8 |
| **检索快照（snapshot）** | 这一轮实际用了哪几层材料、命中几条、为什么降级 | §4.4 |
| **章内块（chunk）** | 向量的最小单位，**永不跨章** | §6.2 |
| **harness** | 悬浮球 + 对话抽屉 + `POST /api/imports/{id}/ask` 这一整套 | §5 / §8 |

### 0.2 六条全局不变量【必须】

- **I1 · harness 对库只读**：`POST /api/imports/{id}/ask` 这条路径打开 `rss.db` **必须**用只读连接
  （`Mode=ReadOnly`），且**不建表、不回填**（§1.6 / §4.2.1a）。
  验收方式：提问前后 `rss.db`（含 `-wal`/`-shm`）的 SHA-256 完全不变（§附录A・A7）。
  ⚠️ **I1 的边界（r1 修订 A23，审计员指出原表述会与严版裁决打架）**：
  **I1 的「只读」针对的是用户信息本体**（`Items` / `Feeds` / 标签），而 **`rss.db` 只是它当前的载体，不是定义**。
  **`chat.db` 的独立只读不变量，是"让 I1 可被哈希证明"的证据手段，不是"对话记录不算写入"的依据** ——
  恰恰相反：提问**创建持久记录**，按 §5.4 的判据**过写闸门**。这两件事必须分清，否则 t5 会照旧表述把严版实现判错。
- **I2 · 位置不依赖 AI**：目录（`--toc` / `GET .../toc`）与跳章/跳页在
  **没有 `ai_config.json`、没有 Key、没有向量索引**时**必须**照常工作（§8.3）。
- **I3 · 章节是"读出来的"，不是"编出来的"**：每个章节来源都如实登记在 `Source` 里
  （`ncx`/`nav`/`spine`/`bookmark`/`page`/`heading`）。拿不到就降级并说明，**不允许**把猜测的标题写进库。
- **I4 · 引用一定跳得回去**：回答里的每个引用必须能落到一个真实存在的章节/页；
  跳不过去的位置**不许**出现在引用里（宁可不给引用）。
- **I5 · 划词永远在上下文里**：无论上下文预算怎么挤，划词段都是**最后**被丢弃的一层（§7 预算梯度）。
- **I6 · 新旧共存**：现有 CLI/HTTP 行为不变。只允许**加法**：新命令、新路由、新列、新响应字段；
  既有字段的含义与取值不改（`--show`、`--pages`、`--vision`、`/api/imports/{id}/text` 的 `bodyHtml` 等）。

---

## 1. 章节数据模型

### 1.1 存在哪里：`Chapters` 进 `rss.db`，对话历史进独立的 `chat.db`

**决定**：

| 数据 | 位置 | 为什么 |
|---|---|---|
| 章节（`Chapters`）、块-章对齐（`VectorsChunks.ChapterId`） | `rss.db`（**派生数据**，与 `VectorsChunks`/`ItemsFts` 同级） | 它描述的是**库内内容的结构**，删除导入项时要和 `Items` 同事务级联；项目已有先例：`VectorsChunks`、`ItemsFts` 都是派生表，都在 `rss.db`，都由 `InitDatabase` 建（`sipcore.cs:4368`、`:4356`） |
| 对话会话与消息（`ChatSessions`/`ChatMessages`） | **独立文件** `readwithhotsoup/chat.db` | 让「harness 对库只读」（I1）成为**连接级**保证而不是口头约定；删一本书的对话 = 删一张表的行，不用碰用户的主库；审计可以拿"`rss.db` 一个字节都没变"当证据。<br>⚠️ **但这条理由不等于"对话记录不算写入"**（r1 修订 A23）：**写 `chat.db` 同样过写闸门**（§5.4 三层判据）—— 独立只因"证据可证"与"删书便利"，与是否算写操作**无关** |

**反面选项（明确否决）**：把对话塞进 `rss.db`。它会立刻破坏 I1 的可证明性
（同一个 `Items` 库既被读又被写，"harness 不改库"就只剩一句注释了），
而且用户删 `rss.db.plaintext.bak`、拷库迁移时会把对话一起卷走。

`chat.db` 的表由独立的 `InitChatDatabase(chatDbPath)` 建（`CREATE TABLE IF NOT EXISTS`，幂等），
打开方式复用现有 `OpenDb(path)` 的约定（同样的 `PRAGMA foreign_keys=ON` 等）。

### 1.2 `Chapters` 表【必须】

```sql
CREATE TABLE IF NOT EXISTS Chapters (              -- 章节锚点：导入电子书的目录模型
    ItemId      INTEGER NOT NULL,                  -- 关联 Items.Id（只对本地导入项建行）
    ChapterId   TEXT    NOT NULL,                  -- 稳定编码，见 §1.3
    Ord         INTEGER NOT NULL,                  -- 书内顺序，1 基，从 1 连续递增
    Depth       INTEGER NOT NULL DEFAULT 0,        -- 层级，0 = 顶层（EPUB 的 NCX / PDF 的书签树）
    ParentId    TEXT    NULL,                      -- 父章 ChapterId（顶层为 NULL）；只表达层级，不表达"对话树"（§7.5）
    Title       TEXT    NOT NULL DEFAULT '',       -- 章节标题；为空是**合法**的（无书签 PDF 的页）
    Kind        TEXT    NOT NULL,                  -- 'chapter' | 'page' | 'front'（front=卷首合成章，见 §1.4.1 N7）
    Source      TEXT    NOT NULL,                  -- 'ncx' | 'nav' | 'spine' | 'bookmark' | 'page' | 'heading'
    Locator     TEXT    NOT NULL DEFAULT '{}',     -- 该章在**原文件**里的定位（JSON，见下）
    CharStart   INTEGER NULL,                      -- 正文（Items.Content **原文**）中的字符偏移，0 基。⚠️ 本轮**恒为 NULL**（见 §12.1-A36）
    CharEnd     INTEGER NULL,                      -- 结束偏移（不含）。⚠️ 同上，恒 NULL
    PageStart   INTEGER NULL,                      -- PDF 起始页，1 基
    PageEnd     INTEGER NULL,                      -- PDF 结束页（含），1 基
    CharCount   INTEGER NOT NULL DEFAULT 0,        -- 该章正文字符数（供预算/排序，不要求精确到字）
    FirstBlock  TEXT    NOT NULL DEFAULT '',       -- 该章首个块级元素的纯文本前 60 字（前端切分用，见 §2.4）
    SourceHash  TEXT    NOT NULL DEFAULT '',       -- 建表时 Items.Content 的 SHA-256 前 16 hex（判定是否要重建，§1.6）
    RulesVersion INTEGER NOT NULL DEFAULT 1,       -- 章节抽取规则版本；规则改了 +1 → 全书重建
    CreatedAt   TEXT    NOT NULL,
    PRIMARY KEY (ItemId, ChapterId)
);
CREATE INDEX IF NOT EXISTS idx_chapters_item_ord ON Chapters (ItemId, Ord);
```

列语义里四条**必须说清**的：

- **`CharStart/CharEnd` 是相对 `Items.Content` 原文的偏移**，服务端切片靠它。
  净化后的 HTML（`ToSafeBodyHtml`）长度/字符位置必然不同 —— 前端**不得**拿它去索引净化后的 DOM（§2.4）。
- **`Locator` 是"怎么找到这段"的原始凭证**，例如
  `{"spine":7,"anchor":"sec2","xhtml":"OEBPS/ch03.xhtml"}` 或 `{"src":"ch3.xhtml#sec2"}` 或 `{"page":87}`。
  它不做查询用，只做**调试与重建**用：当 `CharStart` 漂了（内容被重新导入/换版），靠它回原文件重算。
- **`Title` 允许为空**：无书签 PDF 的页没有标题，写空串，前端按 `Kind='page'` 显示"第 N 页"（§8.1）。
- **`Kind='front'` 是"卷首合成章"**（r1 修订，见 §12.1-A11）：第一个目录项起点**之前**的正文
  （封面/版权页/目录页）不能被丢进"无章节黑洞"，它单独成一行、`Title=''`、`Locator.synthetic=true`。
  它**参与**区间覆盖与 `--locate` 归属，但 UI 显示为"卷首"（§10.2 键 `Front matter`）。为什么要有它：
  那段文字**仍然是可全文搜索的**，如果它不属于任何章，AI 问"这本书里有没有 X"时会出现
  "搜得到、但说不清在哪一章"的空洞 —— 而契约的底线是"位置要么说得准，要么明确说没有"。

### 1.3 chapterId 编码格式【必须】

```
chapterId := epub:<spine>            EPUB，spine 内第 spine 个 XHTML（0 基）
           | epub:<spine>~<anchor>   EPUB，同一 XHTML 内的片段 id（多级目录常见）
           | pdf:p<page>             PDF，起始页（1 基）
           | sec:<n>                 其它格式（mobi/docx/txt/md）按标题切出来的第 n 节（1 基）
```

⚠️ **`<anchor>` 是 `src` 的"片段 id"（`src="ch3.xhtml#sec2"` 里的 `sec2`），不是 navPoint 的 XML `id`**（r1 修订 A32）。
`<navPoint id="np-3">` 那种 id 只活在**目录文件内部**、指向不了正文，目录一重新生成就全变；
而片段 id 指向**正文里的真实位置**，是内容的属性，不会因为目录重建而漂。
**所以：从 `src` 取 `#` 之后的片段 id；`src` 没有片段时才退回 `epub:<spine>` + `~n<k>` 消歧（§1.4.1）。**
（实现方若用了 navPoint 的 XML id，`--chapter`/引用跳转会在正文里找不到落点 —— 这是"引用必须跳得回去"（I4）的直接违约。）

正则（实现方与验证方共用这一条）：

```regex
^(epub:\d{1,5}(~[A-Za-z0-9_.:-]{1,64})?|pdf:p\d{1,6}|sec:\d{1,5})$
```

**为什么这么定**：

- 用 `spine`（OPF spine 的数组下标）而不是"第 N 章"：spine 是书自己的结构，改规则、换解析器都不会变；
  而"第 N 章"是**目录顺序**，目录一变就会串位 —— 而引用要能长期跳得回去。
- 片段分隔符用 `~` 而**不是** `#`：`#` 在 URL 里是 fragment 分隔符，写进路径会被浏览器截断，
  逼着每个调用点做 percent-encoding，早晚会错一次（`epub:7~sec2` 可以直接放进路径段）。
- PDF 用**起始页**而不是"第 N 个书签"：书签树重排不该让引用失效；页码是用户能自己核对的东西
  （草稿里 AI 说的就是"从第 87 页开始"）。
- `sec:` 前缀**自报家门**是启发式：它对应 `Source='heading'`，UI 与 AI 都要把它说成"按标题分的节"，
  不假装是出版方目录（I3）。
- `chapterId` **不含** `/`、空格、中文 —— 于是它可以直接当 URL 路径段、HTML `data-` 属性和 JSON key 值。

### 1.3.1 下标 / 页码的 0 基与 1 基约定（r1 修订，见 §12.1-A2）

**这是实现方最常问、也最容易在边界处差一的地方，一次说清**：

| 位置 | 基 | 说明 |
|---|---|---|
| `PageStart` / `PageEnd`（表）/ `pageStart` / `pageEnd`（JSON） | **1 基** | 页码对用户可见，1 基是唯一合理的选择 |
| `page`（`--page` 入参）/ `/api/imports/{id}/page/{n}` 路由 | **1 基** | 既有路由 `Web.cs:3592` 就是 1 基，不许改（I6） |
| `chapterId` 里的 `p<N>` | **1 基** | 与 `pageStart` 同一个数，`pdf:p87` = 第 87 页 |
| PdfPig `BookmarkNode.PageNumber` / `PdfDocument.GetPage(n)` | **1 基**（实跑钉死，见 §12.1-A12） | 两者同为 1 基：`PageStart` **直接取** `PageNumber`，**不做 ±1**；pdfium / `RenderPdfPages` 的 0 基页索引只在**那一个调用点** −1 |
| `ord` / `turnIndex` / `Ord` / `TurnIndex` | **1 基** | 人读的"第几章/第几轮"，从 1 连续 |
| `epub:<spine>` 里的 `spine` | **0 基** | **全契约唯一的 0 基例外**：它就是 OPF spine 数组下标、不是页码。写代码时不要顺手 +1 |
| `sec:<n>` 里的 `n` | **1 基** | 切出来的第 n 节 |
| `Depth` / `depth` | **0 基** | 层级深度，0 = 顶层 |
| `charStart` / `charEnd` / `offsetInChapter` | **0 基**，且**半开区间** `[start, end)` | 字符偏移，与 `string.Substring(start, end-start)` 的语义一致 |
| 既有 `--pages` 语法（"3-7"/"-10"/"50-"） | **1 基**（用户侧），内部 `ParsePageRange` 返回的 `HashSet<int>` 是 **0 基** | **既有实现不许改**：只在调用 `RenderPdfPages` 的边界转换（`sipcore.cs:5112`） |

【必须】跨这条边界时**只**在函数入口/出口各转换一次，不许在业务逻辑里来回倒（那是差一错的温床）。

【必须】**PDF 侧的四个编号一次说死**（r1 修订，已用 5 个真实 PDF 交叉验证 —— "书签标题是否出现在该页正文里"：
法律与生活 p6 / 逻辑与思维 p6 / 物理选择性必修三 p6 / 中国哲简史(PDF) p1 / 被讨厌的勇气 p4，全部命中且前一页不命中）：

```
PdfPig BookmarkNode.PageNumber (1 基)  ==  Chapters.PageStart (1 基)  ==  chapterId 的 p<N> (1 基)
                                            pdfium / RenderPdfPages 页索引 (0 基)  ← 只在此处 −1
```

【必须】不许再引入第三套编号（例如"第几个书签"或"书签数组下标"）出现在任何对外字段里。

### 1.4 EPUB 与 PDF 的层级与定位差异【必须】

| 维度 | EPUB (.epub/.mobi/.docx/.txt/.md) | PDF (.pdf) |
|---|---|---|
| 层级来源 | **优先级：`toc.ncx` 的 `navMap`（主路径）→ EPUB3 `<nav epub:type="toc">`（兼容分支）→ 退到 spine 平铺**（r1 修订 **A10 把 A3 的顺序反转了**：本机真实 EPUB 全部走 ncx；nav 一次都没在真实书上跑过） | PDF 的 `/Outlines` 书签树（PdfPig `TryGetBookmarks`）。【必须】用**展平后的 `GetNodes()` 有序列表 + `Level`**，**不许**拿 `Roots.Count` 当章节数（实测三种形状：28/28/L0、79/31/L1、39/4/L2，见 §12.1-A12） |
| `Depth`/`ParentId` | 有意义，**保留到 6 层**（更深压平，`Locator.depthFlattened=true`）；粒度到"节"（§1.4.1：navPoint 与 spine 是**多对一**，实测 200/33） | 有意义；`Depth` = **被保留祖先链长度**（可见深度，因为无目标的 `ContainerBookmarkNode` 会被跳过，§1.5），原始层级存 `Locator.rawLevel`；无有效目标但子树有目标的节点**继承**第一个后代页 |
| 定位主键 | `CharStart/CharEnd`（正文原文偏移） | `PageStart/PageEnd`（1 基页码）；文本检索用 `PdfPages`（逐页一行，§1.5.1） |
| 页码 | **没有**，`PageStart/PageEnd` 恒为 NULL | 有；`PageStart = BookmarkNode.PageNumber`（**1 基，别 ±1**，§1.3.1）；`PageEnd` = 下一个**已定位**目录项起始页 −1，末章 = 总页数；第一个目录项**之前**的页 → `Kind='front'` 合成卷首章（N7） |
| 正文可读 | 可（`Items.Content` 是抽出的 HTML/文本） | **可 —— 逐页文本**（r1 修订 A15：PdfPig 抽到 `PdfPages`，一页一行；`ReadPdfFile` 那句占位句**不动**，它只是 `Items.Content`）。`textAvailable` = `pdfHasTextLayer`；**扫描件**（逐页 0 字）才是 `false`（§1.5.1） |
| 章内块进向量 | 会（§6，仅 `--index` 时） | **按需**：带上 `pdf:p<page>` 锚点后**可以**进块表，但**只能由 `--index`/`--reindex` 触发**；导入与懒回填**永不**调用模型（§1.5.1 边界① / §6.2 R5 / §6.6） |
| 目录缺省降级 | 无 NCX/nav → `Source='spine'`（每个 XHTML 一节）；非 EPUB 格式 → `Source='heading'` | 无书签 → `Source='page'`，每页一章，`Title=''`。**这是 436 页级大书的真实主路径**（实测 `SystemVerilog for Design(2nd)`：436 页、文本层完好 722289 字、`TryGetBookmarks=False`），不是边角料（§12.1-A12） |

**标题从哪来、`Source` 说什么：两件事分开记（r1 修订，见 §12.1-A3）【必须】**

- `Source` 描述的是**章节边界（结构）从哪来**：`nav` / `ncx` / `spine` / `bookmark` / `page` / `heading`。
- 标题是**另一个事实**，来源优先级：**ncx 的 `navPoint/navLabel/text` → nav 的锚文本 → 该 XHTML 的首个 `h1`~`h6` → 该 XHTML 的 `<title>` → 空串**。
  用了哪一级记在 `Locator` 里（如 `{"titleFrom":"h1"}`），**不改** `Source`。
- 于是"`Source='spine'` 但标题是 h1 取的"是合法组合，界面照样如实显示"目录按书内顺序（标题取自正文标题）"。
- 【必须】ncx 与 nav **同时存在**时**用 ncx**（r1 修订 A10 反转了 A3 的顺序）：本机真实语料 **100% 是 EPUB2 + `toc.ncx`**，
  ncx 这条路径被 5 本真书验证过，nav **一次都没在真实书上跑过** —— 该用被验证过的那条。
  在 `Locator` 记下实际来源（`{"tocKind":"ncx"}` 或 `{"tocKind":"nav"}`）；
  两者冲突（章数不同）时**不合并**，只认 ncx。

**两条必须写进代码注释的"不许"**：

1. **EPUB 不许伪造纸质页码**。草稿演示数据里 EPUB 章节写着 `p.1-24`，那是**纸质书的页码**，
   sip 的 EPUB 里根本没有这个信息（且拿不到）。所以 EPUB 章节的 UI **不显示页码**，只显示序号与标题。
   这一条是对草稿的**有意偏离**（§12.1 第 1 条），实现方不许"为了跟草图一致"去编页码。
2. **PDF 不许假装有正文**。任何"AI 读过 PDF 第 3 页的内容"的回答都是伪造。PDF 侧只有位置。
   有视觉模型的用户走 `--show <id> --vision --pages 3` 这条既有路径（栅格图给模型看），
   那是**另一条**能力，不改变文本检索的事实。

### 1.4.1 EPUB 章节 = **按 navPoint 建行**（r1 修订，见 §12.1-A9）【必须】

**决定：目录单位是导航点（NCX `navPoint` / nav 的 `<li><a>`），不是 spine 文件。**
**为什么（实测，verifier 用 PyMuPDF/pypdf 独立取真值，不依赖 sip 自己的解析器）**：

| 书 | 目录项 | spine 文件 | 比例 |
|---|---|---|---|
| 中国哲学简史 | **200** | **33** | 6.1 : 1 |
| 看图自学电吉他 | **224** | **18** | 12.4 : 1 |
| 毛泽东选集 | 410 | 416 | 1 : 1 |

两个成因：目录是**嵌套**的（章 → 节），且一条目录项可以只指向文件内的某一段（`src="ch3.xhtml#sec2"`）。
**按 spine 建行会把上面两本书的目录从 200 节压成 33 章**，"第 3 章第 2 节"这种引用就**说不出来** ——
而那正是本次目标的核心。所以：`Source ∈ {nav, ncx}` 时章节行数 ≈ **目录项数**；
`Source='spine'` 只保留给**整本书没有目录**的退化情形。

**chapterId（不改正则，沿用 §1.3 的形状）**：

- 带片段：`epub:<spine>~<anchor>`（`anchor` = `src` 里 `#` 之后的片段 id）
- 不带片段：`epub:<spine>`
- **同一本书内撞键时**追加消歧后缀 `~n<k>`（k 从 2 起，**按目录出现顺序**）：`epub:2~n2`。
  【必须】撞键要**确定性**消歧，不许覆盖 —— 覆盖会让某一节凭空消失，而且消失哪一节取决于遍历顺序。
  【实测 · 真实书就有】（verifier，r1 修订 A20）：《看图自学电吉他零基础篇》的 `OEBPS/text00002.html`
  被**两条无片段目录项**（"扉页"、"版权"）指向 → 第一条 `epub:<spine>`、第二条 `epub:<spine>~n2`，
  且"扉页"同时是一个**零长度章**。所以 `~n<k>` 与 N4 **可以在一本真实书上一起验收**，不必只靠合成夹具。
- `Locator` 里保留原始 `src`、`titleFrom`，供重建与排障。

**切片算法 N1–N8**【必须】：

- **N1 排序键** = `(spine 序号, 锚点在片段内的字符偏移, 目录出现顺序)`。
  **不是**原始目录顺序。⚠️ **依据订正（r1 修订 A20）**：verifier 在 5 本真实书上实测，
  `outOfOrderFiles = 0`（中国哲学简史 28 个多目录项文件、看图自学 15 个、四世同堂 3 个，
  目录序与文档序**完全一致**）—— 所以这条规则**不是**从真实语料归纳出来的，而是**防御性**的：
  它零成本、能挡住畸形/机器生成的目录（那种目录一旦出现，按目录序排会让章区间**互相重叠**，
  后果是引用指到错的章）。
  【必须】因此**不许**把它写成"真实书里常见"；并且**必须**用**合成夹具**覆盖
  （造一本"目录序 ≠ 文档序"的最小 EPUB，见 A39）—— 真实书抓不到按错排序键的缺陷。
- **N2 起点**：(a) 同文件内找到锚点元素（`id="<anchor>"`；**同时要认 HTML4 风格的 `<a name="…">`**）→ 该元素起始处；
  (b) 文件存在但没有片段、或片段找不到 → 该 spine 片段的起点；
  (c) `src` 指向的文件**不在 spine 里** → **丢弃该目录项**（不进 `Chapters`），计入 `tocDropped`。
- **N3 终点 = 全局下一个已定位目录项的起点（可以跨文件）** —— 这就是**边界延续规则**
  （r1 修订，见 §12.1-A11）：一条目录项指到的位置之后、**下一条**目录项目标之前的那些 spine 文件，
  **仍然属于该章**。实测 `咸的玩笑`：NCX 只有 **6** 条，却有 **74** 个 html、其中 34 个含标题 ——
  没有这条规则，"正文一"就没有终点，几十个文件会掉进**"无章节黑洞"**：
  库里明明有内容，AI 问"本章讲了什么"却拿到**空上下文**。
  最后一个已定位目录项的终点 = 最后一个 spine 片段的末尾。
- **N4 零长度是合法的**：起点 == 终点（父章自己没有段落、内容都在子节里）时**保留行**，
  `CharEnd == CharStart`、`Locator.zeroLength = true`。
  【必须】检索命中与关键词定位**归属到"包含该偏移的最深（最具体）章"** —— 零长度章区间为空，
  于是**永远不会**被引用（天然满足 I4，不需要额外过滤）；目录也不需要隐藏它，
  点它等于跳到第一个子节的起点（真实阅读器就是这么做的）。
- **N5 同文件多锚点**：按 N1 排序后依次切片，**区间互不重叠、无空隙、首尾闭合**覆盖该片段全部文本。
- **N6 偏移必须与 `Items.Content` 同一坐标系**：EPUB 的 Content = "按 spine 顺序把每篇 body 的 HTML 拼接（`\n\n` 分隔）"。
  章节抽取【必须】**复用导入路径的同一套遍历与裁剪逻辑**（`ReadEpubFile` + `ExtractEpubHtml`，`sipcore.cs:910/953`），
  【建议】把它重构成"按 spine 返回 `(spineIndex, xhtmlPath, bodyHtml)` 列表"的可复用函数，导入与抽取共用一个实现。
  **不许**另写一套拼接 —— 那会让 `CharStart` 与 `Content` 错位，服务端的章内切片与前端渲染一起错。
  【必须】**重构不得改变产出**：同一个 EPUB 在重构前后导入，`Items.Content` 必须**逐字节相同**
  （验收方式：两次导入的 `Content` 取 SHA-256 比对）。
  为什么这条是硬的：`Content` 一变 → `SourceHash` 变 → 触发 §1.6 的 R-b → **全库重新回填、已存引用全部失效**；
  而且既有老库的书会集体"看起来变了"。这是一个安静的、波及全库的回归。

  ⚠️ **这里还埋着同一个"净化漂移"陷阱**（与 §2.4、附录 C 第 1 条同源）：
  `ExtractEpubHtml` 返回的是 `body.InnerHtml`，而它**已经删过 style/class、把 `img src` 改写成 `file://`** ——
  所以 `HtmlNode.StreamPosition`（**原始文档**里的下标）**不等于**该节点在**返回字符串**里的下标。
  【必须】算锚点偏移时基于**返回字符串本身**（按子节点顺序累加 `OuterHtml` 长度，或
  `IndexOf(node.OuterHtml, start, StringComparison.Ordinal)` 并处理重复出现），
  直接拿 `StreamPosition` 会在"正文里恰好有多个相同元素"时静默错位。

- **N7 卷首合成章**：第一个已定位目录项的起点**之前**的文本（封面/版权页/目录页）不许丢进黑洞 ——
  它单独成一行：`Kind='front'`、`Title=''`、`Ord=1`（其余章顺延）、`Locator.synthetic=true`，
  区间 = `[0, 第一个目录项起点)`。PDF 同理：第一个书签之前的页 → `Kind='front'`、`PageStart=1`、
  `PageEnd=第一个书签起始页−1`。为什么：那段文字**可被全文搜索命中**，
  若它不属于任何章，AI 就会遇到"搜得到、却说不清在哪"的空洞（§1.2 第 4 条）。
- **N8 覆盖不变量（可验收形式）** —— **本轮以"文件空间"表达**（r1 修订 A36，因为 `CharStart/CharEnd` 本轮恒 NULL，见 §12.1-A36）：
  1. **EPUB（本轮可验收）**：**每个 spine 片段**里，各章的锚点区间**按文档序首尾相接、无空隙地覆盖该片段的正文**
     （区间以**文件内锚点**为准：`(spine, 起始锚点)` → 下一个锚点；首章从片段起点或**卷首章**起点算起）。
     判据形式：对每个片段，`chapters` 的锚点序列（含 N7 卷首章）能**无缝**切完该片段，
     **没有任何一段正文不属于任何章**；文本级复核用"各章文本按序拼接 ≈ 该片段文本（空白规范化后）"。
  2. **PDF（本轮可验收）**：`PageEnd + 1 == 下一章 PageStart`，且整体覆盖 `[1, pageCount]`（页区间**不依赖字符偏移**）。
  3. **待偏移落地后追加**（不是本轮要求）：相邻章的 `CharEnd == 下一章 CharStart`、整体覆盖 `[0, 正文末尾)`。
  **底线不变**：任何一个"不属于任何章"的字符/页都是**契约违约**（验收 A28）。

**目录深度**：原样保留到 **6 层**，第 7 层及更深压平到 6（`Depth=6`、`Locator.depthFlattened=true`）。
为什么是 6：真实书最深 4 层（卷/章/节/小节），6 是"远超真实需求、但不至于让缩进树失控"的闸门。

**EPUB3 `<nav>` 路径怎么覆盖**【必须】（verifier 实测：本机 **7 本真实 EPUB 全是 EPUB2 + toc.ncx，没有一本 EPUB3 nav**）：

- 【必须】**照常实现** nav 路径（§1.4 的优先级 **ncx → nav**，r1 修订见 §12.1-A10）。**不许**因为"没有真实样本"就不写。
- 【必须】用**合成夹具**覆盖：测试里用 `System.IO.Compression.ZipArchive` **当场造**一个最小 EPUB3
  （`mimetype` + `META-INF/container.xml` + OPF 里 `<item properties="nav">` + 含 `<nav epub:type="toc">` 的 xhtml）。
  **不要**往仓库里塞二进制样书 —— 夹具当场生成意味着"意图写在代码里"，也避免仓库躺着来路不明的版权书。
- 【必须】验收报告里**如实标注**：EPUB3 的结论来自合成夹具，不是真实书。不许写成"已在真实书上验证"。

### 1.4.2 归档与 XML 的安全硬约束（r1 修订，见 §12.1-A9；与审计侧同款要求）【必须】

- **zip-slip**：EPUB **绝不解包到磁盘**（所有条目只在内存里读）；任何"条目名参与拼路径"的地方
  必须先规范化（去 `..`、统一 `/`）并断言结果仍在目标目录内（`Path.GetFullPath` + `StartsWith`）。
  【必须】拒绝含 `\`、以 `/` 开头、或含 `:`（Windows 备用数据流）的条目名。
  既有 `ResolveZipPath`（`sipcore.cs:895`）已处理 `./`/`../`，新代码**复用**它，别另写一套。
- **zip-bomb**：解压必须带上限 —— 单条目解压 ≤ 64 MB、整本解压总量 ≤ 512 MB、条目数 ≤ 20000、
  单条目压缩比 > 200:1 **且**输出 > 16 MB 时拒绝。实现方式：包一层**计数流**（读满上限即抛），
  **不许**先 `CopyTo` 再检查大小。为什么必须：上传口允许 512 MB（`Web.cs:3289`），
  一个 5 MB 的 epub 能膨胀到几十 GB，把服务端直接打爆。
  ⚠️【必须】**注意那个"且"是合取（AND），不是只看压缩比**（r1 修订 A34）：纯文本 XHTML 正常也能压到
  几百比一（重复标记/空白），**单看 ratio 会误杀一部正常的大部头**（例如 20 MB 的正文压到 400:1 = 完全合法）。
  所以判据是 `ratio > 200:1` **AND** `解压后 > 16 MB` **AND**（配合上面三条绝对上限）。
  两个长度都能从**中央目录**读到（`ZipArchiveEntry.Length` / `CompressedLength`），
  所以这个判定可以放在 `Open()` **之前** —— 先挡掉，别先解压再看。
- **XXE / DTD**：解析 OPF/NCX/nav 这类 XML 时【必须】用
  `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }`（或 `XmlDocument { XmlResolver = null }`）。
  **不许**用默认设置的 `XmlDocument`/`XmlReader`。现状参考：既有 OPF/NCX 走正则（天然不解析实体），
  新写的 XML 解析必须显式关掉。
- **坏归档**（真实反例：`父与子全集.epub` —— 找不到 End of Central Directory 记录）：
  - **导入期**：归类为**新错误码 `ARCHIVE_UNREADABLE`**（HTTP 400 / 退出码 1），
    消息"这个文件不是有效的压缩包或已损坏"；**不留半截** —— 不写 `Items` 行，
    并**清掉本次刚建的 `imported/assets/<guid>/` 目录**（既有 `ImportFileCore` 先建目录再抽正文，
    失败会留下空目录；这次一并修掉）。
  - **回填期**（书已入库、文件后来坏掉 / 本来就打不开）：`--toc` **不崩**，
    `success:true` + `chapters:[]` + `chaptersError:{code:'EPUB_UNREADABLE', message}`，
    **不写任何 `Chapters` 行**（让 §1.6 的 R-a 保持为真 → 下次访问自动重试）；
    此时 `--chapter <id>` 正当报 `CHAPTER_NOT_FOUND`（3/404）。
  - 【必须】不许把"建不出目录"这个结果**缓存**下来 —— 那等于把一次失败固化成永久事实。



### 1.5 PDF 书签：**走 PdfPig（纯托管）**，P/Invoke 已降为一句备选（r1 修订，见 §12.1-A1/A13）

已核实的事实（实现方不必再摸索，照抄结论）：

- **`PdfPig 0.1.16` 已在 NuGet 本地缓存**（`~/.nuget/packages/pdfpig/0.1.16`），
  `lib/net9.0` 目标下**零传递依赖**（nuspec 里 net6.0/net8.0/net9.0 组的 `<dependencies>` 是空的），
  Apache-2.0，公开 API 含 `TryGetBookmarks(out Bookmarks)` / `GetBookmarks()` / `GetPage(n)` / `NumberOfPages`，
  书签节点类型含 `BookmarkNode`（`Title` / `Level` / `PageNumber` / `Children`）与 `ContainerBookmarkNode`，
  展平用 `Bookmarks.GetNodes()`（**这三个成员名已在 0.1.16 的 XML 文档里逐一核对过，可照抄**）。
  **这条最关键**：本机 NuGet 源 `api.nuget.org` **不可达**（`obj/project.assets.json` 的 NU1900 日志），
  所以"能加一个新依赖"的唯一判据是"它本身与它的依赖树都在缓存里" —— PdfPig 恰好满足
  （`net9.0` 目标零依赖）；实测不破坏单文件发布，体积 **+4.25 MB**。
- `PDFtoImage 5.4.0` 的托管 API **只有** `Conversion.GetPageCount / GetPageSize / GetPageSizes /
  SavePng / SaveJpeg / SaveWebp / ToImage(s)` —— 没有任何 Bookmark/Outline 成员
  （`lib/net10.0/PDFtoImage.xml` 里 `Bookmark|Outline` 命中 0 次）。所以它继续**只管渲染**。
- 【背景，不再是路径】`PDFtoImage` 传递依赖的原生 `pdfium.dll` 确实导出 `FPDFBookmark_*`
  （`FPDFBookmark_GetFirstChild/GetNextSibling/GetTitle/GetDest`、`FPDFDest_GetDestPageIndex`），
  **但为什么不用它**：P/Invoke 要在**运行时**显式探测并加载原生库，路径随平台/发布形态变化 ——
  **Windows 上测得通不等于 Linux/macOS 上也找得到**，而"找不到"只是一种**静默降级**（页级目录），
  我们又没有三平台的验证手段。这种"在别的平台上悄悄变差"的失败模式比多 4 MB 昂贵得多。

**选型（最终）**：PdfPig 是**唯一主路径**；P/Invoke 只留一句备选说明（本节末尾）。

**实现骨架（照抄，不要另起一套）**：

```csharp
using var doc = PdfDocument.Open(pdfPath);         // 必须 try/catch，见下方异常条
int pageCount = doc.NumberOfPages;                 // 与 Items.PageCount 交叉校验（缺则回填）
if (!doc.TryGetBookmarks(out var bookmarks))       // false = 没有大纲 → 页级降级（正常情况，不是错误）
    return PageLevelChapters(pageCount);
var flat = bookmarks.GetNodes();                   // 展平后的有序列表；**不要**用 Roots.Count
foreach (var node in flat)
{
    // node.Title / node.Level（0 基原始层级）/ node.IsLeaf 都在基类 BookmarkNode 上
    // ⚠️ PageNumber 只在 DocumentBookmarkNode 上（不在基类）：node is DocumentBookmarkNode d ? d.PageNumber : 无目标
    // ⚠️ node is ContainerBookmarkNode → 纯分组节点，跳过（见下表）
}
```

**书签节点必须分四类处理**【必须】（r1 修订 A13，captain 要求）：

| 类别 | 判据 | 处理 |
|---|---|---|
| **纯分组节点** | `node is ContainerBookmarkNode`（**无目标**） | **跳过、不建行**；子节点的 `ParentId` 指向**最近的被保留祖先**（没有则 NULL）；`Depth` 按**被保留祖先链长度**算（即可见深度），原始层级写进 `Locator.rawLevel` |
| **有有效目标** | `node is DocumentBookmarkNode d` 且 `d.PageNumber ∈ [1, pageCount]` | 正常成章：`PageStart = d.PageNumber`（1 基，**别 ±1**）、`Kind='chapter'` |
| **目标无效**（0 / 越界）但子树里有有效目标 | `PageNumber` 为 0 或 > `pageCount` | 取**第一个有有效目标的后代**页，`Locator.inheritedPage=true`；按零长度处理（**永不产生引用**） |
| **整棵子树都没有有效目标** | — | **丢弃**该节点，计入 `tocDropped`（不造指向不存在内容的章） |

- 【稳健性】若某个 `ContainerBookmarkNode` **竟然带有效目标页**（不同 PdfPig 版本可能有差异），
  则**按普通节点处理、不跳过** —— 判据是"有没有一个跳得过去的位置"，而不是类型名。
- 【必须】**零长度章**（起始页与下一章相同）：行保留、`PageEnd = PageStart − 1`、`Locator.zeroLength=true`、
  **不参与引用**；N8 的覆盖不变量只在**非零长度行**上检查（§1.4.1 N8）。
- 【必须】**不许为了凑层级给分组节点编页码**：它没有目标就是没有目标，
  给它安上"第一个子节的页码"等于把"章"和"节"说成同一页 —— 那是 I3 禁止的猜测。
  （跳过它丢掉的只是"这一行标题"，它下面的节**一个都不会少**；层级信息也不丢，留在 `Locator.rawLevel` 里。）
- 【必须】**PDF 文本抽取已启用**（r1 修订 A15/A26；**取代**更早的旧口径 —— **撤销记录见 §12.1-A26**）：`PdfPig` 既取书签，
  也取**逐页文本**并落 `PdfPages`（§1.5.1 边界②/③）。**但产物只许进 `PdfPages`** ——
  **不得**写进 `Items.Content`（会动 `SourceHash`、打乱 R-b）也不得直接塞 `Chapters`；
  提问上下文通过 `PdfPages` 取（§6.5 本章层）。语义索引按**按需**规则（§6.2 R5 / §6.6）。
- 【必须】把"这份 PDF 有没有**文本层**"这个**事实**记下来 —— 判据见 §1.5.1 边界②：
  `PdfPages` 里 `CharCount>0` 的页数 > 0 即 `pdfHasTextLayer=true`（**不许**再抽一次样本猜）。
  界面据此如实说"这份按页有正文"或"这是扫描件，读不到文字"（§10.2 的键）。
  ⚠️【必须】**不许用文件体积判断**：实测 `1.pdf`(0.5 MB)、`images.pdf`、`1123.pdf`(0.17 MB)
  体积都很小，其中 `1123.pdf` **3 页有完整正文**，而 `掃描全能王 2026-08-25/09-02` 逐页抽出 **0 字**。
  按体积判断会同时误杀和放过（§12.1-A12）。
- 【必须】**措辞纪律（三种说法，r1 修订 A14 → A15 → A26）**：**永远不许**写「**PDF 没有文本层**」这种对格式的整体断言
  —— 它是错的：实测 `SystemVerilog for Design(2nd)` 436 页共 **722,289 字**、`中国哲简史` 468 页共 **187,830 字**，
  都能逐页抽出；只有扫描件/影印页才真没有。允许的说法只有这三种，**粒度必须说准**：
  1. **「sip 已抽取该 PDF 的逐页文本」** —— **逐份**实测的结论（**不是**全局承诺）；
  2. **「这一份的某几页抽不出文字」**（如影印页、扫描件）—— **逐页**实测的结论（§3.4 的每页 `textAvailable`）；
  3. **不提抽取能力，只提位置** —— 当某页确实没文字时（"这一页我只能给你页码"）。
  写错的代价很具体：把**逐份/逐页**的实测事实写成**全局**断言，读者就会以为"PDF 一律不能读"
  或"PDF 一律能读"——两者都与事实不符。
  > **对齐源**：`docs/草稿-AI阅读悬浮球.html` 已按此改过（PDF 场景的引导语、演示回答、页图说明三处；
  > 扫描件场景的"读不到"**保持不变**，因为那句是真的）。契约与它的措辞以此为准，不许再引用草稿的旧文案。
- 【必须】**PdfPig 的异常必须被包住**。实测把 `.jpg` 改名喂给 `PdfDocument.Open` 会抛
  `PdfDocumentFormatException: Could not find any xref tables or streams…`；加密文档还会抛
  `PdfDocumentEncryptedException`。所以：`catch (PdfDocumentFormatException | PdfDocumentEncryptedException | IOException | InvalidOperationException)`
  + 一个兜底 `catch`，映射到 `PDF_UNREADABLE`（回填期）/ 保持既有导入行为（导入期）。
  **绝不允许**一个改名或损坏的文件让 `sip --import` 或 `--toc` 崩掉（验收 A27）。
- 【必须】**PDF 支持页级关键词定位**（r1 修订 A17/A24；**取代** A12 时代的做法 —— **撤销记录见 §12.1-A24**）：
  逐页文本已落 `PdfPages`（§1.5.1），所以 `--locate` 能反查到**页**，且**永远免费、不需要索引、不调用模型**（§3.5）。
  语义检索（按意思搜）另需 `--index`（§6.6）。
  精度只到**页**：不承诺"第几段、第几行"（`offsetInPage` 是附加信息）。
  【建议】"只落页文本给 `--locate` 用、不给 AI 读"的中间路线**不推荐**：
  它让库体积翻倍、行为自相矛盾（搜得到却读不到），收益只有一半。

**契约要求**：

- 【必须】PDF 章节抽取在**有书签**时给出 `Source='bookmark'` 的目录；
  在 **PdfPig 不可用（依赖缺失/加载失败）/ 文档打不开或加密 / 没有书签**
  三种情况下**优雅降级**为页级目录
  （`Source='page'`），**不得抛异常、不得让 `--toc` 失败、不得缓存"坏"结果**。
  三种降级原因要能区分：**解析库缺失 → `chaptersError.code='PDF_LIB_UNAVAILABLE'`**、
  **文档打不开/加密 → `'PDF_UNREADABLE'`**，而
  **"没有书签"是正常情况**（`chaptersError=null` + `chaptersSource='page'` + `chapters` 按页列出），
  不要把它报成错误 —— 无书签 PDF 是**常见**且**预期**的输入（草稿里就有一个这样的场景桶）。
- 【必须】PDF 解析只在**章节抽取**时做一次，且**不联网**（纯本地解析）；
  与 `RenderPdfPages`（`sipcore.cs:5074`）互不替代：渲染继续走 pdfium，抽取走 PdfPig。
- 【备选说明 · 一句】若日后**去掉 PdfPig 依赖**（例如为发布体积），可用原生 pdfium 的
  `FPDFBookmark_*` 替代 —— 但那时【必须】补上三平台（Windows/Linux/macOS）的探测与失败可见性
  （`NativeLibrary.TryLoad` + 明确日志，**不许静默降级**），并在同一轮改写本节的选型理由。
  **本轮不实现这条。**

### 1.5.1 PDF 逐页文本（**用户拍板的范围变更**，r1 修订见 §12.1-A15）【必须】

**决策**：用 PdfPig 抽 PDF **逐页文本**并落库，使 PDF 支持「按页读」与「关键词反查页码」。
**但带四条硬边界**（写进契约是为了防止范围滚雪球，评审请按这四条检查）：

**边界 ①：PDF 文本**可以**进块表 —— 但只能"按需付费"（r1 修订见 §12.1-A17，本条**替换** A15 的"绝不进嵌入"）。**

- 【必须】**PDF 的语义索引只能由 `sip --index` / `--reindex` 显式触发**：用户主动按、能看进度（`ProgBegin`）、能取消。
- 【必须】**导入永不产生 embedding 调用**；**懒回填（章节/页文本）也永不产生 embedding 调用**。
  `ImportFileCore` → `ReadImportedFile` → `PdfPages` 这条路上一次模型都不能调（验收 A33）。
- 【必须】块必须**带位置回指**（否则"检索命中"说不出"第几页"）：`VectorsChunks` 增
  `ChapterId` / `ChapterSpan` / `AnchorState` 三列，PDF 的锚点是 **`pdf:p<page>`**（页级），见 §6.2 R8 / §6.3。
- **为什么不是"禁止"**：禁止的结果是 PDF 永远进不了语义检索 —— 而那恰恰是用户要的能力。
  要防的是**无界成本**（导入一本 436 页的书就自动烧钱），手段是**按需**，不是**禁止**。
- **成本量级（必须让用户有数，§6.6）**：≈ **一次调用 / 一页（或一块）**；468 页的书 ≈ 几百次调用。
  贵的是**调用次数与时间**，不是 token —— 72 万字摊到 436 页，单页文本很小。
- 【必须】**两条路要分清**（否则 AI 会用错）：
  - `--locate`（**关键词反查**）= 子串扫描 `PdfPages`，**永远免费、离线、即时、不需要索引**；
  - `--search` 的**语义**路 = 需要 `--index`；没索引时**必须给出"这本书还没建索引"的提示**，
    **不许安静地返回空**（§4.4 的 `degraded` + §8.1 的界面提示）。
- 【必须】**扫描件没有文本 → 没有块 → 天然不进索引**，与 `textAvailable:false` 一致（§1.5.1 边界③）。

**边界 ②：存储用独立页表，一页一行；不进 `Items.Content`。**

```sql
CREATE TABLE IF NOT EXISTS PdfPages (              -- PDF 逐页文本（派生数据，随书删除级联）
    ItemId      INTEGER NOT NULL,                  -- Items.Id
    Page        INTEGER NOT NULL,                  -- 1 基，与 BookmarkNode.PageNumber / --page / chapterId 的 p<N> **同一编号**
    Text        TEXT    NOT NULL DEFAULT '',       -- 该页抽出的纯文本；空串 = 这一页没有文字（扫描件每页都是空串）
    CharCount   INTEGER NOT NULL DEFAULT 0,        -- Text 的字符数（扫描件页恒为 0）
    ExtractedAt TEXT    NOT NULL,
    SourceHash  TEXT    NOT NULL DEFAULT '',       -- 抽取时的 PDF **文件**指纹（大小+mtime），见下
    RulesVersion INTEGER NOT NULL DEFAULT 1,       -- 与 Chapters.RulesVersion 同一常量
    PRIMARY KEY (ItemId, Page)
);
CREATE INDEX IF NOT EXISTS idx_pdfpages_item ON PdfPages (ItemId, Page);
```

- 【必须】**不进 `Items.Content`**。理由：`Content` 是"正文"语义（`--show` / 导出 / 全文缓存 / `SourceHash` 全挂在它上面），
  把逐页文本塞进去会**改 `SourceHash` → 触发 §1.6 的 R-b → 全书回填**，而且 `--show <pdf>` 会吐出一整本纯文本。
- 【必须】`SourceHash` 对 PDF 用**文件指纹（`<size>-<mtimeTicks>`）而不是 `Items.Content` 的哈希** ——
  PDF 的 `Content` 是那句**永远不变的占位句**（`sipcore.cs:717`），拿它做指纹等于永远判定"没变"。
  指纹放 `PdfPages.SourceHash`（每行重复一份，代价可忽略）或只在 `DbMeta` 里存一条；
  **不要**每次请求都去哈希一个 100 MB 的 PDF（那是给"翻一页"加上几百毫秒）。
- 【必须】**`pdfHasTextLayer` 由这里推出**，且**逐页粒度**（r1 修订 A26）：`SELECT COUNT(*) FROM PdfPages WHERE ItemId=@i AND CharCount>0`
  > 0 即整本 `pdfHasTextLayer=true`（**不许**再抽一次样本猜）。【必须】同时给出整本的两个计数
  （`textPages` / `emptyPages`，或等价的可观测字段）—— 因为**"部分页抽不出文字"是真实存在的形态**
  （影印页与文本页混排；草稿的演示回答里就是"1–24 页抽不出文字"），只给一个整本布尔值会让 UI/AI
  把"这份只有前 24 页是影印图"说成"这份读不了"。
- 【必须】**每页各自的可用性**要能被单独问到（§3.4 的 `--page` 返回该页自己的 `textAvailable`）：
  **整本 `true` 不代表每一页都有字**，反之亦然。这是"逐页实测"能落地的前提。

**边界 ③：抽取时机 = 与章节抽取同一趟、懒回填、幂等。**

- 【必须】**同一次 `PdfDocument.Open`、同一个循环**里同时产出书签章与逐页文本（边界 ④）。
  绝不为了文本再打开一次 PDF。
- 【必须】懒回填（第一次要目录/要页文本时，一次一本，§1.6 同一套触发点）+ `--chapterize` 可强制重跑；
  幂等：`INSERT … ON CONFLICT(ItemId,Page) DO UPDATE`，或"同事务先删后插"。
- 【必须】**扫描件逐页 0 字是正常结果**，不是错误：整本 0 字 → `textAvailable:false` + `success:true`
  （与 §3.4 现有口径一致），行照样写（**"已抽取且确实没文字"必须与"还没抽取"可区分**）。
- 【必须】`PdfDocumentFormatException` / `PdfDocumentEncryptedException` 必须被包住（§1.5），
  失败时**不写任何 `PdfPages` 行**（让"未抽取"保持为真，下次自动重试）。
- 【必须】**防御性上限**：单页文本 > 200,000 字符时截断并记 `Locator.textTruncated`；
  整本文本 > 50 MB 时按页截断到预算内并在 `DbMeta` 记 `pdfTextTruncated=true`。
  理由：这是**本地库**，会被整天备份/拷贝；一个畸形 PDF 不该把库撑成几百 MB。

**边界 ④：页编号与章节必须是同一套判据、同一处代码。**

- 【必须】`PdfPages.Page`、`Chapters.PageStart/PageEnd`、`BookmarkNode.PageNumber`、`--page <n>`、
  `chapterId` 的 `p<N>`、`RenderPdfPages` 的 0 基索引 —— 换算只发生在 §1.3.1 写死的那**一处**。
  不许"抽文本时从 0 数、建章节时从 1 数"（那是差一错，而且症状是"AI 说的页永远差一页"）。
- 【必须】一次抽取产出的 `PdfPages` 行数与 `Chapters` 的页码覆盖必须自洽：
  `PdfPages` 覆盖 `[1, pageCount]`，`Chapters` 的**非零长度**行覆盖同一个区间（§1.4.1 N8）。
  两者对不上就是实现违约（验收 A32/A35）。

**删除**：`ImportItemDelete`（`Web.cs:3672`）【必须】新增 `DELETE FROM PdfPages WHERE ItemId = @id`（§1.7 同款连坐）。

### 1.5.2 新依赖的准入规则（r1 修订 A18）【必须】

**背景**：本机 NuGet 源 `api.nuget.org` **不可达**（`obj/project.assets.json` 里有 NU1900 日志）。
所以"能不能加一个包"**不是**看它有多好，而是看**它本身与它整棵依赖树是否都已经在本地缓存里**。

【必须】任何新依赖（本轮是 `PdfPig 0.1.16`）在写进 `sip.csproj` 之前，必须同时满足：

1. 包在 `~/.nuget/packages/` 里，且有**适配 net10.0 的目标框架目录**（PdfPig 走 `lib/net9.0`）；
2. **传递依赖为空，或全部已在缓存里**（PdfPig 的 net6.0/net8.0/net9.0 依赖组是空的 → **零传递依赖**）；
3. `dotnet build` **可离线成功**（只允许那条预期的 NU1900 警告）；
4. 许可与发布形态可接受（PdfPig：Apache-2.0；实测不破坏单文件发布，体积 **+4.25 MB**）。

- 【必须】不满足就**不加**：宁可让本轮某个能力降级（例如 PDF 只给页级目录），
  也不引入一个"在别人机器上装不起来"的依赖 —— 本地优先产品的最坏结局就是"在我这跑得好好的"。
- 【建议】把这条写进 CHANGELOG/README 的开发者段落，作为**以后所有新依赖**的通行判据（本轮它是 PdfPig 的准入依据）。

### 1.5.3 大 PDF 首次抽取的预算与"两段式"返回（r1 修订 A28，captain 提出）【必须】

**问题**：懒回填挂在"第一次要目录 / 要页文本"上。对一本 **436 页**的 PDF，用户首次打开阅读页要
**同步等**书签解析 + 逐页文本抽取（实测样本 72 万字）。契约原先**没给这条定预算** ——
不写死，实现者很可能做成同步阻塞，变成"打开阅读页卡半分钟"。

**量级（先知道数，才知道要不要分两段）**：
- 书签解析 ≈ **毫秒级**（只读大纲树，不解内容流）；
- 逐页文本抽取 ≈ **每页几十毫秒** → 436 页 ≈ **数秒到 ~20 秒**（PdfPig 要逐页解析字体与内容流）。
- 结论：**书签那部分几乎免费，文本那部分才是"卡半分钟"的来源。**

【必须】**两段式**：
1. **第一段（便宜、同步、必须立刻返回）**：书签章 + 页结构（`Chapters` 的 `bookmark`/`page` 行）
   + 已有的 `textPages`/`emptyPages` 计数，**外加状态字段 `pdfTextState`**（见下）。
   【必须】第一段**≤ 2 秒**（本机 P99）—— 超过就**先返回页级目录**（`chaptersSource='page'`）并标 `extracting`，
   不许为了凑书签把首屏拖住。
2. **第二段（重、不阻塞）**：逐页文本在**同一进程内继续**抽完（一次一本、不与其它书并发），
   抽完把 `pdfTextState` 置 `ready`。

【必须】**触发阈值**：**≥ 300 页 或 ≥ 30 万字** → 走两段式；
小于阈值 → 可以一次抽完再返回（**小书不必两阶段**，免得把小书也做成两个来回）。

【必须】**状态字段 `pdfTextState`**（挂在 `--toc` / `GET /api/imports/{id}/toc` / `GET /api/imports/{id}` 上）：

| 值 | 含义 | 此时其它接口怎么表现 |
|---|---|---|
| `ready` | 逐页文本可用 | `--page` 给文本；`--locate` 可反查到页 |
| `extracting` | 正在抽 | `--page` 对**还没抽到**的页返回 `textAvailable:false` + `note`；`--ask` **用已有部分**并在 `degraded` 里加 `"pdf:extracting"`（**不阻塞、不回填**，§1.6） |
| `absent` | 抽完了，整本 0 字（扫描件） | **正常结果**（§1.5.1 边界③）：`textAvailable:false` + `success:true` |

- 【必须】**两段式不违反 §11-8**：它**不是**后台作业队列/调度器 —— 只是"**一次已经开始的抽取在同进程里跑完**"
  （§11-8 已按此加注）。
- 【必须】**同一本书同一时刻只允许一次抽取**；重入直接返回当前状态（幂等）。
- 【必须】**有界（r1 修订 A37，审计员从"资源耗尽/DoS"视角提的必查项）**：
  1. **总时长上界**：单本抽取**≤ 10 分钟**；超时则**停在部分结果**、`pdfTextState` 保持 `extracting`
     （下次访问接着抽），**不许**把它标成 `ready`、也不许把已抽的页丢掉。
  2. **内存上界**：抽取**逐页处理、逐页落库**，**不许**把整本 `page.Text` 攒在内存里到最后一次写
     （436 页 ≈ 1.4 MB 看着不大，但"逐页落库"这条规则防的是有人图省事改成"全攒完再写"）。
  3. **磁盘上界**：整本正文文本 ≤ **50 MB**（超出按页截断并在 `DbMeta` 记 `pdfTextTruncated=true`）—— §1.5.1 边界③。
  4. **并发上界**：全局同时只跑**一本**（不是"每本一个"）；第二本要抽时排队或按需触发，**不叠加**。
  > 为什么把"有界"写成契约：这条链路是**用户在界面上点一下就能触发**的重活 —— 没有上界，
  > 一个畸形 PDF 就能把进程的内存/线程/磁盘占住，而"读一本书"不该有这种副作用。
- 【必须】抽取**不联网、不需要 AI Key/索引/模型**；失败就停在部分结果、`pdfTextState` 保持 `extracting`
  让下次重试 —— **不许**把"没抽完"写成"抽完了"，也不许写坏行。

### 1.6 回填 / 重建的判定（"旧书自动回填章节"怎么落地）【必须】

一本书的章节数据"需要（重新）生成"当且仅当满足以下任一条：

```
R-a  Chapters 里没有该 ItemId 的行                       → 新书
R-b  max(SourceHash) != SHA256(Items.Content)[..16]     → 正文变过（重新导入/升级重抽）
R-c  max(RulesVersion) < 当前 ChapterRulesVersion 常量   → 抽取规则升级了
```

- 【必须】**懒回填**：第一次有人对该书要目录（CLI `--toc`、Web 打开阅读页、`GET .../toc`）时自动回填，
  **一次只处理一本书**。禁止"打开网页就全库回填"（一本 30 万字的书就是一次压缩包解包+正则，全库就是卡死）。
- 【必须】**幂等**：连跑两次 `--toc`，`Chapters` 行数、`ChapterId` 集合、`Ord` 完全一致（附录A・A3）。
- 【必须】显式重建命令 `sip --chapterize [<itemId>|all] [--json]`（写操作，挡位 2 起拦，§3.6）。
- 【必须】**回填触发点只有两个是强制的**：① 懒回填（上面那条，覆盖所有旧书）；② `sip --chapterize`。
  `--reindex` **可以**顺带跑一次全库回填（它本来就是重运维命令，挂在这里合理），但
  **不许**把它做成唯一的入口 —— 否则"没配 AI/没 Key 的用户永远建不出目录"，
  直接违反 I2 与 §8.3。
- 【必须】**区分两种"写"**（r1 修订 A28，captain 补；不写这条实现者会二选一且**两种选法各错一边**）：
  `/ask` 过 `WebWriteAllowed` 是因为它会往 **`chat.db`** 写对话历史（§4.2.1），
  **不是**因为它会回填章节/页文本。`/ask` 路径**绝不**触发 `Chapters` / `PdfPages` 的抽取或写入 ——
  缺数据时按 §2.2 给 `degraded:["pdf:not-extracted"]` 降级，**不要顺手补**。
  判闸发生在**写响应头之前**；**取上下文、回填、发上游都必须在判闸之后**。
  （反例说明为什么必须写死：若把"要写"读成"可以顺手回填"，A7「提问前后 `rss.db` 哈希不变」当场失效，
  而且第一次提问要等一本 436 页 PDF 抽完。）
- 【必须】**`/api/imports/{id}/ask`（提问路径）不是回填触发点**。
  它只**读** `Chapters` / `PdfPages`；缺数据时按 `degraded:["pdf:not-extracted"]`（或 `chapter:not-built`）
  降级、并在回答里如实说明，**不许**顺手回填。
  为什么：`ask` 一旦回填就会写 `rss.db`，I1（harness 对库只读）与 A7（提问前后哈希不变）
  会从**硬保证**退化成"通常成立"。回填的入口就三个：`--toc` / 打开阅读页 / `--chapterize` ——
  而正常流程里用户必然先打开阅读页（前端 `render()` 会拉 `/toc`），所以实践里数据是齐的。
- 【必须】回填过程**不许**联网、**不许**要求 AI Key/索引/模型（纯本地：解 zip / 解析 XML / 解析 PDF 书签）。
- 【建议】导入成功后**顺手**回填该书（`try/catch` 吞掉失败，失败**不影响**导入结果与返回码）：
  那时文件刚落地，是"文件还在"最有保证的时刻。但懒回填仍然**必须**实现（它是旧书的唯一路径）。
  **不要求**导入时同步回填：导入已经很重（复制文件 + 抽正文），再加一步会让上传路径更脆。
- 【必须】回填**不改** `Items` 的任何一个字段（I1 的锚点侧对应物）：只 INSERT/DELETE `Chapters` 行。
- 【建议】回填失败（文件被删、EPUB 损坏）时**不写任何行**并在响应里给 `chaptersError`，
  下次访问会自然重试 —— 不许写"空目录"占位（那会让 R-a 永久为假，等于把失败固化下来）。

### 1.7 生命周期：删书要连坐

`ImportItemDelete`（`Web.cs:3672`，CLI `--import-rm` 与 `DELETE /api/imports/{id}` 共用）【必须】
新增：`DELETE FROM Chapters WHERE ItemId = @id` **与** `DELETE FROM PdfPages WHERE ItemId = @id`（r1 修订 A15）。

**对话历史【必须】不随书删除**，理由：删的是"库里的文件"，不是"你跟 AI 说过的话"；
但删除响应里要带上 `chat: { sessions: N, kept: true }`，并在 UI 上提示一句
"这本书的 AI 对话记录还在（可在对话面板里删）" —— 用户不会喜欢"删个书把我聊天记录也删了"这个惊喜。

---

## 2. 目录的最小 JSON 契约

### 2.1 CLI 人类可读输出

```
$ sip --toc 42
中国哲学简史 · EPUB · 目录来自书内 NCX（12 章）
  1  第一章 中国哲学的精神
  2  第二章 中国哲学的背景
  ...
  12 第十二章 中国哲学在现代世界
（读完某章：sip --chapter 42 #1 ；PDF 按页：sip --page 42 87）
```

无书签 PDF：

```
物理错题精选精析 · PDF · 书签 0 条 → 只能按页码走（共 240 页，不猜章节名）
  1  第 1 页
  2  第 2 页
  ...
```

**为什么人类输出要显式写"目录来自哪里"**：用户看到"第三章"时会假定这是书里的第 3 章。
如果目录其实是 `--heading` 猜的或按页编的，他有权利知道。这句话就是 §1.4 的 I3 在界面上的落点。

### 2.2 `--json` 结构【必须】（CLI 与 Web **共用同一份**数据对象）

```json
{
  "success": true,
  "data": {
    "itemId": 42,
    "title": "中国哲学简史",
    "type": "epub",
    "kind": "epub",
    "chaptersSource": "ncx",
    "chapterCount": 12,
    "textAvailable": true,
    "pageCount": null,
    "indexed": true,
    "backfilled": true,
    "chaptersError": null,
    "tocDropped": 0,
    "chapters": [
      {
        "chapterId": "epub:3",
        "ord": 1,
        "depth": 0,
        "parentId": null,
        "title": "第一章 中国哲学的精神",
        "kind": "chapter",
        "source": "ncx",
        "charCount": 3210,
        "pageStart": null,
        "pageEnd": null,
        "zeroLength": false,
        "depthFlattened": false,
        "rawLevel": null,
        "firstBlock": "哲学在中国文化中所占的地位，历来可以与宗教在其他文化中的地位相比。"
      }
    ]
  }
}
```

字段契约（**全部 camelCase**，与现有 Web API 一致，如 `bodyHtml`/`isPdf`）：

| 字段 | 类型 | 含义 / 为什么有 |
|---|---|---|
| `kind` | `"epub" \| "pdf" \| "text"` | 书的种类。`text` = mobi/docx/txt/md。前端据此选渲染器，**不要**靠扩展名自己猜 |
| `chaptersSource` | `"ncx"\|"nav"\|"spine"\|"bookmark"\|"page"\|"heading"\|null` | **界面必须显示**（§2.1）。`null` = 还没建/建不出来，此时看 `chaptersError` |
| `chapterCount` | int | 目录长度（`chapters` 数组长度，冗余但省得前端数） |
| `textAvailable` | bool | **整本**有没有可读正文（PDF：`= pdfHasTextLayer`，即**至少一页**有字；扫描件为 `false`；§1.5.1）。⚠️ **整本 `true` ≠ 每页都有字**（影印页混排很常见）—— 单页可用性看 `--page` 的返回（§3.4） |
| `textSource` | `"content" \| "pdf-text" \| null` | 正文从哪来：`content` = `Items.Content`（EPUB/文本格式）；**`pdf-text` = `PdfPages` 逐页文本（r1 修订 A15 起启用）**；`null` = 没有可读正文（扫描件） |
| `pageCount` | int? | PDF 总页数（无书签 PDF 的目录长度就是它） |
| `indexed` | bool | 当前 embedding 模型下有没有这本书的块向量（决定"本书层"能不能做**语义**检索，§6/§9）。PDF 跑过 `--index` 后为 `true`、没索引为 `false`（**这不影响关键词定位**：`--locate` 永远免费可用，§1.5.1 边界①） |
| `backfilled` | bool | 本次调用是否**现场**触发了回填（前端可据此 toast"已建立章节"；`false` 一般因为原本就有） |
| `chaptersError` | `null \| {code,message}` | 回填失败的**原因**（如 `EPUB_UNREADABLE`）。成功必须为 `null`。不许用空目录表示失败 |
| `chapters[].kind` | `"chapter" \| "page" \| "front"` | 章的种类：正常章 / 页（无书签 PDF）/ **卷首合成章**（§1.2 第 4 条、§1.4.1 N7）。前端据此决定显示"第 N 章 / 第 N 页 / 卷首" |
| `chapters[].firstBlock` | string | 该章第一个块级元素的纯文本前 60 字（**超长书按 §9-5 裁到 30 字**，前端匹配只看前 30 字，故不影响切分），**给前端切分用**（§2.4），也可给 AI 做"这段文字在哪章"的线索。⚠️ **本轮恒为空串**（偏离已记账：**§12.1-A36**，captain 批准）—— 消费方**请用单章接口** `GET /api/imports/{id}/chapters/{chapterId}` 逐章取，**不要**依赖 §2.4 的"整书一次拉 + 单调匹配"路径（本轮不可用） |
| `chapters[].charCount` | int | 用于上下文预算排序与 UI 显示"这一章多长" |
| `chapters[].zeroLength` | bool | **零长度章**（起点 == 终点，§1.4.1 N4）。**必须**逐行给出，别只放在 `Locator` 里 —— 验收要按"丢弃数 / 零长度数 / 压平数"逐项对账（r1 修订 A20） |
| `chapters[].depthFlattened` | bool | 该行是否因**超深被压平到 6 层**（§1.4.1）。同上，必须可见 |
| `chapters[].rawLevel` | int? | **仅 PDF**：书签的原始 `Level`（因为 `depth` 是"被保留祖先链长度"，跳过分组节点后会与原始层级不同，§1.5）。EPUB 为 `null` |
| `tocDropped` | int | **被丢弃的目录项数**（§1.4.1 N2(c)：`src` 指向的文件不在 spine 里）。**必须暴露**，否则"丢对了"与"建错了"在输出上长得一样（r1 修订 A20） |

**不得放进 toc 的**：正文本身。目录是目录，正文是正文（一章一次请求，§3.3）。

### 2.3 Web 侧：前端**不许自己猜**

```
GET /api/imports/{id}/toc            →  与 §2.2 的 data 对象**逐字段相同**
GET /api/imports/{id}/chapters/{chapterId}
                                     →  { chapterId, ord, title, kind, source,
                                          html, text, charCount, pageStart, pageEnd, textAvailable }
GET /api/imports/{id}/page/{n}       →  既有接口，不改（栅格 PNG）
GET /api/imports/{id}                →  既有接口【必须新增】三个字段：
                                        chapters(数量或 null)、chaptersSource、kind
GET /api/imports                    →  列表【必须新增】`chapters`（数量或 null，null=尚未回填）
                                        —— 让书库列表能显示"12 章 / 240 页"，且不产生 N+1 次请求
```

**为什么 `/api/imports/{id}` 和列表也要带**：现在书库卡片只显示"EPUB · 2.1 MB"，
用户看不出"这本书 AI 认不认得路"。带上 `chapters` 之后，卡片能写"12 章 · 已建目录"，
一眼就能发现哪本书还没建好 —— 而这正是"AI 能精准指路"这个目标**能被用户验证**的地方。

**前端硬要求**：

- 【必须】章节相关的一切（目录列表、跳章、当前章高亮、引用 chip 跳转）**只用** `/toc` 与
  `/chapters/{chapterId}` 的数据。**禁止**再用 `h1~h3` 启发式去"推算"目录。
- 【必须】`web/app.js:1009 splitBookSections` 只允许作为 **`chaptersSource == null` 时的兜底**
  （老书回填失败、或目录为空）。当目录存在时，节导航必须走章节模型 ——
  否则"AI 说的第 3 章"和"你看到的第 3 章"会是两个不同的东西，整个目标的落点就没了。
- 【必须】引用 chip 的跳转目标用 `chapterId`（`data-chapter="epub:7~sec2"`），
  **不是**节序号（序号会随兜底切分规则改变）。

### 2.4 前端怎么把整书正文切成章（净化漂移问题）【必须】

**问题**：`Chapters.CharStart/CharEnd` 是相对 `Items.Content` **原文**的偏移，
而前端拿到的 `bodyHtml` 是 `ToSafeBodyHtml` **净化后**的 HTML（标签、属性都被改过）
→ 偏移量**必然对不上**。拿它去 `substring` 会切在正文中间。

**契约（两条路径）**：

> ⚠️ **本轮的有效路径只有 ②**（r1 修订 A36，captain 批准的偏离）：`firstBlock` **本轮恒为空串**，
> 所以 ① 的"整书一次拉 + 单调匹配"**本轮不可用**。**本轮主路径 = ② 单章接口**：
> 前端**一章一次请求**（请求数变多，功能不缺）。① 在 `firstBlock` 落地后即可启用，届时它才是"省往返"的优化路径。
> **消费方（t4）请按 ② 实现，不要为 ① 留半截代码**（也**不要**因此去猜章节边界）。

1. **（待 `firstBlock` 落地）整书一次拉 + 单调匹配**：拉 `GET /api/imports/{id}/text`（避免一章一个往返），
   然后**按目录顺序 + `firstBlock` 做单调匹配**切分：两个指针（目录指针、DOM 块指针）都只前进，
   在净化后的块级元素文本里找 `firstBlock`（比较前 30 字，折叠空白）。
   - 匹配到 → 该块是这一章的开始；
   - 匹配不到 → **停在该点**，把余下内容全部归给当前章，并在 UI 上标一句
     `章节边界降级（与内容对不齐）`（`chaptersAligned:false`）。
   为什么这样设计：它**不会静默切错**（单调整匹配失败就退化到"全给一章"），
   而偏移量方案会静默切错。
2. **单章路径（跳转 / AI 读章 / 本轮渲染）**：`GET /api/imports/{id}/chapters/{chapterId}` 直接返回该章净化 HTML。
   chip 跳转、`--chapter` 读取、AI 的"本章层"材料、以及**本轮的整书渲染**都走这条，
   保证"你看到的"和"AI 读到的"是**同一份文本**。

【必须】`charStart/charEnd` 不出现在 `/toc` 的公开字段里给前端索引用 —— 它是**服务端内部**的切片依据
（且**本轮恒为 NULL**，见 §12.1-A36）。
实现方若把 `charStart` 暴露给前端并让前端 `substring`，视为违反契约（这是最容易踩的坑，见文末"最容易错的三点"）。

---

## 3. 按章 / 按页读取、关键词定位

### 3.1 命令总表【必须】

| 命令 | 读/写 | 语义 | 挡位 |
|---|---|---|---|
| `sip --toc <itemId> [--json]` | 读 | 目录（必要时懒回填章节） | 只读白名单 |
| `sip --chapter <itemId> <chapterId\|#ord> [--json] [--max-chars N]` | 读 | 读一章正文 | 只读白名单 |
| `sip --page <itemId> <pageNo> [--json] [--render]` | 读（`--render` 会写 temp PNG） | 读一页的定位信息；`--render` 附栅格 PNG 路径 | 只读白名单 |
| `sip --locate <keyword> [--book <itemId>] [--limit N] [--json]` | 读 | 关键词 → 位置（章/页） | 只读白名单 |
| `sip --chapterize [<itemId>\|all] [--json]` | **写** | 显式回填/重建章节 | 挡位 2 起拦 |

`--book 0`/缺省 = 不限书（全库搜索定位）。`<chapterId|#ord>` 两种写法都要支持：
`--chapter 42 epub:7`（脚本/AI 用 id）与 `--chapter 42 #7`（人手敲，`#` 后是 `Ord`）。
`--max-chars` 默认 12000，上限 200000。

【必须】四个只读命令加入 `simon.cs:121 SimonIsReadOnly(...)` 的白名单
（否则挡位 2 的"只读命令可用"承诺就是假的）；`--chapterize` **不加**（它是写）。

### 3.2 `--toc` 的入参出参

- 入参：`<itemId>`（Items.Id，**真实 id**，不是 `--import-rm` 那种"本地导入源内的显示编号"）。
  出参见 §2.2。
- 【必须】顺带在人类输出里给"怎么读"的提示（§2.1 最后一行）—— 这是"AI 能查目录"的**人类**入口，
  脚本/Agent 用 `--json`。

### 3.3 `--chapter` 的入参出参

```json
{
  "success": true,
  "data": {
    "itemId": 42, "chapterId": "epub:3", "ord": 1,
    "title": "第一章 中国哲学的精神", "kind": "chapter", "source": "ncx",
    "textAvailable": true,
    "text": "哲学在中国文化中所占的地位……",        // 纯文本（StripHtml 后），给 AI/终端
    "html": "<p>哲学在中国文化中所占的地位……</p>", // 净化后的 HTML，给 Web
    "truncated": false,
    "charCount": 3210, "pageStart": null, "pageEnd": null,
    "nextChapterId": "epub:4", "prevChapterId": "epub:2"
  }
}
```

- 【必须】`text` 与 `html` **同源**（同一份切片）：`text = StripHtml(html)`，
  不允许一个从 Content 原文抽、另一个从净化结果抽（那会让"AI 读到的"和"你看到的"差一截）。
- 【必须】`--max-chars` 生效时 `truncated:true`，且**从章首截**（AI 要能看到开头）。
- 【必须】`nextChapterId/prevChapterId` 直接给出来：AI 与前端都会用（草稿的"翻到下一节"），
  让它自己算 `ord±1` 就是让它猜。
- 【必须】**PDF 的章要额外给 `pages`**（r1 修订 A15）：`pages: [{ page, chars, text }]`，
  即该章覆盖页的逐页文本（`PdfPages`），`page` 与 `--page` 同一编号。
  为什么：AI 要能说"第 87 页写着……"而不是"这一章大概在 87-128 页之间" —— **页码级引用靠它才兑现**。
  顶层的 `text` 仍给（= `pages[].text` 按页序以 `\n\n` 拼接），方便只用 `text` 的调用方。

### 3.4 `--page` 的入参出参（PDF）

```json
{
  "success": true,
  "data": {
    "itemId": 42, "page": 87, "pageCount": 240,
    "chapterId": "pdf:p87", "chapterTitle": "第四章 孔子：第一位教师", "ord": 4,
    "textAvailable": true,
    "text": "……这一页的正文（来自 PdfPages，纯文本）……",
    "textSource": "pdf-text",
    "pageImage": null,           // 不带 --render 时为 null
    "note": null
  }
}
```

- 【必须】`text` 来自 `PdfPages`（§1.5.1），**Page 编号与 `--page` 的入参同一个数**（1 基）；不重新解析 PDF。
- 【必须】**这一页的 `textAvailable` 是逐页判定的**（r1 修订 A26）：该页 `CharCount == 0` → `textAvailable:false` +
  `text:null` + `note`（"这一页抽不出文字（影印/扫描）"），**即使整本 `textAvailable:true` 也可能如此**。
  草稿的演示回答就是这个形态：「这份 PDF 的 1–24 页抽不出文字（那几页是影印图），那部分我只能给你页码」——
  **契约要求 API 能让 AI 说出这句话**，而不是让它只拿到一个整本布尔值。
- 【必须】**扫描件**：`textAvailable:false` + `text:null` + `note` 明说"这一份没有文本层（扫描件）"，
  但 **`success:true`** —— "这份没文字"是**正常结果**，不是错误。
  反过来，**没抽取成功**要能区分（`textAvailable:false` + `degraded:["pdf:not-extracted"]`，提示可重跑 `--chapterize`）：
  把这两件事混成一句"读不到"会让 AI 与用户都失去下一步。
- 带 `--render` 时 `pageImage` = 栅格 PNG 的**绝对路径**（复用 `RenderPdfPages(pdf, "87")`，
  150 DPI、带注释、命中磁盘缓存），`note` 为空。
- 页越界 → `PAGE_NOT_FOUND`（§3.6），退出码 3。

### 3.5 `--locate` 的入参出参（关键词反查位置）【必须】

```json
{
  "success": true,
  "data": {
    "query": "有教无类",
    "scope": "book",                    // "book" | "library"
    "itemId": 42,
    "hits": [
      { "itemId": 42, "chapterId": "epub:7", "ord": 4, "title": "第四章 孔子：第一位教师",
        "pageStart": null, "snippet": "……孔子说：「有教无类。」……",
        "offsetInChapter": 812, "reason": "keyword" }
    ],
    "count": 1,
    "truncated": false
  }
}
```

- 实现：**章级切分后**在章内做子串检索（大小写不敏感、折叠空白），命中所在的章就是定位结果。
  **不额外建索引**：`ItemsFts` 是 `fts5(trigram)` 且只索引 Title/Content/Description/Summary，
  **不带任何位置信息**，够不到"第几章"；改它的列/分词等于动既有索引语义（违反 I6），
  而"章内子串扫描"在单本书的量级（几十万字）是毫秒级 —— 没有必要为它引入第二套索引。
- 【必须】**精度目标 = 章节级；PDF 的页码是它的自然粒度**（r1 修订见 §12.1-A4/A12/A15）：
  契约承诺"这个词在**哪一章**"，PDF 额外承诺"**在哪一页**"（因为逐页文本就在 `PdfPages` 里）。
  **不**承诺"第几段、第几行、第几个字"（`offsetInChapter` / `offsetInPage` 是**附加**信息，不是承诺）。
- 【必须】`snippet` 是命中点上下各 ~40 字，`offsetInChapter` 是章内字符偏移（0 基）。
- 【必须】一个关键词在一章里命中多次只返回**首个**命中（`--limit` 限制的是章数，不是命中次数）——
  否则读一本书里出现 300 次的词，返回 300 条同样的章。
- 【必须】**PDF 支持关键词定位到"页"**（r1 修订 A15）：在 `PdfPages`（§1.5.1）上做**子串扫描**，
  命中即返回 `{ itemId, chapterId, ord, title, page, snippet, offsetInPage, reason:"keyword" }`，
  `page` 与 `--page` 同一编号（1 基）。**存储顺序：先按 `Page`、页内按字符偏移，取首个命中**。
- 【必须】**这条路不需要索引、也不调用模型**（r1 修订 A17）：关键词反查是子串扫描，
  **永远免费可用**；而"按意思搜"（`--search` 的语义路）需要 `--index`。
  两条路的区别要在回答与界面上说清（§4.4 / §8.1），别让 AI 把"没建索引"当成"这本书里没有"。
- 【必须】**扫描件 / 未抽取**才返回空：`hits:[]` + `note` 说明是"没有文本层"还是"还没抽取"，
  `success:true`。**绝不允许**对 PDF 猜页码或返回模糊结果（§11）。
- 【必须】**两条路分清**（r1 修订 A17；写成这条是为了让 AI 别用错）：
  **`--locate` = 关键词反查，永远免费可用**（子串扫描 `Chapters`/`PdfPages`，不调用任何模型、不需要索引）；
  **`--search` 的语义路 = 需要 `--index`**。PDF 没索引时 `--locate` 照常可用，
  而语义检索要给"还没建索引"的提示（§4.4），**不许安静返回空**。
- 【必须】无命中 = `success:true` + `hits:[]`（不是错误）：
  "库里没有这个词"是一个**有效答案**，AI 需要能区分它和"查不了"。

### 3.6 退出码与错误码【必须】

退出码沿用既有分类（`sipcore.cs:8090 ExitCodeFor`）：
`0`=成功 · `1`=通用错误（参数/用法/格式）· `2`=网络/服务不可达 · `3`=资源未就绪或找不到。

| 错误码 | 退出码 | HTTP | 场景 | 契约要点 |
|---|---|---|---|---|
| `ITEM_NOT_FOUND` | 3 | 404 | itemId 不存在 | 既有 |
| `NOT_IMPORTED` | **3**（现为 1，本轮改） | 404 | 目标不是本地导入项（是 RSS 文章） | 改的理由：它不是"参数写错"，而是"你要的书不在书架上"，重试无用、应换目标 —— 归 3 与 `ITEM_NOT_FOUND` 同类。CI 里没有断言这个码的用例，改动安全 |
| `CHAPTER_NOT_FOUND` | 3 | 404 | chapterId/ord 不在目录里 | 新增；`message` 里回 `chapterCount` 与相近的 `chapterId`【建议】 |
| `NO_CHAPTERS` | 3 | 404 | 该书没有任何章节模型（内容为空 / 抽取失败） | 新增；与 `chaptersError` 二选一，**不许**同时成功返回空目录 |
| `PAGE_NOT_FOUND` | 3 | 404 | 页越界 | 既有（`Web.cs:3608`） |
| `NO_PAGE_INDEX` | 3 | 409 | PDF 页数未知（`PageCount` 为 null 且 pdfium 读不出） | 新增；人话提示"重跑一次导入可重建页数" |
| `UNSUPPORTED_FORMAT` | 1 | 400 | 格式白名单外 | 既有 |
| `EMPTY_QUERY` | 3 | 400 | `--locate` / 提问内容为空 | 既有码，复用 |
| `AI_NOT_CONFIGURED` | 3 | 409 | 没有 `ai_config.json` 或没有 LLM Key | 既有（`Web.cs:3939` 先例）。响应必须带 `hint`：在真实终端跑 `sip --init` |
| `API_KEY_MISSING` / `API_KEY_INVALID` | 3 | 409 / 502 | 模型拒绝/Key 错 | 既有 |
| `NO_INDEX` | 3 | 409 | 没有任何块向量（本书层语义检索不可用） | 既有（`sipcore.cs:8999`）。**注意**：它只降级"语义"这一路，关键词路仍要工作（§6/§9） |
| `MODEL_UNAVAILABLE` / `NETWORK_ERROR` | 2 | 502 | 模型/网络不通 | 既有 |
| `ALREADY_RUNNING` | — | 409 | 同一本书同一时刻已有一轮在跑 | 既有锁模式（`TryBeginDownload`）复用（§5.7） |
| `BODY_TOO_LARGE` | — | **413** | **请求体超过上限**（如超长提问/会话体） | A29 新增（审计员提）：payload【必须】带 **`limit`（字节数）** —— 否则"有上限"这条边界**无法被断言**（只能断言"413 出现了"，不能断言"上限是多少"）。与导入文件上限的 `TOO_LARGE`（512 MB，`Web.cs:3289`）**分开**：一个是**请求体**、一个是**文件**，别混用一个码 |
| `TOO_LARGE` | — | 413 | 导入文件超过 512 MB | 既有（`Web.cs:3289`）；与 `BODY_TOO_LARGE` 的区别见上一行 |
| `SIMON_BLOCKED` | — | **403** | **挡位 ≥2 拦提问**（写库即过闸，§5.4 / §12.1-A19） | 响应【必须】带 `hint`：说明"提问会把对话记进 `chat.db`，挡位 2 起视为写操作被拦；要问请把挡位降回 1，或到 TUI"。前端在挡位 ≥2 时**预先禁用**输入框，而不是让人按下发送再吃 403（§8.3） |
| `CHAT_PERSIST_FAILED` | — | —（SSE `error` 事件 / `persisted:false`） | 对话历史写 `chat.db` 真失败（磁盘满/只读/打不开） | 新增；见 §5.3.1。**与 `SIMON_BLOCKED` 分开**：一个是策略、一个是故障，用户看到的下一步完全不同 |
| `SESSION_BOOK_MISMATCH` | — | 409 | 请求的 itemId 与会话所属书不一致 | 新增（§7.2） |
| `STREAM_TIMEOUT` | — | —（SSE 内 `error` 事件） | 整轮超过 300s | 新增（§5.5） |
| `CLIENT_GONE` | — | —（不出现在响应里） | 客户端断开 | 只落盘 `Status='aborted'`（§5.6） |

【必须】错误码是**协议**：恒定英文大写、恒定英文含义，**不进语言文件**；
只有 `message`（人话）与 `hint`（下一步怎么做）走 i18n（§10）。

### 3.7 与既有命令的关系

- `--show <id>` / `--show <id> --json` / `--pages` / `--vision` 的行为**一个字节都不改**（I6）。
  PDF 想按章看仍可 `--show <id> --pages 87-128`。
- 【建议】`--show <id> --json` 的 `data` 里**加法**给两个字段：`kind` 与 `chapterCount`
  （AI 用 `--show` 读一本书时能立刻知道"这本书有没有目录"）。标【建议】是因为它有风险：
  任何"多余的字段"都可能让下游脚本的严格 schema 校验炸掉 —— 实现前先确认没有这类下游。

---

## 4. 聊天会话与消息

### 4.1 为什么独立 `chat.db`（再强调一次）

见 §1.1。补充一条工程理由：`chat.db` 是**可写**的，`rss.db` 在 harness 路径上**只读**（I1）。
两个文件让"谁写了什么"一目了然，也让审计能在一次提问前后对 `rss.db` 做哈希比对（A7）。

### 4.2 表结构【必须】

```sql
CREATE TABLE IF NOT EXISTS ChatSessions (
    Id             TEXT    PRIMARY KEY,        -- "chat:<guid:N>"，不含时间戳（时间戳会造成"同一秒两次新建同名"）
    ItemId         INTEGER NOT NULL,           -- **严格限于一本书**（§7.2）
    ItemTitle      TEXT    NOT NULL DEFAULT '',-- 建会话时的书名快照：书删了会话还能读，且不会显示成空白
    CreatedAt      TEXT    NOT NULL,           -- ISO-8601（与既有代码一致，DateTime.Now.ToString("O")）
    UpdatedAt      TEXT    NOT NULL,           -- 每次追加消息更新；会话列表按它倒序
    TurnCount      INTEGER NOT NULL DEFAULT 0, -- 已完成的问答轮数
    Title          TEXT    NOT NULL DEFAULT '',-- 取首个提问的前 30 字（会话列表显示用）
    Summary        TEXT    NOT NULL DEFAULT '',-- 滚动压缩摘要（§7.4）
    SummaryUpToTurn INTEGER NOT NULL DEFAULT 0,-- 摘要已覆盖到第几轮（含）
    SummaryAt      TEXT    NULL
);
CREATE INDEX IF NOT EXISTS idx_chatsessions_item ON ChatSessions (ItemId, UpdatedAt DESC);

CREATE TABLE IF NOT EXISTS ChatMessages (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId  TEXT    NOT NULL,
    TurnIndex  INTEGER NOT NULL,       -- 1 基，一问一答同号
    Role       TEXT    NOT NULL,       -- 'user' | 'assistant'
    Content    TEXT    NOT NULL,       -- 正文（markdown 原文）
    AnchorJson TEXT    NULL,           -- 本轮引用的阅读位置（§4.3），assistant 行也可带（回答的落点）
    CitesJson  TEXT    NULL,           -- 回答里的引用数组（§4.4）
    SnapshotJson TEXT  NULL,           -- 检索快照（§4.4）
    Provider   TEXT    NULL,           -- 用了哪个供应商/模型（事后对账"这条回答是谁写的"）
    Model      TEXT    NULL,
    Status     TEXT    NOT NULL,       -- 'ok' | 'error' | 'aborted'
    ErrorCode  TEXT    NULL,
    CreatedAt  TEXT    NOT NULL,
    UNIQUE (SessionId, TurnIndex, Role)
);
CREATE INDEX IF NOT EXISTS idx_chatmessages_session ON ChatMessages (SessionId, TurnIndex);
```

**设计说明（为什么是这几列）**：

- `TurnIndex` + `UNIQUE(SessionId,TurnIndex,Role)`：一轮一问一答**同号**，天然给前端一个稳定的
  渲染顺序，也让"压缩到第 N 轮"有明确坐标（§7.4）。
- `Status`：断连/报错的回答要能落库并显示成"这轮中断了"，而不是消失或装成完整回答（§5.6）。
- `Provider/Model`：本地优先产品迟早会有人问"这条是谁答的"（换过模型之后尤其明显）。
- `AnchorJson/CitesJson/SnapshotJson` 存成 JSON 文本而不是拆列：它们是**快照**，
  字段会随版本演进，拆列会逼出迁移；快照只读不查询。

### 4.2.1 建表落点、关联键、删除语义、责任归属（r1 修订，见 §12.1-A8）【必须】

**这条是给 t3/t4 划界的，别各自发明。**

**(a) 建表落点：`chat.db`，且【禁止】写进 `InitDatabase`（rss.db）**

- 两张表建在 **`readwithhotsoup/chat.db`** 这个**独立文件**里。
  【必须】由 `sipcore.cs` 里的**独立函数** `InitChatDatabase(chatDbPath)` 创建
  （`CREATE TABLE IF NOT EXISTS`，幂等），**不是**加进 `InitDatabase(dbPath)` 的主 SQL。
- 【必须】**绝不**在 `rss.db` 里出现 `ChatSessions`/`ChatMessages`：
  一旦出现，harness 的每一次提问都会写 rss.db，I1（harness 对库只读）当场失效，
  而"提问前后 rss.db 哈希不变"（附录A・A7）也会变成一条永远红的用例。
- 原因（给 t3 的单写者约定留出路）：`sipcore.cs` 由 t3 单写没问题 ——
  **DDL 文本归 t3、建的文件必须是另一个**。t4 只需要（在启动路径上）调
  `InitChatDatabase(ChatDbPath())`，不需要自己写 SQL。
- 【必须】**调用点 = 启动路径，与 `InitDatabase` 相邻，照 `TelemetryService.Init(dataDir)` 的先例**
  （r1 修订 A16，captain 裁决）：**不在 Web 请求路径里建表**。
  为什么重要：这样"harness 只读 rss.db"仍然是**连接级**保证 ——
  提问路径既不建表也不写主库，`rss.db` 的哈希比对（A7）与"`rss.db` 里没有 `Chat%` 表"（A19）两条都成立；
  反过来，如果把建表塞进请求路径，第一次提问就会去碰主库/建文件，把一条干净的边界搅成时序问题。
- `chat.db` 的打开沿用既有 `OpenDb(path)` 约定（同样的 PRAGMA），**不为它单独调 journal/foreign_keys**；
- 【必须】**两套建表语句必须分开**（r1 修订 A19 / 裁决 C1 ④）：`chat.db` 的 `CREATE TABLE` 属于
  `InitChatDatabase(chatDbPath)`，**不进** `InitDatabase(dbPath)` 的 SQL；
  `InitChatDatabase` 只是**调用点**与 `InitDatabase` 相邻（启动路径，A16），**语句不混**。
  验收上这就是 A19 那两条（`rss.db` 的 `sqlite_master` 里没有 `Chat%` 表；`chat.db` 两张表齐）。
  未来给 `chat.db` 补列也用 rss.db 那套 `try/catch ALTER TABLE` 写法。

**(b) 「按书关联」挂什么键：`ChatSessions.ItemId` = `Items.Id`（INTEGER）**

- 与 `/api/imports/{id}`、`--toc <itemId>` **同一把钥匙**（同一个数）。
- 【必须】**不**用 `Items.Guid`（`import:<guid>`）：导入项每次导入都新生成 Guid，
  挂着它等于"重新导入一次就丢历史"。导入项本来也没有跨副本的稳定身份。
- 【必须】**不**加跨库外键（`chat.db` → `rss.db` 在 SQLite 里做不到），
  一致性靠应用层：每次写入前校验 `Items` 里该 id 还在**本地导入源**下；
  书被删后历史仍可读（靠 `ItemTitle` 快照 + §9-25 的"只读历史"行为）。
- 【必须】不设 `UNIQUE(ItemId)`：一本书**可以**有多个会话（草稿有「新会话」按钮）。

**(c) 角色 / 顺序 / 时间**

- `Role` 取值域 `'user' | 'assistant'`；`TurnIndex` **1 基**，一问一答**同号**。
- 【必须】读取顺序固定为 `ORDER BY TurnIndex ASC, CASE Role WHEN 'user' THEN 0 ELSE 1 END` ——
  不要靠 `Id` 自增排序（重试/中断会让"插入顺序"与"对话顺序"不再等价）。
- `CreatedAt` 一律 `DateTime.Now.ToString("O")`（与既有代码一致）；会话的 `UpdatedAt` 每次追加消息更新。
- 【必须】**重试 = 新一轮**（`TurnIndex+1`），不是覆盖或新增分支：
  历史是**只追加**的线性序列（§7.5 不做对话树）；失败的上一轮作为"中断的一轮"留着。
- 【必须】一行 `TurnIndex` 一旦开始落盘，就必须**写齐 user + assistant 两行**
  （assistant 即使一个 delta 都没收到也要写，`Status='aborted'`、`Content=''`），
  否则 `TurnCount` 与界面的"第几轮"会错位。
- 【必须】流**开始之前**的错误（`AI_NOT_CONFIGURED`/`SESSION_BOOK_MISMATCH`/`ALREADY_RUNNING`/`EMPTY_QUERY`）
  **不写任何行**（那一轮不算开始）。

**(d) 删除：硬删，不设 `DeletedAt`**

- 【必须】物理删除。两张表都**不要** `DeletedAt`/`IsDeleted` 之类的软删列 ——
  本地文件数据库里软删只会在几个月后变成"删了怎么还占地方"，而防误删的正确位置是
  `?yes=1` + 前端 confirm（§4.5），不是留一列垃圾。
- 【必须】删会话用**两条显式 DELETE**（先 `ChatMessages WHERE SessionId=@sid`，再 `ChatSessions WHERE Id=@sid`），
  **不依赖**外键 `ON DELETE CASCADE`（`db` 打开参数一变，级联就可能不生效，那是最难查的一类 bug）。
- 删书（`ImportItemDelete`）**不**级联删对话（§1.7）。

**(e) t3 与 t4/t5 的验收归属（避免两头都以为对方在验）**

| 项 | 归属 | 内容 |
|---|---|---|
| DDL 落地 | **t3** | ① 表建在 `chat.db`；② 幂等（连跑两次不报错）；③ `rss.db` 的 `sqlite_master` 里 **没有** `Chat%` 表；④ 不引入对 t4 代码的编译期依赖 |
| 行为验收 | **t4 实现 / t5 验证** | 提问落库、`turnIndex` 连续、断连写 `aborted`、`DELETE ?yes=1` 生效、无 `yes` 时 400、划词锚点只活一轮（§7.1） |

### 4.3 引用锚点 `AnchorJson`【必须】


```json
{
  "locType": "selection",          // "selection" | "chapter" | "book" | "library"
  "itemId": 42,
  "chapterId": "epub:7",
  "chapterTitle": "第四章 孔子：第一位教师",
  "ord": 4,
  "page": null,
  "selection": {
    "text": "教育的门第一次向普通人打开……",
    "chars": 38,
    "truncated": false,             // 原文超过 1500 字被截时 true
    "inChapter": true               // 划词是否落在 chapterId 指的那一章（false 时是"跨章划词"，见 §9）
  }
}
```

- `locType` 四值对应草稿的四种提问姿态：划了一段问、问当前章、问这本书、（开启后）问全库。
- 【必须】锚点**只写在 `ChatMessages.AnchorJson` 里**，不写进 `ChatSessions`：
  会话**没有**"当前划词"这种状态列（§7.1 的实现手段）。
- 【必须】`selection.text` 服务端最多保留 1500 字符（前端已截 400 字，服务端兜底防脚本灌大文本）。

### 4.4 检索快照 `SnapshotJson` 与引用 `CitesJson`【必须】

`SnapshotJson`（**这是"AI 凭什么这么说"的唯一凭证**，草稿里那行 `ⓘ 检索：…` 就是它）：

```json
{
  "locType": "selection",
  "layers": ["selection", "chapter", "book"],
  "budgetTokens": 16000, "usedTokens": 4210,
  "chapterIdUsed": "epub:7",
  "bookHits": [ { "chapterId": "epub:7", "title": "第四章 孔子：第一位教师",
                  "score": 0.81, "reason": "vector", "snippet": "……" } ],
  "libraryHits": [ { "itemId": 88, "title": "论语译注", "score": 0.76, "snippet": "……" } ],
  "libraryEnabled": false,
  "vectorModel": "nomic-embed-text",
  "chunksConsidered": 88,
  "degraded": ["library:disabled", "library:no-index"],
  "elapsedMs": 412
}
```

- 【必须】`degraded` 是**字符串数组**，每个元素 `<层>:<原因>`，原因取值
  `disabled`（用户没开）/ `no-index`（没建索引）/ `no-text`（这一份没有文本层，扫描件）/ `no-hit`（没命中）
  / `error`（检索过程出错，但本轮仍继续）。
- 【必须】**PDF 未索引时不许静默返回空**（r1 修订 A17）：语义路不可用时，
  `degraded` 必须带 `no-index`（PDF 再加 `pdf:keyword-only`），并且**回答里要有一句可操作的提示**
  —— "这本书还没建索引，跑 `sip --index` 后就能按意思搜；现在按关键词也能查"。
  为什么单说 PDF：EPUB 的语义检索早就有了，PDF 是**新接上**的一条路，
  用户不会知道"为什么同一样的书，一本能按意思搜、另一本不能"（§8.1 的界面提示与此对应）。
  **为什么必须有**：AI 回答时必须知道"我这次没看到全库"，否则它会把"这本书没提"说成"这件事不存在"。
  前端也靠它显示诚实的降级说明。
- 【必须】`CitesJson` 每条：`{ chapterId, ord, title, page, label, kind }`。
  `label` 是**服务端按当前界面语言**生成的人话（`第 4 章 · 孔子：第一位教师（p.87）`），
  同时必须给结构化字段 —— 前端**优先**用结构化字段自己拼 label（切语言不用重新请求）。
- 【必须】快照里的 `bookHits[].chapterId` 必须是**真实存在的章**（I4）：
  检索命中一个块后要把它的 `ChapterId` 回查 `Chapters`，查不到就不许出现在引用里。

### 4.5 会话生命周期【必须】

```
GET    /api/imports/{id}/chat                  → { sessions:[{id,title,turnCount,updatedAt,summary?}],
                                                    activeSessionId, messages:[...] }（默认给最近一个会话的消息）
POST   /api/imports/{id}/chat                  → 新建会话（body: {title?}）→ { sessionId }
GET    /api/imports/{id}/chat/{sid}            → 该会话全部消息（含 anchor/cites/snapshot）
DELETE /api/imports/{id}/chat/{sid}?yes=1      → 删一个会话（写操作，挡位 2 起拦 + 前端 confirm）
DELETE /api/imports/{id}/chat?yes=1            → 删这本书**全部**对话历史（同上）
```

- 【必须】`yes=1` 缺失时返回 400 `CONFIRM_REQUIRED`（照 `--import-rm` 的 `--yes` 风格）；
  理由：本地优先产品里"删掉我的聊天记录"应该有一次明确的确认，而不是一个手滑的 DELETE。
- 【必须】**路由名是契约的一部分，不许自拟别名**（r1 修订 A19 / 裁决 C1）：
  `POST /api/imports/{id}/ask`、`GET|POST /api/imports/{id}/chat`、
  `GET|DELETE /api/imports/{id}/chat/{sid}?yes=1`、`DELETE /api/imports/{id}/chat?yes=1`。
  任何"更顺手的另一套名字"（例如 `/api/chat/{id}/ask`）一律作废 —— 两套路由并存会让鉴权/写保护/测试
  三处各测一半。
- 【必须】会话列表**按书**：`GET /api/imports/{id}/chat` 只返回这本书的会话。
  跨书列表本轮不做（§11）。
- 【必须】删除是**物理删除**（不是软删标记）：本地文件数据库里"标记删除"只会在几个月后
  变成"为什么删了还在占地方"的困惑。

---

## 5. SSE 流式响应契约

### 5.1 路由与内容协商【必须】

```
POST /api/imports/{id}/ask
     Accept: text/event-stream            → SSE 流（默认）
     Accept: application/json 或 ?stream=0 → 非流式，返回下面那个 JSON
Body: { "question": "...", "sessionId": "chat:…" | null,
        "anchor": { "locType":"selection", "chapterId":"epub:7", "selection":"…" } | null,
        "library": false }
```

- 【必须】两种模式**共用同一套实现**：检索、上下文组装、LLM 调用**只有一份**；
  非流式只是"把事件折叠成一个对象"。验收方式：同一个问题在同一个假模型下，
  两种模式的 `answer` 文本**逐字相同**（附录A・A5）。
- 非流式响应：
  ```json
  { "success": true, "data": {
      "sessionId": "chat:…", "turnIndex": 3, "messageId": 1002,
      "answer": "…", "cites": [ … ], "snapshot": { … },
      "persisted": true, "elapsedMs": 3120 } }
  ```
- 【必须】前端**不用** `EventSource`：它是 GET-only，而提问要 POST body。
  用 `fetch()` + `res.body.getReader()` 手解析 SSE 帧（同时能读到流**开始前**的
  401/409 JSON 错误体 —— `EventSource` 读不到，那正是"AI 没配置"这种错误最常见的时刻）。
- 【必须】`Accept` 不带、且没有 `?stream=` 时**默认流式**（草稿的交互就是流式）。

### 5.2 事件序列【必须】（严格按此顺序）

```
event: session    ← 立刻发，第一帧
event: anchor
event: snapshot
event: delta      ← 0..n 次，每次一段增量文本
event: cites      ← 生成结束后一次
event: done       ← 最后
（任意时刻可被 event: error 替换其后的全部事件，然后连接关闭）
```

帧格式（**必须**）：

```
event: delta
data: {"text":"哲学在中国"}

```

（字段名在 `data:` 之后是 JSON；**每行 data 单行**，帧尾一个空行 ——
LF 结尾是 SSE 规范允许的，不必 CRLF。）

**为什么 `session` 必须是第一帧**：前端要在回答开始前就知道 `sessionId`/`turnIndex`（标题栏、
"第几轮"、以及**断线重连后往哪写**）。等 `done` 才给，会让"回答到一半刷新页面"变成一次丢失。

**为什么 `snapshot` 在 `delta` 之前**：它证明"先检索、后生成"这个顺序（而不是先生成再补个检索说明）；
同时前端能在等模型的几百毫秒里先把"检索：本书命中 3 段"显示出来，观感上"它在找"而不是"它在卡"。

### 5.3 每类事件的字段【必须】

| 事件 | data 字段 | 说明 |
|---|---|---|
| `session` | `sessionId, itemId, turnIndex, messageId, persisted, itemTitle` | `persisted` 的语义见 §5.3.1：**只有两行都写成功才为 `true`**；挡位拦下 / 写失败为 `false`，且**必须带可区分的原因** |
| `anchor` | `locType, chapterId, ord, title, page, selectionChars, inChapter` | 回显服务端**实际采用**的锚点（与前端请求的可能不同：跨章划词会被修正） |
| `snapshot` | 同 §4.4 的 `SnapshotJson` | 必须**完整**，不是摘要 |
| `delta` | `text` | 增量文本。**必须**不切断 UTF-16 代理对（按 `Rune` 边界切）；不保证按"字"切，一次可能多字 |
| `cites` | `cites: [{chapterId,ord,title,page,label,kind}]` | 与落库的 `CitesJson` **完全相同** |
| `done` | `turnIndex, messageId, fullLength, elapsedMs, truncated, persisted` | `truncated` = 因长度/超时被截断；`persisted` 同 §5.3.1 |
| `error` | `code, message, hint` | 见 §5.5；发出后**立即**结束响应 |
| `ping` | （无 data，直接 `: ping`） | 注释帧，每 15s 无输出时发一次，防浏览器/中间层掐连接 |

### 5.3.1 `persisted` 的语义（r1 修订 A19 / 裁决 C1；**A27 收窄为单一原因**）【必须】

`persisted` **只回答一件事：这一轮的一问一答，是否已经完整落进 `chat.db`**。

- 【必须】**只有 `user` + `assistant` 两行都写成功**才允许 `persisted:true`。
  **任何"没写成功"或"不确定"的情形一律 `false`** —— 绝不许在没落库时回 `true`。
- 【必须】`persisted:false` **只有一种原因：写库失败**（磁盘满 / `chat.db` 只读 / 打不开）：

  | 原因 | 判据 | 必须带的信息 |
  |---|---|---|
  | **写库失败** | 写入抛错 | `ErrorCode='CHAT_PERSIST_FAILED'`；`degraded` 加 `"chat:persist-failed"`；若已写入部分行（user 行成功、assistant 行失败）→ **UPDATE 该轮为 `Status='error'` + `ErrorCode`**（连 UPDATE 也失败就放弃，只留 `degraded`） |

- 【必须】**不存在"策略跳过落盘"分支**（A27，captain 定稿）：挡位 ≥2 在**写响应头之前**就被 403 拦下，
  根本进不了流式阶段 —— 所以没有"流里发现被策略拦下 → `persisted:false`"这种情形。
  **不许为不可达情形留代码或注释占位**；`level` 由 **403 响应体**承载，**不由 `persisted` 承载**（§5.4）。
- 【必须】**`persisted:true` 是正常路径，不是例外**（A29 补，防误读）：成功落库时**必须**回 `true`，
  **不许**把字段实现成"永远 false"。精确的说法是「**失败/被拦时**绝不回 `true`」——
  §12.1-A19 里原写"任何情况都不许回 `true`"，那是**措辞不准**（已按 A29 更正），
  只读变更记录的人容易把它当成"这个字段永远为假"。
- 【必须】**因此不存在"静默不落库"的可能**（A29 补，回应审计员的"删掉会留洞"之问）：
  要么**入口 403**（不进流、无 `persisted` 字段），要么**正常落库**（`persisted:true`）。
  夹在中间的第三种情况（流中途被拦而悄悄不写）**不是"未覆盖"，而是被设计排除**：
  闸门只在入口判一次，流中途升档不中止本轮（§5.4）。**没有任何一条路径会"答了但不写、且不告诉用户"。**
- 【必须】**入口闸门（挡位 ≥2）不是 `persisted:false` 的情形**：那是 **403 `SIMON_BLOCKED`、不进流、
  响应里没有 `persisted` 字段**（§5.4）。分清这两层很重要：
  **"我一开始就不让你问"** 与 **"我问了但没记下来"** 是两件不同的事 ——
  前者给 403 + 可执行提示，后者给"这轮没存上"的显式告知。
- 【必须】前端见到 `persisted:false` **要显示出来**（如"这轮对话没有保存"），不许静默。
  理由与本契约反复强调的同一条：把它藏起来，用户会以为记录还在 —— 而这是本地产品**唯一**的记录。

### 5.4 HTTP / 连接层要求（HttpListener 专属）【必须】

- `res.SendChunked = true`，**不得**设 `ContentLength64`（设了就不是流了）。
- `res.ContentType = "text/event-stream; charset=utf-8"`。
- 每写一帧：`res.OutputStream.Write(...)` + **`res.OutputStream.Flush()`**。
  这是"逐字流出"的**物理条件**：不 Flush，`HttpListener` 会缓冲到连接结束 —— 那就退化成非流式了。
- 全局已有的 `Cache-Control: no-store`（`Web.cs:187`）对 SSE 同样合适，**不要**改成 `no-cache`。
- 不得启用任何压缩（本机没有反向代理，但别加 `Content-Encoding`）。
- 【必须】`/api/*` 的会话鉴权照旧（`Web.cs:341`）：**未认证时必须回 401 JSON，不能开始流**。
  前端在流开始前检查 `res.ok`/`status`，把 401 交给现有 `api()` 的登录跳转逻辑。
- 【必须】**SSE 与 `WebWriteAllowed` 的关系：写库即过闸**（r1 修订 A19 / 裁决 C1，A22 补严，A27 定稿）。
  **提问会往 `chat.db` 写持久记录，属于"创建库条目"而非"覆写游标"，因此与「抓全文」「生成摘要」同类，必须过写闸门。**
- 【必须】**四个写端点逐一过闸**（别只挑一个），完整枚举是：
  ① `POST /api/imports/{id}/ask`（会写对话历史）
  ② `POST /api/imports/{id}/chat`（新建会话）
  ③ `DELETE /api/imports/{id}/chat/{sid}`（删一个会话）
  ④ `DELETE /api/imports/{id}/chat`（删本书全部历史）
  挡位 ≥2 → **403 `SIMON_BLOCKED`**。
- 【必须】**纯读端点不过闸，挡位 ≥2 仍须 200**：会话历史 `GET /api/imports/{id}/chat`、
  目录 `/toc`、页级取文本 `/page/{n}`、`/chapters/{id}`、`--toc/--chapter/--page/--locate` 都属此类。
  一句话：**挡位不是"整个界面变灰"的理由**（§8.3）。
- 【必须】**闸门必须在"写响应头之前"判定**（A22 补严，captain 点名）：即先判 `WebWriteAllowed`，
  被拦时回 **`application/json` 的 403**（`SIMON_BLOCKED`），**不许**先 `SendChunked`/开 `text/event-stream`
  再判、也不许置 200 再在流里报错（**不留半截流**）—— 否则前端拿到的是"HTTP 200 + 一个错误事件"，
  会把"被策略拦下"显示成"回答中断"，用户看到的下一步完全不同。
  同理，鉴权（401）也必须在写响应头之前判（上面那条）。
- 【必须】字段 **`persisted` 只表达"落库结果"**（A27 定稿，与 §5.3.1 一致）：
  `true` = **确实落库**（一问一答两行都写成功）；`false` = **`chat.db` 不可写**（配错误码 + `Status='error'`，
  另见 §9-18 的 `degraded:["chat:persist-failed"]`）。
  **不存在"策略跳过落盘"分支** —— 挡位 ≥2 在写响应头之前就被 403 拦下，根本进不了流式阶段；
  不许为不可达情形留代码或注释占位。
  **挡位信息（`level`）由 403 响应体承载，不由 `persisted` 承载。**
- 【必须】**闸门拦下时必须留下可审计的事件，且 `what` 取值固定**（A29，审计员提 —— 这条让"闸门真的在最前面拦下了"可被断言，而不只依赖 HTTP 状态码）：
  `WebWriteAllowed(res, what)`（`Web.cs:492`）内部会 `SimonRecord("blocked_cmd", $"web:{what}", level)`（`Web.cs:502`），
  而 `GET /api/simon` 会回显**最近 20 条**事件（`Web.cs:4026-4043`）。所以 `what` 必须是**文档化的固定取值**：

  | `what` | 对应端点 |
  |---|---|
  | `ask` | `POST /api/imports/{id}/ask` |
  | `chat-new` | `POST /api/imports/{id}/chat` |
  | `chat-del-one` | `DELETE /api/imports/{id}/chat/{sid}` |
  | `chat-del-all` | `DELETE /api/imports/{id}/chat` |

  **可断言的形式**：挡位 2 下 `POST /api/imports/{id}/ask` → `GET /api/simon` 的 `events` 里出现
  `type="blocked_cmd"`、`detail` 含 `web:ask`、`level=2`（验收 A41）。这条对**用户实跑**那一层同样直接可用。
- 【必须】**闸门只在入口判一次**（A27 明确，避免留下"流中途升档怎么办"的空当）：
  流进行中若挡位被其他通道升到 2（`simon.cs` 允许任意通道升档，网页也可以 —— `Web.cs:4045 HandleSimonLevel`：
  升档任意通道放行、降档才要真终端 + Web 口令），**不中止**这一轮 ——
  它已经通过了入口闸门，这一轮的落盘照常完成。**规则就这一条，不再在写库前重判**
  （重判会引入一个几乎不可达、却要长期维护的分支，见上面"不许留占位"那条）。
- **判据（三层，与"落盘到哪个文件"无关）**（A22 记一句话，A23 补全三层，captain/审计员定）：
  1. 是否创建/修改**用户信息本体**（`Items` / `Feeds` / 标签）→ **是则过闸**（抓全文、生成摘要属此类）；
  2. 是否创建/修改**持久记录** → **是则也过闸**（**提问每次新增一行用户亲手写的问与答**，属此类）；
  3. 是否只是**就地覆写程序自动维护的游标** → **不过闸**（阅读位置属此类）。
  一句话版本：**"这次操作会不会改动信息库本体或创建持久记录"** —— 会，就过闸。
- **一条必须写下来的区分**（防以后有人拿阅读位置来类比，A23）：
  **阅读位置**是程序自动记的环境状态、一次滚动覆写一次、**丢了无损失**；
  **对话历史**是用户亲手提问留下的记录、**持续累积**、**用户会回来找它**。
  两者落盘强度相似，**性质相反** —— 故一个豁免、一个过闸。
- 为什么（依据是项目自己踩过的坑，`Web.cs:494-500` 注释原文）：
  「同一个操作仅因走的通道不同就裁决相反，实际成了一条绕过 CLI 限制的写通道（审计 §2.3）」——
  本契约早先把提问当成"只读、挡位不拦"的那种判断，正是同一种误判（**撤销记录见 §12.1-A19**）；而 `WebWriteAllowed` 拒绝时打印的是
  **"Reading still works"** —— 提问不是 reading。

### 5.5 错误事件【必须】

- 流开始**之前**的错误 → 正常 HTTP 状态码 + JSON（401/403/404/409/400）：`AI_NOT_CONFIGURED`、
  `ITEM_NOT_FOUND`、`SESSION_BOOK_MISMATCH`、`ALREADY_RUNNING`、`EMPTY_QUERY`，
  以及 **`SIMON_BLOCKED`（403，挡位 ≥2 拦提问；§5.4 / §12.1-A19）**。
- 流开始**之后**的错误 → `event: error` + `data: {"code":…,"message":…,"hint":…}`，然后关闭连接。
  此时 HTTP 状态码已经是 200，**改不了** —— 所以前端**必须**把 `error` 事件当成一等公民渲染，
  而不是只看 HTTP 状态。
- 【必须】已生成的部分要落盘为 `Status='error'`（内容保留）+ `ErrorCode`，
  并在界面上显示"回答中断（`code`）"而不是把半截回答伪装成完整回答。
- 整轮超时 300s → `STREAM_TIMEOUT`。上游 LLM 单次 120s（与既有 `CallLlmAsync` 一致）。

### 5.6 断连与取消【必须】

- 客户端断开 → 下一次 `Write` 抛 `IOException`/`HttpListenerException` →
  **必须**取消 `CancellationTokenSource`，停止继续拉上游模型（否则用户关掉页面，
  后台还在烧 token、还在写库）。
- 该轮落盘为 `Status='aborted'`，保留已生成文本；**不记** 500 日志噪音（正常事件，不是故障）。
- 上游取消要让 `HttpClient` 真的断开（把 token 传进 `SendAsync`），不是只停止读取。

### 5.7 并发【必须】

- 同一本书并发提问 → 第二个 `409 ALREADY_RUNNING`（复用 `TryBeginDownload(key)`/
  `EndDownload(key)` 的锁模式，key = `"ask:" + itemId`）。
  为什么：`turnIndex` 是一个序列，两轮并发写会互相盖号，
  "划词锚点活一轮"的语义也随之崩掉。
- 不同书可以并发提问（锁的粒度是书，不是全局）。
- 同一浏览器多标签：天然由上面的锁保护。

### 5.8 「首个 delta 及时到达」怎么验收【必须】

这是本目标里**最容易自欺**的一条（"我用了 SSE"不等于"字是流出来的"）。三条判据**全都要过**：

1. **帧证据**：在假模型（stub）下，客户端能观察到 **≥2 个独立时间的 `delta` 帧**，
   且它们的到达时间**不同**（差值 > 20ms）。
   - 假模型：`GET /api/index` 之类既有的 stub 机制不够用 → 测试里起一个**本地假 LLM**
     （`HttpListener` 或最小 TCP 服务器，按 `stream:true` 逐块吐 20 个 chunk、每块间隔 50ms，
     端点写进 `ai_config.json` + Key）。这也是 t5 唯一不依赖真实模型就能验流式的办法。
2. **时序证据**：`timeToFirstDelta < 0.5 × totalDuration`（首帧明显早于整体结束）。
   逐字流式的实现自然满足；"先整段生成、再一次性发出"必然 ≈1.0。
3. **端到端证据**：浏览器 DevTools（或 `WebServerHarness` 的原始 `HttpClient` 逐块读）
   能看到 `session → anchor → snapshot → delta…→ cites → done`，且
   **在 `done` 之前就已经渲染出文字**（草稿里那个闪烁光标 `<span class="cur">` 存在的意义就是它）。

【必须】`WebServerHarness`（`tests/Sip.Tests/WebServerHarness.cs`）复用即可：
它已经能起隔离实例、拿 `BaseUrl`、逐块读响应体（`HttpCompletionOption.ResponseHeadersRead` 是新增的一行）。

---

## 6. 三层切块与章节对齐

### 6.1 三层的定义

```
章（Chapters）       ← 新增：定位的单位，"第几章/第几页"
  └ 块（VectorsChunks）  ← 既有表，新增 ChapterId 列：**永不跨章**
      └ 向量（Models 维度的 float[]） ← 既有
```

### 6.2 对齐规则【必须】

| 规则 | 内容 | 为什么 |
|---|---|---|
| **R1** | 切块以**章为边界**：先按章取文本，再在章内按 `ChunkingCfg.SizeTokens`(2000)/`OverlapTokens`(200) 切 | 一个块跨两章时，命中了也说不清是"第几章"——定位能力就没了 |
| **R2** | 章太小（< `SizeTokens/5`，即 400 token）时与**相邻章合并**成一个块，`ChapterId` 记**起始**章，另存 `ChapterSpan`（起止 chapterId）。**合并优先取同一 `ParentId`（同一章）下的相邻节**，并且**绝不跨 spine 文件**（`ChapterId` 的 `epub:<spine>` 段不同就不合并）；无可合并对象时允许单独成块 | §1.4.1 之后章节粒度到了"节"（实测 200 节/33 文件），小章会**大量**出现，不合并则检索质量塌掉；但合并后如果引用说不出"这一段属于哪一节"，定位能力也就废了 —— 所以合并范围必须收敛到"同一个父章 + 同一个文件"内 |
| **R3** | 章太大时章内按段落切，**重叠只在本章内取** | 跨章重叠会让同一段文本出现在两章，引用出现二义 |
| **R4** | `Source='heading'` 的兜底节同样按 R1~R3 处理（它就是"章"的角色） | 规则一致，代码不用分叉 |
| **R5** | **嵌入只能"按需"发生**（r1 修订 A17，替换 A15 的"PDF 永不进嵌入"）：`EmbedItemChunks` / `BackfillChunks` 只在 `--index` / `--reindex` 链路里被调用；**导入路径与懒回填（章节/页文本）绝不调用**（验收 A33）。PDF 的块带上 `pdf:p<page>` 锚点后**可以**进块表 | 要防的是**无界成本**（导入一本 436 页的书就自动烧钱），不是"PDF 能不能被搜到"。按需付费 = 用户按 `--index` 时**看得见进度、能取消、心里有数**（§6.6 有量级）；自动嵌入 = 一次导入把几百次调用花掉而用户毫不知情。④ 老理由仍然成立：PDF 的 `Content` 是那句"终端暂不支持阅读 PDF"的**占位话**，**它本身**永远不许进嵌入（块只来自 `PdfPages`） |
| **R6** | 无章节数据的书（回填失败）→ 按 R5 之外的**整篇**切块（保持既有行为），块的 `ChapterId` 为 NULL、`AnchorState='none'` | 不许因为"没有章节"就退化成"不能检索"；`AnchorState='none'` 的块不产生引用（I4），但可以作为材料 |
| **R7** | **PDF 以"页"为块边界**（A17 新增）：一个块**不跨页**；单页文本太短（< `SizeTokens/5`）时**与相邻页合并**，`ChapterId` 记**起始页**、`ChapterSpan` 记起止页（`"pdf:p87..pdf:p88"`） | 与 EPUB 的"以章为边界"（R1）同理：块的边界必须落在**用户能核对的定位单位**上。PDF 的定位单位就是页 —— 一个块跨页，命中后就说不出"第几页"，那正是这次要修的东西 |
| **R8** | **块必须带位置回指**（A17 新增，替代原先"把 span 塞进 `Snippet` 前缀"的【建议】做法）：`VectorsChunks` 增三列 —— `ChapterId TEXT NULL`（起始锚点：EPUB 是 `epub:7~sec2`，PDF 是 `pdf:p87`）、`ChapterSpan TEXT NULL`（合并块的起止锚点；不合并为 NULL）、`AnchorState TEXT NOT NULL DEFAULT 'unknown'`（`anchored` / `unknown` / `none`，见 §6.3.1）。检索命中后**用 `ChapterId` 回查 `Chapters`**，查不到就不产出引用（I4） | 没有这三列，"检索命中"就只剩 `Snippet` 一段裸文本，AI 依然说不出"第几页" —— 定位能力的最后一公里就断在这里。**一个 `ChapterId` 列同时覆盖 EPUB 章与 PDF 页**，因为 PDF 的书签章与页级章都在 `Chapters` 里（`Kind='chapter'` / `'page'`），不需要两套列 |

【建议】`ChapterSpan` 存进 `VectorsChunks.Snippet` 之外的新列会膨胀迁移；
推荐把它塞进 `Snippet` 的一个前缀注释里（`[epub:7~epub:8] …`）—— 但**契约不强制**实现方式，
只强制"合并块必须能说出它的起始章"。

### 6.3 迁移（`InitDatabase`，`sipcore.cs:4297` 附近）【必须】

照项目既有的"加列 + try/catch SqliteException 忽略"模式（`sipcore.cs:4368-4476`）：

```csharp
// 章节表（派生数据，与 VectorsChunks 同级）
CREATE TABLE IF NOT EXISTS Chapters ( ... );            -- §1.2
CREATE INDEX IF NOT EXISTS idx_chapters_item_ord ...;

// PDF 逐页文本（§1.5.1）
CREATE TABLE IF NOT EXISTS PdfPages ( ... );
CREATE INDEX IF NOT EXISTS idx_pdfpages_item ON PdfPages (ItemId, Page);

// 块 → 章/页 的位置回指（r1 修订 A17：三列，见 R8）
try { ALTER TABLE VectorsChunks ADD COLUMN ChapterId   TEXT; }                 catch (SqliteException) { }
try { ALTER TABLE VectorsChunks ADD COLUMN ChapterSpan TEXT; }                 catch (SqliteException) { }
try { ALTER TABLE VectorsChunks ADD COLUMN AnchorState TEXT NOT NULL DEFAULT 'unknown'; } catch (SqliteException) { }
CREATE INDEX IF NOT EXISTS idx_chunk_chapter ON VectorsChunks (ChapterId);

// DB 级元数据（版本常量：章节规则版本、切块规则版本、锚点规则版本）
CREATE TABLE IF NOT EXISTS DbMeta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
```

### 6.3.1 三列的语义 + **旧库怎么回填**（r1 修订 A17）【必须】

| 列 | 类型 | 语义 |
|---|---|---|
| `ChapterId` | `TEXT NULL` | **起始锚点**，值 = §1.3 的 chapterId 字符串（EPUB `epub:7~sec2`、PDF `pdf:p87`），**不存行号**（行号会随回填重建而变） |
| `ChapterSpan` | `TEXT NULL` | **合并块的起止锚点**：`"epub:7~sec2..epub:8"` / `"pdf:p87..pdf:p88"`；不合并时为 `NULL` |
| `AnchorState` | `TEXT NOT NULL DEFAULT 'unknown'` | `anchored` = 锚点已算出且有效；`unknown` = **本该有锚点但没算出来**（旧行/匹配失败）；`none` = 这本书本来就没有章节模型（§6.2 R6），`NULL` 是正常状态 |

- 【必须】检索命中后**用 `ChapterId` 回查 `Chapters`**；查不到（或 `AnchorState != 'anchored'`）就不产出引用（I4）。
- 【必须】**不要**用"块的文本去 `Chapters` 里做区间反查"去替代这一列：
  `Snippet` 只有前 200 字、且 `ChunkText`（`sipcore.cs:8311`）有 200 token 的**重叠**，
  同一段文字天然会落在两个块里 → 区间反查必然二义，净化前后文本不一致时还会直接失配。

**旧库回填规则（既有行没有锚点，写死为三步）**【必须】：

1. **不花钱的原地回填**：对每一行 `ChapterId IS NULL` 的旧块，用它的 `Snippet` 在该书的
   **章节纯文本序列**上做**规范化后的单调匹配**（折叠空白；按 `Ord` 顺序推进，不做全局搜索）。
   命中 → 写 `ChapterId`（必要时 `ChapterSpan`）+ `AnchorState='anchored'`。
   ⚠️ 匹配空间必须是**纯文本**：`ChunkText` 切的是 `StripHtml(Content)`，而 `Chapters.CharStart/CharEnd`
   是 **Content 原文**偏移 —— 所以是"逐章取文本 → `StripHtml` → 拼成纯文本序列"再匹配，
   不能拿 `Snippet` 直接去 `Content`（HTML）里找。
   **【必须】"章的纯文本"从哪来 —— 两条来源（r1 修订 A35，A36 调整优先级）**：
   - **⚠️ 本轮唯一可用的路径 = ② 回原文件 / `PdfPages`**（因为 `CharStart/CharEnd` **恒 NULL**，见 §12.1-A36）：
     **EPUB** 按 `(spine, 片段 id)` 用与 N1–N5 **同一套**锚点逻辑从 zip 里切出该章 HTML；
     **PDF 直接拼 `PdfPages` 的页区间**（不需要偏移、也不需要原文件）。
   - **① `Items.Content` 切片**（`Content[CharStart..CharEnd]` → `StripHtml`）：**等偏移落地后**才成为**主路径**
     （届时顺序固定为"先建/重建章节（带偏移）→ 再做原地回填"）。
   - 两条来源都【必须】经**同一个"取章文本"函数**，产出**同一份规范化纯文本** —— 否则同一个 `Snippet`
     会在两条路上得到不同的匹配结果（那比"匹配不上"更难查）。
   - **③ 两条都不可用**（文件被删 **且** 偏移缺失）→ 该书旧块**保持 `AnchorState='unknown'`**，
     可作材料、**不产出引用**，等 `--reindex` 重建。**不许猜**（§6.3.1 第 2 步）。
   - 【必须】**回填的触发点不在启动路径**：它在"拿到该书的章节模型之后、且只对那一本"时做
     （`--toc` 懒路径 / `--chapterize` / `--reindex` 前置）—— 启动时不做全库扫描（§6.4）。
2. **匹配不上就如实标 `unknown`**，**不猜**（猜测的锚点比没有更糟：引用会指向错的页）。
   这类块**仍可作为检索材料**，但**不产出引用**（I4 不破）。
3. 用户跑 **`--index` / `--reindex`**（显式、花钱、可见进度）时，这些行**被重建**为带锚点的块
   （`--reindex` 本就清空重建，天然覆盖）。

- 【必须】`AnchorState` 的取值只在上面三种；**不许**用 `ChapterId IS NULL` 一个条件兼表三义
  （"没有章节模型"与"没算出来"是两件不同的事，界面与 AI 的说法也不同）。
- 【必须】迁移只**添加**列/表/索引；老库启动后第一次 `--toc`/`--index` 不因缺列报错。
- 【必须】**一次迁移做完、不许分两次动 schema**（r1 修订 A18，captain 要求）：
  `Chapters` / `PdfPages` 两张新表、`VectorsChunks` 三列（`ChapterId`/`ChapterSpan`/`AnchorState`）、
  `DbMeta`、以及相关索引，**全部放进同一轮 `InitDatabase`**。
  为什么：半迁移状态（有表没列、或块表有列没锚点列）会让"哪些功能可用"取决于**用户是哪天升级的**，
  排障成本远高于把语句攒在一起发布。

### 6.4 「需不需要重建」的判定式【必须】

| 对象 | 判定式 | 触发时做什么 |
|---|---|---|
| 章节 | §1.6 的 R-a/R-b/R-c | 懒回填（读一本书）或 `--chapterize` |
| 块的章/页锚点 | 存在 `VectorsChunks` 行且 `AnchorState != 'anchored'`，但该书已有 `Chapters` 行 | ① 先跑 §6.3.1 的**免费**原地回填；② 仍未锚定的只在 `sip --index` 的响应/日志里提示"重建索引可获得页码级引用"（**不自动**） |
| 切块规则升级 | `DbMeta.chunker_version` ≠ 常量 | 同上（提示，不自动） |
| **PDF 逐页文本**（r1 修订 A15） | `PdfPages` 无该书行，或 `max(SourceHash)` ≠ 当前 PDF 文件指纹（`<size>-<mtimeTicks>`），或 `max(RulesVersion)` < 常量 | **懒重抽**（与章节同一趟、同一本书，§1.5.1 边界③）；`--chapterize` 可强制。**这一趟不调用任何模型**（A17） |
| **PDF 的块**（r1 修订 A17） | 该书有 `PdfPages` 但 `VectorsChunks` 里没有对应的 `pdf:p<N>` 锚点块 | **只在 `--index`/`--reindex` 里建**（用户显式触发）；`--locate` 的关键词路**不依赖**它 |

**为什么块重建不自动做**：重建块 = 重新调用 embedding API = **花钱、花时间、可能失败**。
自动触发等于"用户打开一本书，结果电脑开始联网烧钱"。章节回填是纯本地计算（解 zip/正则），
所以可以懒做 —— 这两件事的成本差了一个数量级，规则必须分开。

### 6.5 上下文供给的预算梯度（四层）【必须】

| 层 | 内容 | 预算 | 丢弃顺序 |
|---|---|---|---|
| 1 划词段 | 用户选中的原文 | ≤1500 字 | **永不丢**（I5） |
| 2 本章 | 当前章全文（`--chapter` 同一份文本；**PDF 用该章覆盖页的 `PdfPages` 文本拼接，页序不变**） | ≤8000 token；超长取划词位置前后窗口 | 3 |
| 3 本书 | 本书向量/关键词 top 4 段（每段 ≤400 字） | ≤2000 token | 2 |
| 4 全库 | 全库 top 3 条（每条 ≤200 字，**默认关闭**） | ≤800 token | 1 |
| — | 总预算 | ≤16000 token（`EstimateTokens`，`sipcore.cs:8286` 的近似算法） | — |

- 【必须】超出总预算时**从第 4 层往下丢**，且把丢弃记进 `snapshot.degraded`
  （例如 `"book:budget-dropped"`）—— AI 必须知道"这次没看到全书"。
- 【必须】全库层默认**关闭**（`library:false`）：草稿的检索说明里写的正是"全库未启用"，
  且本地优先产品不该在用户只问一段话时去扫全库。开启需要显式参数/开关。
- 检索路由【必须】：**语义（向量）与关键词（子串）两条路都试**，任一条可用即可；
  无索引时只走关键词并把 `degraded` 标上 `no-index`（§9"无向量索引"一行）。

---

### 6.6 成本量级：按一次 `--index` 到底意味着什么（r1 修订 A17）【必须】

**规则**：`--index` / `--reindex` 是**唯一**会花钱的地方；**导入与懒回填一次 embedding 调用都不产生**（R5 / A33）。

**量级：≈ 一次 embedding 调用 / 一块。**

| 书 | 规模 | 块数 ≈ 调用数 | 说明 |
|---|---|---|---|
| EPUB《中国哲学简史》 | ~30 万字 | ~150 | 2000 token/块（R1） |
| EPUB 超长书 | ~60 万字 | ~300 | |
| PDF《中国哲简史》468 页 | 18.7 万字（实测） | ~468（短页合并后更少） | **一页一块**（R7） |
| PDF《SystemVerilog》436 页 | 72.2 万字（实测） | ~436 | 同上 |
| PDF 110 页 | — | ~110 | |

- 【必须】贵的是**调用次数与时间**，不是 token —— 单块文本都很小（72 万字摊到 436 页 = 平均每页 ~1.6k 字）。
  所以"PDF 比 EPUB 贵"由**页数**决定，不由字数决定。
- 【必须】`--index` 结束时**报出实际调用数/块数**（写进响应与 `--json`），让用户事后能对上账。
- 【必须】`--index` **可取消**，且取消不留坏状态：块按 item 幂等（`ON CONFLICT` 覆盖），重跑安全。
- 【建议】第一次对某本书索引前，预告一行"这本书大约要 N 次调用"（本地产品的"预算感"）。

## 7. 多轮状态分层语义

### 7.1 划词锚点**活一轮**【必须】

- 锚点只随**当次请求**来（`body.anchor`），只写进**这条 user 消息**的 `AnchorJson`，
  **不**写进 `ChatSessions`（§4.3）。
- 服务端**没有** `activeSelection`/`currentAnchor` 这类会话级状态列。
- 于是自然得到：第二轮还问"这段"时，如果前端没重新带锚点 → 服务端就没有划词段。
- 【必须】前端在**发送后**清掉引用框（草稿的 `S.sel` 在 `send()` 之后仍留在消息里作为历史显示，
  但输入区不再带上它）。
- 为什么：这是"我明明换了一段，AI 还在说上一段"这类**最让人恼火**的错。
  把锚点做成一次性，错误就变成不可能，而不是"记得更新它"。

### 7.2 会话**严格限于一个阅读项**【必须】（r1 修订 A40①：原为"严格限于一本书"）

- `ChatSessions.ItemId` 是单值；请求体 `itemId` 与会话 `ItemId` 不一致 → `409 SESSION_BOOK_MISMATCH`。
- 「阅读项」= `Items` 表的一行：**本地导入的书**或 **RSS 文章**都算（靠 `Feeds.FeedUrl='local://import'` 区分载体，见 A40①）。
- 前端换书/换文章 → 换会话（`state.ebookId` / `state.articleId` 变化即 `GET .../chat` 重新取），**不**把上一项的会话带过去。
- 为什么：引用胶囊要能**跳回原文**；跨项引用跳不回去（I4），那引用就从"指路"退化成"装饰"。

### 7.3 检索**每轮重算**【必须】

- 上一轮的检索材料**不得**复用（除"历史问答文本"本身）。
- 为什么：阅读位置会变（换章、翻页、重新划词），而"上一次命中哪几段"与新锚点无关。
  复用会让 AI 在换了章节之后继续引用旧章 —— 与 §7.1 是同一类错误的两种形态。

### 7.4 历史滚动压缩【必须】

- 送给模型的会话历史 = `Summary` + **最近 K 轮**（K=6，可配置）+ 本轮四层材料。
- 触发压缩：`TurnCount - SummaryUpToTurn > K` **或** 历史估算 token > 3000。
- 压缩产物写回 `Summary` + `SummaryUpToTurn`（一次 LLM 调用；**失败不阻塞本轮**：
  保留旧摘要、`degraded` 标 `summary:error`）。
- 【必须】旧消息**不删除**：用户还要能往上翻（草稿的对话面板就是可滚动的）。
  压缩只影响"喂给模型的窗口"，不影响"屏幕上显示的历史"。
- 为什么：读一本 30 万字的书问 20 轮，不压缩必然爆上下文；
  而"悄悄删掉历史"是更坏的选择（用户会以为记录丢了）。

### 7.5 **不做对话树**【必须】

- 表里**没有** `ParentMessageId`/`BranchIndex`；`Chapters.ParentId` 只表达书籍层级，
  与对话无关（列名撞车是巧合，实现方别混用）。
- "新会话"是唯一的分叉手段。
- 为什么：树形 UI + 分支命名 + 分支合并，是另一个产品的工作量；
  而"按书可删的线性历史"已经满足了目标（用户要的是"能接着问"，不是"能同时问三条路"）。

---

## 8. 对话 UI 交互契约

**总则**：界面与手感以 `docs/草稿-AI阅读悬浮球.html` 为准
（`#orb` 的行内样式、`#drawer` 的宽度与断点、`.sel`/`.inp`/`.chip`/`.cur` 的行为都有了）。
本节只写**草稿没写、但实现必须定**的部分，以及**必须偏离**草稿的部分。

### 8.1 元素与行为【必须】

| 元素 | 行为 | 验收点 |
|---|---|---|
| 悬浮球 `#orb` | `position:fixed; right:26px; bottom:26px; 52×52 圆形`；**可拖动**（pointer 事件，`|dx|+|dy|>4` 视为拖动）；位置存 `localStorage["sip-orb-pos-v1"]`；点击（未拖动）打开抽屉 | 拖走再刷新，位置还在；拖动**不会**误触打开 |
| 球上的角标 `.dot` / `pulse` | 划词后出现 + 脉冲 + 提示条 2.6s | 与草稿一致 |
| 抽屉 `#drawer` | 宽 422px、右侧滑入；`≤900px` 变成底部 78vh 圆角面板；`Esc` 关闭；**关闭不中止生成** | 窗口拉窄 → 变底部；关掉再开，流仍在（或已落盘） |
| 标题栏 | 显示**书名** + `会话按书保存 · 现在在 <位置>`；右侧「新会话 ↺」「关闭 ✕」 | 换章后位置文字更新 |
| 元信息条 `.meta` | 徽标：类型（EPUB/PDF）、当前位置、`目录 N 章 · 来自 NCX` / `书签 N 章` / `无书签 · 仅页码` / **`按页文本`** 或 **`扫描件 · 无文字`**；**并且必须显示索引状态**（r1 修订 A17）：`已建索引` / **`未建索引 · 语义检索不可用（关键词可查）`** —— 后者要能点/提示"跑 `sip --index`"（**不是**"无文本层"这种整体断言，§1.5 措辞纪律） | **必须**显示目录来源（§2.1 的界面落点）+ 索引状态（§4.4 / §8.3） |
| 无 AI 说明条 `#noai` | `ai.llm.keySet == false` 时显示；文案含"目录和跳章现在就能用（不依赖 AI）"与 `sip --init` | §8.3 的验收 |
| 划词引用框 `.sel` | 显示选中文本（前 46 字），可 ✕ 去掉；**发送后清空输入框里的引用** | §7.1 |
| 输入区 `.inp` | `Enter` 发送、`Shift+Enter` 换行；发送中禁用；`textarea` 自适应高 42~120px | 与草稿一致 |
| 回答块 `.a` | 流式期间显示闪烁光标 `.cur`；结束后渲染引用胶囊与 `ⓘ 检索：…` 快照行 | §5.8 |
| 引用胶囊 `.chip` | 点击 → 跳到该章/该页；跳转后高亮 300ms | I4：**每个** chip 都跳得动 |
| 空态 | 首次打开显示草稿那三条引导（直接问 / 划词问 / 点胶囊跳章） | 与草稿一致 |

### 8.2 状态机（前端）【必须】

```
idle ──(划词)──> hasSelection ──(点球)──> open
open ──(Enter)──> streaming ──(done/error)──> open
streaming ──(Esc)──> open（**不中止**）
streaming ──(换书/新会话/删会话)──> abort（AbortController.abort()）
open ──(换书)──> 重新 GET /chat（会话换了）
ai.keySet=false 时：streaming 不可达（发送按钮禁用）
```

【必须】`abort()` 要真发出去（`fetch` 的 signal），否则前端"停止"了后端还在跑（§5.6）。

### 8.3 「目录/跳页不依赖 AI 配置」的边界【必须】

**不需要 AI 就能用的（I2）**：书库列表、打开书、目录、跳章、翻页、跳页、书签/目录高亮、
"用浏览器打开原文件"、复制临时链接、搜索（**关键词路**，含 `--locate` 反查页码）。

**另一件与 AI 配置无关、但决定"能不能按意思搜"的事（r1 修订 A17）**：语义检索需要 **`--index`**。
它与 `ai.llm.keySet` 是**两件事**：可能 AI 配好了但**这本书**没索引（→ 提示"跑 `sip --index`"），
也可能建过索引但**换了 embedding 模型**（→ 提示需要重建）。界面【必须】把这两种状态分开显示（§8.1）。

**挡位 ≥2 时（r1 修订 A19 / 裁决 C1）**：提问会被 403 拦下（写库即过闸，§5.4）。
界面【必须】**预先**把输入框/发送按钮禁用，并显示这句可执行提示（不是让人按下发送再吃 403）：
"提问会把对话记进 `chat.db`，挡位 2 起视为写操作被拦；要问请把挡位降回 1，或到 TUI。"
目录、跳章、跳页、关键词搜索**照旧可用** —— 挡位不是"整个界面变灰"的理由。

**需要 AI 的**：只要 `ai_config.json` 缺失 **或** `ai.llm.keySet == false`：

- 抽屉仍可打开（用户看得到解释与目录），
- 输入框与发送按钮**禁用**并显示 `#noai` 说明条（文案必须给出**可执行的下一步**），
- 目录/元信息条照常渲染。

**「下一步」现在有两条路（r1 修订 A40②）**，文案要按实际可用性给：

1. **Web 配置**（`sip_settings.json` 的 `AiConfigWebWrite: true` 时可用）→ 指向 AI 面板的 ⚙ 配置页；
2. **真终端** `sip --init`（永远可用）→ 开关关着时这是唯一的路，文案必须给出来。

【必须】不许只写其中一条：开关默认关，只提 Web 会把人指向一个关着的功能；
而只提终端正是 A40② 要修的那个失败（用户第一次上手就卡在这里）。

**验收（黑盒，两条）**：

1. 删掉数据目录里的 `ai_config.json`（或让 `/api/config` 返回 `ai.llm.keySet=false`）后：
   `GET /api/imports/{id}/toc` 仍 **200**，`sip --toc <id> --json` 仍退出 0；
   阅读页的目录与跳章按钮仍可用。
2. 清掉 LLM Key 后 `POST /api/imports/{id}/ask` → **409 `AI_NOT_CONFIGURED`**（不是 500、不是空流），
   响应带 `hint`。

### 8.4 与草稿的**必然偏离**（实现方不许"照草稿实现"这五处）【必须】

| # | 草稿 | 契约 | 为什么 |
|---|---|---|---|
| 1 | EPUB 章节显示 `p.1-24`（纸质页） | **不显示页码**，只显示序号+标题 | sip 拿不到纸质页码，编出来就是欺骗（§1.4） |
| 2 | PDF "有书签" 场景说"没有文本层" | **已不构成偏离**（r1 修订 A26）：草稿已改完 —— PDF 场景的引导语说"文字层 sip 已经抽出来了（逐页）……抽取是否可用**逐份而定**，扫描件仍然抽不出任何字"，演示回答带页码 + 原文引用块并说明"1–24 页抽不出文字（影印图）"，页图说明改成"文字层是 sip 另外抽出来给 AI 用的"。**扫描件场景保持不变**（确实读不到）。契约只需守住 §1.5 的三说法纪律（逐份/逐页，永不做格式断言） | 草稿是**有意入库的交付物**（`.gitignore` 有说明）：**不要删、不要移、不要当旧文案去改** —— 它现在就是新文案，契约与它对齐（措辞以文件现状为准） |
| 3 | header 里的「AI 已配置」开关是演示开关 | 真产品以 `GET /api/config` 的 `ai.llm.keySet` 为准，**没有**"假装配好了"的开关 | 演示开关在真产品里就是"说谎按钮" |
| 4 | 对话历史在内存（切换场景即清空） | 落 `chat.db`，按书可删（§4） | 用户已拍板"按书保存可删的对话历史" |
| 5 | `snapshot` 文案写死 `全库未启用` | 必须来自真实 `snapshot.degraded`（§4.4） | 写死的状态行迟早和事实不符 |

---

## 9. 边界案例表【必须】

| # | 场景 | 期望行为 | 关键字段/码 |
|---|---|---|---|
| 1 | 目标是 RSS 文章（非导入项） | 404 + `NOT_IMPORTED`，退出码 3；消息里说"这不是本地导入项，用 `--show`" | `NOT_IMPORTED` |
| 2 | 不支持的格式（`.zip` 等） | 导入阶段就 400 `UNSUPPORTED_FORMAT`；若历史库里存在这类行，`--toc` 给 404 `NO_CHAPTERS` 并说明 | `UNSUPPORTED_FORMAT` |
| 3 | **无书签 PDF** | `chaptersSource='page'`，每页一章，`Title=''`（不猜标题）；**正文照抽**（`PdfPages`，A15）→ `textAvailable=true`、`--page` 有 `text`；`--locate` **按页可用**（关键词级，免费；A17）；语义检索要 `--index` | `chaptersSource='page'` / `textAvailable:true` |
| 4 | **扫描件**（实测逐页 0 字 → `pdfHasTextLayer=false`） | 同 3（书签可能仍存在 → `'bookmark'`）；`textAvailable:false`、`text:null`、**明说"这一份没有文字层"**；`PdfPages` 行照样写（**"已抽取且确实没文字"必须与"还没抽取"可区分**）；绝不编造内容 | `textAvailable:false` / `pdfHasTextLayer=false` |
| 5 | 超长书（30 万字+ / 1000+ 章） | 目录响应**分页**吗？→ 不分页，但 `chapters` 里每章**只带元数据**（`firstBlock` 默认 60 字）；**【建议】> 500 章时裁到 30 字**（实测 1000 章 ≈ 200KB JSON）；读取仍一章一次请求。⚠️ 这个 30 字**不是拍出来的**：§2.4 的前端单调匹配**只比较前 30 字**，所以裁到 30 字**不影响切分**，也**不参与 A22 的任何判据**（A22 只看章节行数 / `Depth` 分布 / 区间覆盖） | `chapterCount` |
| 6 | AI 未配置（无 `ai_config.json`） | 目录/跳页正常；提问 409 `AI_NOT_CONFIGURED` + hint | §8.3 |
| 7 | 有配置但**无 Key** | 同上（`keySet=false`）；**不要**在启动时就去连模型探测 | `ai.llm.keySet` |
| 8 | **无向量索引** | 提问仍成功：本书层退关键词路（章内子串），`degraded:["book:no-index"]`；AI 被明确告知"没做语义检索" | `degraded` |
| 9 | **断连**（关页面/断网） | 取消上游；该轮 `Status='aborted'`；不写 500 噪音；重开会话能看到那半截回答 | §5.6 |
| 10 | **并发**（同书两轮/两个标签/连点发送） | 第二个 409 `ALREADY_RUNNING`；前端按钮已禁用属第一道防线，服务端锁是第二道 | 409 |
| 11 | EPUB **无 NCX/nav** | `chaptersSource='spine'`：每个 spine 文档一节，标题取该文档 `<title>`/首个 `h1`，都没有则空标题 | `'spine'` |
| 12 | EPUB 损坏/加密（DRM） | 回填失败 → `chaptersError:{code:'EPUB_UNREADABLE',message}`，**不写空目录行**；下次访问自动重试 | `chaptersError` |
| 13 | 书文件被删（`imported/` 里没了） | 目录仍可用（章节在库里）；`--chapter` 用库内 Content 仍可读；`--page`/`--render` 失败 → `RENDER_FAILED`；**不自动删书** | `RENDER_FAILED` |
| 14 | 同一本书重复导入（现有已知问题） | 两个 itemId 各有自己的章节与对话，互不干扰；**不**试图合并。（实测语料里**真的存在**这种残渣：`20260925_085719_4b93de88caf641c1869180d567883968.epub` 与《咸的玩笑》同源 —— opf/ncx/6 个 navPoint/74 个 html 完全一致。契约**仍然不**自动合并：判定"同源"是启发式，误合并的代价远大于多留一份。请在验证时把它当作已知数据、不要当成缺陷） | — |
| 15 | `PageCount` 为 null 的 PDF | `NO_PAGE_INDEX`（409/退出 3），提示"重跑一次导入可重建页数"；`--render` 仍可尝试渲染 | `NO_PAGE_INDEX` |
| 16 | 挡位 2（严格） | **只读命令与只读端点可用（挡位 ≥2 仍须 200）**：`--toc/--chapter/--page/--locate`、`GET /chat`、`/toc`、`/page/{n}`；**写操作与「问 AI」一并被拦（403）**；`--chapterize`、删会话被拦（§5.4 / §12.1-A19+A27） | `SimonIsReadOnly` / `SIMON_BLOCKED` |
| 17 | 挡位 3（极致） | CLI 全拦（既有语义）；Web 只读界面可用；**提问同样 403（不存在"可用但不落盘"路径）**（撤销记录见 §12.1-A19，A27 定稿） | `SIMON_BLOCKED` |
| 18 | `chat.db` 不可写（磁盘满/只读） | **提问仍要能答**（这是故障、不是策略）：`persisted:false` + `ErrorCode='CHAT_PERSIST_FAILED'` + `degraded:["chat:persist-failed"]`；界面显式告知"这轮对话没有保存"。**不许**因为存不了历史而回答失败（§5.3.1）。⚠️ 本行只在**挡位 0/1** 下可达 —— 挡位 ≥2 在写响应头之前就已经 403，根本走不到这里（A22） | `persisted:false` |
| 19 | 划词跨章（选中两章之间的一大段） | 锚点 `inChapter:false`，`chapterId` 取**起点**所在章，`anchor` 事件回显修正结果；本章层仍给起点那一章 | `inChapter:false` |
| 20 | 划词只选到 2 个字/纯空白 | 前端不触发（草稿阈值 `>2`）；服务端收到空/空白 → 视同"无划词"（退成章/书提问），不报 `EMPTY_QUERY` | — |
| 21 | 超长粘贴（10 万字提问） | 服务端截断到 4000 字并 `truncated` 标注，**不**因体积拒绝 | — |
| 22 | 模型返回空/全是空白 | `Status='error'` + `EMPTY_RESPONSE`；界面上说"模型没给出内容"，不显示空白气泡 | `EMPTY_RESPONSE` |
| 23 | 模型回答里带 `**粗体**`/`>` 引用（草稿支持） | 前端按草稿的 `md()` 极小 Markdown 渲染；**先转义再套标签**（草稿 `esc()` 的顺序） | 防 XSS |
| 24 | 引用 chip 的章在跳转时已被删/重建 | 跳转前用它落库时的 `chapterId` 查一次；查不到 → chip 置灰 + toast"这一章已不可定位"，**不**跳到错位置 | I4 |
| 25 | 会话里的书已删 | 会话列表仍能读到（`ItemTitle` 快照），打开时提示书已删除，只读历史 | §4.2 |
| 26 | **目录项远多于 spine 文件**（实测 200 节 / 33 文件） | 章节行数 ≈ 目录项数，`Depth` 保留到 6 层；**不许**退化成"一个文件一章" | §1.4.1 |
| 27 | 目录项 `src` 指向的**文件不在 spine 里** | 丢弃该目录项、计入 `tocDropped`，**不造**指向不存在内容的章 | N2(c) |
| 28 | 目录项的**锚点在正文里找不到**（片段拼错/正文被改过） | 降级到该 spine 片段起点（N2(b)）；区间不与邻居重叠（N1/N3 保证） | §1.4.1 |
| 29 | **零长度章**（父章无正文、内容都在子节） | 行保留、`zeroLength=true`；命中/定位归到**最深**章 → 零长度章永不被引用 | N4 |
| 30 | 目录**超深**（畸形/机器生成） | 6 层以内原样、更深压平到 6 并记 `depthFlattened` | §1.4.1 |
| 31 | **坏归档**（`父与子全集.epub`：找不到 EOCD） | 导入期 `ARCHIVE_UNREADABLE`(400/1) 且不留空 asset 目录；回填期不崩、`chaptersError` 说明、不写章节行、不缓存失败 | §1.4.2 |
| 32 | **zip-bomb / zip-slip / XXE** | 计数流上限拒绝（64 MB/512 MB/20000 条/200:1）；条目名含 `\`、前导 `/`、`:` 即拒；XML 解析 `DtdProcessing=Prohibit` + `XmlResolver=null` | §1.4.2 |
| 33 | **目录稀疏**：NCX 只有 6 条、却有 74 个 html（《咸的玩笑》） | 边界延续规则：中间那些没有目录项指向的文件**全部归属上一条目录项**（N3），且 N8 覆盖不变量成立 → **没有"无章节黑洞"**（否则 AI 问"本章"拿到空上下文） | §1.4.1 N3/N8 |
| 34 | **非 PDF / 损坏 PDF / 加密 PDF** | PdfPig 的异常被包住：导入期**不崩**（沿用既有占位行为）、回填期 `chaptersError={code:'PDF_UNREADABLE'}`；**不是**崩溃、不是 500 | §1.5 / A27 |
| 35 | **无书签的大 PDF**（实测 436 页、文本层完好 722289 字） | 出**页级**目录（436 行、`Title=''`、`Source='page'`）；**`textAvailable:true`**（逐页文本落 `PdfPages`，r1 修订 A15）；**不许**因页数/体积把它当异常或跳过 | §1.4 / A29 |
| 37 | **PDF 关键词反查**（有文本层） | `--locate <词> --book <id>` → 返回 `page`（1 基）+ `chapterId` + `snippet`；**不调用任何模型**（永远免费，不需要索引） | §3.5 / A34 |
| 38 | **PDF 语义检索**（有人会问"为什么搜不到"） | 有索引（跑过 `--index`）→ 正常按意思搜，命中带 `pdf:p<page>` 引用；**没索引** → `degraded` 带 `no-index` + `pdf:keyword-only`，**回答里给"跑 sip --index"的提示**，**不许静默返回空** | §4.4 / §6.6 / A33 / A37 |
| 39 | **页文本还没抽取就提问** | 不报错：`degraded` 标 `pdf:not-extracted`，回答里如实说明"这本书的页文本还没抽取"；**`/ask` 不许顺手回填**（I1），提示用户打开阅读页或跑 `--chapterize` | §1.6 / I1 |
| 40 | **旧库的块没有锚点**（升级后第一次用） | 先免费原地回填（§6.3.1：用 `Snippet` 在**章节纯文本序列**上单调匹配）→ 命中标 `anchored`、匹配不上标 `unknown`；`unknown` 的块**可作材料但不产出引用**，只有 `--reindex` 才会重建带锚点 | §6.3.1 / A36 |
| 41 | **大 PDF 首次打开**（≥300 页或 ≥30 万字，实测样本 436 页 / 72 万字） | **两段式**（§1.5.3）：首屏 **≤2 秒**返回书签章 + `pdfTextState='extracting'`；逐页文本在同一进程继续抽；抽完置 `ready`。期间 `--page` 对未抽页给 `false`+`note`、`--ask` 用已有部分 + `degraded:["pdf:extracting"]` | §1.5.3 / A40 |
| 36 | **扫描件**（逐页抽出 0 字） | `pdfHasTextLayer=false`，明说"读不到文字"，退回按页栅格化 / `--vision`；**不许**用文件体积判定（实测 0.5 MB、0.17 MB 的都是**有**正文的） | §1.5 / §12.1-A12 |

---

## 10. i18n 键命名约定

### 10.1 规则【必须】

- **R1 键 = 英文原文**。`Lang.T(key)` 查不到就显示 key 自身 —— 于是"英文原文当键"让缺译**降级为英文**，
  而不是降级为乱码或空串（既有设计，不动）。
- **R2 三份文件**：`languages/zh-CN.json`、`languages/zh-Moe.json`、`languages/en-US.json`。
  【必须】新键**同时**进 `zh-CN` 与 `zh-Moe`（`tests/Sip.Tests/LangParityTests.cs` 强制
  `zh-CN ⊆ zh-Moe`，缺一条就是红色）。`en-US` 是"英文原文的显式覆盖表"，**不要求**齐全 ——
  缺了正好回落到 key（也就是英文原文），这是**正确**行为，不是 bug。
- **R3 不用命名空间**：键里**不许**出现 `orb.`/`ai.chat.` 这类前缀或点号分组。
  理由：同一个字符串要在 CLI（`Lang.T`）与前端（`t()`）共用，而 `t()` 读的是同一份
  `/languages/<code>.json` 拍平后的扁平表（`web/app.js:97`）；加了前缀反而两边写法不一致。
- **R4 先查再建**：新增前必须 `grep` 现有键。**已有同义键直接复用**，不许造近义新键
  （例：`No vector index yet, run sip --index first`、`Article {0} not found` 已存在）。
- **R5 占位符**：统一 `{0}`/`{1}`；三份译文里的占位符集合**必须完全一致**
  （本轮新增约定：现有代码不做校验，但缺一个占位符会在 `string.Format` 时
  被 `catch(FormatException)` 静默吞掉并显示原始模板 —— 那是很难查的界面 bug）。
- **R6 错误码不进语言文件**：`code` 是协议（恒定英文大写，见 §3.6）；
  只翻译 `message`/`hint`/按钮/标题。
- **R7 动态内容不进键**：键是**常量**；变量只走占位符（"Chapter 3 of 12" 要写成
  `"Chapter {0} of {1}"`，不许 `$"Chapter {n}"` 拼键 —— 那样永远查不到译文）。
- **R8 前端硬编码中文要收敛**：`web/app.js` 里现存的裸中文文案（如 `"已删除"`、`"打不开"`）
  本轮**不强制**全部改造（会淹没这次的目标）；但**本轮新增的** AI 阅读文案
  【必须】走 `t()`，不许再添裸中文。
- **R9 话术只有一个来源**（r1 修订 A28，reviewer 明确会查）：同一句话在 **CLI / Web / 文档**里
  **共用 §10.2 的同一个键**，不许各写一份文案。契约只写**键名与语义**，不写"另一套措辞"；
  界面上出现的每一句用户可见的 AI 阅读话术，都必须能在 §10.2 找到对应的键（实在没有就先加键）。
  为什么：三处各写一份，改一处漏两处 —— 而**用户可见的承诺**（能不能读 PDF 正文、挡位下能不能问）
  恰恰是这轮反复对话的焦点，措辞漂移会直接变成"文档说的和产品做的不一样"。

### 10.2 本轮必须新增的键（最小集，供实现方按 R4 复核后落表）

| 英文原文（=键） | zh-CN | zh-Moe |
|---|---|---|
| `Ask AI` | 问 AI | 问 AI 喵 |
| `Ask about the selection` | 就这段问 AI | 就这段问 AI 喵 |
| `AI chat` | AI 对话 | AI 对话喵 |
| `Table of contents` | 目录 | 目录喵 |
| `Chapters` | 章节 | 章节喵 |
| `Pages` | 页码 | 页码喵 |
| `No bookmark — page numbers only` | 没有书签 —— 只能按页码走 | 没有书签喵 —— 只能按页码走喵 |
| `PDF text is read page by page` | PDF 正文按页读取 | PDF 正文按页读取喵 |
| `Table of contents from the book's own NCX` | 目录来自书里的 NCX（出版方目录，不是猜的） | 目录来自书里的 NCX 喵（出版方目录，不是猜的） |
| `Table of contents from PDF bookmarks` | 目录来自 PDF 自带书签 | 目录来自 PDF 自带书签喵 |
| `Could not read the table of contents: {0}` | 读不到目录：{0} | 读不到目录喵：{0} |
| `Unsupported file type: {0}. Supported: txt, md, pdf, epub, mobi, docx` | 不支持的文件类型：{0}。支持：txt、md、pdf、epub、mobi、docx | 不支持的文件类型喵：{0}。支持：txt、md、pdf、epub、mobi、docx |
| `AI is not configured yet.` | 还没配置 AI。 | 还没配置 AI 喵。 |
| `Table of contents and jumping to a chapter work without AI. To ask questions, run sip --init in a real terminal and refresh this page.` | 目录和跳章现在就能用（不依赖 AI）。要问问题，请先在真实终端跑一次 sip --init，然后刷新本页。 | 目录和跳章现在就能用喵（不依赖 AI）。要问问题，请先在真实终端跑一次 sip --init，然后刷新本页喵。 |
| `Send` | 发送 | 发送喵 |
| `Stop` | 停止 | 停止喵 |
| `New chat` | 新会话 | 新会话喵 |
| `Reading position: {0}` | 现在在 {0} | 现在在 {0} 喵 |
| `Chapter {0}` | 第 {0} 章 | 第 {0} 章喵 |
| `Page {0}` | 第 {0} 页 | 第 {0} 页喵 |
| `Searching: {0}` | 检索：{0} | 检索喵：{0} |
| `answered from this book only` | （本次回答只用这本书） | （本次回答只用这本书喵） |
| `Chat history for this book` | 这本书的对话历史 | 这本书的对话历史喵 |
| `Delete this chat history? This cannot be undone.` | 删掉这本书的对话历史？无法撤销。 | 删掉这本书的对话历史喵？无法撤销。 |
| `No index yet — answering with keyword search only` | 还没建索引 —— 本次只用关键词检索 | 还没建索引喵 —— 本次只用关键词检索喵 |
| `Answer interrupted ({0})` | 回答中断（{0}） | 回答中断喵（{0}） |

| `Front matter` | 卷首 | 卷首喵 |
| `Scanned PDF — no text layer, the AI can only point at pages` | 扫描件 —— 没有文字层，AI 只能指到页码 | 扫描件喵 —— 没有文字层，AI 只能指到页码 |
| `This PDF's text was extracted page by page` | 这份 PDF 的文字层已逐页抽取 | 这份 PDF 的文字层已逐页抽取喵 |
| `Some pages have no extractable text (scanned images) — for those I can only give you page numbers` | 有些页抽不出文字（影印图）—— 那几页我只能给你页码 | 有些页抽不出文字喵（影印图）—— 那几页我只能给你页码 |
| `Page {0} has no extractable text` | 第 {0} 页抽不出文字 | 第 {0} 页抽不出文字喵 |
| `Extracting this PDF's text — page text will be ready shortly` | 正在抽取这份 PDF 的正文 —— 页文本稍后可用 | 正在抽取这份 PDF 的正文喵 —— 页文本稍后可用 |
| `Keyword look-up (always free) · semantic search needs sip --index` | 关键词查（永远免费）· 语义检索需要先跑 sip --index | 关键词查喵（永远免费）· 语义检索需要先跑 sip --index |
| `This book isn't indexed yet — run sip --index to search by meaning` | 这本书还没建索引 —— 跑 `sip --index` 就能按意思搜 | 这本书还没建索引喵 —— 跑 `sip --index` 就能按意思搜 |
| `Indexing about N chunks (≈ N model calls)` | 大约 N 块（≈ N 次模型调用） | 大约 N 块喵（≈ N 次模型调用） |
| `Asking saves the conversation to chat.db; at level 2+ that counts as a write, so it is blocked. Lower the level back to 1, or ask from the TUI.` | 提问会把对话记进 chat.db，挡位 2 起视为写操作被拦；要问请把挡位降回 1，或到 TUI 提问。 | 提问会把对话记进 chat.db 喵，挡位 2 起视为写操作被拦喵；要问请把挡位降回 1，或到 TUI 提问喵。 |
| `This turn was not saved.` | 这轮对话没有保存。 | 这轮对话没有保存喵。 |
| `Writing the chat history failed — the answer is still shown.` | 对话历史写入失败 —— 回答照常显示。 | 对话历史写入失败喵 —— 回答照常显示。 |
| `The file is not a valid PDF (or is damaged)` | 这个文件不是有效的 PDF（或已损坏） | 这个文件不是有效的 PDF 喵（或已损坏） |

【必须】上表**只是最小集**；实现时按 R4 复核（有些键可能已存在），且
`LangParityTests` 必须保持绿色。

---

## 11. 明确「不做」

以下每一条都是**契约**：评审按"有没有偷偷做"来判断，做了就要说清理由并走 §12 变更流程。

⚠️ **本节只列"真正不做"的事**（r1 修订 A21 / captain 明确要求）：
凡**"能力做了、但受成本或时机约束"**的，写在规范位置而**不**放这里 ——
例如 **PDF 的语义索引是做了的**（走 `--index` 按需，`--locate` 关键词反查永远免费），
它的约束写在 §1.5.1 边界① / §6.2 R5 / §6.6。
把"按需"写成"不做"，后来人读到的就是"**做不到**"而不是"**我们选择按需**" —— 这正是本次要避免的坑。

1. **不写回笔记 / 高亮**：不新增 `Notes`/`Highlights` 表；不改 `Items.Content`；
   不写 `Items.Summary`（摘要字段是既有功能的，AI 对话不碰它）。
2. **不猜章节**：扫描件、无书签 PDF、NCX 缺失的 EPUB，一律**不生成**猜测性标题。
   `--heading` 的启发式仅用于"非 EPUB/PDF 的文本格式"（那里没有更好的结构可用），且必须标 `source='heading'`。
3. **不承诺 PDF 的段/行级精度**（r1 修订 A15/A17）：PDF **支持关键词反查到"页"**（`PdfPages` 子串，永远免费），
   也**支持按需的语义检索**（需要 `--index` —— 见 §6.6 与 §1.5.1 边界①）；但**不**承诺"第几段、第几行"
   （`offsetInPage` 是附加信息）。
   扫描件的 `--locate` 返回**空结果 + 说明**（"这一份没有文本层"），
   绝不返回"大概在第 3 页"这种模糊命中。
4. **不做 OCR**（扫描件转文本是另一个项目）。
   ⚠️ 与"PDF 逐页文本"**不是一回事**（r1 修订 A15）：**有文本层的 PDF 我们会读**（落 `PdfPages`，§1.5.1），
   **扫描件不读、也不猜** —— 它逐页 0 字是**正常结果**（`textAvailable:false` + `success:true`），不是待修缺陷。
   也不许拿"要支持 PDF"当理由顺手接一个 OCR 引擎进来。
5. **不做对话树 / 跨书会话 / 会话分享导出**。
6. **不做 rerank / 多路召回融合调优**：只用"章内块向量 + 章内关键词"两条路，够用即可。
7. **不改 EPUB/PDF 原文件**，不重新导入，不移动 `imported/` 里的文件。
8. **不引入后台作业队列**：懒回填是同步的一次一本书；块重建只在 `--index/--reindex` 里做。
   （r1 修订 A28：**大 PDF 的两段式抽取是唯一例外** —— 它是"一次已经开始的抽取在同进程里跑完"，
   **不是**队列/调度器，见 §1.5.3。）
9. **不把对话写进 `rss.db`**（§1.1）。
10. **不把 `chat.db` 纳入每日备份**（本轮）：因此删除动作必须显式确认（§4.5）；
    也不提供"清空全库对话"的一键入口。
11. **不做移动端专用 UI**：响应式（草稿的 `≤900px` 底部面板）即可。
12. **不改既有命令/接口的语义**（I6）。

> **本节已删掉原第 13 条**（r1 修订 A21）：那条写的是「**不做"自动嵌入"** —— 导入与懒回填永不产生 embedding 调用；
  PDF 的语义索引只能由 `--index`/`--reindex` 显式触发」。
  它的**内容一点没丢**，只是**不该放在"不做"清单里**：它现在完整躺在
  §1.5.1 边界①（PDF 可进块表、只能按需）＋ §6.2 R5（嵌入只在 `--index` 链路发生）
  ＋ §6.6（成本量级与"导入 0 次调用"）三处规范位置。
  留在本节只会让人读成"PDF 不能进语义检索"——与用户拍板相反。

---

## 12. 变更流程与修改记录

- 任何实现方想偏离契约的地方：先在**本文件**的 §12.1 记一行（日期 / 条目 / 理由 / 谁批准的），
  再改代码。没有记录的偏离，t7 直接判 `needs_revision`。
- 契约的**数值型参数**（K=6、预算 16000 token、300s、15s ping、1500 字）都是【建议】可调，
  但**改了要在 §12.2 记账**，且验收用例要跟着改。

### 12.1 修改记录

| 日期 | 条目 | 变更 | 理由 |
|---|---|---|---|
| 2026-（r1 冻结当日） | A1 | §1.5 选型定为 **PdfPig 优先 / pdfium P/Invoke 备选**（原文只写了 P/Invoke）；新增三条降级码建议与"渲染走 pdfium、抽取走 PdfPig 互不替代" | 由 anchor-eng 的底座侦察提出并经契约侧核实：PdfPig 0.1.16 在本地缓存、`net9.0` **零传递依赖**、Apache-2.0、有 `TryGetBookmarks`。本机 NuGet 源不可达（`obj/project.assets.json` NU1900），**零依赖**是能否离线加包的硬条件 —— P/Invoke 从"唯一解"降为"备选" |
| 2026-（r1 冻结当日） | A2 | 新增 §1.3.1「下标 / 页码的 0 基与 1 基约定」 | anchor-eng 明确把"页码 1-based 还是 0-based"列为必须冻结项；原本散在各节，容易在边界差一。明确 `epub:<spine>` 是全契约**唯一** 0 基例外 |
| 2026-（r1 冻结当日） | A3 | §1.4 明确 EPUB 目录优先级 **nav → ncx → spine**，并把「章节边界来源（`Source`）」与「标题来源」拆成两件事 | 采纳 anchor-eng 建议的标题优先级（补 `<title>` 兜底），同时避免"标题取自 h1 就把 `Source` 写成 heading"这种污染 |
| 2026-（r1 冻结当日） | A4 | §3.5 明确关键词定位**精度目标 = 章节级**，不扩 `ItemsFts`，不承诺段/行级 | anchor-eng 指出 `ItemsFts` 是 `fts5(trigram)` 且**不带位置信息**；改它等于动既有索引语义（违反 I6），而章内扫描成本可忽略 |
| 2026-（r1 冻结当日） | A5 | §6.3 明确 `VectorsChunks.ChapterId TEXT NULL` 的语义，并**禁止**用块文本做区间反查 | anchor-eng 提出"加列 vs 区间反查"两条路；重叠块 + 200 字 `Snippet` 让区间反查必然二义 |
| 2026-（r1 冻结当日） | A6 | §1.6 明确回填触发点：懒回填 + `--chapterize` 是**强制**两条；`--reindex` 可顺带但不得成唯一入口；导入后顺手回填为【建议】；回填必须离线可跑 | anchor-eng 提议把回填挂 `--reindex`。但 `--reindex` 属 AI 链路（要 Key/模型），挂在那里会让"没配 AI 就没有目录"，直接违反 I2 |
| 2026-（r1 冻结当日） | A7 | §11 新增第 13 条「本轮不启用 PDF 正文抽取」，§2.2 增预留位 `textSource` | anchor-eng 计划用 PdfPig 同时抽正文。但那会改变草稿里 AI 对 PDF 的公开承诺（"我不会凭书签猜正文讲了什么"），属产品决策；先在契约里留门，开启需 captain/用户拍板 |
| 2026-（r1 冻结当日） | A8 | §4.2 新增 §4.2.1「建表落点、关联键、删除语义、责任归属」：绑定 `chat.db` 独立文件（**禁止**进 `InitDatabase`/rss.db）、关联键 = `Items.Id`、顺序规则、重试=新一轮、一轮两行必齐、硬删且**不设** `DeletedAt`、删会话用两条显式 DELETE、t3/t4/t5 验收归属表；附录 A 增 A19~A21 | anchor-eng 拟在 t3 的 `InitDatabase` 里建这两张表（`sipcore.cs` 单写者约定）。若照字面落进 `InitDatabase`，表会建在 **rss.db** —— harness 每问一次就写主库，I1 与验收 A7 同时失效。故把"DDL 文本归 t3、文件必须另建"写成硬约束，并把两张表的功能验收归到 t4/t5，t3 只认 DDL 落点与幂等 |
| 2026-（r1 冻结当日） | A9 | 新增 **§1.4.1「EPUB 章节 = 按 navPoint 建行」**（含 chapterId 撞键消歧 `~n<k>`、切片算法 N1–N6、零长度章、深度上限 6、EPUB3 合成夹具、`StreamPosition` 漂移陷阱）与 **§1.4.2「归档与 XML 的安全硬约束」**（zip-slip / zip-bomb / XXE / 坏归档两阶段行为 + `ARCHIVE_UNREADABLE`）；§1.4 的 `Depth` 行 3 层→6 层；§6.2 R2 收敛合并范围（同父章 + 不跨 spine 文件）；§9 增 26–32；附录 A 增 A22–A25 | verifier 用第三方库（PyMuPDF/pypdf）独立取证：**navPoint 与 spine 文件是多对一**（中国哲学简史 200/33、看图自学电吉他 224/18、毛泽东选集 410/416），按 spine 建行会把 200 节压成 33 章、"第 3 章第 2 节"这类引用直接说不出来 —— 那正是本目标的核心。另三条实证：本机 7 本真实 EPUB **全是 EPUB2+ncx**（EPUB3 路径必须靠合成夹具覆盖，且不许假装验过）、`父与子全集.epub` 是**坏 zip**（天然反例夹具，须定义两阶段期望行为）、审计侧要求 zip-slip/zip-bomb/XXE 进契约 |
| 2026-（r1 冻结当日） | A10 | §1.4 / §1.4.1 把 EPUB 目录优先级由 **nav → ncx 反转为 ncx → nav**，并同步改标题来源优先级；`Locator` 记 `{"tocKind":"ncx"\|"nav"}` | anchor-eng 实跑证实：真实语料 **100% 是 EPUB2 + toc.ncx**（中国哲学简史 200 navPoints/maxDepth 2、毛泽东选集 410/3、四世同堂 110/3、周恩来选集 122/1、咸的玩笑 6/1），`nav.xhtml` 一本都没有。nav 路径从未在真实书上跑过 —— 该用被验证过的那条。nav 仍必须实现并用合成夹具覆盖（A23） |
| 2026-（r1 冻结当日） | A11 | §1.4.1 把 N3 从"同文件内"改为**跨文件的边界延续规则**；新增 **N7 卷首合成章**（`Kind='front'`）与 **N8 覆盖不变量**；§1.2 `Kind` 增 `'front'`；§9 增 33 行；附录 A 增 A28 | anchor-eng 实跑发现**目录条数与正文文件数严重不匹配**（《咸的玩笑》NCX 6 条 vs 74 个 html、34 个含标题）。若章区间只在本文件内闭合，"正文一"之后几十个文件会掉进**无章节黑洞**：库里明明有内容，AI 问"本章"却拿到空上下文 —— 这是会直接伤到目标的缺陷，不是边界瑕疵 |
| 2026-（r1 冻结当日） | A12 | §1.3.1 写死 PDF 四个编号（`BookmarkNode.PageNumber` 1 基 = `PageStart` 1 基 = `p<N>` 1 基 = 渲染索引 0 基，只在调用处 −1）；§1.4 规定**必须用 `GetNodes()` 展平 + `Level`、禁用 `Roots.Count`**、无目标节点继承后代页；§1.5 把 `pdfHasTextLayer` 提为【必须】、**禁用体积判据**、要求包住 PdfPig 异常（`PdfDocumentFormatException` 等）；§3.5 明确**本轮对 PDF 不要求页级关键词精度**（与 §11-13 同一开关）；§9 增 34–36；附录 A 增 A27/A29/A30 | anchor-eng 五项实跑取证：① 书签页码与 `GetPage(n)` 同为 **1 基**（5 个真 PDF 用"标题是否出现在该页正文"交叉验证，全中，前一页全不中）；② `Roots.Count` 不是章节数（实测 28/28/L0、79/31/L1、39/4/L2，须用展平列表）；③ 无书签的 436 页大书**文本层完好**（`SystemVerilog for Design(2nd)`，722289 字）→ 页级目录是主路径；④ 扫描件实测逐页 0 字，而 **0.17 MB / 0.5 MB 的小 PDF 反而是完整正文** → 体积不能当判据；⑤ 把 `.jpg` 喂给 `PdfDocument.Open` 抛 `PdfDocumentFormatException` → 必须包住，否则损坏文件能让 `--import` 崩 |
| 2026-（r1 冻结当日） | A13 | §1.5 整节重写：**PdfPig 为唯一主路径**（补 `PdfDocument.Open` → `TryGetBookmarks(out Bookmarks)` → `GetNodes()` 实现骨架、成员名逐一核对）、**新增书签节点四分类表**（`ContainerBookmarkNode` 跳过不建行 / 目标无效但子树有目标→继承首个后代页 / 整棵子树无目标→丢弃计 `tocDropped` / 有有效目标→成章）、零长度章 `PageEnd = PageStart − 1` 且不参与引用、`Depth` 改为**可见深度**（`rawLevel` 留在 `Locator`）、P/Invoke 降为**一句备选说明（本轮不实现）**；§1.4 同步；附录 A 增 A31；**附录 C 第 3 条重写**（原文仍在"推荐写一层 P/Invoke"，已成误导） | captain 要求「修订第 1 条」：P/Invoke 要在运行时探测并加载原生库，路径随平台/发布形态变化 —— Windows 能通不代表 Linux/macOS 找得到，而失败只表现为**静默降级成页级目录**，是本项目最不希望的"悄悄变差"；PdfPig 纯托管、零传递依赖、实测离线可还原且不破坏单文件发布（+4.25 MB）。注：A1 已把 PdfPig 定为首选，但**表述仍把 P/Invoke 当活路径**、且附录 C 仍在推荐它，故 A13 把整套表述与规则收口 |
| 2026-（r1 冻结当日） | A14 | **纠正一处事实性措辞**：全文把"PDF 没有文本层"改成 **"本轮 sip 不抽 PDF 正文"**（产品范围）或 **"这一份没有文本层（`pdfHasTextLayer=false`）"**（逐份实测事实）；§1.4「正文可读」行、§3.4、§3.5、§4.4 的 `degraded` 取值域、§8.1 徽标、§8.4 偏离表第 2 条、§10.2 的键、§11-3、附录 C-2、§9-4 全部同步；§1.5 新增**措辞纪律**【必须】；另把实现骨架与四分类表按真实 API 收口：**`PageNumber` 只在 `DocumentBookmarkNode` 上**（基类没有），基类上是 `Title`/`Level`/`IsLeaf` | anchor-eng 实跑指出：原文把「sip 本轮不抽 PDF 文本层」写成了「**PDF 没有文本层**」。后者是**对格式的错误断言** —— 实测 `SystemVerilog for Design(2nd)` 436 页 **722,289 字**、`中国哲简史` 468 页 **187,830 字**，PdfPig 都能逐页抽出；只有扫描件（`掃描全能王 …`、`1.pdf`、`images.pdf`）逐页 0 字。两种说法的**降级路径并不一样**（"读不到"vs"本轮读不到"），照着错前提写注释与降级逻辑会得到一个把产品范围说成格式缺陷的系统。同时据其探针核对：`IsLeaf`/`DocumentBookmarkNode.PageNumber` 的归属，与本契约 A13 的骨架一致化 |
| 2026-（r1 冻结当日） | A15 | **用户拍板的范围变更（第二次）**：PDF 逐页文本启用 —— 新增 **§1.5.1**（`PdfPages(ItemId,Page,Text,CharCount,ExtractedAt,SourceHash,RulesVersion)` 建表 + 四条硬边界）与 §6.4 的重抽判定；同步改 §0.2(I1 补"不建表、不回填")、§1.4（定位主键/正文可读/章内块进向量）、§1.6（**`/ask` 不是回填触发点**）、§1.7（级联删 `PdfPages`）、§2.2（`textAvailable`/`textSource`/`indexed`）、§3.3（PDF 章给 `pages[]`）、§3.4（`--page` 返回 `text` + 扫描件/未抽取要区分）、§3.5（PDF 关键词反查到**页**）、§6.2 R5（升级为"不进任何嵌入路径"）、§6.5（本章层含 PDF 页文本）、§8.1/§8.4、§10.2（i18n 键替换为「按页读取」/「扫描件·无文字层」/「按关键词查」）、§11-3、§11-4、§11-13（改为"PDF 不做语义检索"硬边界）、§9（增 37~39）、附录 A（A29 改 + 新增 A32~A35）、附录 C-3</br>**A14 的措辞规则随之更新**：由"本轮 sip 不抽 PDF 正文"改为「**sip 按页抽取 PDF 正文**」或「**这一份没有文本层（扫描件）**」 | 用户拍板「PDF 文本层要做」，并同时给出四条硬边界（① 不进嵌入/块表，`--locate` 是关键词级免费 ② 独立页表、不进 `Items.Content`（否则改 `SourceHash` → 触发 R-b 全库回填）③ 与章节同一趟懒回填、幂等、扫描件逐页 0 字是**正常结果** ④ 页编号与章节同一套判据、同一处代码）。写进契约的目的正是**防止范围滚雪球**：PDF 可读正文 ≠ PDF 可被语义索引，后者是**明确不做**（§11-13），评审不该把它当遗漏。**⚠️ 本条的模式①（"PDF 绝不进嵌入"）已被 §12.1-A17 撤销并替换** —— 用户随后裁定"该按需付费，不该禁止"，请以 A17 为准 |
| 2026-（r1 冻结当日） | A16 | §4.2.1a / §0.2 / 附录 B 对齐 captain 对 `chat.db` 的裁决：`InitChatDatabase(chatDbPath)` **在启动路径上与 `InitDatabase` 相邻调用**（照 `TelemetryService.Init(dataDir)` 的先例），**不在 Web 请求路径建表**；I1 的表述补上"且不建表、不回填" | captain 指出：建表如果落在请求路径上，"第一次提问"就会碰主库/建文件，把"harness 只读、可哈希证明"这条**硬保证**退化成时序问题。挪到启动路径后，A7（提问前后 rss.db 哈希不变）与 A19（`rss.db` 里没有 `Chat%` 表）两条同时成立 |
| 2026-（r1 冻结当日） | A17 | **撤销并替换 A15 的边界①**（PDF 从"绝不进嵌入"改为"**可以**进块表，但**只能按需**"）：§1.5.1 边界① 重写（`--index` 显式触发 / 导入与懒回填 **0 次调用** / PDF 锚点 `pdf:p<page>` / 两条路分清）；§6.2 新增 **R7**（PDF 以**页**为块边界，短页合并记 `ChapterSpan`）与 **R8**（块必须带位置回指：`ChapterId`/`ChapterSpan`/`AnchorState` 三列）；新增 **§6.3.1 三列语义 + 旧库三步回填规则**（免费单调匹配 → 匹配不上标 `unknown` 且不产出引用 → `--reindex` 重建）；新增 **§6.6 成本量级表**（≈1 次调用/块；468 页 ≈ 几百次；贵在次数与时间不在 token；`--index` 可取消、要报块数）；§3.5 两条路分清；§4.4 `no-index` 必须给可操作提示（**不许静默返回空**）；§8.1 界面必须显示索引状态；§10.2 键替换/新增；§11-3 与 §11-13 重写（"不做自动嵌入"）；§9 更新 37/38 并新增 40；附录 A 重写 A33、新增 A36/A37 | captain 转达用户裁决：**"撤掉『PDF 文本绝不进 `VectorsChunks`』"** —— 要防的是**无界成本**（导入一本 436 页的书就自动烧钱），正确手段是**按需付费**而不是**禁止**；
| 2026-（r1 冻结当日） | A18 | **两笔记账 + 一条新规则**：<br>① **PDF 文本层抽取 + 可按需语义索引**（**用户拍板**）：正文抽到 `PdfPages`、`textSource='pdf-text'` 落实为实现要求（不再是预留位）、PDF 可按需进块表；<br>② **PdfPig 选型替代 P/Invoke**（captain 裁决）：并把"**依赖树是否全在本地缓存里**"写成**新依赖准入规则**（新增 §1.5.2 —— 本机 NuGet 不可达，这是本轮所有新依赖的准入依据）；<br>③ §6.3 补【必须】**一次迁移做完、不许分两次动 schema**（`Chapters`/`PdfPages`/`VectorsChunks` 三列/`DbMeta` 同轮）；<br>④ §9-3 修正陈旧断言（无书签 PDF 的 `--locate` 已从"返回空"改为"**按页可用**"） | captain 转达用户两个决定：「**PDF 也需要 embedding**」与「PDF 文本层**做**」——正好是 A15/A17 的内容，两封信交叉；本轮把四处**尚未写死的部分**补齐：`textSource` 从预留位提升为实现要求、依赖准入规则成文、迁移不许分批、§9-3 的陈旧断言（它会让 t5 把正确实现判红）。**§11-13 已同步改写**，不留与决策矛盾的条目 |
| 2026-（r1 冻结当日） | A19 | **裁决 C1 落入契约（偏离记录）**：<br>① **偏离项一：挡位 ≥2 下的 chat 行为。** 原契约 §5.4/§9-16/17 写"提问是只读 + 外部网络，挡位 2 不拦；挡位 3 才 `persisted:false`" → **改为**"**写库即过闸**：`POST /ask` 会写 `chat.db`，与新建/删除会话同属写端点，挡位 ≥2 **一律** 403 `SIMON_BLOCKED`、不进流"（**限四个写端点**；纯读端点仍须 200 —— 见 A27）；挡位 3 不再有特例"。<br>依据：`Web.cs:494-500` 的注释先例 ——「同一个操作仅因走的通道不同就裁决相反，实际成了一条绕过 CLI 限制的写通道（审计 §2.3）」，而 `WebWriteAllowed` 的拒绝文案正是 **"Reading still works"**（提问不是 reading）。<br>② **字段名：维持 `persisted`**，**captain 撤回**其 v4 里新造的 `saved`；但**语义加严**（新增 **§5.3.1**）：只有两行都写成功才 `true`；`false` 必须区分**策略拦下**（`blockedBy:{gate:"simon",level:N}` + `degraded:["chat:blocked"]`，不写行）与**写库真失败**（`ErrorCode='CHAT_PERSIST_FAILED'` + `degraded:["chat:persist-failed"]`，回答照给）；**任何情况都不许回 `persisted:true`**（**A29 更正措辞**：应为「**失败/被拦时**绝不回 `true`」，成功落库当然回 `true` —— 原措辞被 harness-eng 读成"字段恒为假"，特此更正）。<br>③ 路由名以契约为准（不许自拟别名）；④ `chat.db` 以契约为准（独立文件 + `InitChatDatabase`，两套建表语句分开）。<br>同步改：§3.6（新增 `SIMON_BLOCKED`(403) 与 `CHAT_PERSIST_FAILED`）、§5.3、§5.3.1（新）、§5.4、§5.5、§4.5（路由冻结）、§4.2.1a（④）、§8.3（挡位 ≥2 预先禁用输入框 + 可执行提示）、§9-16/17/18、§10.2（三条键）、附录 A（A17 改 + 新增 A38）。<br>**验收归属确认**：A19/A20（DDL 落点/幂等/`rss.db` 无 `Chat%` 表/无对 t4 的编译期依赖）归 **t3**；A8/A21 归 **t4 实现 + t5 验证** | 裁决来源：captain C1。它纠正的是**契约自己的误判**（"提问只读"只对 `rss.db` 成立，对 `chat.db` 是写），并把 captain 早先那句"DDL 进 InitDatabase"作废（那句写在 I1/哈希验收之前）。本条是 §12 要求的**偏离记录**：没有它，t7 按 §12 判 `needs_revision` |
| 2026-（r1 冻结当日） | A20 | **按 verifier 的取证升级 A22 并订正一处立论**（证据：`tests/verification/NOTES-fixtures.md` + `fixture_probe.py toc-map`，真值由第三方库独立解析，不经 sip）：<br>① **A22 从「≈」升级为精确对账**：`len(chapters) == navPointCount − tocDropped` **且** `!= spineCount`；判据书改为**强判别**的 `中国哲学简史`(200/33)、`看图自学电吉他`(224/18)、`四世同堂`(110/125)；`毛泽东选集`(410/416，只差 6) 降为辅助；**`周恩来选集`(122/122) 禁止用作判据**（两法同数、无判别力）。层级断言写 `COUNT(DISTINCT depth) >= 2` **且** `MIN(depth) == 0`（§1.3.1 已钉死 0 基，避免"实现按 0 基、验收按 1 基"误判）。<br>② **订正 §1.4.1 N1 的立论依据**：原文写"真实书里目录顺序常与文档顺序不一致"—— verifier 实测 5 本 `outOfOrderFiles = 0`（目录序与文档序完全一致）。改成"这条规则是**防御性**的（零成本、挡畸形/机器生成的目录），**不许**写成真实书里常见"，并要求用合成夹具覆盖。<br>③ **新增可观测性字段**（否则对账数字对不上）：top-level **`tocDropped`**；逐行 **`zeroLength` / `depthFlattened` / `rawLevel`(仅 PDF)**。<br>④ 新增 **A39**：五条**零语料覆盖**路径必须靠合成夹具（锚点缺失 / `src` 不在 spine / 第 7 层压平 / HTML4 `<a name>` / 目录序≠文档序）。<br>⑤ §1.4.1 记下**真实书免费样本**：《看图自学电吉他》`OEBPS/text00002.html` 被"扉页/版权"两条无片段项指向 → `~n<k>` 消歧与零长度章可在**真实书**上一起验收 | verifier 的取证把 A22 从"看起来对"变成"能逐项对账"，并**证伪了契约里的一句立论**（N1 的"真实书常见"）—— 这类订正必须落到契约本体，否则实现方会照着错依据写注释（与 A14 的措辞纪律同源）。周恩来选集那条尤其重要：用它做判据等于**没有判据**（122 == 122），会把"建错了"判成"通过" |
| 2026-（r1 冻结当日） | A21 | **删掉 §11 的原第 13 条**（「不做"自动嵌入"」），并把它的内容明确指向三处规范位置（§1.5.1 边界① / §6.2 R5 / §6.6）；**§11 开头新增防误读说明**：「本节只列**真正不做**的事；凡'能力做了、但受成本或时机约束'的（如 PDF 的语义索引走 `--index` 按需），写在规范位置而**不**放这里」；顺带修好 §11-3 里指向被删条目的交叉引用 | captain 指令：「§11-13 那条'不做'请删掉 —— 别留一条与决策矛盾的条目」。核对后确认：**该条内容并不与决策矛盾**（它本来就是 captain 边界①"导入永不自动嵌入"），但**放在"不做"清单里会被读成"PDF 不能进语义检索"**，与用户拍板相反。所以采取"删条目 + 留内容 + 加防误读说明"：规则一个字不少（三处规范位置都在），清单不再有可被误读的条目。这与 A14、A20 是同一条纪律：**别把产品范围/成本约束写成能力缺失** |
| 2026-（r1 冻结当日） | A22 | **§5.4 再补严三条**（captain 点名 + 审计员独立核对原文后提出）：<br>① **四个写端点逐一枚举**（`POST /ask`、`POST /chat`、`DELETE /chat/{sid}?yes=1`、`DELETE /chat?yes=1`），别只挑一个过闸；<br>② **闸门必须在"写响应头之前"判定** —— 被拦时回**普通 403 JSON**，**不许**先 `SendChunked`/置 200 再在流里报错（否则前端把"被策略拦下"显示成"回答中断"，用户看到的下一步完全不同）；鉴权 401 同理；<br>③ 补**判据一句话**：「这次操作会不会**改动信息库本体**」—— 提问往 `chat.db` **创建持久条目**，不是"覆写一个游标"（阅读进度那类），因此与"抓全文""生成摘要"**同类、过闸** | 动因（审计员）：**t5 是按契约验收的** —— 若契约写宽松、实现走严版，**一次正确的实现会在验收环节被判不合格**，t7 还会看到"契约与实现不符"。这不是实现方的问题，是需求与裁决没同步。本条的四处严版（§5.4 / §9-16 / §9-17 / §9-18 保留）**已在 A19 落地**；A22 补的是"过闸点在哪一刻"与"完整枚举"，把"严"写到不会被执行歧义解释的程度 |
| 2026-（r1 冻结当日） | A23 | **澄清 I1 的边界，并给出三层判据**（审计员指出契约**自身**的表述会与严版裁决打架）：
① **§0.2 的 I1** 补一段：I1 的「只读」**针对用户信息本体**（`Items`/`Feeds`/标签），**`rss.db` 只是它当前的载体、不是定义**；**`chat.db` 的独立不变量是"让 I1 可被哈希证明"的证据手段，不是"对话记录不算写入"的依据** —— 恰恰相反，提问**创建持久记录**，按 §5.4 过闸。<br>② **§1.1 表格里 `chat.db` 那一行**同步补同一句（原来只写了"连接级保证 / 哈希证据"，读起来像"不算本体"）。<br>③ **§5.4 的判据升级为三层**（与"落盘到哪个文件"无关）：1) 改动**用户信息本体** → 过闸；2) 创建/修改**持久记录** → 也过闸（提问属此）；3) 只**覆写程序自动维护的游标** → 不过闸（阅读位置属此）。<br>④ 写进一条**区分**：阅读位置＝程序自动记的环境状态、覆写一次、**丢了无损失**；对话历史＝用户亲手留下的记录、**持续累积**、**用户会回来找它** —— 落盘强度相似、**性质相反**，故一个豁免一个过闸。<br>⑤ §9-18（`chat.db` 不可写仍要作答）**按要求保留不动**，并已注明"本行只在挡位 0/1 下可达" | 动因（审计员独立核对原文）：I1 与 §1.1 的措辞读起来像「**`chat.db` 不算信息库本体**」，于是"提问写 `chat.db` 要不要过写闸门"就说不清 —— **而 t5 会照这段判**。本次只**澄清边界、不改 I1 的含义**（提问路径对 `rss.db` 仍只读、A7 哈希不变照旧成立），并顺手把"哪类写入算不算"沉淀成可复用的三层判据，供以后新增端点直接套用 |
| 2026-（r1 冻结当日） | A24 | **两件**：<br>① **清掉 §1.5 里一条 A12 时代的残留**：原文「【必须】本轮**对 PDF 不要求页级关键词精度**……'落逐页文本'就是 §11-13 那个**被挡下的开关**」—— 在 A15/A17（逐页文本启用、页级关键词定位可用、§11-13 已删）之后**自相矛盾**。改为「**PDF 支持页级关键词定位**（`--locate`，永远免费、不需要索引）；语义检索另需 `--index`；精度只到**页**」，并注明替换了哪一句。<br>② **在文档开头加"30 秒自查"块**：以 §12.1 的**最大 `A<n>` 编号**为当前版标志；给出"关键字量级"判据（**故意不写死命中数** —— 那玩意儿每改一次就自我失效，第一版草稿写了 `PdfPig`(29) 而文内当时已是 32，故改掉）；要求**引用时带原句 + §章节号** | captain 的全队广播（自查方法）——**按他给的方法自查，立刻抓出了上面那条残留**：说明这套方法有效，所以把它**沉淀进契约开头**（而不是只留在广播里，广播会被淹没）。同时向 captain 报告：广播里"契约 1516 行 / A1~A14 / PdfPig 23 次 / §5.4 还是宽松版 / PDF 抽取正在改"这五处**仍是旧快照**（当前 A1~A24、§5.4 已严、PDF 抽取早已落地），避免全队按广播去等一件已经在做的事 |
| 2026-（r1 冻结当日） | A25 | **本批"一次做完"的索引卡 + 一条新规则**：<br>① **本批状态（全部已落）**：挡位口径严版 = **A19 + A22**（四个写端点过闸 / 挡位 ≥2 → 403 / 闸门在写响应头之前判 / 判据三层）；**PDF 页级文本与页级关键词定位 = A15 + A17 + A24**（用户拍板"pdf需要embedding" + "要不要顺手抽文本层 → 做"）；I1 边界澄清 = **A23**；偏离记录 = **A19**（依据 `Web.cs:494-500` 跨通道绕过 + `WebWriteAllowed` 的 "Reading still works"；判据＝是否改动信息库本体 / 提问是**创建库条目**不是覆写游标）。<br>② **新规则（写进 §0 自查块第 4 条）**：**正文（§0~§11）里不许出现被撤销旧表述的原文** —— 旧文只许留在 §12.1；否则 grep 自查会把"引用旧文说明已改"误判成"契约还是旧版"。<br>③ 按此规则清掉三处正文里的逐字旧文引用：§5.4 的"为什么"、§9-17 的删除说明、§1.5 的替换说明（都改成"撤销记录见 §12.1-A<n>"） | 动因：captain 按他发布的"grep 自查法"复核，报告契约仍有三处宽松旧文 + §12.1 无该条目。**逐行核对后的结论是：那三处（§5.4/§9-16,17）自 A19 起已是严版，§12.1 的 A19/A22 就是那笔偏离记录**；他 grep 命中的其实是**规范段落里对旧文的逐字引用**（§5.4 的"早先那句…正是同一种误判"、§9-17 的"…特例已删除"、§1.5 的"…替换 A12 时代那句"）—— **自查法与写法互相打架**。故本条把写法改掉：正文只留现行说法 + 指针，旧文集中在 §12.1。这不是形式问题：契约 §12 自带硬规则"没有记录的偏离 t7 判 needs_revision"，而"记录能被人一眼核到"同样得在文件里成立 |
| 2026-（r1 冻结当日） | A26 | **按改完的草稿对齐措辞 + 清掉一条真残留 + 把粒度说准**（用户拍板："改承诺"，草稿已改并通过 `node --check`，全文 grep「读不到里面的句子」「读得到书签、读不到句子」「没有文本层」**零命中**）：<br>① **§1.5 措辞纪律升级为三种说法**（粒度必须说准）：1) 「sip **已抽取该 PDF 的逐页文本**」= **逐份**实测，**不是**全局承诺；2) 「**这一份的某几页抽不出文字**」（影印页/扫描件）= **逐页**实测；3) 只提位置（"这一页我只能给你页码"）。**永不许**「PDF 没有文本层」这种格式断言。并注明**对齐源 = `docs/草稿-AI阅读悬浮球.html`**（PDF 场景引导语/演示回答/页图说明三处已改；扫描件场景的"读不到"保持不变），**不许再引用草稿旧文案**。② **清掉 §1.5 一条真残留**：「**PDF 文本抽取本轮不启用**：`PdfPig` 只用于书签…」→ 改为"**已启用**（逐页文本落 `PdfPages`），但产物只许进 `PdfPages`：不得写 `Items.Content`（动 `SourceHash`/打乱 R-b）也不得直接塞 `Chapters`"。（它逃过了前几轮扫描，因为词序是"文本抽取**本轮不启用**"而非"本轮不启用 PDF…"。）③ **逐页粒度**：§1.5.1 边界②要求整本 `pdfHasTextLayer` **外加** `textPages`/`emptyPages` 之类的可观测计数；§2.2 的 `textAvailable` 注明"整本 `true` ≠ 每页有字"；§3.4 新增【必须】**每页各自的 `textAvailable`**（该页 `CharCount==0` → `false` + `note`），并要求 API 能让 AI 说出草稿那句「有些页抽不出文字（影印图）—— 那几页我只能给你页码」。④ §8.4 偏离表第 2 条改为"**已不构成偏离**"（草稿已改完）+ 注明草稿是**有意入库的交付物**（不要删/移/当旧文案改）。⑤ §10.2 新增 3 条 i18n 键（逐页抽取完成 / 某些页抽不出文字 / 单页抽不出文字），并重申**三份语言文件同步**（`LangParityTests` 守 zh-CN ⊆ zh-Moe） | 用户拍板"改承诺"：草稿里那句「它没有文本层……读不到句子」曾是**用户可见承诺**，现在抽取已开，承诺必须跟着改 —— 这是 A7 时代把它当"决策依据"的那句话的**自然收尾**。**粒度是本条的关键**：开抽取之后事实变成"逐份可用、逐页可能为空"，任何全局断言（正向或反向）都会误导；所以契约要求整本/逐页两级可观测，让 AI 能像草稿演示那样说出"1–24 页抽不出文字" |
| 2026-（r1 冻结当日） | A27 | **挡位与「问 AI」口径定稿**（captain 提供可直接粘贴的最终文本；**他信中把它编号为 "A15"，但本文件 A15 已被「PDF 逐页文本」占用，故记为 A27**，以免变更记录出现重号）：<br>① **§5.4 整段定稿**：提问会往 `chat.db` 写**持久记录**，属"**创建库条目**"而非"覆写游标"，与「抓全文」「生成摘要」**同类，必须过写闸门**；四个写端点（`POST /ask`、`POST /chat`、`DELETE /chat/{sid}`、`DELETE /chat`）逐一过闸，挡位 ≥2 → **403 `SIMON_BLOCKED`**；【必须】**纯读端点不过闸、挡位 ≥2 仍须 200**（会话历史 GET / 目录 `/toc` / `/page/{n}` / `/chapters/{id}` / 四个只读 CLI 命令）；闸门**在写响应头之前**判，回 `application/json` 的 403，**不留半截流**（不许先开 `text/event-stream`）。<br>② **`persisted` 语义收窄为单一原因**（§5.3.1 改写）：**不存在"策略跳过落盘"分支** —— 挡位 ≥2 在写响应头之前就被拦，进不了流式阶段；`true` = 确实落库，`false` = **`chat.db` 不可写**（配 `ErrorCode` + `Status='error'`）；**`level` 由 403 响应体承载，不由 `persisted` 承载**；**不许为不可达情形留代码或注释占位**。<br>③ 【必须】**闸门只在入口判一次**：流进行中若挡位被其他通道升到 2（`simon.cs` 允许任意通道升档），**不中止**这一轮 —— 它已通过入口闸门，落盘照常完成；**规则只此一条，不在写库前重判**（避免长期维护一个几乎不可达的分支）。<br>④ §9-16/17 按定稿改写（表 16：只读命令与只读端点仍 200；写操作与「问 AI」一并 403。表 17：提问同样 403，**不存在"可用但不落盘"路径**）；**§9-18 保留不动**（挡位 0/1 可达）。<br>⑤ 附录 A 的 **A38** 同步改写（② 从"流中被拦"改为"挡位 2 下直接 403、不进流、无 `persisted` 字段"） | 依据 `Web.cs:494-500`：本项目**已经因为"同一操作仅因通道不同就裁决相反"付过一次代价** —— 挡位 2 原先只拦部分写操作，Web 仍可 archive/summary，于是成了一条绕过通道（审计 §2.3）。批准：captain（用户已知悉；该议题历经 v1~v5 五次表态，过程留档于**安全审计报告 §8**）。<br>**本条的净效果**：把 A19/A22 的"严版"从"两处分散的补丁"收敛成**一段自洽的口径**（谁过闸、何时判、字段表达什么、不可达情形不建模）。 |
| 2026-（r1 冻结当日） | A28 | **三件 + 一条**（captain 提）：<br>① **§1.6 补"区分两种写"**（可直接粘贴的那句）：`/ask` 过闸是因为它写 **`chat.db`** 对话历史，**不是**因为它会回填；`/ask` 路径**绝不**触发 `Chapters`/`PdfPages` 的抽取或写入，缺数据就 `degraded:["pdf:not-extracted"]`；**判闸在写响应头之前，取上下文/回填/发上游都在判闸之后**。（不写这条，实现者二选一**两种选法各错一边**：漏闸门，或"要写就顺手回填"→ A7 当场失效。）<br>② **新增 §1.5.3「大 PDF 首次抽取的预算与两段式返回」**：给出量级（书签 ≈ 毫秒；逐页文本 ≈ 每页几十毫秒 → 436 页 ≈ 数秒~20 秒）、**阈值（≥300 页 或 ≥30 万字）**、**两段式**（首屏 **≤2 秒**返回书签章 + `pdfTextState='extracting'`，文本在同进程继续抽）、状态字段 `pdfTextState` 的三种值（`ready`/`extracting`/`absent`）与各自的接口表现；§11-8 加注"**两段式是唯一例外：一次已开始的抽取在同进程跑完，不是队列**"；§9 增第 41 行；附录 A 增 **A40**（436 页真实书首屏 ≤2s + 状态流转 + 小书不出现 `extracting`）。<br>③ **§10.1 新增 R9「话术只有一个来源」**：CLI/Web/文档共用 §10.2 的键，contract 只写键名与语义；§10.2 增 `Extracting this PDF's text…` 键。<br>④ 自查块补一句：**行号（`:NNN`）不是版本标志**（它每次修订都漂）。 | 动因：captain 发现"懒回填挂第一次要目录"对 436 页 PDF 会产生**同步等待**，而契约没定预算 —— 这类"没写死就必然被做成最省事那种"的缺口，正是契约该堵的。<br>**顺带定位了反复出现的"旧文"来源**：`docs/安全边界审计-AI阅读定位与悬浮球.md:265` 的引用表里写着 `:973-974` + 旧句（那是审计报告对**当时版本**的摘录）。**审计报告是队友的交付物，本契约不修改它**；只在自查块里点明"行号不是版本标志、先确认你读的是哪份文件" |
| 2026-（r1 冻结当日） | A29 | **回应审计员的两处"契约内部不一致" + 采纳两条 observability**：<br>① **`chat:blocked` 分支**：审计员按（A27 之前的）§5.3.1 指出"契约要求该分支、实现侧却被告知不要写"。**核对结果：该分支已在 A27 删除** —— §5.3.1 现在只剩"写库失败"一种原因，并新增两条【必须】把洞**显式关掉**：(a)「**`persisted:true` 是正常路径**，成功落库时必须回 `true`，不许实现成'永远 false'」；(b)「**因此不存在'静默不落库'**：要么入口 403（不进流、无该字段）、要么正常落库 —— 流中途升档不中止本轮，**第三种情况被设计排除**，而不是"未覆盖"」。<br>② **A19 的措辞更正**：原文"任何情况都不许回 `persisted:true`"**不准**（已被人当字面要求引用），改为「**失败/被拦时**绝不回 `true`，成功落库当然回 `true`」，并在 A19 行内留更正说明。<br>③ **`BODY_TOO_LARGE`(413) 必须带 `limit`**（§3.6 新增，与文件的 `TOO_LARGE` 分开）—— 否则"有上限"不可断言。<br>④ **`blocked_cmd` 的 `what` 取值固定为文档化表**（§5.4 新增：`ask`/`chat-new`/`chat-del-one`/`chat-del-all`）：`WebWriteAllowed` 已经会 `SimonRecord("blocked_cmd", "web:"+what, level)`（`Web.cs:502`），`GET /api/simon` 回显最近 20 条（`Web.cs:4026-4043`）—— 这是一条**现成的、可审计的证据链**，配上固定取值就能断言"闸门真的在最前面拦下了"，而不只依赖 HTTP 状态码。<br>⑤ 附录 A 增 **A41**（事件链断言）、**A42**（413 payload 带 `limit`） | 动因（审计员独立复核）：他先按契约自查法读了 §5.3.1 与 §12.1(A19/A22) 再来核，确认那四处已落地 —— 这轮是**第一次"先读变更记录再核"的完整闭环**；他抓的两点里，① 是"契约比裁决更严"的镜像风险（若真按旧 §5.3.1 实现，A38 ② 必然失败、t5 会判实现不符契约），② 则说明**变更记录里的措辞同样会被当规范读**。本条的两条【必须】与措辞更正，目的都是让"照哪一句实现"不再有歧义。<br>附带核对了代码事实：`Web.cs:4045 HandleSimonLevel`（**网页可升档**、降档要真终端 + Web 口令）与 `Web.cs:4026-4043 HandleSimonStatus`（回显 20 条事件）—— 审计员给的行号略偏，实质正确 |
| 2026-（r1 冻结当日） | A30 | **新增「实现清单（索引式）」到附录 B**（12 行：要做什么 → 看哪节 → 验收编号），并借此**重申三条被反复按旧口径误读的现行要求**：<br>① **EPUB 目录优先级是 `ncx（主）→ nav（兼容）→ spine（退化）`**（A10 反转了 A3 的顺序）—— 不是 "nav → ncx"；nav 仍必须实现（合成夹具 A23）；<br>② **PDF 可以进块表** —— R5 早已从 A15 的"永不进嵌入"改为**"嵌入只能按需发生"**（`--index`/`--reindex` 是唯一调用点；导入与懒回填 0 次调用），PDF 的块带 `pdf:p<page>` 锚点、必须填 `ChapterId`/`ChapterSpan`/`AnchorState`；<br>③ **PDF 逐页文本已启用**（§1.5.1 / A15+A17+A18+A26），`textSource` 是**实现要求**不是预留位。 | 动因：anchor-eng 基于 **1218 行旧快照**报告"契约说 PDF 抽取不做、与 captain 拍板冲突"，**并进一步据旧口径列出了自己的缺口**（"契约要求 nav→ncx"、"契约要求 PDF 永不进块表"）。核对后：三项都是**旧口径**，现行契约与之相反 —— 若不纠正，他会照着**错的要求**实现，然后被 t5 按**现行契约**判不合格。故本条把三条现行口径连同索引清单一起摆到实现方一眼可见处；同时再次说明"以 §12.1 最大 `A<n>` 编号为当前版标志"（当前 A30） |
| 2026-（r1 冻结当日） | A31 | **两件**：<br>① **A19/A20 的 DDL 落点已被实测闭合**（anchor-eng，隔离实例 + 只读连接回读 `sqlite_master`）：`rss.db` 里 `Chat%` 表数 = **0**；`chat.db` 有 `ChatSessions(10 列)` / `ChatMessages(13 列)`，列名与 §4.2 冻结清单**逐字一致**（含 `UNIQUE(SessionId, TurnIndex, Role)` 与两个索引）；连跑两次启动（全新库 + 老库）**幂等、无报错、schema 不变**；`InitChatDatabase(chatDbPath)` 是独立函数、语句**没进** `InitDatabase`、调用点在**启动引导段**（照 `TelemetryService.Init` 先例）。验收分工照 §4.2.1e：**t3 认领 ①②③④ / A19 / A20**，功能验收（落库、`turnIndex` 连续、断连 `aborted`、`DELETE ?yes=1`、锚点活一轮）归 **t4 实现 + t5 验证**（A8/A21）。<br>② **自查块新增一条：通信也会过期** —— 消息里引用的"契约说 X"**同样要回文件核**（以 §12.1 最大编号为准）；「消息不是契约，文件才是」。另修正附录 C-2 里的旧措辞（原写"①按页抽取 ②这一份没有文本层"，改为 A26 的**三种**粒度说法）。 | 动因：anchor-eng 引**我早先的一封邮件**（写于用户拍板**之前**）当成"契约仍说不开正文抽取"，并据此**暂停 PDF 相关实现**等待收敛。这暴露了一个新失败模式：**旧的不只是副本，还有消息**。本轮把"以文件为准、消息不算契约"写进自查块，并把 DDL 落点的实测证据记入本条 —— 它是 t3 **第一块被真正闭合**的验收（A19/A20） |
| 2026-（r1 冻结当日） | A32 | **① 再次闭合"G"（PDF 逐页文本）：答案是"做"，契约早已落地，不存在待拍板项** —— 现行依据：§1.5（392 行「PDF 文本抽取已启用」）、§1.5.1（441 行 `PdfPages` + 四边界）、§3.4（每页 `text`/`textAvailable`）、§3.5（PDF 页级关键词 `--locate`）、§6.2 R5（1368 行：嵌入只能按需）、记账 A15/A17/A18/A26。<br>**② 新增一条防错**（§1.3）：**`<anchor>` 是 `src` 的片段 id（`#sec2`），不是 navPoint 的 XML `id`（`np-3`）** —— navPoint id 只活在目录文件里、目录一重建就变、指向不了正文；用它会直接违反 I4（引用必须跳得回去）。<br>**③ 自查块补一条推论**：**遇到"两处说法相反"时不要停下工作等拍板** —— 先回文件核；本轮两次"等你收敛"其实都是邮件旧版，**文件里从来没有冲突**。<br>**④ 记下实现方本轮清单的口径校正**：块锚点要覆盖 **R1/R2/R3/R5/R6/R7/R8**（不是 R1/R2/R3/R5/R6 —— R7 是"PDF 以页为块边界"、R8 是"三列位置回指"，都是 A17 新增的） | 动因：anchor-eng 第三次把"G"报成待收敛项，依据的是**我早先那封邮件**（写于用户拍板之前）。两次停顿（PDF 抽取、块表禁令）都源于"照旧邮件等指令"。所以本条不仅闭合结论，更把**排查动作**写进自查块：先核文件、别停等。第 ② 条来自他自报的实现偏差（`~n<k>` 消歧用了 navPoint id）—— 这类"命名像、语义不同"的错最容易一路带到引用跳转才发现 |
| 2026-（r1 冻结当日） | A33 | **在文档开头新增「本轮已拍板项」一览（5 条）**：PDF 逐页文本=做 / PDF 可按需进块表 / 草稿承诺已改 / 挡位 ≥2 拦问 AI 且纯读端点仍 200 / PDF 仍不做的三件（OCR、章节猜测、段行级精度）。每条带 § 号与 A 编号，并写明"**这几条都已落进本文件，不存在等某人裁决的悬置状态**；若谁要推翻，按 §12 记一笔偏离再说"。 | 动因：anchor-eng **第四次**把"PDF 抽取"报成待收敛/待裁决项（并说"契约落后于裁决"）。核对后：契约自 A15/A17/A18 起就与裁决一致，**落后的是他手上的快照**。前几轮我用"给行号 + 讲清哪封邮件作废"来回应，仍被循环；本轮改为**结构性止血**：把已拍板项提到开头，让任何人**不读 2000 行也能看到结论**。这比继续解释更省全队的时间 |
| 2026-（r1 冻结当日） | A34 | **两件**：<br>① **§1.4.2 的 zip-bomb 判据补一句【必须】：那个"且"是合取（AND），不是只看压缩比** —— 纯文本 XHTML 正常也能压到几百比一（重复标记/空白），**单看 ratio 会误杀一部正常的大部头**（20 MB 正文压到 400:1 完全合法）；并写明两个长度都来自**中央目录**（`ZipArchiveEntry.Length`/`CompressedLength`），所以判定可以放在 `Open()` **之前**。<br>② 附录 B 实现清单第 4 行补上**DDL 的直接指针**（"`PdfPages` 的 DDL 可直接抄 §1.5.1 边界②（PK `(ItemId, Page)` = 一页一行）"）。 | 动因：anchor-eng 交底时说他给 `ReadZipText` 加的判定**只用 `Length / CompressedLength`**，并再次索要 `PdfPages` 的 DDL（该 DDL 自 A15 起就在 §1.5.1 边界②，第 490 行）。①是真实现风险：合取被读成了单条件，会误杀正常书；②是"指针不够尖"——清单只说"看 §1.5/§1.5.1"，他便没找到那段 SQL。两处都是**文档可读性**问题，不是实现方不认真 |
| 2026-（r1 冻结当日） | A35 | **给"章文本从哪来"定死两条来源 + 一条禁止**（§6.3.1 第 1 步扩写）：**① 主路径 = `Items.Content` 切片**（偏移是 §1.2【必须】列，所以顺序是"先建/重建章节（带偏移）→ 再做原地回填"）；**② 兜底 = 回原文件按锚点切片**（仅当偏移缺失：EPUB 用与 N1–N5 **同一套**锚点逻辑；**PDF 直接拼 `PdfPages`**，不需要偏移也不需要原文件）；**两条必须经同一个"取章文本"函数、产出同一份规范化纯文本**（否则同一 `Snippet` 会在两条路上得到不同匹配）。**③ 两条都不可用**（文件被删 + 偏移缺失）→ 保持 `AnchorState='unknown'`、可作材料不产出引用、等 `--reindex` 重建，**不许猜**。并写明**回填触发点不在启动路径**（拿到该书章节模型之后、只对那一本；启动时不做全库扫描）。<br>另确认收到：`VectorsChunks` 的 `ChapterSpan`/`AnchorState` **两列已加入同一轮迁移**（`try/catch` 风格、与 `Chapters`/`DbMeta` 同批），旧库既有块落 `AnchorState='unknown'` —— 正是第 2 步语义 | 动因：anchor-eng 指出他的 `Chapters.CharStart/CharEnd` **恒为 NULL**（偏移从未计算），于是 §6.3.1 第 1 步"逐章取切片"**取不到切片**，并在"等偏移算出来"与"改成回文件取文本"之间**要求指定**——这正是契约要求"不许在两条路之间自选"的那一步。本条给出优先级 + 一致性要求 + 禁止项，把选择权收回契约；同时把"偏移必须先算"这个隐含前提显式化（它本来就是 N6 的要求，但没人把它和回填的顺序关系写出来） |
| 2026-（r1 冻结当日） | A36 | **⚠️ 偏离记录（captain 批准）：`firstBlock` 本轮不填充、`CharStart/CharEnd` 恒 NULL。**<br>**偏离项**：§2.2 的 `chapters[].firstBlock` 写成【必须】，但实现只声明/读回/输出、**无赋值**（恒空串）；同批的 `CharStart/CharEnd` 也**恒 NULL**（偏移从未计算）。<br>**依据（三条，captain 已核实）**：① **功能完整** —— 单章接口 `GET /api/imports/{id}/chapters/{chapterId}` 走"回原文件按锚点切片"，**不依赖 `firstBlock` 也不依赖字符偏移**，所以点章跳页与 AI 读章都能工作；② **`firstBlock` 是优化不是正确性** —— 它只服务 §2.4"整书一次拉"那条省往返的路径，缺它的代价是**请求变多**，不是功能缺失；③ **成本收益** —— 正确填充须把 `ExtractChapters` 重构成"一次开包、批量算"（410 章逐章开包不可接受），属**新代码 + 新验收**，而当前实现方上下文接近耗尽，硬塞未验证的性能优化风险高于收益。<br>**批准人**：captain。<br>**连带修改（本轮已做，否则契约自相矛盾）**：<br>a) §2.2 `firstBlock` 行标注"**本轮恒空**；消费方请用单章接口"；<br>b) §1.2 的 `CharStart/CharEnd` 列注恒 NULL；<br>c) **§2.4 两条路径重排**：**本轮有效路径 = 单章接口**（一章一次请求），"整书一次拉 + 单调匹配"标注**待 `firstBlock` 落地**，并明确**不许为它留半截代码、不许因此猜章节边界**；<br>d) **N8 覆盖不变量改按"文件空间"表达**（本轮可验收）：EPUB = 每个 spine 片段的锚点序列无缝切完该片段（文本级复核用"各章文本按序拼接 ≈ 该片段文本"）；PDF = 页区间相邻相接且覆盖 `[1,pageCount]`；**"`CharEnd == 下一章 CharStart`"那种基于字符偏移的形式明确标注"待偏移落地"**；<br>e) **§6.3.1 第 1 步的数据来源优先级翻转**：本轮**唯一可用**的是"回原文件 / `PdfPages`"路径（原 A35 的"主路径 = Content 切片"标注为**待偏移落地**）；<br>f) **A28 的验收判据同步改写**（改为文件空间，避免 t5 照旧判据把一个"本轮不适用"的项判红）。<br>**对下游的净效果**：t4 的整书渲染按"一章一次请求"实现；t5 的 A28 用文件空间判；`--reindex` 仍是"未锚定块"的唯一重建路径。 | 动因：captain 主动核实实现后**接受偏离并记账**（而不是要求硬修），理由充分且明确；同时点出"`CharStart/CharEnd` 恒 NULL"这一事实。**但该事实的连带影响比 `firstBlock` 本身大**：N8 与 §6.3.1 主路径都建立在偏移之上 —— 若不改，契约会出现"要求一个恒 NULL 的列做判据"的自相矛盾，且 t5 会照旧判据判红。故本条把连带项一次改完，并**保留偏移落地后的进阶判据**（不删，只标注时点） |
| 2026-（r1 冻结当日） | A37 | **两件（均来自审计员的审计视角，我按"可审计性"补齐）**：<br>① **§1.5.3 补"有界"四条【必须】**：**总时长上界 ≤10 分钟**（超时停在部分结果、保持 `extracting`，不许标 `ready`、不许丢已抽页）；**内存上界**（逐页处理、**逐页落库**，不许整本攒到最后一次写）；**磁盘上界**（整本正文 ≤50 MB，超出按页截断 + `DbMeta.pdfTextTruncated`）；**并发上界**（**全局同时只跑一本**，第二本排队/按需触发，不叠加）。<br>② **A7 改为双向断言**：除了"`rss.db`(+`-wal`/`-shm`) 三个哈希提问前后完全相同"，**还必须断言 `chat.db` 变了**（至少多两行）—— 否则无法区分"正确地只写了 `chat.db`"与"什么都没发生"。 | 动因：审计员说他将按**资源耗尽/DoS** 视角验 §1.5.3，**不只验字段写没写**；并强调 A7 是"harness 对库只读"**唯一可证明**的形式。核对后确认：§1.5.3 原先只有"≤2 秒首屏""单本一次""失败停部分"三条，**没有时长/内存/并发上界** —— 一条"用户点一下就能触发"的重活缺上界，正是 DoS 面。A7 原先只测单向（主库不变），是**半条断言**：一个"什么都没做"的实现也能通过。两处都是"审计视角倒逼契约变严"的例子 |
| 2026-（r1 冻结当日） | A39 | **① 全量校准附录 B 的代码行号**（captain 批准这条"改完代码回来校准"的规则 + 审计员按**符号名**复核后发现我引的行号已过期）：<br>· 契约里 **43 处** `文件:行号` 引用全部换成当前值（`sipcore.cs` 从 8330 涨到 **10190** 行、`Web.cs` 4550→**4698**、`web/app.js` 2350→**2589** —— t3/t4 落了大量代码）；<br>· **附录 B 改为"符号名优先 + 行号列"**：新增一行纪律「**查这一列请按符号名查，不要按行号查**」（采审计员的纪律 —— 他复核时按符号名查、不按行号），行号列只作定位加速，并在表头注明**校准时点与当时的文件规模**；<br>· 修掉一处**引错文件**：`ImportItemDelete` 在 **`Web.cs:3672`**，不在 `sipcore.cs`（我原先写 `sipcore.cs:3526`）；顺带记录它的签名已含 `ChatSessions` 回传（即 §1.7"删书不级联删对话、但报出会话数"已落地）；<br>· 记录一处**已完成**：`Web.cs:3197` 的「没有章节模型」注释**已改**为"电子书：**有章节模型**…本轮已改"（附录 B 原列它是待办）。<br>② **实现清单加进度标注**（✅/⬜）+ 明确"自称已做 ≠ 已验证"（呼应 A38 的证据纪律）。<br>③ 记录审计员报告的处置：他把那张"契约原文对照表"加了**摘录时点（A18 之前）**、顶部加**引用纪律**（引用必须标时点 + `A<n>`），并自述"**我当时的判断在那一版契约下是对的 —— 是依据换了**"（`chat:blocked` 从"未决分歧"改标"已解决"）。 | 动因：**契约自己定了"改完代码要回来校准行号"，而 t3/t4 改了约 2000 行代码、我一次都没校准** —— 于是审计员按符号名复核时发现我给的 `WebWriteAllowed:484` / `SimonRecord:494` 等全是旧值（当前 **492 / 502**）。这条既是履约，也是"行号不是版本标志"的**第三次实证**：这次漂的不是契约版本，而是**它指向的代码**。附录 B 改成符号名优先后，同类漂移只需更新一列 |
| 2026-（r1 冻结当日） | A38 | **三件（captain 反馈 + t3 收口后的证据纪律）**：<br>① **确认产品取舍：升档不中止本轮、落盘照常完成**（A27 的规则**维持**，不改）。理由（captain）：用户**已经看到了那半截回答**，丢弃历史不会把回答收回来，只会让他事后找不到自己刚读过的东西 —— **纯净损失**；且这一轮在入口已按当时挡位获得授权，升档不授予新能力、不绕过任何闸门。<br>② **自查块新增"绝对化措辞要回读原文"**（captain 提）：凡在 §12.1 摘要里看到「任何情况 / 永不 / 一律 / 绝不 / 只有」，都去 § 正文回读一遍 —— **摘要容易把带条件的规则压成无条件**；反过来**写变更记录的人也【必须】给绝对化表述带上条件**。**我按此扫了一遍 §12.1**：全部 19 处绝对化表述里，只有 A19 的"挡位 ≥2 **一律** 403"值得加限定词（**限四个写端点**；纯读端点仍 200 —— A27），已补。<br>③ **验收证据纪律（新增到附录 A 前言，【必须】）**：引用某条验收为"通过"**必须附命令 + 输出**；**存在测试文件 ≠ 测试通过**；**从未执行过的用例必须标"未验证"**。据此把 **A5 / A6 / A22 / A39 标为"本轮未验证"** —— 它们由 `tests/Sip.Tests/ChapterTests.cs` 与 `SseStreamingTests.cs` 承载，而**这两个文件一次都没跑过**（captain 已按此收口 t3，并声明"未经执行验证，不得当作已通过的证据"）。复核者看到"通过但无证据"按**未验证**计。 | 动因：captain 收口 t3（实现方上下文耗尽、交付后未能自行标记），并确认两条测试文件从未执行。这条**同时暴露了契约的一个缺口**：附录 A 只写了"怎么验"，没写"**什么叫验证过**" —— 于是"写了测试"与"测试通过"在报告里可能长得一样。补上证据纪律后，A5/A6/A22/A39 从"看起来有覆盖"回到"如实未验证"，这正是本契约反复强调的诚实口径 |
禁止的结果是 PDF 永远进不了语义检索，而那正是用户要的能力。用户同时点名三件必须写进契约的事：块表**必须带回指列**（否则命中说不出"第几页"）、**成本量级要让用户有数**、**UI 必须告知索引状态**。不变的三条（独立页表不塞 `Items.Content` / 懒回填补填幂等 / 扫描件 0 字是正常结果、抽取与章节同一套判据）原样保留 |

| 2026-09-25 | A40 | **两件范围变更（用户拍板）**：<br>**① 问 AI 扩展到 RSS 文章**（原契约把 `/ask` 限定在「本地导入项」；`§7.2` 的「会话严格限于一本书」随之改为「**严格限于一个阅读项**」）。<br>· **数据模型不用改**：本地导入的书与 RSS 文章**同在 `Items` 表**，靠 `Feeds.FeedUrl='local://import'` 区分（`sipcore.cs:5975 ImportBookOf` 就是这个判据）。原契约把"书"当默认、把文章当"非导入项"报 404，是把**载体**当成了**定义**。<br>· **四层梯度对文章的含义**：第 1 层划词（不变）→ 第 2 层「本段落」= 划词所在段落，无划词时退化为整篇（文章没有 `Chapters` 行，**不建**、也不许拿 h1~h3 猜小节：§2.3 那条禁令同样适用）→ 第 3 层「整篇」= `Items.Content` 净化后的纯文本 → 第 4 层全库（不变）。<br>· **章节级 API 对文章仍是 404 `NOT_IMPORTED`**（`--toc` / `--chapter` / `--page`）—— 扩的是**问答**，不是章节模型。<br>· **响应新增 `locType`**（`book`/`article`）与 `ArticleNoText`(409 `ARTICLE_NO_TEXT`)：源只给标题摘要、`Items.Content` 为空时，**如实说"这篇没有正文可读"**，不许让模型拿着标题硬答（同 §1.5 的措辞纪律：别把"这篇没有正文"说成"我读不到"）。<br>· **索引口径**：文章继续走**既有**的 `--index`（标题向量 + 长文块），**不新增**嵌入调用点（R5 不变）。<br>**② Web 端可配置 AI（含 API Key）** —— **越过一条原契约的硬边界**，故按偏离记账：<br>· 原边界（`§8.3` 与终端口径）：**key 只在真终端输入**；理由是"终端是别的进程读不到的带外信道"。现改为**开关式**：`AiConfigWebWrite` 默认 **false**（保持原加强），置 true 才开放。<br>· **安全要求【必须】**：(a) key **只经 POST body 进、永不出**（任何响应/日志/错误体里不得回显，只回"已设置/未设置"）；(b) **改 key 与改端点/模型分级** —— 改端点必须带**确认参数**且落 `simon_events` 审计（`type='ai_endpoint_changed'`），因为"端点"决定**问题与文段被发到哪里**；(c) 端点必须 `http(s)`；**`ValidateFetchUrl` 的 SSRF 规则在这里【不适用】** —— 它拦 loopback 与私网，而本地 Ollama/LM Studio（`http://localhost:11434/v1`，正是 `EmbeddingCfg.ApiEndpoint` 的默认值）是本项目的核心用例，照搬会把"用本地模型"直接判死。**判据的区别要写清**：抓取正文面对的是**文章里的 URL**（不可信输入），配置端点面对的是**已认证用户亲手填的地址**（可信输入），两者不是同一类信任假设 —— 所以这里只校验 `http(s)` + 主机非空 + 主机与"本机/私网"的关系**只作提示不作拦截**；(d) 挡位 ≥2 时改 AI 配置**过写闸门**（这是改配置，比写聊天记录更重）；(e) 界面**必须**写明"改端点会把你的问题和文段发到该端点"。<br>· **理由**：用户按 `sip --init` 是真终端专属路径，而他第一次上手就走到了"web 上问 AI → 报错 → 不知道 key 没配"这一步；把配置留在终端等于**把唯一的上手路径设在一个他不在的地方**。风险（会话被抄走 = 能改 key/端点）由"密码 + 会话绑浏览器指纹 + 开关默认关 + 写闸门 + 审计"承担。<br>· 原 §8.3「目录/跳页不依赖 AI 配置」**不变**：没配 AI 也必须能看目录、能跳章。 | **批准人：用户**（2026-09-25，原话「要不试一试web端配置」，并选定「做：文章也能问」）。<br>①②**同源**：用户的诉求是「随时随地打开 AI 窗口」—— 文章里问不了、以及问了只报"这本书不在本地导入里"，都是这个诉求的直接失败。① 修的是**能力范围**，② 修的是**上手路径**，两者都不改 `rss.db` 只读、不改四层梯度、不新增嵌入调用点。<br>**本条不撤销 A27/A19 的写闸门口径**，只是在同一判据（"改动信息库本体 / 创建持久记录"）下把 AI 配置归入**过闸**那一类 |

| 2026-09-25 | A45 | **前端"当前节"送不出可回查的章节 id —— 修 `FindChapter` 的回查规则**（用户实测暴露：在《咸的玩笑》正文里划词提问，资料区全空，AI 答"正在读第 1 章，但该章文本未成功载入"）。<br>**现状（已取证）**：<br>· 服务端给前端的 `GET /api/imports/{id}/text` 是**整本书的 HTML**；前端 `splitBookSections` 按 `h1~h3` 或每 ~12000 字**自己切节**。这套"节"与 `Chapters` 行**不是同一个东西**（契约 §2.4 的整书路径遗留下来的）。<br>· `aiBuildAnchor` 送 `chapterId = secs[i].title`，而切节在无标题时兜底成 `第 N 节`；服务端 `FindChapter(list, spec)` **只认**两种：精确 `ChapterId` 相等，或 `#<ord>`（`sipcore.cs:6513`）。`"第 1 节"` 两者都不是 → `current` 为 null → 本章层空 → 资料区只剩划词（有划词时）或全空。<br>· 实测数据（《咸的玩笑》#19）：目录 7 章，`epub:0~front`(ord1, front, **title 为空**) / `epub:5`(ord3, "正文一") …… 而用户划的那句属于 **`text/part0005.html` = `epub:5` = ord 3**，前端却报"第 1 章"（`第 N 节` 的 N 来自**切割序号**，与 `Ord` 无关）。<br>**修法（三件，都不改 §2.4 的整书路径）**：<br>① **前端送可回查的锚点**：`chapterId` 一律送 `#<节序号>`（服务端已支持的形式），并**另外**送 `sectionLead`（当前节开头的 ~200 字纯文本）作为兜底线索。<br>② **服务端回查规则扩成四级**（顺序固定）：精确 `ChapterId` → `#<ord>` → **标题相等**（去空白后比较；同名取 Ord 最小者）→ **`sectionLead` 内容定位**（单调扫描各章文本，命中即取那一章）。四级都不中 → 保持现有的 `chapter:none` 降级，**不许猜**。<br>③ **界面不许再显示"第 N 章"这种由切割序号推出来的章号**：位置标签用 `aiWhereNow()` 的结果（"第 N 节"或章名），或等服务端回查结果回来再显示真实章名。契约 §8.1 的"章节读数"必须以 `Chapters` 为准，不许以切割序号为准。<br>**验收 A46**：在《咸的玩笑》正文任意处划词提问 → `snapshot.chapterIdUsed` **非空且等于 `epub:5`**；快照 `layers` 含 `chapter`、`degraded` **不含** `chapter:none`；回答能引用该章原文。反向：目录**确实为空**的书（`Chapters` 0 行）→ 仍给 `chapter:none`，不许编一个章出来。 | **动因：用户实测**。这不是"没索引"也不是配置问题（他的 `ai_config.json` 与 key 都已配好，模型确实在作答）——是**划词段之外的第二层上下文整个拿不到**，而那一层正是"AI 能说出这段在讲什么"的依据。取证方式：只读数据库副本拿 `Chapters` 行 + 用 Python `zipfile` **独立解包 EPUB**搜 `长顺` 落在哪个 spine 文件，两边对照（不经 sip 自己的解析器）。<br>**与 A36 的关系**：A36 记的 `firstBlock` 恒空、`CharStart/CharEnd` 恒 NULL 是**另一件事**（那是块锚点回填），本条不依赖它们 —— `sectionLead` 是前端手上已有的文本，不需要服务端先算出偏移。故本条不解除 A36。 |

| 2026-09-25 | A47 | **两处"看得见却读不到"的真缺陷（用户实测暴露）**<br>**① 文章的第 3 层（整篇）恒空。** `SearchBook` 是"遍历 `Chapters` 逐章找关键词命中"实现的，而**文章没有 `Chapters` 行**（A40① 明确不建）→ 文章的第 3 层一条都搜不到。实测症状：用户在文章里问「看看正文？」，模型**如实回答**"资料区里没有可引用的正文"。<br>**修法**：文章在 `SearchBook` 里走单独一条 —— **整篇就是第 3 层**。命中关键词给命中处片段（`reason="article"`），一个词都没命中就**给开头**（`reason="article-head"`）——「没有关键词命中」≠「没有正文」，只有后者才能说读不到。命中的 `ChapterId` **必须是空串**：引用构造那段要求 ChapterId 能回查到章节行，空串会被跳过 → 文章内容**只当材料、不产出跳不回去的引用**（I4）。另：文章的 `Items.Content` 是**原始 HTML**，进资料区前必须 `StripHtml`（否则模型看到的是标签噪音）。<br>**② `snapshot` 恒为 `{}` —— 可解释性整块失效。** `Snapshot`（以及 `Hit`/`Cite`）是**字段**型类，而 `Web.cs` 的 `WriteJson` 与 `PersistAssistantTurn` 都用**默认** `JsonSerializerOptions`，而**默认不序列化字段**。后果：非流式响应里 `snapshot` 是空对象、落库的 `SnapshotJson` 也是空对象 → 前端「检索详情」永远空白，而"这一轮为什么答不准"**只能**从快照看出来。<br>**修法**：新增 `SnapshotJson(Snapshot)`，用 `IncludeFields = true` 单独序列化成 `JsonElement` 再交给 `WriteJson`；落库那条同样加 `IncludeFields`。**不改 `WriteJson` 的全局选项** —— 那会连带改变所有匿名对象的输出，影响面不可控。<br>**验收 A48**：文章上非流式提问 → `snapshot.layers` 含 `book`、`bookHits ≥ 1`、首个命中 `chapterId` 为空串、`snippet` 里**不含 HTML 标签**；`degraded` 含 `chapter:none`（文章本就没有章节）。书（EPUB）上提问 → `snapshot` **非空**且 `layers` 含 `chapter`。 | **动因：用户实测**。① 是 A40① 的**实现缺口**（契约写了"整篇"这一层，实现漏了文章分支）；② 更隐蔽 —— 它让**所有**可解释性信息静默消失，而且 `{}` 看起来像"这一轮没有降级"而不是"序列化没生效"，属于"沉默的错"。两条都是**先被用户的真实提问撞出来**，再被探针固化成断言（`article_config_probe.mjs` 的 A43-1 段与 `harness_e2e_probe.mjs` 的第 8b 段）。<br>**方法论教训（写进来免得重犯）**：我第一次断言①时用的是**假 LLM 日志里找标记**，而假 LLM 只记 `promptHead`（前 120 字符）、"资料区"在后面 → **假阴性**，白跑一轮。判据要选**能完整观察到目标**的那个面：落库的 anchor / 响应里的 snapshot，而不是被截断的日志摘要。 |

| 2026-09-25 | A48 | **模型自选读物：多轮读取协议（用户拍板）** —— 让 AI 自己决定"读到哪儿"，而不是由服务端给死一个片段长度。<br>**原话**：「ai能自己多读几轮 但是可以选择一口气读取全文（但是对epub来说非必要绝对不可能 读一整章节还差不多）」。<br>**协议**：模型在回答里**独占一行**写控制指令，服务端截住它、按指令重组上下文、**再问一次**；指令本身不进回答、也不推给前端。<br>· `【读全文】` —— **只对文章有效**。EPUB 请求这条会被**拒绝**并记 `readRounds.why="book-not-article"`。<br>· `【读本章】起-止` / `【读本章】` —— 读第 2 层的行区间 / 整章（仅 EPUB/PDF 路径）。<br>**三条硬边界【必须】**：<br>① **EPUB 绝不整本进资料区**。这是用户点名的绝对禁止项 —— 读整本既超 §6.5 预算也没有必要；上限就是**一章**（第 2 层本来的口径）。<br>② **轮数上限 `AskMaxReadRounds = 3`**。每多一轮 = 一次真金白银的上游调用，绕圈子的模型可以无限"再读一点"。<br>③ **"读全文"是放宽片段截断，不是取消预算**：文章默认给 `AskArticleDefaultChars = 3000` 字符；`【读全文】` 后放宽到 `AskArticleFullChars = 8000`，**仍受第 3 层的 2000 token 预算约束**。<br>**为什么是文本指令而不是 function calling**：本地模型（Ollama 小模型）大多不支持 function calling，用它会让这能力变成"只有云端大模型能用"；文本指令任何 OpenAI 兼容端点都能跑。代价只有一处 —— 流式推送时必须**截住指令**（`DirectiveFilter`）。<br>**实现要点（踩过坑，必须照此实现）**：<br>· 标记用**中文方括号** `【读全文】`，不用 `@@读全文`。第一版用 `@@`，模型把 `@@` 当正文照抄，而过滤器在**第一个 `@`** 上就判"不是前缀"直接放行 → 指令原样漏给了用户（探针实测：客户端收到正文 `@@读全文`）。<br>· 过滤器的判据是「行首攒到的字符**只要还是某个标记的前缀就继续攒**」，不是"是否等于标记"。前缀判定写错 = 过滤器形同虚设。<br>· 正文里出现 `【` 只会多缓冲一个字符，下一个字符一到就判定完毕 —— **正常回答仍是逐字流出**，不会因这个机制退化成一次性输出。<br>· 行内出现的标记（不在行首）一律当正文处理：宁可漏一条指令，也不要把用户的回答吃掉。<br>**验收 A49**：用假 LLM 起 `--directive-once 【读全文】` → ① 客户端收到的正文里**不含** `【读全文】`；② 上游 `chat_request` **≥ 2**（真为指令重读了一轮）；③ 第二轮提示词里出现"已按你的要求加长"。三条**互为正反面**：只证①可能是"指令被吞了但没重试"，只证②可能是"重试了但指令也漏给了用户"。 | **动因：用户拍板 + 实测**。用户先问「如何让 ai 能选择片段去读 自己选择读到哪里」，并同时给出 EPUB 的硬边界。<br>**与既有条款的关系**：本条**不推翻** §6.5 的预算梯度，而是给它加了一条"模型可以主动要更多"的通道；`§7.3 检索每轮重算` 在多轮下同样成立（每轮都重取材料，不复用上一轮的命中）。<br>**成本可见性**：每轮的"要了什么/给了没给/为什么没给"都进 `snapshot.readRounds` —— 用户看到回答变长时，能查出多花的 token 花在哪。这是 A38「可解释性」在多轮场景的延续。 |

| 2026-09-25 | A50 | **「重启后聊天记录就没了」的真因：历史端点对文章 id 回 404**（用户报障）。<br>**现象**：用户每次重启软件，AI 面板的历史就是空的。<br>**取证（两步，缺一不可）**：① 只读 `chat.db` 副本 → **数据一条没丢**（5 个会话、20 条消息都在，最近一条是用户最后的「尝试看一下全文」）；② 打 `GET /api/imports/{id}/chat` → 对**文章**的 id（用户的会话挂在 itemId=2，即那篇《杂鱼ai…》）直接 **404**。<br>**根因**：`HandleChatList` 用 `ImportBookOf` 做存在性判断，而它**只认 `local://import`**（那是审计约束 #5 的安全边界，不能放宽）。会话表里 `ItemId` 就是普通 `Items.Id`，文章和书一视同仁，**但这个端点只认书** → 前端拿不到任何历史。<br>**修法**：判据统一走 `ItemTitleFor(itemId)`（书**或**文章），与 `HandleChatNew` 同一条。<br>**⚠️ 同一个坑犯了两次**：A40① 时先修了 `POST /chat`，这一轮才发现 `GET /chat` 漏了 —— 症状还是**误导性**的（"记录丢了"而不是"接口不认这个 id"）。所以这次把判据**只留一处**（`ItemTitleFor`），并逐端点核对了其余四处：`HandleChatMessages` / `HandleChatDeleteOne` 走 `SessionOwner`（按会话里的 itemId 比，不受影响）；`HandleChatDeleteAll` 按 `ItemId` 删，安全。<br>**验收 A51**：`GET /api/imports/{文章id}/chat` **必须 200**，且返回该文章的会话数与消息数（实测用户库：`#2 [article] 会话=4 当前会话消息=4`）；同时 `#19 [epub]` 也必须能读出历史。**反向**：一个真的不存在的 id 仍须 404。 | **动因：用户报障**（"每一次重启软件聊天记录就没了"）。<br>**这条的诊断价值**：用户描述的是"数据丢了"，而**真相是数据一直在**（第①步取证直接否掉了"没存"这个方向）。若不先做"库里有没有"这一步，就会去查写入路径 —— 那是一条完全错误的路。**先分清"没存"还是"没读出来"**，是这类报障的第一个分叉。<br>**与 A40① 的关系**：A40① 把"会话粒度 = 阅读项（书或文章）"写进了契约，但**实现只落了一半**。契约里写了"会话的粒度是阅读项"并不等于每个端点都照做了 —— 本条是那次变更的**收尾**。 |

### 12.2 参数记账

| 参数 | 契约值 | 实际值 | 原因 |
|---|---|---|---|
| 历史窗口 K | 6 | | |
| 总上下文预算 | 16000 token | | |
| SSE 心跳 | 15s | | |
| 整轮超时 | 300s | | |
| 划词段上限 | 1500 字 | | |
| 文章第 2 层（本段）上限 | 1500 字（与划词段同） | | A40① 新增：文章无章节模型，本段层用段落文本 |
| `AiConfigWebWrite` 默认值 | false | | A40② 新增：默认保持"key 只在终端输入"的加强 |
| 多轮读物轮数上限 | 3 | | A48 新增：每轮 = 一次上游调用，必须有上界 |
| 文章默认片段 | 3000 字符 | | A48 新增：默认给片段，不默认给全文 |
| 文章"读全文"上限 | 8000 字符 | | A48 新增：放宽片段截断，**不取消** §6.5 的 2000 token 预算 |

---

## 附录 A · 验收清单（给 t5 / t6 / t7 / t8）

> 每条都写成"可执行 + 可观察"。**不依赖真实模型**的用例必须能在 CI 里跑。
>
> ⚠️ **验收证据纪律**（r1 修订 A38，captain 提；【必须】）：
> 1. 引用某条验收为"**通过**"时，**必须附上跑过的命令 + 输出**（或测试运行记录）。**存在测试文件 ≠ 测试通过。**
> 2. **从未执行过的用例必须标"未验证"**，不得写成"已覆盖""已通过"。
>    当前已知：`tests/Sip.Tests/ChapterTests.cs` 与 `SseStreamingTests.cs` **本轮一次都没跑过**
>    （captain 已按此收口 t3），所以下表里由它们承载的 **A5 / A6 / A22 / A39 一律按"未验证"处理**。
> 3. 复核者（t5/t7）若看到"通过"**没有命令/输出**，按**未验证**计 —— 这是 §12 那条"没有记录的偏离判 `needs_revision`"的同族规则：
>    **没有记录的证据，不算证据。**

| # | 验收项 | 做法 | 通过标准 |
|---|---|---|---|
| A1 | 目录可用（EPUB） | 导入一个含 NCX 的 EPUB → `sip --toc <id> --json` | `chaptersSource='ncx'`，`chapters[].chapterId` 匹配 §1.3 正则，`ord` 从 1 连续 |
| A2 | 目录可用（PDF 有书签） | 导入带书签的 PDF → `--toc` | `chaptersSource='bookmark'`，`pageStart` 单调递增且 ≤ `pageCount` |
| A3 | 懒回填幂等 | 连续两次 `--toc`，比较两次输出与 `SELECT COUNT(*) FROM Chapters` | 完全一致，第二次 `backfilled=false` |
| A4 | 目录不依赖 AI | 删 `ai_config.json` + 清 Key → `--toc` / `GET .../toc` | 退出码 0 / HTTP 200（§8.3） |
| A5 | 流式 vs 非流式等价 ⚠️**本轮未验证**（由从未执行的 `SseStreamingTests.cs` 承载） | 假 LLM 下同一问题跑两种模式 | `answer` 逐字相同（§5.1） |
| A6 | 首 delta 及时 ⚠️**本轮未验证**（同上） | 假 LLM（20 chunk × 50ms）→ 逐块读响应 | ≥2 个 delta 帧、时间不同、`tFirst < 0.5 × total`（§5.8） |
| A7 | harness 只读（**双向断言**，r1 修订 A37） | 挡位 0/1 下提问一次（用假 LLM），**提问前后各取一次** `rss.db`(+`-wal`/`-shm`) 与 `chat.db` 的 SHA-256 | ① `rss.db` 三个哈希**完全相同**（I1：一问一答都不许碰主库）；② **`chat.db` 必须变了**（至少多两行）—— 这一半同样重要：只测"主库没变"无法区分"**正确地只写了 chat.db**"与"**什么都没发生**"（§1.6 区分两种写） |
| A8 | 对话落库与可删 | 提问 → `GET /chat` → `DELETE /chat/{sid}?yes=1` → 再 GET | 消息在；会话消失；无 `yes=1` 时 400 |
| A9 | 划词活一轮 | 第一轮带 anchor，第二轮不带 | 落库的 `AnchorJson`：第一条有、第二条为 null（§7.1） |
| A10 | 会话限一本书 | 用 A 书的 sessionId 请求 B 书 | 409 `SESSION_BOOK_MISMATCH`（§7.2） |
| A11 | 无索引仍能答 | 清 `Vectors`/`VectorsChunks` 后提问 | 成功 + `degraded` 含 `no-index`（§9-8） |
| A12 | **PDF 的块带页锚点**（A17 改：原先断言"PDF 不进块表"，已被用户裁决撤销） | 对含 PDF 的库跑一次 `--index`（用假 embedding 端点，不花钱） | `VectorsChunks` 里 PDF 的行 **`ChapterId` 形如 `pdf:p<N>`**、`AnchorState='anchored'`；`ChapterSpan` 只在合并块上有值；扫描件**零块**（R5/R7/R8） |
| A13 | 块不跨章 | 检查重建后的 `VectorsChunks.ChapterId` 与 `Chapters` | 每个非 NULL 值都能在 `Chapters` 找到（I4 的前提） |
| A14 | 断连不烧钱 | 读到第一个 delta 后关连接 → 看服务端日志与库 | 上游被取消；该轮 `Status='aborted'`；无 500（§5.6） |
| A15 | 三语键齐 | `dotnet test --filter LangParityTests` | 绿（§10） |
| A16 | 既有行为不变 | 现有全部用例 | 全绿（I6） |
| A17 | 挡位白名单 + 提问闸门 | 挡位 2 下跑 `--toc/--chapter/--locate`，再 `POST /api/imports/{id}/ask`，再 `--chapterize` | 只读命令**放行**；`/ask` **403 `SIMON_BLOCKED`**（带 hint，且**不进流**）；`--chapterize` 被拦（§3.6 / §5.4 / §12.1-A19） |
| A18 | 引用跳得回去 | 对回答里的每个 `cite.chapterId` 查 `Chapters` | 全部存在（I4） |
| A19 | 对话表在**正确的文件**里 | 建库后对 `rss.db` 与 `chat.db` 分别查 `SELECT name FROM sqlite_master WHERE name LIKE 'Chat%'` | `rss.db` **空**、`chat.db` 两张表齐（§4.2.1a） |
| A20 | `chat.db` 建表幂等 | 连续两次触发建库（`sip --help` / 首次 `GET /chat`） | 无异常、表结构不变、`chat.db` 不被覆盖清空（§4.2.1a / §4.2.1e） |
| A21 | 一轮的原子性 | 提问中断后查表 | 同一 `TurnIndex` 下 user+assistant 两行齐；assistant `Status='aborted'`（§4.2.1c） |
| A22 | **目录粒度**（最重要的一条 · r1 修订 A20 升级为**精确对账**；⚠️**本轮未验证** —— 由从未执行的 `ChapterTests.cs` 承载） | 对下列真实书跑 `sip --toc <id> --json`，与**第三方库**（PyMuPDF/pypdf，不依赖 sip 解析器）取的真值比对 | 【必须】**精确等式，不用「≈」**：`len(chapters) == navPointCount − tocDropped` **且** `len(chapters) != spineCount`。判据书（判别力强）：`中国哲学简史` **200**（按 spine 会得 33）、`看图自学电吉他` **224**（spine 18）、`四世同堂` **110**（spine 125）。`毛泽东选集`（410/416，只差 6）只作辅助、**不单独承担主判据**；⚠️ `周恩来选集`（122/122 两法同数）**禁止**用作判据。<br>层级：`COUNT(DISTINCT depth) >= 2` **且** `MIN(depth) == 0`（§1.3.1 已钉死 **0 基**，见下）。区间：`CharStart/CharEnd` 不重叠且覆盖整个片段。<br>【必须】`firstBlock` 的长度与内容**不参与**本判据（`毛泽东选集` 会命中 §9-5 的 30 字裁断，拿它断言会假红 —— captain 点名确认）。<br>另：`看图自学电吉他` 需同时验证 `~n<k>` 消歧（`OEBPS/text00002.html` 被"扉页/版权"两条无片段项指向）与零长度章（§1.4.1） |
| A23 | **EPUB3 `<nav>` 路径** | 用 `ZipArchive` 当场合成最小 EPUB3 夹具（mimetype + container.xml + OPF `properties="nav"` + nav.xhtml） | `chaptersSource='nav'`，层级与标题正确；**且验收报告明确标注"结论来自合成夹具，本机无真实 EPUB3 样本"** |
| A24 | **坏归档** | 用 `父与子全集.epub`（或截断一个 zip）分别在**导入期**与**回填期**跑 | 导入：400 `ARCHIVE_UNREADABLE` + `Items` 无新行 + `imported/assets/<guid>/` 无残留；回填：退出码 0、`chapters:[]` + `chaptersError` + `Chapters` 0 行；两次都不崩 |
| A25 | **归档 / XML 防护** | 造三类样本：高压缩比 zip-bomb 条目、条目名含 `..\..\evil`/前导 `/`、NCX 带外部实体引用 | 三者都被拒或被规范化（无文件落到目标目录外）；XML 不解析外部实体（不产生网络请求、不读本地文件）；不 OOM |
| A26 | **重构没动导入产出**（本轮最有价值的一条验收，captain 确认收下） | 同一 EPUB，用重构前/重构后的代码各导入一次，比对 `Items.Content` 的 SHA-256 | **完全相同**（否则 `SourceHash` 变 → 全库重新回填 + 已存引用失效，§1.4.1 N6）。⚠️ 它防的是**安静的数据损坏**：`Content` 变 → `SourceHash` 变 → 全库回填 + 引用失效，这**中间没有一步会报错** —— 所以必须**逐字节 SHA-256 比对**，不许用"看起来一样" |
| A27 | **非 PDF / 损坏 PDF 不崩** | 把 `.jpg` 改名成 `.pdf` 导入；再对同一条跑 `--toc` / `--chapter` | 导入沿用既有行为、**无未捕获异常**；`--toc` 不崩且 `chaptersError.code='PDF_UNREADABLE'`；`--chapter` 报 `CHAPTER_NOT_FOUND`（§1.5） |
| A28 | **覆盖不变量（无黑洞）** | 对《咸的玩笑》（NCX 6 条 / 74 个 html）与至少一本大 EPUB 跑 `--toc --json`，检查区间 | **本轮判据（A36 改）**：① EPUB 按**文件空间** —— 每个 spine 片段的锚点序列（含 N7 卷首章）**无缝切完该片段**、无重叠无空隙；文本级复核 = "各章文本按序拼接 ≈ 该片段文本（空白规范化后）"；② PDF `PageEnd+1 == 下一章 PageStart` 且覆盖 `[1,pageCount]`。**"不属于任何章"的字符/页即违约**。（`CharEnd == 下一章 CharStart` 那种**基于字符偏移**的形式待偏移落地后才要求，本轮**不判**） |
| A29 | **无书签大 PDF 走页级主路径** | 对 `SystemVerilog for Design(2nd)`（436 页、无书签、文本层完好）跑 `--toc` + `--page 1` | `chaptersSource='page'`、436 行、`Title=''`、`PageStart` 从 1 连续到 436；**`textAvailable:true` 且 `--page 1` 给出该页文本**（A15 起）；**不报错、不跳过**（§1.4） |
| A32 | **PDF 逐页文本落库 / 幂等 / 扫描件** | 对一本文本层完好的 PDF 跑两次懒回填；再对扫描件跑一次 | 第一次后 `PdfPages` 覆盖 `[1,pageCount]`、`CharCount` 合计 ≈ 实测字数；第二次行数与内容不变（幂等）；扫描件：行齐全但全为 `CharCount=0`、`textAvailable:false`、`success:true`（§1.5.1 边界②③） |
| A33 | **嵌入只在 `--index` 发生（成本硬边界）** | ① 用**假 embeddings 端点计数**：导入一本文本层完好的 PDF + 触发懒回填（`--toc`）→ 计数；② 再跑 `--index` → 计数 | ①**导入与懒回填期间调用数 = 0**；② `--index` 后 PDF 有 `VectorsChunks` 行、且**每行 `ChapterId` 形如 `pdf:p<N>`**、`AnchorState='anchored'`；③ 扫描件**零块**（§1.5.1 边界① / §6.2 R5 / §6.6） |
| A34 | **PDF 关键词反查到页** | 在一本有文本层的真 PDF 上，用已知出现在第 87 页的词跑 `--locate` | 返回 `page=87`（1 基，与 `--page` 一致）、`chapterId` 命中包含该页的书签章、`snippet` 含该词；**零模型调用**；扫描件返回空 + 说明（§3.5） |
| A35 | **页编号只有一套** | 同一本 PDF 上比对 `PdfPages.Page` / `Chapters.PageStart` / `--page <n>` 输出 / `chapterId` 的 `p<N>` | 四处**指向同一页**；`PdfPages` 覆盖 `[1,pageCount]`，`Chapters` 非零长度行也覆盖同一区间（§1.3.1 / §1.4.1 N8） |
| A36 | **旧库块锚点回填** | 造一个"有 `VectorsChunks` 行但三列全空"的旧库（直接 `Exec` 构造），跑一次目录/索引路径 | 迁移不报错；§6.3.1 的免费单调匹配给能匹配的块写上 `ChapterId`+`AnchorState='anchored'`；匹配不上的为 `'unknown'` 且**不出现在任何引用里**；全书跑完 **embedding 调用数 = 0** |
| A37 | **`--index` 的量级与可取消** | 对一本 ~110 页的 PDF 跑 `--index`，同时用假端点计数；中途 Ctrl+C 再重跑 | 调用数 ≈ 块数（文档 §6.6 的量级表对得上，偏差 < 20%）；`--json`/响应报出块数；取消后不留坏状态、重跑幂等（同 item 覆盖） |
| A38 | **`persisted` 不会说谎** | 三条路径各跑一次：① 挡位 0/1 正常提问；② 挡位 2 下直接 `POST /api/imports/{id}/ask`；③ 挡位 0/1 下让 `chat.db` 不可写（只读或磁盘满） | ① `persisted:true` 且两行都在；② **403 `SIMON_BLOCKED` 的 JSON**（`Content-Type: application/json`、**不进流、响应里没有 `persisted` 字段**、body 含 `level`）；③ `persisted:false` + `ErrorCode='CHAT_PERSIST_FAILED'` + `degraded:["chat:persist-failed"]`、**回答仍然完整给出**。**只有 ① 允许出现 `persisted:true`**（§5.3.1 / §5.4） |
| A39 | **零语料路径必须靠合成夹具覆盖**（r1 修订 A20；⚠️**本轮未验证** —— 同上，夹具所在测试文件从未执行） | 测试里用 `ZipArchive` **当场生成**最小 EPUB，逐个覆盖：① **锚点缺失**（`src` 带片段但正文没有该 id）→ 降级到片段起点（N2(b)）；② **`src` 不在 spine 里**（指向不存在的文件）→ **丢弃并计入 `tocDropped`**（N2(c)）；③ **第 7 层目录** → 压平到 6 且 `depthFlattened=true`；④ **HTML4 风格 `<a name="…">`** → 认得住（N2(a)）；⑤ **目录序 ≠ 文档序**（后出现的目录项指向更靠前的锚点）→ 区间仍**不重叠、无空隙**（N1/N5） | 五条全部按 §1.4.1 的规则通过；`tocDropped` / `zeroLength` / `depthFlattened` 逐项能与夹具的构造对上；**验收报告必须如实标注"来自合成夹具"**（本机无真样本，A23 同款要求）。这五条**不许**因为"真实书没出现"而跳过 |
| A40 | **大 PDF 首屏不阻塞**（A28） | 对 `SystemVerilog for Design(2nd)`（436 页 / 72 万字）**清掉该书的 `PdfPages`/`Chapters`**，然后计时 `sip --toc <id> --json` 与打开阅读页 | **首屏 ≤ 2 秒**返回（书签章齐全）；响应里 `pdfTextState='extracting'`；随后（同一进程）再查变为 `ready` 且逐页文本齐全；期间 `--page` 对未抽页给 `textAvailable:false`+`note`；`--ask` 不阻塞、`degraded` 含 `pdf:extracting`；**小书（<300 页）一次抽完**、不出现 `extracting` |
| A41 | **闸门拦下留下可审计事件**（A29，审计员提的"现成证据链"） | 挡位 2 下 `POST /api/imports/{id}/ask`，随后 `GET /api/simon` | 403 `SIMON_BLOCKED`；且 `/api/simon` 的 `events`（最近 20 条）里出现 `type="blocked_cmd"`、`detail` 含 **`web:ask`**、`level=2`。四个写端点各自的 `what` 取值按 §5.4 的固定表（`ask`/`chat-new`/`chat-del-one`/`chat-del-all`） |
| A42 | **请求体上限可断言**（A29） | 发一个超过上限的 `/ask`（或 `/chat`）请求体 | 413 + `error.code='BODY_TOO_LARGE'`，**payload 里带 `limit`（字节数）**；与导入文件的 `TOO_LARGE`（512 MB）**不是同一个码**（§3.6） |
| A30 | **书签页码 1-based 交叉验证** | 对 5 个真实 PDF（法律与生活 p6 / 逻辑与思维 p6 / 物理选择性必修三 p6 / 中国哲简史 p1 / 被讨厌的勇气 p4），断言"书签标题出现在 `PageStart` 那一页、且不出现在前一页" | 5/5 命中（§1.3.1；这条是 t5 能独立复算的判据） |
| A31 | **分组节点不占行、层级不丢** | 对《法律与生活》（39 条 / 4 根 / maxLevel 2，4 个单元是 `ContainerBookmarkNode`）跑 `--toc --json` | 4 个单元**不出现**在 `chapters` 里；子节点 `ParentId` 指向最近**被保留**祖先（没有则 null）；`Depth` = 被保留祖先链长度（可见深度）；`Locator.rawLevel` 保留原始层级；零长度章 `PageEnd = PageStart − 1` 且**不出现在任何 `cites`/`--locate` 结果里**（§1.5） |
| A43 | **RSS 文章也能问 AI**（A40①，用户拍板） | 在一篇**有正文的 RSS 文章**上 `POST /api/imports/{文章id}/ask`（前端从文章页发起） | HTTP 200 + `text/event-stream`；帧序仍为 `session → delta… → cites → done`（§5.2）；历史落 `chat.db` 且 `ChatSessions.ItemId` = **文章 id**；响应/快照里 `locType='article'`。**反向断言（不许过度扩张）**：同一篇文章上 `GET /api/imports/{id}/toc` 仍 **404 `NOT_IMPORTED`** —— 扩的是问答，不是章节模型。另测一篇 `Items.Content` 为空的文章 → **409 `ARTICLE_NO_TEXT`**，且**不调用上游模型**（假端点计数为 0） |
| A44 | **Web 配 AI 的分级与不回流**（A40②，用户拍板） | ① 关着开关（默认）时 `POST /api/ai/config`；② 开着开关时改 model；③ 开着开关时改 endpoint（不带确认参数 / 带确认参数）；④ 改完 `GET /api/ai/config` 与 `GET /api/simon` | ① **403/404**（开关关闭 = 不开放，且**其它 AI 端点行为不变**）；② 200；③ 不带确认参数 → **400 `CONFIRM_REQUIRED`**，带则 200 **且 `/api/simon` 出现 `ai_endpoint_changed`**；④ **任何响应体、`chat.db`、`simon_events`、日志里都搜不到 key 原文**，`GET` 只回 `apiKeySet:true/false`；挡位 ≥2 时全部分支 **403 `SIMON_BLOCKED`** |

## 附录 B · 契约落点索引（实现方按此找位置，避免"另起一套"）

> ⚠️ **查这一列请按"符号名"查，不要按行号查**（r1 修订 A39，采审计员的纪律）：
> 行号随每次代码改动漂；**符号名不漂**。下面"行号"列是**A39 校准值**，只作定位加速。
> **校准于 A39 时的文件规模**：`sipcore.cs` **10190** 行 / `Web.cs` **4698** 行 / `web/app.js` **2589** 行 / `simon.cs` **533** 行。
> 【必须】改完代码回来**只更新行号列**（符号名不动）；t7 评审会看这一列是否过期。

| 契约 | 符号（权威） | 行号（A39 校准） |
|---|---|---|
| 章节表 / 迁移 / `DbMeta` / `PdfPages` | `sipcore.cs` `InitDatabase`；迁移段风格见 `VectorsChunks` 建表与"补列 `ALTER`" | 4297 / 4368 / 4476 |
| 导入入口（章节抽取挂这里） | `sipcore.cs` `ImportFileCore`、`ReadImportedFile`、`ReadEpubFile`、`ExtractEpubHtml`、`ReadPdfFile`、`ResolveZipPath` | 1045 / 1025 / 910 / 953 / 717 / 895 |
| PDF 页数 / 栅格化 / 页范围 | `sipcore.cs` `GetPdfPageCount`、`RenderPdfPages`、`ParsePageRange`、`ParsePagesArg` | 5058 / 5074 / 5112 / 5135 |
| 切块与块向量 | `sipcore.cs` `ChunkText`、`SaveChunkVector`、`EmbedItemChunks`、`EstimateTokens` | 8311 / 8354 / 8376 / 8286 |
| 语义检索（本书/全库材料来源） | `sipcore.cs` `DoSearch`（`NO_INDEX` 判定在其内） | 8986（`NO_INDEX` 8999） |
| LLM 调用 + 遥测 | `sipcore.cs` `CallLlmAsync`（新增流式版本，**保留**原非流式） | 9177 |
| `ExitCodeFor` / `ReportError` | `sipcore.cs` | 8090 / 8109 |
| CLI 命令分发 + `PrintHelp` | `sipcore.cs` `switch (cmd)` / `PrintHelp` | 3330 / `PrintHelp` |
| `--show --json` 的字段 | `sipcore.cs` `ShowArticleJson`（只做加法） | 4958 |
| 导入路由与既有接口 | `Web.cs` 导入路由段；`ImportItemInfo`、`HandleImportDetail`、`HandleImportText`、`HandleImportPage`、`ImportItemDelete` | 428 / 3346 / 3453 / 3496 / 3592 / **3672** |
| ⚠️ `ImportItemDelete` 在 **`Web.cs`** | 不在 `sipcore.cs`（CLI `--import-rm` 与 Web DELETE 共用它） | 3672（签名已含 `ChatSessions` 回传） |
| 「没有章节模型」的旧注释 | `Web.cs` —— ✅ **已改**（现写着"电子书：**有章节模型**…本轮已改"） | 3197 |
| 鉴权 / 写保护 / JSON 输出 | `Web.cs` `/api/*` 鉴权、`WebWriteAllowed`（内含 `SimonRecord("blocked_cmd", …)`）、`WriteJson` | 341 / **492**（记录在 **502**）/ 518 |
| AI 配置状态（前端判断能否提问） | `Web.cs` `HandleConfig`（`ai.llm.keySet` 已有，直接用） | 4157 |
| PDF 书签解析（PdfPig） | 新增函数放 `sipcore.cs` 的 PDF/导入段（`ReadPdfFile`/`GetPdfPageCount`/`ReadImportedFile`/`ImportFileCore` 附近）；渲染仍走 `RenderPdfPages`（职责不混） | 见上 |
| EPUB 章节抽取的"同坐标系" | 与 `ReadEpubFile` / `ExtractEpubHtml` **共用**同一套 spine 遍历（§1.4.1 N6，禁止另写拼接） | 910 / 953 |
| `chat.db` 建表 | `sipcore.cs` 里**独立于** `InitDatabase` 的 `InitChatDatabase(chatDbPath)`，**在启动路径上与 `InitDatabase` 相邻调用**（照 `TelemetryService.Init(dataDir)` 先例）；**不要**加进 `InitDatabase`、也**不要**放 Web 请求路径（§4.2.1a） | `InitDatabase` 4297 |
| 对话路由 | 与导入路由同段：`Web.cs:428` 之后的 `/api/imports/{id}/chat*`；SSE 走 `WriteJson` 同级的新写流辅助 | 428 / 518 |
| 前端阅读器与分节 | `web/app.js` `splitBookSections`、`sectionsFromNodes`、eBook 渲染段 | 1009 / 1019 / `render()` 内 |
| 前端事件代理 / i18n | `web/app.js` `data-act` 分发、`t()` | 2217 / 97 |
| 悬浮球/抽屉的挂载点 | `web/index.html` `#content`、`#toast`；CSS 变量与三套主题同文件 | 423 / 501 |
| 挡位白名单 | `simon.cs` `SimonIsReadOnly` | 121 |
| 测试隔离 | `tests/Sip.Tests/TestHost.cs`（`SipInstance`：独立数据目录 + 隔离凭据命名空间）、`WebServerHarness.cs`（真起 `--start`、可 `Restart()`、逐块读响应） | — |

---

> **关于"索引要不要跟着文档行号更新"**：本表索引的是**代码**的落点（`文件:行号`），
> 不索引本文档自身的行号 —— 文档内部一律用**§章节号**引用（章节号只在结构变更时才变，
> 所以本文件从 917 行长到 1450+ 行，本表一行都不用改）。
> 反过来，**代码**落点才是会漂的东西：实现方改动 `sipcore.cs`/`Web.cs`/`web/app.js` 之后
> 【必须】回来把本表的行号顺手校准一次（这是 t3/t4 的收尾动作，t7 评审时会看）。

### 实现清单（**索引式**，不复制条文 —— r1 修订 A30；进度见每行末尾）

> 用途：给 t3/t4 一张"要做什么 → 看哪节 → 验收编号"的地图，**避免照半截记忆或旧快照开工**。
> 每条只给指针，条文以 § 引用处为准；**凡与本表冲突的，以正文为准**。
> **进度标注（A39）**：✅=t3 交付并自称已做（**注意 A38 的证据纪律：自称已做 ≠ 已验证**）；⬜=未做。

| # | 要做的事 | 看哪节 | 验收 |
|---|---|---|---|
| 1 | `Chapters`/`PdfPages`/`VectorsChunks` 三列/`DbMeta` **一次迁移做完**，只增不改 | §1.2、§1.5.1 边界②、§6.3、§6.3.1 | A19/A20/A32 |
| 2 | EPUB 章节：**按 navPoint 建行**（不是按 spine 文件），优先级 **ncx（主）→ nav（兼容）→ spine（退化）**，标题 ncx → nav → h1~h6 → `<title>` → 空串 | §1.4、§1.4.1 | A22/A23/A39 |
| 3 | EPUB 切片算法 N1–N8（排序键用**文档序**、**跨文件边界延续**、卷首合成章、零长度、覆盖不变量） | §1.4.1、§1.6 | A28/A39 |
| 4 | PDF：`PdfPig` 取书签（`GetNodes()` 展平 + `Level`，跳过 `ContainerBookmarkNode`）+ **逐页文本落 `PdfPages`**。**`PdfPages` 的 DDL 可直接抄 §1.5.1 边界②**（PK `(ItemId, Page)` = 一页一行） | §1.5、**§1.5.1 边界②（DDL 原文在这里）** | A12/A27/A29/A31 |
| 5 | 引用锚点：`VectorsChunks.ChapterId`（PDF 用 `pdf:p<page>`）/`ChapterSpan`/`AnchorState` **要真的填**；块不跨章、小章同父合并、PDF **可按需**进块表 | §6.2 R1–R8、§6.3.1 | A13/A33/A36 |
| 6 | 大 PDF 两段式：首屏 ≤2s 出书签章 + `pdfTextState='extracting'`，文本同进程续抽 | §1.5.3 | A40 |
| 7 | 目录/页文本的**可观测字段**：`tocDropped`、`zeroLength`、`depthFlattened`、`rawLevel`、`textPages`/`emptyPages`、每页 `textAvailable` | §2.2、§3.4、§1.5.1 边界② | A22/A26/A39 |
| 8 | 问答：SSE 事件序 + 四层上下文 + 会话落 `chat.db`（独立库、`InitChatDatabase`） | §5、§6.5、§7、§4.2/§4.2.1 | A6/A8/A19/A21 |
| 9 | 写闸门：四个写端点过 `WebWriteAllowed`、纯读端点仍 200、**判闸在写响应头之前**、`blocked_cmd` 的 `what` 用固定取值 | §5.4、§5.3.1 | A17/A38/A41 |
| 10 | `--ask` **不是回填触发点**；`/ask` 只读 `Chapters`/`PdfPages`（区分两种"写"） | §1.6（首条） | A7/A33 |
| 11 | 把 `Web.cs:3197` 那段「**没有章节模型**」的注释改掉 | 附录 B | — |
| 12 | 改完代码回来校准附录 B 的代码行号 | 附录 B | t7 评审项 |

## 附录 C · 实现方最容易理解错的三个点（写在这里，省一次返工）

1. **`charStart/charEnd` 不能用来切前端的 DOM。**
   它相对 `Items.Content` **原文**，而前端拿到的是净化后的 HTML —— 两个长度体系。
   正确做法：前端用 `firstBlock` 做**单调匹配**切分（§2.4），或者干脆一章一次请求
   `GET /chapters/{chapterId}`。谁要是写了 `bodyHtml.substring(charStart, charEnd)`，
   表现是"章节边界偶尔错开半句话"，而且**很难复现**（取决于净化器删了多少属性）。
   **同一陷阱在服务端还有一个孪生版本**：算 EPUB 锚点偏移时误用 `HtmlNode.StreamPosition`
   （它是**原始文档**下标，返回值却是删过属性的 `body.InnerHtml`）—— 见 §1.4.1 的 N6 末尾那段警告。
2. **`AI_NOT_CONFIGURED` 与"本轮读不到 PDF 正文"不是一类东西。**
   前者是**真错误**（409/退出 3，必须给 hint），后者是**正常成功**（`success:true` +
   `textAvailable:false` + `degraded`）。把后者报成错误，AI 会开始重试一个永远不会成功的事；
   把前者报成空结果，用户会以为"AI 觉得这本书没内容"。
   ⚠️ 顺带一句：**不要把它写成"PDF 没有文本层"** —— 那是错的（实测大 PDF 能抽几十万字，
   只有扫描件才真没有）。正确说法见 §1.5 的**三种**（r1 修订 A26/A31）：
   ①「sip **已抽取该 PDF 的逐页文本**」（**逐份**实测）②「**这一份的某几页抽不出文字**」（**逐页**实测）③只提位置
   （"这一页我只能给你页码"）。（§12.1-A14/A15/A26）
3. **PDF 的书签、页数、逐页文本，全来自 `PdfPig`**（r1 修订 A13/A15）；
   `PDFtoImage`/pdfium 只负责**渲染**。
   书签走 `PdfDocument.Open` → `TryGetBookmarks(out Bookmarks)` → `GetNodes()` 展平 + `Level`；
   **`Roots.Count` 不是章节数**（实测 28/28、79/31、39/4 三种形状），
   `ContainerBookmarkNode`（无目标的纯分组节点）**跳过不建行**；
   **`PageNumber` 只在 `DocumentBookmarkNode` 上**（基类没有），且是 **1 基**（与 `GetPage(n)` 对齐，
   5 个真 PDF 交叉验证全中）。
   【不再提 P/Invoke 作为方案】原生 `pdfium.dll` 里**确实有** `FPDFBookmark_*`，但那是运行时探测原生库的路径：
   Windows 上测得通不等于 Linux/macOS 上也找得到，而"找不到"只表现为**静默降级** ——
   这种"在别的平台悄悄变差"的失败模式比多 4 MB 昂贵得多（§1.5 / §12.1-A13）。
   但**不许**因为没有书签就把 PDF 的目录功能整体砍掉（降级为页级即可），
   也不许为了"看起来支持书签"按字号猜标题（§11 第 2 条禁止的猜测）。
