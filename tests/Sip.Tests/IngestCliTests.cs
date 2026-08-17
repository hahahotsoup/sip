using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// Phase1 ingest MVP 测试(进程级黑盒,与既有 38 用例同模式)。
/// 本轮(第二轮·ingest 命令本体)已绿:
///   · 表结构 + 孟思琳挡位(第一轮)
///   · --stdin 落库 / --evidence 证据包校验 / --url SSRF 矩阵
///   · 版本链(superseded) / 无变化不存 / watch_targets 首快照
///   · confirm 审核门槛 / rm 轻存易删 / --json 契约 / 退出码
/// 后续轮次(改动分级/反转/语义去重/分组/retrieve/ask)以 Skip 占位。
/// </summary>
public class IngestCliTests
{
    private static SipInstance NewInstance()
    {
        var sip = new SipInstance();
        sip.EnsureDatabase();
        return sip;
    }

    private static void InsertEvidence(SipInstance sip, long id, string sourceKey, string content, string hash)
        => sip.Exec("""
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Excerpt, Content, Hash,
                                      Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays)
                VALUES (@id, 'sip-evidence-v1', 'evidence', @k, '测试源', 'https://example.com/a', '标题', '片段', @c, @h,
                        1, 'active', '2026-08-16T00:00:00+08:00', '2026-08-16T00:00:00+08:00', 'fresh', 30)
                """,
            ("@id", id), ("@k", sourceKey), ("@c", content), ("@h", hash));

    // ── 表结构(第一轮,绿)────────────────────────────────────────

