using Xunit;

namespace Sip.Tests;

/// <summary>
/// 终端注入防护:RSS 源可在标题/正文里塞 ANSI 转义序列(\x1b[...),
/// TUI 渲染与 CLI 导出(共用 BuildArticleMarkdown)必须剥掉,
/// 否则恶意源能控制用户终端(清屏/伪造提示/滚走缓冲区)。
///
/// 注意:断言一律显式 StringComparison.Ordinal —— xunit 的
/// Assert.Contains/DoesNotContain(string, string) 默认 CurrentCulture,
/// 而 0x1B 是文化比较下的「可忽略字符」,会导致 DoesNotContain 误报。
/// </summary>
public class TerminalInjectionTests
{
    private const string Esc = "\u001b";

    private static SipInstance NewWithEscItem()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        sip.InsertFeed(1, "FeedA", "http://example.com/feedA.xml");
        sip.InsertItem(1, 1,
            "正常标题" + Esc + "[31m红字标题",
            "http://example.com/a1",
            "正文第一段。" + Esc + "[2J清屏指令" + Esc + "[0m",
            "g1");
        return sip;
    }

    [Fact]
    public void Export_StripsEscapesFromTitleAndBody()
    {
        using var sip = NewWithEscItem();
        var outFile = Path.Combine(sip.Root, "out.md");
        var (exit, _, _) = sip.Run("--export", "1", outFile, "--yes");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(outFile));
        string md = File.ReadAllText(outFile);
        Assert.DoesNotContain(Esc, md, StringComparison.Ordinal);
        // 过滤后正文与标题内容仍应保留(只剥控制字符,不剥可见文本)
        Assert.Contains("正常标题", md, StringComparison.Ordinal);
        Assert.Contains("红字标题", md, StringComparison.Ordinal);
        Assert.Contains("正文第一段", md, StringComparison.Ordinal);
        Assert.Contains("清屏指令", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ListFeed_StripsEscapesFromTitle()
    {
        using var sip = NewWithEscItem();
        var (exit, stdout, _) = sip.Run("-l", "1");
        Assert.Equal(0, exit);
        Assert.DoesNotContain(Esc, stdout, StringComparison.Ordinal);
        Assert.Contains("正常标题", stdout, StringComparison.Ordinal);
        Assert.Contains("红字标题", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Grep_StripsEscapesFromTitle()
    {
        using var sip = NewWithEscItem();
        var (exit, stdout, _) = sip.Run("--grep", "红字标题");
        Assert.Equal(0, exit);
        Assert.DoesNotContain(Esc, stdout, StringComparison.Ordinal);
        Assert.Contains("红字标题", stdout, StringComparison.Ordinal);
    }
}
