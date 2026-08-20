using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// Phase2 ingest 测试：stats + cleanup + tags + tree
/// </summary>
public class IngestPhase2Tests
{
    private static SipInstance NewInstance()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        return sip;
    }

    private static void InsertEvidence(SipInstance sip, long id, string sourceKey, string content, string hash,
        string observedAt = "2026-08-16T00:00:00+08:00", int ttlDays = 30, int viewCount = 0)
        => sip.Exec($@"
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Excerpt, Content, Hash,
                                      Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays, ViewCount)
                VALUES (@id, 'sip-evidence-v1', 'evidence', @k, '测试源', 'https://example.com/a', '标题', '片段', @c, @h,
                        1, 'active', '2026-08-16T00:00:00+08:00', @obs, 'fresh', @ttl, @vc)",
            ("@id", id), ("@k", sourceKey), ("@c", content), ("@h", hash),
            ("@obs", observedAt), ("@ttl", ttlDays), ("@vc", viewCount));

    // ── stats ──

    [Fact]
    public void Stats_EmptyDb_ReturnsZero()
    {
        using var sip = NewInstance();
        var r = sip.Run("ingest", "stats");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("证据：0 条", r.Stdout);
    }

    [Fact]
    public void Stats_WithEvidence_ReturnsCorrectCounts()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:test1", "内容1", "hash1");
        InsertEvidence(sip, 2, "evidence:test2", "内容2", "hash2");

        var r = sip.Run("ingest", "stats");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("证据：2 条", r.Stdout);
    }

    [Fact]
    public void Stats_JsonOutput()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:test1", "内容1", "hash1");

        var r = sip.Run("ingest", "stats", "--json");
        Assert.Equal(0, r.ExitCode);
        var doc = JsonDocument.Parse(r.Stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("data").GetProperty("totalEvidence").GetInt64());
    }

    // ── cleanup ──

    [Fact]
    public void Cleanup_EmptyDb_NothingToDo()
    {
        using var sip = NewInstance();
        var r = sip.Run("ingest", "cleanup", "--stale");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("没有过期证据", r.Stdout);
    }

    [Fact]
    public void Cleanup_StaleEvidence_ListedForDeletion()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:old", "旧内容", "hash_old",
            observedAt: "2026-01-01T00:00:00+08:00", ttlDays: 1);

        var r = sip.Run("ingest", "cleanup", "--stale", "--dry-run");
        Assert.Contains("待清理", r.Stdout);
    }

    [Fact]
    public void Cleanup_FrequentView_NotDeleted()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:frequent", "常用内容", "hash_freq",
            observedAt: "2026-01-01T00:00:00+08:00", ttlDays: 1, viewCount: 5);

        var r = sip.Run("ingest", "cleanup", "--stale");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("保留", r.Stdout);
    }

    [Fact]
    public void Cleanup_DryRun_NoDeletion()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:old", "旧内容", "hash_old",
            observedAt: "2026-01-01T00:00:00+08:00", ttlDays: 1);

        var r = sip.Run("ingest", "cleanup", "--stale", "--dry-run");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("dry-run", r.Stdout);
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM Evidence WHERE Id = 1"));
    }

    [Fact]
    public void Cleanup_JsonOutput()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:old", "旧内容", "hash_old",
            observedAt: "2026-01-01T00:00:00+08:00", ttlDays: 1);

        var r = sip.Run("ingest", "cleanup", "--stale", "--json");
        Assert.Equal(0, r.ExitCode);
        var doc = JsonDocument.Parse(r.Stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("toDelete").GetArrayLength() > 0);
    }

    // ── tags ──

    [Fact]
    public void Init_CreatesTagsTables()
    {
        using var sip = NewInstance();
        Assert.Equal("2", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Tags','EvidenceTags')"));
    }

    [Fact]
    public void TagAdd_CreatesTagAndLink()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:test", "内容", "hash");

        var r = sip.Run("ingest", "tag", "add", "1", "AI");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("已添加", r.Stdout);

        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM Tags WHERE Name = 'AI'"));
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM EvidenceTags WHERE EvidenceId = 1"));
    }

    [Fact]
    public void TagList_ShowsTags()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:test", "内容", "hash");
        sip.Run("ingest", "tag", "add", "1", "AI");

        var r = sip.Run("ingest", "tag", "list");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("AI", r.Stdout);
    }

    [Fact]
    public void TagRm_RemovesLink()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:test", "内容", "hash");
        sip.Run("ingest", "tag", "add", "1", "AI");

        var r = sip.Run("ingest", "tag", "rm", "1", "AI");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("移除", r.Stdout);

        Assert.Equal("0", sip.QueryScalar("SELECT COUNT(*) FROM EvidenceTags WHERE EvidenceId = 1"));
    }

    // ── tree ──

    [Fact]
    public void Tree_ShowsChildEvidence()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:parent", "父评论", "hash_parent");
        sip.Exec("""
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, Title, Content, Hash, Version, Status, FragmentId, CapturedAt, ObservedAt, Freshness, TtlDays)
                VALUES (2, 'sip-evidence-v1', 'evidence', 'evidence:child', '子评论', '子内容', 'hash_child', 1, 'active', '1', '2026-08-16T00:00:00+08:00', '2026-08-16T00:00:00+08:00', 'fresh', 30)
                """);

        var r = sip.Run("ingest", "tree", "1");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("[1]", r.Stdout);
        Assert.Contains("[2]", r.Stdout);
    }
}
