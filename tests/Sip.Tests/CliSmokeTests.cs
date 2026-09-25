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

    [Fact]
    public void Diff_OneLineHtml_PrintsOnlyTheChangedParagraph()
    {
        // 回归：RSS 的正文常常整篇只有一行 HTML（段落靠 <p> 分，行里没有换行）。
        // 旧行为是**按行** diff → 输出里只有一条"整篇 <p>…"的删除和一条整篇的插入，
        // 网页上就是"一大块红接一大块绿"（用户报的"比对功能严重问题"）。
        // 正确行为：按段落比，没变的段落不打印，只报改了的那一段。
        using var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "源 A", "http://a.example/feed.xml");
        const string oldBody = "<p>第一段两版一致，用来确认没被当成重写。</p><p>第二段：结论是清缓存就好。</p><p>结尾也一致。</p>";
        const string newBody = "<p>第一段两版一致，用来确认没被当成重写。</p><p>第二段：更正——是 DNS split-horizon。</p><p>结尾也一致。</p>";
        sip.InsertItem(701, 1, "一行 HTML 的改稿", "http://a.example/701", oldBody, "g-cli-html");
        sip.Exec("UPDATE Items SET Version=1, Status='archived' WHERE Id=701");
        sip.InsertItem(702, 1, "一行 HTML 的改稿", "http://a.example/702", newBody, "g-cli-html");
        sip.Exec("UPDATE Items SET Version=2 WHERE Id=702");

        var (exit, stdout, _) = sip.Run("--diff", "702");
        Assert.Equal(0, exit);

        var minus = stdout.Split('\n').Where((l) => l.StartsWith("- ")).ToList();
        var plus = stdout.Split('\n').Where((l) => l.StartsWith("+ ")).ToList();
        Assert.Single(minus);
        Assert.Single(plus);
        Assert.Contains("清缓存就好", minus[0]);
        Assert.Contains("split-horizon", plus[0]);
        Assert.DoesNotContain("<p>", stdout);                  // 标签不该出现在差异文本里
        Assert.DoesNotContain("第一段两版一致", stdout);        // 没变的段落不打印
        Assert.DoesNotContain("结尾也一致", stdout);
    }
}
