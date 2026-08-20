using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// watch 命令测试
/// </summary>
public class WatchCliTests
{
    private static SipInstance NewInstance()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        return sip;
    }

    private static void InsertWatchEvidence(SipInstance sip, long id, string url, string content, string hash)
        => sip.Exec($@"
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Excerpt, Content, Hash,
                                      Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays, WatchEnabled, WatchInterval)
                VALUES (@id, 'sip-evidence-v1', 'watch', @sk, '测试源', @url, '标题', '片段', @c, @h,
                        1, 'active', '2026-08-16T00:00:00+08:00', '2026-08-16T00:00:00+08:00', 'fresh', 30, 0, 5)",
            ("@id", id), ("@sk", "watch:" + url), ("@url", url), ("@c", content), ("@h", hash));

    [Fact]
    public void WatchAdd_EnablesMonitoring()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");

        var r = sip.Run("ingest", "watch", "add", "1");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("已启用监控", r.Stdout);

        Assert.Equal("1", sip.QueryScalar("SELECT WatchEnabled FROM Evidence WHERE Id = 1"));
    }

    [Fact]
    public void WatchAdd_WithInterval()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");

        var r = sip.Run("ingest", "watch", "add", "1", "--interval", "10");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("10 分钟", r.Stdout);

        Assert.Equal("10", sip.QueryScalar("SELECT WatchInterval FROM Evidence WHERE Id = 1"));
    }

    [Fact]
    public void WatchAdd_MinIntervalEnforced()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");

        var r = sip.Run("ingest", "watch", "add", "1", "--interval", "1");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("5 分钟", r.Stdout);

        Assert.Equal("5", sip.QueryScalar("SELECT WatchInterval FROM Evidence WHERE Id = 1"));
    }

    [Fact]
    public void WatchAdd_NonWatchType_Rejected()
    {
        using var sip = NewInstance();
        sip.Exec($@"
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceUrl, Title, Content, Hash, Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays)
                VALUES (1, 'sip-evidence-v1', 'evidence', 'evidence:test', 'https://example.com/x', '标题', '内容', 'hash', 1, 'active', '2026-08-16T00:00:00+08:00', '2026-08-16T00:00:00+08:00', 'fresh', 30)");

        var r = sip.Run("ingest", "watch", "add", "1");
        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("watch", r.Stdout);
    }

    [Fact]
    public void WatchAdd_NoUrl_Rejected()
    {
        using var sip = NewInstance();
        sip.Exec($@"
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, Title, Content, Hash, Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays)
                VALUES (1, 'sip-evidence-v1', 'evidence', 'evidence:test', '标题', '内容', 'hash', 1, 'active', '2026-08-16T00:00:00+08:00', '2026-08-16T00:00:00+08:00', 'fresh', 30)");

        var r = sip.Run("ingest", "watch", "add", "1");
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public void WatchRm_DisablesMonitoring()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");
        sip.Run("ingest", "watch", "add", "1");

        var r = sip.Run("ingest", "watch", "rm", "1");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("已停止监控", r.Stdout);

        Assert.Equal("0", sip.QueryScalar("SELECT WatchEnabled FROM Evidence WHERE Id = 1"));
    }

    [Fact]
    public void WatchRm_NotMonitored_Rejected()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");

        var r = sip.Run("ingest", "watch", "rm", "1");
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public void WatchList_Empty()
    {
        using var sip = NewInstance();

        var r = sip.Run("ingest", "watch", "list");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("没有监控目标", r.Stdout);
    }

    [Fact]
    public void WatchList_ShowsMonitoredItems()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");
        sip.Run("ingest", "watch", "add", "1");

        var r = sip.Run("ingest", "watch", "list");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("[1]", r.Stdout);
        Assert.Contains("间隔：5 分钟", r.Stdout);
    }

    [Fact]
    public void WatchList_JsonOutput()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "https://example.com/page1", "内容1", "hash1");
        sip.Run("ingest", "watch", "add", "1");

        var r = sip.Run("ingest", "watch", "list", "--json");
        Assert.Equal(0, r.ExitCode);
        var doc = JsonDocument.Parse(r.Stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("data").GetProperty("count").GetInt32());
    }
}