    [Fact]
    public void Init_CreatesEvidenceTables()
    {
        using var sip = NewInstance();
        Assert.Equal("4", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Evidence','Groups','WatchTargets','EvidenceVectors')"));
    }

    [Fact]
    public void Init_CreatesEvidenceIndexes()
    {
        using var sip = NewInstance();
        Assert.Equal("4", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('idx_evidence_status','idx_evidence_url','idx_evidence_group','idx_evidence_sourcekey')"));
    }

    [Fact]
    public void Init_IsIdempotent_SecondRunDoesNotBreak()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("--help").ExitCode);
        Assert.Equal("4", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Evidence','Groups','WatchTargets','EvidenceVectors')"));
    }

    [Fact]
    public void ExistingTables_StillIntact_AndRssFixtureWorks()
    {
        using var sip = NewInstance();
        Assert.Equal("4", sip.QueryScalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Feeds','Items','Models','Vectors')"));
        sip.InsertFeed(1, "测试源", "https://example.com/rss");
        sip.InsertItem(1, 1, "标题", "https://example.com/a", "正文", "guid-1");
        Assert.Equal("active", sip.ItemStatus(1));
    }

    // ── 孟思琳挡位(第一轮,绿)────────────────────────────────────

    [Fact]
    public void Level2_BlocksIngestWriteSubs()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "2").ExitCode);
        Assert.Equal(3, sip.Run("ingest", "--url", "http://example.com/x").ExitCode);
        Assert.Equal(3, sip.Run("ingest", "--stdin").ExitCode);
        Assert.Equal(3, sip.Run("ingest", "confirm", "1").ExitCode);
        Assert.Contains("孟思琳", sip.Run("ingest", "--url", "http://example.com/x").Stdout);
    }

    [Fact]
    public void Level2_AllowsIngestReadOnlySubs()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "2").ExitCode);
        Assert.NotEqual(3, sip.Run("ingest", "list").ExitCode);
        // show:过了 simon 门(未被拦),由 ingest 自己报"找不到"(退出码 3)——与 simon 拦截(3+孟思琳文案)可区分
        var (exitShow, stdoutShow, _) = sip.Run("ingest", "show", "1");
        Assert.Equal(3, exitShow);
        Assert.Contains("找不到证据", stdoutShow);
        Assert.NotEqual(3, sip.Run("ingest", "groups").ExitCode);
    }

    [Fact]
    public void Level3_BlocksIngestEvenReadOnly()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.Run("simon", "level", "3").ExitCode);
        Assert.Equal(3, sip.Run("ingest", "list").ExitCode);
        Assert.Equal(0, sip.Run("simon", "status").ExitCode);
    }

    // ── ingest --stdin(第二轮,绿)────────────────────────────────

    [Fact]
    public void Ingest_Stdin_Roundtrip()
    {
        using var sip = NewInstance();
        var (exit, stdout, _) = sip.RunWithInput(
            "这是从外面查到的原文内容，需要沉淀进本地证据库。",
            "ingest", "--stdin", "--origin", "https://example.com/a", "--producer", "argo", "--yes", "--json");
        Assert.Equal(0, exit);
        Assert.Contains("success", stdout);
        Assert.Contains("\"id\": 1", stdout);
        Assert.Equal("sip-evidence-v1|evidence|evidence:argo|https://example.com/a|64|fresh", sip.QueryScalar(
            "SELECT Schema || '|' || SourceType || '|' || SourceKey || '|' || SourceUrl || '|' || Length(Hash) || '|' || Freshness FROM Evidence WHERE Id = 1"));
        // origin 提供 → excerpt 自动摘取(防转述失真:引用从这里摘)
        Assert.True(int.Parse(sip.QueryScalar("SELECT Length(Excerpt) FROM Evidence WHERE Id = 1")!) > 0);
    }

    [Fact]
    public void Ingest_Stdin_NoChangeNoStore()
    {
        using var sip = NewInstance();
        const string text = "同一段内容";
        var first = sip.RunWithInput(text, "ingest", "--stdin", "--producer", "argo", "--yes", "--json");
        Assert.Equal(0, first.ExitCode);
        var second = sip.RunWithInput(text, "ingest", "--stdin", "--producer", "argo", "--yes", "--json");
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("unchanged", second.Stdout);   // 无变化不存
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM Evidence WHERE SourceKey = 'evidence:argo'"));
    }

    [Fact]
    public void Ingest_Stdin_EmptyInput_ExitCode3()
    {
        using var sip = NewInstance();
        var (exit, stdout, _) = sip.RunWithInput("   ", "ingest", "--stdin", "--json");
        Assert.Equal(3, exit);
        Assert.Contains("EMPTY_STDIN", stdout);
    }

    [Fact]
    public void Ingest_VersionChain_SupersedesOld()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.RunWithInput("版本一内容", "ingest", "--stdin", "--producer", "argo", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("版本二内容，作者改了", "ingest", "--stdin", "--producer", "argo", "--yes").ExitCode);
        // 旧版 superseded 不删除,新版 Version+1 且 PrevId 链接版本链(版本即事实)
        Assert.Equal("1|superseded", sip.QueryScalar("SELECT Version || '|' || Status FROM Evidence WHERE Id = 1"));
        Assert.Equal("2|active", sip.QueryScalar("SELECT Version || '|' || Status FROM Evidence WHERE Id = 2"));
        Assert.Equal("1", sip.QueryScalar("SELECT PrevId FROM Evidence WHERE Id = 2"));
    }

    // ── ingest --evidence(第二轮,绿)─────────────────────────────

    [Fact]
    public void Ingest_Evidence_SchemaValidation()
    {
        using var sip = NewInstance();
        string pkg = Path.Combine(sip.Root, "pkg.json");

        // 合法证据包(含 captured_at 双时态字段)
        File.WriteAllText(pkg, """{"schema":"sip-evidence-v1","source_type":"evidence","source_key":"evidence:argo","source_name":"Argo","source_url":"https://example.com/b","title":"标题","excerpt":"片段","content":"正文内容","captured_at":"2026-08-16T00:00:00+08:00","producer_meta":{"argo_selection":0.8}}""");
        var ok = sip.Run("ingest", "--evidence", pkg, "--yes", "--json");
        Assert.Equal(0, ok.ExitCode);
        Assert.Equal("2026-08-16T00:00:00+08:00", sip.QueryScalar("SELECT CapturedAt FROM Evidence WHERE Id = 1"));

        // schema 不符 → 拒绝
        File.WriteAllText(pkg, """{"schema":"sip-evidence-v2","source_type":"evidence","content":"x"}""");
        Assert.Equal(1, sip.Run("ingest", "--evidence", pkg).ExitCode);

        // content 与 excerpt 都空 → 拒绝
        File.WriteAllText(pkg, """{"schema":"sip-evidence-v1","source_type":"evidence"}""");
        Assert.Equal(1, sip.Run("ingest", "--evidence", pkg).ExitCode);

        // 非法 JSON → 拒绝
        File.WriteAllText(pkg, "{not json");
        Assert.Equal(1, sip.Run("ingest", "--evidence", pkg).ExitCode);
    }

    [Fact]
    public void Ingest_Evidence_WatchType_StoresWatchTarget()
    {
        using var sip = NewInstance();
        string pkg = Path.Combine(sip.Root, "pkg.json");
        File.WriteAllText(pkg, """{"schema":"sip-evidence-v1","source_type":"watch","source_url":"https://example.com/w","source_name":"示例站","title":"标题","content":"正文内容"}""");
        var (exit, _, _) = sip.Run("ingest", "--evidence", pkg, "--yes");
        Assert.Equal(0, exit);
        // Evidence 行 source_type=watch + watch_targets 首快照(Phase2 watch 接管)
        Assert.Equal("watch", sip.QueryScalar("SELECT SourceType FROM Evidence WHERE Id = 1"));
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM WatchTargets WHERE Url = 'https://example.com/w'"));
        Assert.Equal("1", sip.QueryScalar("SELECT FirstEvidenceId FROM WatchTargets WHERE Url = 'https://example.com/w'"));
    }

    // ── ingest --url(第二轮,绿:SSRF 矩阵;成功路径同 --fulltext 先例不自动化)──

    [Fact]
    public void Ingest_Url_SSrfMatrix()
    {
        using var sip = NewInstance();
        Assert.Equal(2, sip.Run("ingest", "--url", "http://127.0.0.1:9/x").ExitCode);        // 回环
        Assert.Equal(2, sip.Run("ingest", "--url", "https://169.254.169.254/latest/meta-data/").ExitCode);  // 云元数据
        Assert.Equal(2, sip.Run("ingest", "--url", "http://192.168.1.1/x").ExitCode);         // 私网段
        Assert.Equal(2, sip.Run("ingest", "--url", "ftp://example.com/x").ExitCode);          // 非 http(s)
        Assert.Equal(1, sip.Run("ingest", "--url").ExitCode);                                 // 缺 URL 参数
    }

    // ── list / show / confirm / rm(第二轮,绿)─────────────────────

    [Fact]
    public void Ingest_List_Empty_And_ShowNotFound()
    {
        using var sip = NewInstance();
        var (exit, stdout, _) = sip.Run("ingest", "list");
        Assert.Equal(0, exit);
        Assert.Contains("还没有证据", stdout);
        // 找不到 → 退出码 3(资源未就绪,与 ITEM_NOT_FOUND 对齐)
        Assert.Equal(3, sip.Run("ingest", "show", "999").ExitCode);
    }

    [Fact]
    public void Ingest_Confirm_SetsVerifiedAndConsensus()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:t", "正文", "abcd");
        var (exit, _, _) = sip.Run("ingest", "confirm", "1");
        Assert.Equal(0, exit);
        Assert.Equal("1|0.5", sip.QueryScalar("SELECT Verified || '|' || Consensus FROM Evidence WHERE Id = 1"));
        Assert.True(int.Parse(sip.QueryScalar("SELECT Length(ConfirmedAt) FROM Evidence WHERE Id = 1")!) > 0);  // 修正留痕
    }

    [Fact]
    public void Ingest_Rm_DeletesEvidenceAndVectors()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:t", "正文", "abcd");
        sip.Exec("INSERT INTO EvidenceVectors (EvidenceId, ModelId, Vector, CreatedAt) VALUES (1, 1, x'0000', '2026-08-16')");
        var (exit, _, _) = sip.Run("ingest", "rm", "1", "--yes");
        Assert.Equal(0, exit);
        Assert.Equal("0", sip.QueryScalar("SELECT COUNT(*) FROM Evidence WHERE Id = 1"));
        Assert.Equal("0", sip.QueryScalar("SELECT COUNT(*) FROM EvidenceVectors WHERE EvidenceId = 1"));
    }

    [Fact]
    public void Ingest_Rm_NonInteractive_RequiresYes()
    {
        using var sip = NewInstance();
        InsertEvidence(sip, 1, "evidence:t", "正文", "abcd");
        var (exit, stdout, _) = sip.Run("ingest", "rm", "1");
        Assert.Equal(1, exit);
        Assert.Contains("删除需要确认", stdout);
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM Evidence WHERE Id = 1"));   // 未删
    }

    [Fact]
    public void Ingest_List_JsonContract()
    {
        using var sip = NewInstance();
        Assert.Equal(0, sip.RunWithInput("列表契约测试", "ingest", "--stdin", "--producer", "argo", "--yes").ExitCode);
        var (exit, stdout, _) = sip.Run("ingest", "list", "--json");
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        var items = root.GetProperty("data").GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        var item = items[0];
        Assert.Equal(1, item.GetProperty("id").GetInt32());
        Assert.Equal("fresh", item.GetProperty("freshness").GetString());
        Assert.False(item.GetProperty("verified").GetBoolean());
    }

    // ── 改动分级 + 反转检测(第三轮,绿;桩 embedding)──────────────────

    private static void ConfigureStubAi(SipInstance sip, StubEmbedding stub)
    {
        File.WriteAllText(Path.Combine(sip.DataDir, "ai_config.json"), JsonSerializer.Serialize(new
        {
            Embedding = new { Provider = "stub", Model = "stub-model", Dimensions = 8, ApiEndpoint = stub.Endpoint, SearchThreshold = 0.7 },
            AllowPrivateNet = false
        }));
        // 注册当前 embedding 模型(CurrentEmbeddingModelId != 0 才走语义分级)
        sip.Exec("INSERT INTO Models (ModelType, Provider, ModelName, Dimensions, IsCurrent, CreatedAt) VALUES ('embedding','stub','stub-model',8,1,'2026-01-01')");
    }

    [Fact]
    public void Ingest_ChangeGrade_ThreeBands()
    {
        // 三对向量互相独立(不同方向),避免跨 producer 被去重门误判(cos≥0.92 会跳过)
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["polishA"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["polishB"] = new[] { 0.98f, 0.1f, 0, 0, 0, 0, 0, 0 },                 // cos≈0.995 → 距离≈0.005 → ⚪
            ["adjustA"] = new[] { 0f, 1f, 0, 0, 0, 0, 0, 0 },
            ["adjustB"] = new[] { 0f, 0.8f, 0.6f, 0, 0, 0, 0, 0 },                // cos=0.8 → 距离=0.2 → 🟡
            ["revA"] = new[] { 0f, 0f, 1f, 0, 0, 0, 0, 0 },
            ["revB 不再认为之前的观点"] = new[] { 0f, 0f, 0.4f, 0.9f, 0, 0, 0, 0 }, // cos=0.4 → 距离=0.6 → 🔴 + 立场词
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);

        Assert.Equal(0, sip.RunWithInput("polishA", "ingest", "--stdin", "--producer", "p1", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("polishB", "ingest", "--stdin", "--producer", "p1", "--yes").ExitCode);
        Assert.Equal("polish", sip.QueryScalar("SELECT Grade FROM Evidence WHERE Id = 2"));

        Assert.Equal(0, sip.RunWithInput("adjustA", "ingest", "--stdin", "--producer", "p2", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("adjustB", "ingest", "--stdin", "--producer", "p2", "--yes").ExitCode);
        Assert.Equal("adjust", sip.QueryScalar("SELECT Grade FROM Evidence WHERE Id = 4"));

        Assert.Equal(0, sip.RunWithInput("revA", "ingest", "--stdin", "--producer", "p3", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("revB 不再认为之前的观点", "ingest", "--stdin", "--producer", "p3", "--yes").ExitCode);
        Assert.Equal("reverse", sip.QueryScalar("SELECT Grade FROM Evidence WHERE Id = 6"));
        Assert.Equal("1", sip.QueryScalar("SELECT Reversed FROM Evidence WHERE Id = 6"));   // 距离 + 立场词双验证
    }

    [Fact]
    public void Ingest_ReversalDetection_OnlyDistance_NoSignal()
    {
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["revNoA"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["revNoB 纯距离变化无立场词"] = new[] { 0.4f, 0.9f, 0, 0, 0, 0, 0, 0 },   // 距离≈0.59 ≥ 反转阈值
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);
        Assert.Equal(0, sip.RunWithInput("revNoA", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("revNoB 纯距离变化无立场词", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        Assert.Equal("reverse", sip.QueryScalar("SELECT Grade FROM Evidence WHERE Id = 2"));
        Assert.Equal("0", sip.QueryScalar("SELECT Reversed FROM Evidence WHERE Id = 2"));   // 只距离达标,无立场词 → 不标反转
    }

    [Fact]
    public void Ingest_ChangeGrade_CharFallback_NoAi()
    {
        using var sip = NewInstance();   // 不配 AI → CurrentEmbeddingModelId=0 → 字符级降级(不打扰)
        Assert.Equal(0, sip.RunWithInput("原始内容第一版内容", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("完全不同的第二版内容差异很大", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        string? grade = sip.QueryScalar("SELECT Grade FROM Evidence WHERE Id = 2");
        Assert.Contains(grade, new[] { "adjust", "reverse" });   // 字符差异大 → 至少 🟡调整
    }

    // ── ingest refresh(第三轮,绿;网络成功路径同 --fulltext 先例不自动化)──

    private static void InsertWatchEvidence(SipInstance sip, long id, string url, string content, string capturedAt, int ttl)
        => sip.Exec("""
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Content, Hash, Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays)
                VALUES (@id, 'sip-evidence-v1', 'watch', @k, '示例站', @u, '标题', @c, 'abc', 1, 'active', @cap, @cap, 'fresh', @ttl)
                """,
            ("@id", id), ("@k", "watch:" + url), ("@u", url), ("@c", content), ("@cap", capturedAt), ("@ttl", ttl));

    [Fact]
    public void Ingest_Refresh_Blocked_KeepsActive()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "http://127.0.0.1:9/x", "旧内容", "2026-08-01T00:00:00+08:00", 7);
        var (exit, stdout, _) = sip.Run("ingest", "refresh");
        Assert.Equal(0, exit);
        Assert.Contains("被拒", stdout);                                                         // SSRF 策略拒绝
        Assert.Equal("active", sip.QueryScalar("SELECT Status FROM Evidence WHERE Id = 1"));      // 不动状态
        Assert.Equal("旧内容", sip.QueryScalar("SELECT Content FROM Evidence WHERE Id = 1"));      // 内容保留(不覆盖)
    }

    [Fact]
    public void Ingest_Refresh_DefaultOnlyStale()
    {
        using var sip = NewInstance();
        InsertWatchEvidence(sip, 1, "http://127.0.0.1:9/a", "旧", "2026-08-01T00:00:00+08:00", 7);   // stale
        InsertWatchEvidence(sip, 2, "http://127.0.0.1:9/b", "新", "2026-08-17T00:00:00+08:00", 30);  // fresh
        var (exit, stdout, _) = sip.Run("ingest", "refresh", "--json");
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout);
        var arr = doc.RootElement.GetProperty("data").GetProperty("refreshed");
        Assert.Equal(1, arr.GetArrayLength());      // 默认只刷 stale 的
        Assert.Equal(1, arr[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void Ingest_Refresh_Empty_NothingToRefresh()
    {
        using var sip = NewInstance();
        var (exit, stdout, _) = sip.Run("ingest", "refresh");
        Assert.Equal(0, exit);
        Assert.Contains("没有可刷新的目标", stdout);
    }

    // ── 后续轮次用例骨架(占位,实现后点亮)──────────────────────────

    // ── 语义去重 + 主题分组(第四轮,绿;桩 embedding)────────────────

    [Fact]
    public void Ingest_SemanticDedup_SkipsNonInteractive()
    {
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["dupA"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["dupB"] = new[] { 0.97f, 0.1f, 0, 0, 0, 0, 0, 0 },   // cos≈0.995 ≥ 0.92 → 重复
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);

        Assert.Equal(0, sip.RunWithInput("dupA", "ingest", "--stdin", "--producer", "p", "--yes", "--json").ExitCode);
        var second = sip.RunWithInput("dupB", "ingest", "--stdin", "--producer", "p2", "--yes", "--json");
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("duplicateOf", second.Stdout);            // 不替你删:返回重复指向
        Assert.Contains("skipped", second.Stdout);
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM Evidence"));   // 只存了第一条
    }

    [Fact]
    public void Ingest_SemanticDedup_ForceStores()
    {
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["dupA"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["dupB"] = new[] { 0.97f, 0.1f, 0, 0, 0, 0, 0, 0 },
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);
        Assert.Equal(0, sip.RunWithInput("dupA", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("dupB", "ingest", "--stdin", "--producer", "p2", "--yes", "--force").ExitCode);
        Assert.Equal("2", sip.QueryScalar("SELECT COUNT(*) FROM Evidence"));   // --force 强制存
    }

    [Fact]
    public void Ingest_EvidenceVectors_Written_AfterStore()
    {
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["doc1"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);
        Assert.Equal(0, sip.RunWithInput("doc1", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        Assert.Equal("1", sip.QueryScalar("SELECT COUNT(*) FROM EvidenceVectors WHERE EvidenceId = 1"));
    }

    [Fact]
    public void Ingest_TopicGrouping_AssignAndManage()
    {
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["AI主题词"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["docAI 这是讲大模型的证据"] = new[] { 0.8f, 0.6f, 0, 0, 0, 0, 0, 0 },   // cos(簇心)=0.8 ≥ 0.75 → 归组
            ["docOther 这是讲做饭的证据"] = new[] { 0f, 1f, 0, 0, 0, 0, 0, 0 },      // cos=0 → 不归组
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);

        // 你定义主题:group add <主题词> [--seed <查询>]
        var add = sip.Run("ingest", "group", "add", "AI", "--seed", "AI主题词", "--json");
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("\"id\": 1", add.Stdout);

        // 相关证据自动归组
        Assert.Equal(0, sip.RunWithInput("docAI 这是讲大模型的证据", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        Assert.Equal("1", sip.QueryScalar("SELECT GroupId FROM Evidence WHERE Id = 1"));
        // 无关证据不归组
        Assert.Equal(0, sip.RunWithInput("docOther 这是讲做饭的证据", "ingest", "--stdin", "--producer", "p2", "--yes").ExitCode);
        Assert.Equal("", sip.QueryScalar("SELECT IFNULL(GroupId, '') FROM Evidence WHERE Id = 2") ?? "NULL");

        // groups 浏览
        var groups = sip.Run("ingest", "groups", "--json");
        Assert.Equal(0, groups.ExitCode);
        using (var doc = JsonDocument.Parse(groups.Stdout))
        {
            var arr = doc.RootElement.GetProperty("data").GetProperty("groups");
            Assert.Equal(1, arr.GetArrayLength());
            Assert.Equal("AI", arr[0].GetProperty("label").GetString());
            Assert.Equal(1, arr[0].GetProperty("count").GetInt32());
        }

        // rename
        Assert.Equal(0, sip.Run("ingest", "group", "rename", "1", "人工智能").ExitCode);
        Assert.Equal("人工智能", sip.QueryScalar("SELECT Label FROM Groups WHERE Id = 1"));

        // rm:证据回未分组(不删文章),主题删除
        Assert.Equal(0, sip.Run("ingest", "group", "rm", "1").ExitCode);
        Assert.Equal("0", sip.QueryScalar("SELECT COUNT(*) FROM Groups"));
        Assert.Equal("", sip.QueryScalar("SELECT IFNULL(GroupId, '') FROM Evidence WHERE Id = 1"));
        Assert.Equal("active", sip.QueryScalar("SELECT Status FROM Evidence WHERE Id = 1"));   // 文章未删
    }

    // ── retrieve + ask(第五轮,绿;桩 embedding + 桩 LLM)────────────────

    [Fact]
    public void Ingest_Retrieve_EvidenceAttached()
    {
        using var stub = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["大模型文档内容A"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["大模型文档内容B 补充"] = new[] { 0.9f, 0.2f, 0, 0, 0, 0, 0, 0 },
            ["查询词大模型是什么"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
        }, new float[8]);
        using var sip = NewInstance();
        ConfigureStubAi(sip, stub);
        // 同 producer 存两条 → 版本链(hasDiff=true);检索只查 active(最新版)
        Assert.Equal(0, sip.RunWithInput("大模型文档内容A", "ingest", "--stdin", "--producer", "p", "--origin", "https://example.com/llm", "--yes").ExitCode);
        Assert.Equal(0, sip.RunWithInput("大模型文档内容B 补充", "ingest", "--stdin", "--producer", "p", "--origin", "https://example.com/llm", "--yes").ExitCode);

        var (exit, stdout, _) = sip.Run("ingest", "retrieve", "查询词大模型是什么", "--json");
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout);
        var hits = doc.RootElement.GetProperty("data").GetProperty("hits");
        Assert.Equal(1, hits.GetArrayLength());
        var h = hits[0];
        Assert.Equal(2, h.GetProperty("id").GetInt32());
        Assert.Equal("https://example.com/llm", h.GetProperty("sourceUrl").GetString());
        Assert.True(h.GetProperty("hasDiff").GetBoolean());                 // RAG 带 diff 状态:被改过
        Assert.True(h.GetProperty("excerpt").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public void Ingest_Retrieve_FtsFallback_NoAi()
    {
        using var sip = NewInstance();   // 无 AI:语义路径跳过,LIKE 兜底
        Assert.Equal(0, sip.RunWithInput("量子纠缠测试文本内容", "ingest", "--stdin", "--producer", "p", "--yes").ExitCode);
        var (exit, stdout, _) = sip.Run("ingest", "retrieve", "量子纠缠", "--json");
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(1, doc.RootElement.GetProperty("data").GetProperty("hits").GetArrayLength());
    }

    [Fact]
    public void Ingest_Ask_QuotesEvidence_AndKnowsUnknown()
    {
        using var stubEmb = new StubEmbedding(new Dictionary<string, float[]>
        {
            ["大模型文档内容"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
            ["大模型是什么"] = new[] { 1f, 0, 0, 0, 0, 0, 0, 0 },
        }, new float[8]);
        using var stubLlm = new StubLlm("根据证据 #1 逐字摘录:大模型文档内容。");
        using var sip = NewInstance();
        ConfigureStubAi(sip, stubEmb);
        // 补配 LLM 端点(桩)
        string cfgPath = Path.Combine(sip.DataDir, "ai_config.json");
        var cfg = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(cfgPath))!;
        cfg["Llm"] = new System.Text.Json.Nodes.JsonObject { ["Provider"] = "stub", ["Model"] = "stub-llm", ["ApiEndpoint"] = stubLlm.Endpoint, ["Dimensions"] = 0 };
        File.WriteAllText(cfgPath, cfg.ToJsonString());

        Assert.Equal(0, sip.RunWithInput("大模型文档内容", "ingest", "--stdin", "--producer", "p", "--origin", "https://example.com/llm", "--yes").ExitCode);

        // 命中 → LLM 回答 + 证据并现(ask 会触发 EnsureAiPrompted 安全横幅,测试加 --ignoresafeannouncement 保持纯 JSON)
        var (exit, stdout, _) = sip.Run("ingest", "ask", "大模型是什么", "--json", "--ignoresafeannouncement");
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Contains("根据证据", data.GetProperty("answer").GetString());
        Assert.Equal(1, data.GetProperty("evidence").GetArrayLength());

        // 无命中 → 不知道(不生成式填空,不调 LLM)
        var (exit2, stdout2, _) = sip.Run("ingest", "ask", "不存在的主题xyz", "--json");
        Assert.Equal(0, exit2);
        using var doc2 = JsonDocument.Parse(stdout2);
        Assert.Contains("不知道", doc2.RootElement.GetProperty("data").GetProperty("answer").GetString());
        Assert.Equal(0, doc2.RootElement.GetProperty("data").GetProperty("evidence").GetArrayLength());
    }

    // ── 保鲜字段(第五轮,绿:ObservedAt 而非 CapturedAt)────────────────

    [Fact]
    public void Ingest_Refresh_UsesObservedAt_NotCapturedAt()
    {
        using var sip = NewInstance();
        // CapturedAt 新(今天)但 ObservedAt 旧(8/1, ttl 7 → stale):保鲜应看本地记录时间 ObservedAt
        sip.Exec("""
                INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Content, Hash, Version, Status, CapturedAt, ObservedAt, Freshness, TtlDays)
                VALUES (1,'sip-evidence-v1','watch','watch:http://127.0.0.1:9/x','示例','http://127.0.0.1:9/x','标题','旧内容','abc',1,'active','2026-08-17T00:00:00+08:00','2026-08-01T00:00:00+08:00','fresh',7)
                """);
        var (exit, stdout, _) = sip.Run("ingest", "refresh", "--json");
        Assert.Equal(0, exit);
        Assert.Contains("\"id\": 1", stdout);   // ObservedAt 旧 → stale → 默认刷新(结果 blocked)
    }
}

/// <summary>
/// 本地桩 embedding 服务(TcpListener + 手动 HTTP,端口 0 随机,无需管理员 ACL)。
/// 对已知文本返回表内向量,未知文本返回默认向量——使分级/去重可离线确定性测试。
/// 注:embedding 端点不经过 ValidateFetchUrl(SSRF 只作用于网页抓取),故回环地址可用。
/// </summary>
internal sealed class StubEmbedding : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly Dictionary<string, float[]> _table;
    private readonly float[] _defaultVec;
    private readonly Thread _thread;

    public string Endpoint { get; }

    public StubEmbedding(Dictionary<string, float[]> table, float[] defaultVec)
    {
        _table = table;
        _defaultVec = defaultVec;
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        Endpoint = "http://127.0.0.1:" + ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port + "/v1";
        _thread = new Thread(AcceptLoop) { IsBackground = true };
        _thread.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private void Handle(System.Net.Sockets.TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string? line;
            int contentLength = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            }
            if (contentLength <= 0) return;
            var buf = new char[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = reader.Read(buf, read, contentLength - read);
                if (n <= 0) break;
                read += n;
            }
            string body = new string(buf, 0, read);
            string? input = null;
            using (var doc = JsonDocument.Parse(body))
                input = doc.RootElement.GetProperty("input").GetString();
            float[] vec = (input != null && _table.TryGetValue(input, out var v)) ? v : _defaultVec;
            var resp = JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } });
            var bytes = System.Text.Encoding.UTF8.GetBytes(resp);
            var header = System.Text.Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            var all = new byte[header.Length + bytes.Length];
            Buffer.BlockCopy(header, 0, all, 0, header.Length);
            Buffer.BlockCopy(bytes, 0, all, header.Length, bytes.Length);
            stream.Write(all, 0, all.Length);
        }
        catch { }
        finally { client.Dispose(); }
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>本地桩 LLM 服务(返回固定回答;ask 测试用)。与 StubEmbedding 同结构。</summary>
internal sealed class StubLlm : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly string _answer;
    private readonly Thread _thread;

    public string Endpoint { get; }

    public StubLlm(string answer)
    {
        _answer = answer;
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        Endpoint = "http://127.0.0.1:" + ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port + "/v1";
        _thread = new Thread(AcceptLoop) { IsBackground = true };
        _thread.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private void Handle(System.Net.Sockets.TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string? line;
            int contentLength = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            }
            if (contentLength <= 0) return;
            var buf = new char[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = reader.Read(buf, read, contentLength - read);
                if (n <= 0) break;
                read += n;
            }
            var resp = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = _answer } } } });
            var bytes = System.Text.Encoding.UTF8.GetBytes(resp);
            var header = System.Text.Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            var all = new byte[header.Length + bytes.Length];
            Buffer.BlockCopy(header, 0, all, 0, header.Length);
            Buffer.BlockCopy(bytes, 0, all, header.Length, bytes.Length);
            stream.Write(all, 0, all.Length);
        }
        catch { }
        finally { client.Dispose(); }
    }

    public void Dispose() => _listener.Stop();
}
