using System.Text;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 「一次导入多本」的黑盒用例（CLI 侧）。
///
/// 为什么必须有这组：批量导入是**用户明确要的用法**，而它的契约有三条互相牵制的约束 ——
///   ① 单个文件的输出形状**不能变**（脚本与既有测试都在吃 <c>{success,id,title,file,feed}</c>）；
///   ② 多个文件要能一次吃完，且失败**不中断**后面的文件；
///   ③ 只要有一个失败，退出码就必须非 0 —— sip 的退出码是 agent 判断
///      "该重试 / 该换目标 / 该报错"的依据，批量里 9 成 1 败若返回 0，agent 会以为整批都进去了。
/// 这三条任意一条退回去，用户侧的表现都是"导入看起来成功了，其实少了几本"。
///
/// PDF 那条用例顺带钉住「导入 PDF 会记下页数」—— 对开双页的页码与末页判断都靠它。
/// </summary>
public class ImportBatchTests
{
    // ── fixtures ───────────────────────────────────────────────

    /// <summary><paramref name="dir"/> 必须是实例目录**里面**的：sip 数据目录固定在 exe 同级，
    /// 把源文件放在 Root 下，SipInstance.Dispose 才会连同临时目录一起收走。</summary>
    private static string WriteText(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, body, new UTF8Encoding(false));
        return p;
    }

    // ── 1. 一次导入多个：全部成功 ───────────────────────────────

    [Fact]
    public void BatchImport_ImportsEveryFile_AndReportsEachOne()
    {
        using var sip = new SipInstance();
        string dir = Path.Combine(sip.Root, "books");
        string a = WriteText(dir, "a.md", "# 甲\n\n第一本书的正文。");
        string b = WriteText(dir, "b.txt", "第二本书的正文。");
        string c = WriteText(dir, "c.md", "# 丙\n\n第三本书的正文。");

        var r = sip.Run("--import", a, b, c, "--json");

        Assert.True(r.ExitCode == 0, $"批量导入应成功，实际退出码 {r.ExitCode}\n{r.Stdout}\n{r.Stderr}");
        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;
        // stdout 必须是**整份可解析的 JSON**：非 JSON 模式那些 "Importing i/n…" 进度行
        // 一行都不能漏进来，否则脚本侧 JSON.parse 直接炸
        Assert.True(root.GetProperty("success").GetBoolean());
        var counts = root.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("total").GetInt32());
        Assert.Equal(3, counts.GetProperty("ok").GetInt32());
        Assert.Equal(0, counts.GetProperty("failed").GetInt32());

        // items 必须**每本一行**且都有真 id：只回一个总数的话，用户无从知道哪本没进去
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, x => Assert.True(x.GetProperty("success").GetBoolean()));
        var ids = items.Select(x => x.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));

        // 三本都真的落进了「本地导入」源
        string? n = sip.QueryScalar(
            "SELECT COUNT(*) FROM Items i JOIN Feeds f ON i.FeedId = f.Id WHERE f.FeedUrl = 'local://import'");
        Assert.Equal("3", n);
    }

    // ── 2. 部分失败：不中断，且退出码非 0 ──────────────────────

    [Fact]
    public void BatchImport_KeepsGoingAfterAFailure_AndExitsNonZero()
    {
        using var sip = new SipInstance();
        string dir = Path.Combine(sip.Root, "books");
        string good1 = WriteText(dir, "good1.md", "# 好的一\n\n正文。");
        string missing = Path.Combine(dir, "does-not-exist.md");
        string bad = WriteText(dir, "bad.xyz", "格式不支持");
        string good2 = WriteText(dir, "good2.md", "# 好的二\n\n正文。");

        var r = sip.Run("--import", good1, missing, bad, good2, "--json");

        // ① 失败不中断：夹在两个坏文件之间的 good2 必须也进去了。
        //    这里用**数据库**断言而不是只看 JSON —— "报了几个" 和 "真的落了几本" 是两件事。
        string? n = sip.QueryScalar(
            "SELECT COUNT(*) FROM Items i JOIN Feeds f ON i.FeedId = f.Id WHERE f.FeedUrl = 'local://import'");
        Assert.Equal("2", n);

        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        var counts = root.GetProperty("counts");
        Assert.Equal(4, counts.GetProperty("total").GetInt32());
        Assert.Equal(2, counts.GetProperty("ok").GetInt32());
        Assert.Equal(2, counts.GetProperty("failed").GetInt32());

        // ② 每条失败都要说清**是哪个文件、哪一类错**：批量里"2 个失败"没有可操作性
        var fails = root.GetProperty("items").EnumerateArray()
            .Where(x => !x.GetProperty("success").GetBoolean()).ToList();
        Assert.Equal(2, fails.Count);
        Assert.Contains(fails, x => x.GetProperty("error").GetProperty("code").GetString() == "FILE_NOT_FOUND");
        Assert.Contains(fails, x => x.GetProperty("error").GetProperty("code").GetString() == "UNSUPPORTED_FORMAT");

        // ③ 退出码非 0 —— agent 靠它判断"整批是否真的进去了"
        Assert.True(r.ExitCode != 0, $"有失败时退出码必须非 0，实际 {r.ExitCode}\n{r.Stdout}");
    }

    // ── 3. 单文件：输出形状向后兼容 ─────────────────────────────

    [Fact]
    public void SingleFileImport_KeepsTheOriginalJsonShape()
    {
        using var sip = new SipInstance();
        string dir = Path.Combine(sip.Root, "books");
        string a = WriteText(dir, "solo.md", "# 独本\n\n正文。");

        var r = sip.Run("--import", a, "--json");
        Assert.True(r.ExitCode == 0, $"单文件导入应成功：{r.Stdout}\n{r.Stderr}");

        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;
        // 单文件走的仍然是**旧形状**：多出来的 total/items 会把既有脚本打懵
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("title", out _));
        Assert.True(root.TryGetProperty("file", out _));
        Assert.False(root.TryGetProperty("items", out _), "单个文件不该返回批量形状");
        Assert.False(root.TryGetProperty("counts", out _), "单个文件不该返回批量形状");

        // --title 仍然只作用于单文件
        var r2 = sip.Run("--import", a, "--title", "我起的名字", "--json");
        using var doc2 = JsonDocument.Parse(r2.Stdout);
        Assert.Equal("我起的名字", doc2.RootElement.GetProperty("title").GetString());
    }

    /// <summary>批量 + --title 是**无意义的组合**（一次给 N 本书起同一个名字）：
    /// 与其静默地只给第一本用、或者给所有本用，不如当场拒绝 —— 用户能立刻改命令行。</summary>
    [Fact]
    public void BatchImport_RejectsTitleFlag()
    {
        using var sip = new SipInstance();
        string dir = Path.Combine(sip.Root, "books");
        string a = WriteText(dir, "x.md", "正文甲");
        string b = WriteText(dir, "y.md", "正文乙");

        var r = sip.Run("--import", a, b, "--title", "同一个名字", "--json");
        Assert.True(r.ExitCode != 0, "批量 + --title 该被拒绝");
        Assert.Equal("0", sip.QueryScalar(
            "SELECT COUNT(*) FROM Items i JOIN Feeds f ON i.FeedId = f.Id WHERE f.FeedUrl = 'local://import'"));
    }

    // ── 4. PDF：页数要记下来（对开双页的页码/末页判断靠它）────────

    [Fact]
    public void BatchImport_RecordsPdfPageCount()
    {
        using var sip = new SipInstance();
        string dir = Path.Combine(sip.Root, "books");
        string pdf = Path.Combine(dir, "three-pages.pdf");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(pdf, MinimalPdf.Build(3, "Batch Import Probe"));

        var r = sip.Run("--import", pdf, "--json");
        Assert.True(r.ExitCode == 0, $"PDF 导入应成功：{r.Stdout}\n{r.Stderr}");

        string? pages = sip.QueryScalar(
            "SELECT i.PageCount FROM Items i JOIN Feeds f ON i.FeedId = f.Id WHERE f.FeedUrl = 'local://import'");
        Assert.Equal("3", pages);
    }

    // ── 5. PDF 逐页 PNG：对开双页吃的就是这两个接口 ─────────────

    /// <summary>对开双页**一屏要两张图**，所以"按页取一张 PNG"这条路必须是稳的：
    /// 页数对、两张图都能拿到、越界给 404 而不是一张空白图。
    /// 这条走 Web 接口，因为它就是浏览器里 &lt;img src="/api/imports/{id}/page/{n}"&gt; 打的那个地址。</summary>
    [Fact]
    public async Task PdfPages_AreServedAsDistinctImages_AndOutOfRangeIs404()
    {
        using var server = new SipWebServer();
        string dir = Path.Combine(server.Instance.Root, "books");
        Directory.CreateDirectory(dir);
        string pdf = Path.Combine(dir, "spread.pdf");
        File.WriteAllBytes(pdf, MinimalPdf.Build(4, "Spread Probe"));

        var imp = server.Instance.Run("--import", pdf, "--json");
        Assert.True(imp.ExitCode == 0, $"PDF 导入应成功：{imp.Stdout}\n{imp.Stderr}");
        long itemId = long.Parse(server.Instance.QueryScalar(
            "SELECT i.Id FROM Items i JOIN Feeds f ON i.FeedId = f.Id WHERE f.FeedUrl = 'local://import'")!);

        using var client = server.NewClient(out var jar);
        using (var login = await server.LoginAsync(client, jar))
            Assert.True(login.IsSuccessStatusCode, $"登录失败：{login.StatusCode}");

        // 对开的一屏是「3-4」这种成对取图 —— 两张都得能独立拿到
        byte[] p3 = await GetPage(client, itemId, 3);
        byte[] p4 = await GetPage(client, itemId, 4);
        Assert.True(p3.Length > 0 && p4.Length > 0, "第 3/4 页的 PNG 都不该是空的");
        // PNG magic：确认服务端真的把图递出来了，而不是把某个 JSON 错误当图片发
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, p3.Take(4).ToArray());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, p4.Take(4).ToArray());
        // 两页内容不同（页眉印着页码）—— 相同的话说明接口其实一直在给同一页，
        // 那"对开"就会变成左右两张一模一样的图
        Assert.False(p3.SequenceEqual(p4), "第 3 页与第 4 页的图不该完全相同");

        using var outOfRange = await client.GetAsync($"/api/imports/{itemId}/page/99");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, outOfRange.StatusCode);
    }

    private static async Task<byte[]> GetPage(HttpClient client, long itemId, int page)
    {
        using var res = await client.GetAsync($"/api/imports/{itemId}/page/{page}");
        Assert.True(res.IsSuccessStatusCode, $"第 {page} 页应返回 200，实际 {(int)res.StatusCode}");
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        return await res.Content.ReadAsByteArrayAsync();
    }
}

