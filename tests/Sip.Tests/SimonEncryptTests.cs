using Xunit;

namespace Sip.Tests;

/// <summary>
/// 孟思琳(simon)挡位 3 数据加密(SQLCipher):
///  · 升挡 3 后 rss.db 被加密(.db-encrypted 标记 + .plaintext.bak 备份)
///  · sip 自身(带系统凭据库密钥)正常读写
///  · 「其他软件」(测试用普通 SQLite 连接,无密钥)读加密库必须失败
/// </summary>
public class SimonEncryptTests
{
    private static void AssertLevel3(SipInstance sip, string step)
    {
        var (exit, stdout, stderr) = sip.Run("simon", "level", "3");
        if (exit != 0)
            throw new Xunit.Sdk.XunitException($"[{step}] level 3 failed: exit={exit}\nstdout={stdout}\nstderr={stderr}");
    }

    [Fact]
    public void Level3_EncryptsDb_AndExternalReaderCannotRead()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1, "秘密文章", "http://example.com/a1", "机密内容xyz", "g1");

        AssertLevel3(sip, "encrypt");

        // 加密标记 + 明文备份
        Assert.True(File.Exists(Path.Combine(sip.DataDir, ".db-encrypted")));
        Assert.True(File.Exists(sip.DbPath + ".plaintext.bak"));

        // sip 自身正常读取(密钥在系统凭据库)
        var (exit, stdout, _) = sip.Run("-l", "1");
        Assert.Equal(0, exit);
        Assert.Contains("秘密文章", stdout);

        // 「其他软件」:无密钥的普通 SQLite 连接读加密库 → 必须失败
        Assert.ThrowsAny<Exception>(() => sip.QueryScalar("SELECT COUNT(*) FROM Items"));
    }

    [Fact]
    public void EncryptionSurvivesRestart_AndRemainsReadable()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1, "重启后仍在", "http://example.com/a1", "机密内容xyz", "g1");
        AssertLevel3(sip, "encrypt");

        // 同一数据目录下多次调用仍可读
        for (int i = 0; i < 3; i++)
        {
            var (exit, stdout, _) = sip.Run("-l", "1");
            Assert.Equal(0, exit);
            Assert.Contains("重启后仍在", stdout);
        }
    }

    [Fact]
    public void Status_ReportsEncryptionOn()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        AssertLevel3(sip, "encrypt");
        var (exit, stdout, _) = sip.Run("simon", "status");
        Assert.Equal(0, exit);
        Assert.Contains("已开启", stdout);
    }
}
