using Xunit;

namespace Sip.Tests;

/// <summary>
/// 孟思琳(simon)安全守护(默认开启,无法关闭,只能调节挡位):
///  · 挡位 1(默认):不拦截,行为不变
///  · 挡位 2:非交互调用禁破坏性命令(--remove/--purge-fulltext/telemetry clear/--dedup undo 等)
///  · 挡位 3:非交互调用禁全部写操作(只读命令仍可用)
///  · 降挡必须交互终端(测试进程 stdin 已重定向=非交互,降挡应被拒绝)
/// 测试进程 stdin 重定向 → Console.IsInputRedirected=true,即"非交互调用"场景
/// </summary>
public class SimonTests
{
    private static SipInstance NewInstance()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        return sip;
    }

    [Fact]
    public void DefaultLevel_IsOne_AndDoesNotBlock()
    {
        using var sip = NewInstance();
        var (exit, stdout, _) = sip.Run("simon", "status");
        Assert.Equal(0, exit);
        Assert.Contains("孟思琳", stdout);
        Assert.Contains("1", stdout);   // 挡位 1

        // 挡位 1:非交互破坏性命令不拦截(行为不变)
        var (exit2, _, _) = sip.Run("-r", "999", "--yes");
        Assert.Equal(1, exit2);         // 源不存在报错(未被拦截为 3)
    }

    [Fact]
    public void Level2_BlocksAllWrites_ReadOnlyStillWorks()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "2").ExitCode);

        // 挡位 2:CLI 写操作一律拒绝(无论交互/非交互)——删除、更新、添加、点赞全拒
        Assert.Equal(3, sip.Run("-r", "1", "--yes").ExitCode);
        Assert.Equal(3, sip.Run("-u", "1").ExitCode);
        Assert.Equal(3, sip.Run("-d", "http://example.com/f.xml").ExitCode);
        Assert.Equal(3, sip.Run("--like", "1").ExitCode);
        Assert.Contains("孟思琳", sip.Run("-r", "1", "--yes").Stdout);

        // 只读命令不受影响
        Assert.Equal(0, sip.Run("-l").ExitCode);
        Assert.Equal(0, sip.Run("simon", "status").ExitCode);

        // 拦截事件已记录
        var (exit3, status, _) = sip.Run("simon", "status", "--json");
        Assert.Equal(0, exit3);
        Assert.Contains("blocked_cmd", status);
    }

    [Fact]
    public void Level2_BlocksDedupUndo_AndPurge()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "2").ExitCode);
        Assert.Equal(3, sip.Run("--dedup", "undo", "x").ExitCode);
        Assert.Equal(3, sip.Run("--purge-fulltext", "--yes").ExitCode);
    }

    [Fact]
    public void Level3_BlocksAllCli_ExceptSimonStatus()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "3").ExitCode);

        // 挡位 3:CLI 所有调用一律拒绝(读写都拒)
        Assert.Equal(3, sip.Run("-u", "1").ExitCode);          // 写
        Assert.Equal(3, sip.Run("--like", "1").ExitCode);      // 写
        Assert.Equal(3, sip.Run("-l").ExitCode);               // 读也拒
        Assert.Equal(3, sip.Run("--grep", "熊猫").ExitCode);   // 读也拒
        Assert.Equal(3, sip.Run("--help").ExitCode);           // 帮助也拒
        // 唯一例外:simon status(守护状态查询)
        Assert.Equal(0, sip.Run("simon", "status").ExitCode);
    }

    [Fact]
    public void Downgrade_RequiresInteractiveTerminal()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "3").ExitCode);

        // 非交互降挡必须被拒绝(守护不能被脚本调弱)
        var (exit, stdout, _) = sip.Run("simon", "level", "1");
        Assert.NotEqual(0, exit);
        Assert.Contains("孟思琳", stdout);

        // 挡位仍为 3
        var (_, status, _) = sip.Run("simon", "status");
        Assert.Contains("3", status);
    }

    [Fact]
    public void InvalidLevel_Rejected()
    {
        using var sip = NewInstance();
        Assert.NotEqual(0, sip.Run("simon", "level", "0").ExitCode);
        Assert.NotEqual(0, sip.Run("simon", "level", "9").ExitCode);
        Assert.NotEqual(0, sip.Run("simon", "level", "off").ExitCode);   // 不存在 off = 无法关闭
    }
}
