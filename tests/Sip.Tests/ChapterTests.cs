using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 章节锚点（t3）的黑盒用例：导入一个**自己造的** EPUB，然后只用 CLI 观察行为。
///
/// 为什么自造而不依赖本机现成的书：验收必须能在任何机器上跑。
/// 本机那些真书（中国哲学简史 / 毛泽东选集 / 物理教科书）是用来**人工**验证解析器
/// 对付真实世界脏数据的（见契约 §12.1 的核验记录），不该成为 CI 的前提。
///
/// 这些用例走的是 <see cref="SipInstance"/>（把构建产物复制到临时目录再跑），
/// 所以绝不可能碰到用户的真实 readwithhotsoup/。
/// </summary>
public class ChapterTests
{
    /// <summary>造一本三章的 EPUB2（有 NCX）。spine 顺序 = NCX 顺序，最标准的那种。</summary>
    private static string MakeEpub(string dir, string name, int chapters = 3)
    {
        string path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string entry, string content)
        {
            var e = zip.CreateEntry(entry);
            using var s = e.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(content);
        }

        // mimetype 必须是第一个条目且不压缩 —— 有些严格实现靠这个认 EPUB
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open()) s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));

        Add("META-INF/container.xml",
            """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");

        var items = new StringBuilder();
        var spine = new StringBuilder();
        var navPoints = new StringBuilder();
        for (int i = 1; i <= chapters; i++)
        {
            items.Append($"""<item id="c{i}" href="ch{i}.xhtml" media-type="application/xhtml+xml"/>""");
            spine.Append($"""<itemref idref="c{i}"/>""");
            navPoints.Append($"""<navPoint id="np{i}" playOrder="{i}"><navLabel><text>第{i}章 测试章节</text></navLabel><content src="ch{i}.xhtml"/></navPoint>""");
            Add($"OEBPS/ch{i}.xhtml",
                $"""<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>书</title></head><body><h1>第{i}章 测试章节</h1><p>这是第{i}章的正文内容，用来验证按章切片。</p></body></html>""");
        }
        items.Append("""<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""");

        // OPF 故意**不带**名字空间：真实世界里的 EPUB2 经常这样，绑定命名空间会整批读不出目录
        Add("OEBPS/content.opf",
            $"""<?xml version="1.0"?><package version="2.0"><manifest>{items}</manifest><spine toc="ncx">{spine}</spine></package>""");

        Add("OEBPS/toc.ncx",
            $"""<?xml version="1.0"?><ncx version="2005-1"><navMap>{navPoints}</navMap></ncx>""");
        return path;
    }

    private static long ImportBook(SipInstance sip, string epubPath)
    {
        var r = sip.Run("--import", epubPath, "--json");
        Assert.True(r.ExitCode == 0, $"--import 失败（{r.ExitCode}）：{r.Stdout}\n{r.Stderr}");
        using var doc = JsonDocument.Parse(r.Stdout);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>A1：含 NCX 的 EPUB → chaptersSource='ncx'，chapterId 合契约 §1.3 正则，ord 从 1 连续。</summary>
    [Fact]
    public void Toc_EpubWithNcx_ReportsNcxChapters()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        string epub = MakeEpub(sip.Root, "book.epub", chapters: 3);
        long id = ImportBook(sip, epub);

        var r = sip.Run("--toc", id.ToString(), "--json");
        Assert.True(r.ExitCode == 0, $"--toc 失败：{r.Stdout}\n{r.Stderr}");

        using var doc = JsonDocument.Parse(r.Stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("ncx", data.GetProperty("chaptersSource").GetString());
        Assert.Equal(3, data.GetProperty("chapterCount").GetInt32());

        var chapters = data.GetProperty("chapters").EnumerateArray().ToList();
        Assert.Equal(3, chapters.Count);
        var re = new System.Text.RegularExpressions.Regex(
            @"^(epub:\d{1,5}(~[A-Za-z0-9_.:-]{1,64})?|pdf:p\d{1,6}|sec:\d{1,5})$");
        for (int i = 0; i < chapters.Count; i++)
        {
            string cid = chapters[i].GetProperty("chapterId").GetString()!;
            Assert.Matches(re, cid);
            Assert.Equal(i + 1, chapters[i].GetProperty("ord").GetInt32());
            Assert.Contains($"第{i + 1}章", chapters[i].GetProperty("title").GetString());
        }
        // 第一次访问现场建目录 → backfilled=true
        Assert.True(data.GetProperty("backfilled").GetBoolean());
    }

    /// <summary>A3：懒回填幂等 —— 连跑两次，目录完全一致，第二次 backfilled=false。</summary>
    [Fact]
    public void Toc_IsIdempotent()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        long id = ImportBook(sip, MakeEpub(sip.Root, "book.epub", chapters: 2));

        var first = sip.Run("--toc", id.ToString(), "--json");
        var second = sip.Run("--toc", id.ToString(), "--json");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);

        using var d1 = JsonDocument.Parse(first.Stdout);
        using var d2 = JsonDocument.Parse(second.Stdout);
        Assert.True(d1.RootElement.GetProperty("data").GetProperty("backfilled").GetBoolean());
        Assert.False(d2.RootElement.GetProperty("data").GetProperty("backfilled").GetBoolean());

        var c1 = d1.RootElement.GetProperty("data").GetProperty("chapters").ToString();
        var c2 = d2.RootElement.GetProperty("data").GetProperty("chapters").ToString();
        Assert.Equal(c1, c2);   // 行数、chapterId 集合、Ord 完全一致

        // 库里行数也一致（用 --toc 的 chapterCount 间接断言，避免依赖内部实现）
        Assert.Equal(
            d1.RootElement.GetProperty("data").GetProperty("chapterCount").GetInt32(),
            d2.RootElement.GetProperty("data").GetProperty("chapterCount").GetInt32());
    }

    /// <summary>A4 / I2：目录**不依赖 AI** —— 没有 ai_config.json、没有 Key、没有索引，照样 200 / 退出 0。</summary>
    [Fact]
    public void Toc_WorksWithoutAnyAiConfig()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        long id = ImportBook(sip, MakeEpub(sip.Root, "book.epub", chapters: 2));

        // 全新实例本来就没有 ai_config.json；显式确认一下，免得将来默认值变了这条用例悄悄失效
        Assert.False(File.Exists(Path.Combine(sip.DataDir, "ai_config.json")));

        var r = sip.Run("--toc", id.ToString(), "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("\"success\": true", r.Stdout, StringComparison.Ordinal);
    }

    /// <summary>按章读：第 1 章的正文必须只包含第 1 章的内容（不能把整本书倒出来）。</summary>
    [Fact]
    public void Chapter_ReturnsOnlyThatChaptersText()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        long id = ImportBook(sip, MakeEpub(sip.Root, "book.epub", chapters: 3));

        var r = sip.Run("--chapter", id.ToString(), "#2", "--json");
        Assert.True(r.ExitCode == 0, $"--chapter 失败：{r.Stdout}\n{r.Stderr}");

        using var doc = JsonDocument.Parse(r.Stdout);
        var data = doc.RootElement.GetProperty("data");
        string text = data.GetProperty("text").GetString()!;
        Assert.Contains("这是第2章的正文内容", text, StringComparison.Ordinal);
        Assert.DoesNotContain("这是第1章的正文内容", text, StringComparison.Ordinal);
        Assert.DoesNotContain("这是第3章的正文内容", text, StringComparison.Ordinal);
        Assert.Equal(2, data.GetProperty("ord").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    /// <summary>非法章节号必须是**可读的失败**（退出码 3），不能 500 / 不能崩。</summary>
    [Fact]
    public void Chapter_UnknownChapterId_FailsCleanly()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        long id = ImportBook(sip, MakeEpub(sip.Root, "book.epub", chapters: 2));

        var r = sip.Run("--chapter", id.ToString(), "#999", "--json");
        Assert.Equal(3, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("CHAPTER_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>契约 §1.7：删书要**连坐**目录（Chapters），但**不**连坐对话历史。</summary>
    [Fact]
    public void Delete_RemovesChapters()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        long id = ImportBook(sip, MakeEpub(sip.Root, "book.epub", chapters: 2));
        Assert.Equal(0, sip.Run("--toc", id.ToString(), "--json").ExitCode);   // 先把目录建出来

        Assert.Equal("2", sip.QueryScalar("SELECT COUNT(*) FROM Chapters WHERE ItemId = @id", ("@id", id)));

        var del = sip.Run("--import-rm", id.ToString(), "--yes", "--json");
        Assert.Equal(0, del.ExitCode);
        Assert.Equal("0", sip.QueryScalar("SELECT COUNT(*) FROM Chapters WHERE ItemId = @id", ("@id", id)));
    }

    /// <summary>老库兼容：Chapters 表在**第一次 InitDatabase** 时就建好了，CLI 任何命令都不会因为缺表报错。</summary>
    [Fact]
    public void Schema_ExistsOnFreshDatabase()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        Assert.Equal("1", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Chapters'"));
        Assert.Equal("1", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='DbMeta'"));
        // VectorsChunks 的 ChapterId 列也必须补上（老库靠 ALTER 迁移）
        Assert.Equal("1", sip.QueryScalar(
            "SELECT COUNT(*) FROM pragma_table_info('VectorsChunks') WHERE name='ChapterId'"));
    }
}
