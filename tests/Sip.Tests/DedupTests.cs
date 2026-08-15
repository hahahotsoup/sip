using Xunit;

namespace Sip.Tests;

/// <summary>
/// dedup 领域不变量(黑盒 + DB 双重断言):
///  · hide 自己必须失败
///  · hide 相似文章成功,且被隐藏者 Status='dedup'
///  · 重复 hide 幂等失败(不产生第二条规则)
///  · 不相似文章 hide 必须失败(防误伤)
///  · undo 恢复 Status='active'
/// </summary>
public class DedupTests
{
    private const string SharedBody = "第一段:这是一篇关于技术的文章。\n第二段:讨论了架构与工程实践。\n第三段:结论是坚持长期主义。";

    private static SipInstance NewWithItems()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertFeed(2, "FeedB", "http://example.com/feedB.xml");
        sip.InsertItem(1, 1, "A 转载", "http://example.com/a1", SharedBody, "g1");
        sip.InsertItem(2, 2, "B 转载", "http://example.com/b1", SharedBody, "g2");
        sip.InsertItem(3, 2, "C 原创", "http://example.com/c1", "完全不同的正文内容。", "g3");
        return sip;
    }

    [Fact]
    public void HideSelf_IsRejected()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--dedup", "hide", "1", "1");
        Assert.NotEqual(0, exit);
        Assert.Contains("不能隐藏自己", stdout);
    }

    [Fact]
    public void HideSimilar_Succeeds_AndSetsDedupStatus()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--dedup", "hide", "1", "2");
        Assert.Equal(0, exit);
        Assert.Contains("已隐藏", stdout);
        Assert.Equal("dedup", sip.ItemStatus(1));
        Assert.Equal("active", sip.ItemStatus(2));   // canonical 保持 active
    }

    [Fact]
    public void HideAgain_IsRejected_Idempotent()
    {
        using var sip = NewWithItems();
        Assert.Equal(0, sip.Run("--dedup", "hide", "1", "2").ExitCode);
        var (exit, stdout, _) = sip.Run("--dedup", "hide", "1", "2");
        Assert.NotEqual(0, exit);
        Assert.Contains("已经", stdout);
    }

    [Fact]
    public void HideDissimilar_IsRejected()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--dedup", "hide", "2", "3");
        Assert.NotEqual(0, exit);
        Assert.Contains("不相似", stdout);
        Assert.Equal("active", sip.ItemStatus(3));
    }

    [Fact]
    public void Undo_RestoresActive()
    {
        using var sip = NewWithItems();
        Assert.Equal(0, sip.Run("--dedup", "hide", "1", "2").ExitCode);
        Assert.Equal("dedup", sip.ItemStatus(1));

        // 规则键格式: {hiddenFeedId}:{hiddenUrl}
        var (exit, stdout, _) = sip.Run("--dedup", "undo", "1:http://example.com/a1");
        Assert.Equal(0, exit);
        Assert.Equal("active", sip.ItemStatus(1));
    }
}
