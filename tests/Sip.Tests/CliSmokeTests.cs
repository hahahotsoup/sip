using Xunit;

namespace Sip.Tests;

/// <summary>CLI 契约冒烟:退出码与基础输出结构(空库 fixture)。</summary>
public class CliSmokeTests
{
    [Fact]
    public void Help_ReturnsZero_AndPrintsUsage()
    {
        using var sip = new SipInstance();
        var (exit, stdout, _) = sip.Run("--help");
        Assert.Equal(0, exit);
        Assert.Contains("sip", stdout);
        Assert.Contains("用法", stdout);
    }

    [Fact]
    public void UnknownCommand_ReturnsOne()
    {
        using var sip = new SipInstance();
        var (exit, stdout, _) = sip.Run("--bogus-command");
        Assert.Equal(1, exit);
        Assert.Contains("未知命令", stdout);
    }

    [Fact]
    public void List_EmptyDb_ReturnsZero()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        var (exit, _, _) = sip.Run("-l");
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Grep_NoHit_ReturnsZero_AndZeroCount()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        var (exit, stdout, _) = sip.Run("--grep", "不存在关键词xyz123");
        Assert.Equal(0, exit);
        Assert.Contains("0", stdout);
    }

    [Fact]
    public void Today_EmptyDb_ReturnsZero()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        var (exit, stdout, _) = sip.Run("--today");
        Assert.Equal(0, exit);
        Assert.Contains("今日哈汤", stdout);
    }

    [Fact]
    public void Fulltext_MissingItem_ExitCode3()
    {
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        var (exit, stdout, _) = sip.Run("--fulltext", "999", "--yes");
        Assert.Equal(3, exit);
        Assert.True(stdout.Contains("没有找到文章", StringComparison.OrdinalIgnoreCase)
                 || stdout.Contains("not found", StringComparison.OrdinalIgnoreCase),
            $"unexpected output: {stdout}");
    }
}
