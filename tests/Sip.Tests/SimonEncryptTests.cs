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

    // 降挡模拟(等价于用户在 TUI 降挡):挡位权威值在系统凭据库,
    // 直接改 sip_settings.json 无效——测试进程写该实例隔离凭据(sip.KeyName + "_level")
    private static void DowngradeForTest(SipInstance sip, int level)
    {
        var store = ktsu.CredentialCache.Storage.CredentialStoreFactory.CreateDefault("hotsoupreader");
        var cache = new ktsu.CredentialCache.CredentialCache(store);
        cache.AddOrReplace(
            new ktsu.CredentialCache.PersonaGUID { WeakString = sip.KeyName + "_level" },
            new ktsu.CredentialCache.CredentialWithToken { Token = new ktsu.CredentialCache.CredentialToken { WeakString = level.ToString() } });
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

        // 「其他软件」:无密钥的普通 SQLite 连接读加密库 → 必须失败
        Assert.ThrowsAny<Exception>(() => sip.QueryScalar("SELECT COUNT(*) FROM Items"));

        // 挡位 3 下 CLI 全拒,无法用 -l 验证可读;
        // 降挡模拟(等价于用户在 TUI 降挡,挡位权威值在系统凭据库):
        // 验证加密库在低挡位仍可读(密钥在系统凭据库,与挡位无关)
        DowngradeForTest(sip, 1);

        var (exit, stdout, _) = sip.Run("-l", "1");
        Assert.Equal(0, exit);
        Assert.Contains("秘密文章", stdout);
    }

    [Fact]
    public void EncryptionSurvivesRestart_AndRemainsReadable()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1, "重启后仍在", "http://example.com/a1", "机密内容xyz", "g1");
        AssertLevel3(sip, "encrypt");

        // 降回挡位 1(见上一测试的说明),验证加密库多次调用仍可读
        DowngradeForTest(sip, 1);

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

    [Fact]
    public void Reencrypt_IsBlocked_NoDuplicateBackup()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1, "幂等测试文章", "http://example.com/a1", "机密内容xyz", "g1");

        // 第一次升挡 3 → 加密
        AssertLevel3(sip, "first encrypt");
        var bak = sip.DbPath + ".plaintext.bak";
        Assert.True(File.Exists(bak));

        // 挡位 3 下再次 level 3:被拦截在门外(exit 3)——不可能重复加密/重复复制
        var (exit, stdout, _) = sip.Run("simon", "level", "3");
        Assert.Equal(3, exit);
        Assert.Contains("孟思琳", stdout);

        // 备份只有一份,无重复复制
        Assert.Single(Directory.GetFiles(sip.DataDir, "rss.db.plaintext.bak*"));
    }
}
