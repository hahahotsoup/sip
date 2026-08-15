using Xunit;

namespace Sip.Tests;

/// <summary>
/// FTS5 全文搜索行为(百万级适配):
///  · ≥3 字符关键词走 FTS(trigram)命中
///  · 短词(<3 字符)LIKE 回退仍命中
///  · fixture 直插 DB(绕过增量同步)后,grep 的懒回填兜底仍能命中
///  · 大小写/无匹配正常
/// </summary>
public class GrepFtsTests
{
    private static SipInstance NewWithItems()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1, "量子计算的工程实践", "http://example.com/a1", "量子计算在密码学与优化问题上有前景。", "g1");
        sip.InsertItem(2, 1, "熊猫保护", "http://example.com/a2", "熊猫的栖息地保护。", "g2");
        return sip;
    }

    [Fact]
    public void Grep_ThreePlusChars_HitsViaFts()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--grep", "量子计算");
        Assert.Equal(0, exit);
        Assert.Contains("量子计算的工程实践", stdout);
    }

    [Fact]
    public void Grep_TwoChar_FallsBackToLike()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--grep", "熊猫");
        Assert.Equal(0, exit);
        Assert.Contains("熊猫保护", stdout);
    }

    [Fact]
    public void Grep_TriggersLazyBackfill_WhenFtsMissing()
    {
        // fixture 直插 DB(不走 InsertNewItem 的增量同步),FTS 为空;
        // grep 必须触发懒回填后仍命中(老库升级/回填兜底路径)
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--grep", "密码学");
        Assert.Equal(0, exit);
        Assert.Contains("量子计算在密码学", stdout);
    }

    [Fact]
    public void Grep_NoMatch_ReturnsZero()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--grep", "不存在的词组xyz");
        Assert.Equal(0, exit);
        Assert.Contains("0", stdout);
    }
}
