using Xunit;

namespace Sip.Tests;

/// <summary>SSRF 防护矩阵:全文抓取必须拒绝回环 / 云元数据 / 私网地址。</summary>
public class SsrfTests
{
    private static SipInstance NewWithItems()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1, "Loopback", "http://127.0.0.1/secret", "短正文", "g1");
        sip.InsertItem(2, 1, "Metadata", "http://169.254.169.254/latest/meta-data", "短正文", "g2");
        sip.InsertItem(3, 1, "Private", "http://192.168.1.10/internal", "短正文", "g3");
        return sip;
    }

    [Fact]
    public void Loopback_IsRejected()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--fulltext", "1", "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("127.0.0.1", stdout);
    }

    [Fact]
    public void CloudMetadata_IsRejected()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--fulltext", "2", "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("169.254", stdout);
    }

    [Fact]
    public void PrivateRange_IsRejected()
    {
        using var sip = NewWithItems();
        var (exit, stdout, _) = sip.Run("--fulltext", "3", "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("192.168", stdout);
    }

    [Fact]
    public void NonHttpScheme_IsRejected()
    {
        using var sip = NewWithItems();
        sip.Exec("UPDATE Items SET Link = 'file:///etc/passwd' WHERE Id = 1");
        var (exit, stdout, _) = sip.Run("--fulltext", "1", "--yes");
        Assert.NotEqual(0, exit);
        Assert.True(stdout.Contains("Invalid URL", StringComparison.OrdinalIgnoreCase)
                 || stdout.Contains("http", StringComparison.OrdinalIgnoreCase),
            $"unexpected output: {stdout}");
    }
}
