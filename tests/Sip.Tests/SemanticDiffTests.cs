using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 语义 diff 测试
/// </summary>
public class SemanticDiffTests
{
    private static SipInstance NewInstance()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        return sip;
    }

    private static void InsertFeed(SipInstance sip, int feedId = 1)
        => sip.Exec($@"
            INSERT INTO Feeds (Id, Title, FeedUrl)
            VALUES ({feedId}, '测试源', 'https://example.com/feed')");

    [Fact]
    public void Diff_WithSemantic_ShowsGradeInfo()
    {
        using var sip = NewInstance();
        InsertFeed(sip);

        // 创建两个版本的文章
        sip.Exec("""
                INSERT INTO Items (Id, FeedId, Guid, Title, Content, Version, Status)
                VALUES (1, 1, 'test-guid-1', '测试文章', '这是第一版内容，讲的是人工智能的发展。', 1, 'active')
                """);
        sip.Exec("""
                INSERT INTO Items (Id, FeedId, Guid, Title, Content, Version, Status)
                VALUES (2, 1, 'test-guid-1', '测试文章', '这是第二版内容，讲的是大模型的发展。', 2, 'active')
                """);

        var r = sip.Run("--diff", "1", "--semantic");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("语义距离", r.Stdout);
    }

    [Fact]
    public void Diff_WithSemantic_JsonOutput()
    {
        using var sip = NewInstance();
        InsertFeed(sip);

        sip.Exec("""
                INSERT INTO Items (Id, FeedId, Guid, Title, Content, Version, Status)
                VALUES (1, 1, 'test-guid-2', '测试文章', '原始内容版本一。', 1, 'active')
                """);
        sip.Exec("""
                INSERT INTO Items (Id, FeedId, Guid, Title, Content, Version, Status)
                VALUES (2, 1, 'test-guid-2', '测试文章', '修改后内容版本二。', 2, 'active')
                """);

        var r = sip.Run("--diff", "1", "--semantic", "--json");
        Assert.Equal(0, r.ExitCode);
        var doc = JsonDocument.Parse(r.Stdout);
        Assert.True(doc.RootElement.TryGetProperty("semantic", out var semantic));
        Assert.True(semantic.TryGetProperty("distance", out _));
        Assert.True(semantic.TryGetProperty("grade", out _));
    }

    [Fact]
    public void Diff_WithoutSemantic_NoGradeInfo()
    {
        using var sip = NewInstance();
        InsertFeed(sip);

        sip.Exec("""
                INSERT INTO Items (Id, FeedId, Guid, Title, Content, Version, Status)
                VALUES (1, 1, 'test-guid-3', '测试文章', '原始内容。', 1, 'active')
                """);
        sip.Exec("""
                INSERT INTO Items (Id, FeedId, Guid, Title, Content, Version, Status)
                VALUES (2, 1, 'test-guid-3', '测试文章', '修改后内容。', 2, 'active')
                """);

        var r = sip.Run("--diff", "1");
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("语义距离", r.Stdout);
    }
}
