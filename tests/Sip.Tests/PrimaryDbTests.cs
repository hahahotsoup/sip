using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 主数据库（跨位置那一份）的契约：**第一次运行认领，之后在别处打开就提示，可改可合**。
///
/// 为什么必须真起进程：这件事的全部内容就是「凭据库里那条记录 + 两个不同位置的库之间的比较」，
/// 只测函数等于把最关键的一环（跨进程、跨目录的状态）测掉了。
///
/// 两个实例共享同一个凭据命名空间（<c>keyName</c>）—— 那正是「同一个用户的两份 sip」。
/// 生产下这条记录不带数据目录作用域，就是为了跨所有副本可见。
/// </summary>
public class PrimaryDbTests : IClassFixture<PrimaryDbFixture>
{
    private readonly PrimaryDbFixture _fx;

    public PrimaryDbTests(PrimaryDbFixture fx) => _fx = fx;

    /// <summary>把「主库」重新指回 A。用例之间不共享假设：谁依赖 A 是主库，谁自己先声明。</summary>
    private void EnsurePrimaryIsA()
    {
        var r = _fx.Primary.Run("db", "set", "--here");
        Assert.Equal(0, r.ExitCode);
    }

    private static JsonElement DataOf(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public void FirstRun_ClaimsTheFolder_AndSaysSo()
    {
        // fixture 里 A 的第一次运行就是「认领」那一次
        Assert.Equal(0, _fx.FirstRun.ExitCode);
        Assert.Contains(_fx.Primary.DataDir, _fx.FirstRun.Stderr);
    }

    [Fact]
    public void SecondRun_InTheSameFolder_IsSilent()
    {
        EnsurePrimaryIsA();
        var r = _fx.Primary.Run("--help");
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain(_fx.Primary.DataDir, r.Stderr);   // 一致就不该再唠叨
    }

    [Fact]
    public void AnotherFolder_GetsTheNotice_WithPrimaryPathAndCommands()
    {
        EnsurePrimaryIsA();
        var r = _fx.Other.Run("--help");

        Assert.Equal(0, r.ExitCode);                            // 只提示，不当门：照样能跑
        Assert.Contains(_fx.Primary.DataDir, r.Stderr);         // 主库在哪
        Assert.Contains("sip db", r.Stderr);                    // 怎么改、怎么合
    }

    [Fact]
    public void Status_ReportsBothSides_AndJsonStdoutStaysClean()
    {
        EnsurePrimaryIsA();

        var a = _fx.Primary.Run("db", "status", "--json");
        Assert.Equal(0, a.ExitCode);
        var da = DataOf(a.Stdout);
        Assert.Equal(_fx.Primary.DataDir, da.GetProperty("primaryDb").GetString());
        Assert.True(da.GetProperty("match").GetBoolean());

        // 在别处看：主库仍是 A，但 match=false —— 且提示走 stderr，stdout 是纯 JSON（脚本要能解析）
        var b = _fx.Other.Run("db", "status", "--json");
        Assert.Equal(0, b.ExitCode);
        var db = DataOf(b.Stdout);
        Assert.Equal(_fx.Primary.DataDir, db.GetProperty("primaryDb").GetString());
        Assert.False(db.GetProperty("match").GetBoolean());
        Assert.Contains("sip db", b.Stderr);
    }

    [Fact]
    public void SetHere_MovesThePrimary_AndBack()
    {
        EnsurePrimaryIsA();
        try
        {
            var set = _fx.Other.Run("db", "set", "--here");
            Assert.Equal(0, set.ExitCode);
            Assert.Equal(_fx.Other.DataDir, DataOf(_fx.Other.Run("db", "status", "--json").Stdout).GetProperty("primaryDb").GetString());

            // 换了主库之后，原来那个（A）开始被提示
            Assert.Contains(_fx.Other.DataDir, _fx.Primary.Run("--help").Stderr);
        }
        finally
        {
            EnsurePrimaryIsA();
        }
    }

    [Fact]
    public void Set_RejectsMissingFolder()
    {
        var r = _fx.Primary.Run("db", "set", Path.Combine(_fx.Primary.Root, "no-such-folder"));
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public void Merge_WithoutYes_IsRefused_WhenNotInteractive()
    {
        EnsurePrimaryIsA();
        var r = _fx.Other.Run("db", "merge");
        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("--yes", r.Stdout);
    }

    [Fact]
    public void Merge_OfThePrimaryIntoItself_IsRefused()
    {
        EnsurePrimaryIsA();
        var r = _fx.Primary.Run("db", "merge", "--yes");
        Assert.NotEqual(0, r.ExitCode);
    }

    /// <summary>合并的主契约：源里同 URL 的源复用、同 Guid 的文章跳过、新文章进来；
    /// 侧挂的收藏与阅读进度按**新 id** 映射过去；再合一次 = 全部跳过（幂等）。</summary>
    [Fact]
    public void Merge_BringsFeedsItemsAndSidecars_AndIsIdempotent()
    {
        EnsurePrimaryIsA();

        var first = _fx.Other.Run("db", "merge", "--yes", "--json");
        Assert.Equal(0, first.ExitCode);
        var d = DataOf(first.Stdout);

        Assert.Equal(1, d.GetProperty("feedsAdded").GetInt32());     // other.xml 是新的
        Assert.Equal(1, d.GetProperty("feedsReused").GetInt32());    // main.xml 与主库同 URL → 复用
        Assert.Equal(2, d.GetProperty("itemsAdded").GetInt32());     // g-dup、g-new
        Assert.Equal(1, d.GetProperty("itemsSkipped").GetInt32());   // g-main 已存在
        Assert.Equal(1, d.GetProperty("signalsMerged").GetInt32());
        Assert.Equal(1, d.GetProperty("progressMerged").GetInt32());

        // 主库里真的多了东西
        Assert.Equal("2", _fx.Primary.QueryScalar("SELECT COUNT(*) FROM Feeds"));
        Assert.Equal("3", _fx.Primary.QueryScalar("SELECT COUNT(*) FROM Items"));
        Assert.Equal("1", _fx.Primary.QueryScalar("SELECT COUNT(*) FROM Items WHERE Guid = 'g-dup'"));
        Assert.Equal("1", _fx.Primary.QueryScalar("SELECT COUNT(*) FROM Items WHERE Guid = 'g-main'"));
        // 复用的那条源没有多出一份同名源
        Assert.Equal("1", _fx.Primary.QueryScalar("SELECT COUNT(*) FROM Feeds WHERE FeedUrl = 'http://example.com/main.xml'"));

        // 侧挂文件按新 id 落地（键是主库里的新编号，不是源里的编号）
        string sig = File.ReadAllText(Path.Combine(_fx.Primary.DataDir, "article_signals.json"));
        Assert.Contains("UserLike", sig);
        using (var doc = JsonDocument.Parse(sig))
            Assert.Equal(1, doc.RootElement.EnumerateObject().Count());
        string prog = File.ReadAllText(Path.Combine(_fx.Primary.DataDir, "reading_progress.json"));
        Assert.Contains("42", prog);

        // 幂等：再合一次什么都不加
        var second = _fx.Other.Run("db", "merge", "--yes", "--json");
        Assert.Equal(0, second.ExitCode);
        var d2 = DataOf(second.Stdout);
        Assert.Equal(0, d2.GetProperty("feedsAdded").GetInt32());
        Assert.Equal(0, d2.GetProperty("itemsAdded").GetInt32());
        Assert.Equal(3, d2.GetProperty("itemsSkipped").GetInt32());
        Assert.Equal("3", _fx.Primary.QueryScalar("SELECT COUNT(*) FROM Items"));
    }
}

/// <summary>
/// 两个**共享同一凭据命名空间**的实例：A = 主库所在，B = 「别的地方」。
/// 两份内容刻意交错：同 URL 的源、同 Guid 的文章、各自独有的源与文章、以及两份侧挂文件。
/// </summary>
public sealed class PrimaryDbFixture : IDisposable
{
    private const string SharedKey = "primary_db_test_shared";
    private const string MainUrl = "http://example.com/main.xml";
    private const string OtherUrl = "http://example.com/other.xml";

    public SipInstance Primary { get; }
    public SipInstance Other { get; }

    /// <summary>A 的第一次运行（认领那一次）的输出，供用例断言。</summary>
    public (int ExitCode, string Stdout, string Stderr) FirstRun { get; }

    public PrimaryDbFixture()
    {
        Primary = new SipInstance(keyName: SharedKey);
        Other = new SipInstance(keyName: SharedKey);

        // A 的第一次运行 = 认领；顺带把库建出来
        FirstRun = Primary.Run("--help");
        // B 也要跑一次才有 rss.db（这就是「在别的地方打开」那一次，此时 A 已认领）
        Other.Run("--help");

        // A：一条源 + 一篇 g-main
        Primary.InsertFeed(1, "主库源", MainUrl);
        Primary.InsertItem(1, 1, "主库里的文章", "http://example.com/main-1", "正文", "g-main");

        // B：同 URL 的源（应复用）+ 它的两篇新文章（g-dup、g-main 重复项），另一条独有源 + 一篇文章
        Other.InsertFeed(1, "副本里的同名源", MainUrl);
        Other.InsertFeed(2, "副本独有源", OtherUrl);
        Other.InsertItem(1, 1, "副本里的新文章", "http://example.com/other-1", "正文", "g-dup");
        Other.InsertItem(2, 1, "主库里已有的那篇", "http://example.com/main-1", "正文", "g-main");
        Other.InsertItem(3, 2, "副本独有源里的文章", "http://example.com/other-2", "正文", "g-new");

        // B 的侧挂：收藏一篇、读到一半一篇
        File.WriteAllText(Path.Combine(Other.DataDir, "article_signals.json"),
            """{"1":{"UserLike":true,"AiLike":false,"AiReason":"","UpdatedAt":"2026-09-24T00:00:00"}}""");
        File.WriteAllText(Path.Combine(Other.DataDir, "reading_progress.json"), """{"1":42}""");
    }

    public void Dispose()
    {
        Primary.Dispose();
        Other.Dispose();
    }
}
