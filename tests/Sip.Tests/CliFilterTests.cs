using Xunit;

namespace Sip.Tests;

/// <summary>
/// 本轮新增契约(Bot 实测反馈的两个痛点):
///  · --grep --feed N 限源(之前 --feed 被忽略,依然全库搜)
///  · -l N --limit M 截断(之前大源全量输出)
/// 编号规则(实测更正):-l N 用显示序号,--show/--versions/--summary 用真实 itemId。
/// </summary>
public class CliFilterTests
{
    private const string Kw = "熊猫";

    private static SipInstance NewWithFixtures()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertFeed(2, "FeedB", "http://example.com/feedB.xml");
        sip.InsertItem(1, 1, "甲源熊猫一", "http://example.com/a1", "讲熊猫的习性。", "g1");
        sip.InsertItem(2, 1, "甲源熊猫二", "http://example.com/a2", "熊猫保护措施。", "g2");
        sip.InsertItem(3, 2, "乙源熊猫一", "http://example.com/b1", "讲熊猫的习性。", "g3");
        sip.InsertItem(4, 2, "乙源无关文", "http://example.com/b2", "完全无关的内容。", "g4");
        return sip;
    }

    [Fact]
    public void Grep_WithoutFeed_SearchesAllFeeds()
    {
        using var sip = NewWithFixtures();
        var (exit, stdout, _) = sip.Run("--grep", Kw);
        Assert.Equal(0, exit);
        Assert.Contains("甲源熊猫一", stdout);
        Assert.Contains("乙源熊猫一", stdout);
        Assert.DoesNotContain("乙源无关文", stdout);
    }

    [Fact]
    public void Grep_WithFeed_FiltersToThatFeed()
    {
        using var sip = NewWithFixtures();
        var (exit, stdout, _) = sip.Run("--grep", Kw, "--feed", "1");
        Assert.Equal(0, exit);
        Assert.Contains("甲源熊猫一", stdout);
        Assert.Contains("甲源熊猫二", stdout);
        Assert.DoesNotContain("乙源熊猫一", stdout);   // 关键:跨源命中必须被过滤
    }

    [Fact]
    public void Grep_WithFeedJson_KeepsFeedFilter()
    {
        using var sip = NewWithFixtures();
        var (exit, stdout, _) = sip.Run("--grep", Kw, "--feed", "1", "--json");
        Assert.Equal(0, exit);
        // JSON 输出经默认编码器会把中文转义为 \uXXXX,这里用不转义的 itemId 断言
        Assert.Contains("\"itemId\": 1", stdout);
        Assert.Contains("\"itemId\": 2", stdout);
        Assert.DoesNotContain("\"itemId\": 3", stdout);   // feed2 的命中必须被过滤
    }

    [Fact]
    public void Grep_WithInvalidFeed_ReturnsError()
    {
        using var sip = NewWithFixtures();
        var (exit, _, _) = sip.Run("--grep", Kw, "--feed", "999");
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public void ListFeed_WithLimit_TruncatesOutput()
    {
        using var sip = NewWithFixtures();   // 源1 有 2 篇
        var (exit, stdout, _) = sip.Run("-l", "1", "--limit", "1");
        Assert.Equal(0, exit);
        Assert.Contains("甲源熊猫一", stdout);
        Assert.DoesNotContain("甲源熊猫二", stdout);
    }

    [Fact]
    public void ListFeed_WithoutLimit_ReturnsAll()
    {
        using var sip = NewWithFixtures();
        var (exit, stdout, _) = sip.Run("-l", "1");
        Assert.Equal(0, exit);
        Assert.Contains("甲源熊猫一", stdout);
        Assert.Contains("甲源熊猫二", stdout);
    }
}