/// <summary>
/// 手搓一个**最小但合法**的多页 PDF（含正确的 xref 偏移）。
///
/// 为什么不塞一份二进制 fixture 进仓库：PDF 生成器只有几十行、可读可改，
/// 而二进制文件一旦入库就没人敢动（也说不清它是怎么来的）。
/// 这里刻意用**真文本**（Helvetica + WinAnsiEncoding）而不是纯图形：
/// PdfPig 的文本层探测与 PDFtoImage 的栅格化都能因此被真正走到。
///
/// ⚠️ 对象槽位是**预先算好**的，写入时还要断言编号对得上（<see cref="ObjAt"/>）。
/// 第一版按 `3 + i*2` / `4 + i*2` 推编号，结果第 1 页的内容流也落在对象 3 ——
/// 而对象 3 是字体，**两个对象撞了号**。撞号的 PDF 仍然"看着像"PDF：
/// pdfium 数得出 4 页（所以页数断言照样绿），却一页都渲染不出来，
/// PdfPig 则直接报 `Could not find the object number 3 0 …`。
/// 这种错只有真去渲染才暴露，所以夹具必须结构正确，不能"差不多"。
/// </summary>
internal static class MinimalPdf
{
    public static byte[] Build(int pageCount, string text)
    {
        if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));

        // 槽位布局：1=Catalog 2=Pages 3=Font，其后每页两个（内容流、页对象）
        const int catalog = 1, pages = 2, font = 3;
        int ContentNum(int i) => 4 + i * 2;
        int PageNum(int i) => 5 + i * 2;
        int size = 4 + pageCount * 2;          // 对象总数 + 1（xref 那条空闲头）

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        var offsets = new long[size];          // [0] 恒为 0：xref 的第 0 项是空闲头

        void Raw(string s) => w.Write(Encoding.ASCII.GetBytes(s));

        // 写入时必须就是那个编号：算错就当场炸，而不是产出一个坏文件让下游去猜
        void ObjAt(int num, string body)
        {
            if (offsets[num] != 0) throw new InvalidOperationException($"对象 {num} 被写了两次");
            offsets[num] = ms.Position;
            Raw($"{num} 0 obj\r\n{body}\r\nendobj\r\n");
        }

        Raw("%PDF-1.4\r\n");
        // 二进制标记行：按规范必须有一行 >=128 的字节，告诉传输层"这是二进制"
        w.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\r', (byte)'\n' });

        ObjAt(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        ObjAt(pages, $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{PageNum(i)} 0 R"))}] /Count {pageCount} >>");
        ObjAt(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        for (int i = 0; i < pageCount; i++)
        {
            string stream = $"BT /F1 24 Tf 72 700 Td ({text} - page {i + 1}) Tj ET";
            ObjAt(ContentNum(i), $"<< /Length {stream.Length} >>\r\nstream\r\n{stream}\r\nendstream");
            ObjAt(PageNum(i),
                $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {ContentNum(i)} 0 R >>");
        }

        long xrefPos = ms.Position;
        Raw($"xref\r\n0 {size}\r\n");
        Raw("0000000000 65535 f\r\n");
        for (int i = 1; i < size; i++) Raw($"{offsets[i]:D10} 00000 n\r\n");
        Raw($"trailer\r\n<< /Size {size} /Root {catalog} 0 R >>\r\nstartxref\r\n{xrefPos}\r\n%%EOF\r\n");
        w.Flush();

        return ms.ToArray();
    }
}
