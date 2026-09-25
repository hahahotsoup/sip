using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// v2.0.0 补齐的 Web 功能的**接口契约**（黑盒：真起 <c>sip --start</c> + 真 HTTP + 真 cookie）。
///
/// 这一批用例钉的是"补齐"这件事本身有没有真做到：
///   · 改稿追踪 / 跨源去重 / 源规则 / 本地导入 / 治理面 都有接口，且**返回真数据**；
///   · 版本链按 (FeedId, Guid) 隔离 —— 跨源转载（Guid 相同）不算"作者改稿"；
///   · CSP + 外置脚本落地（index.html 里不能再有内联脚本或 onclick=）；
///   · 命令面板只认**只读白名单**，不是通用 CLI 执行器。
/// </summary>
public sealed class WebFeaturesServer : SipWebServer
{
    public const int FeedA = 1;
    public const int FeedB = 2;
    public const int CrossFeedItemA = 101;   // 跨源转载：A 源那一份
    public const int CrossFeedItemB = 102;   // 跨源转载：B 源那一份（Guid 相同）
    public const int EditedV1 = 201;         // 同源改稿：旧版
    public const int EditedV2 = 202;         // 同源改稿：新版（active）
    public const int HtmlV1 = 401;           // 同源改稿：正文是**一整行 HTML**（RSS 的常态）
    public const int HtmlV2 = 402;           // 同源改稿：同上一整行 HTML，只改了一段
    public const int DupItemA = 301;         // 跨源重复：A 源
    public const int DupItemB = 302;         // 跨源重复：B 源

    private const string SharedBody =
        "本地优先的阅读器把每一版都留在自己机器上。\n\n这一段是共享的正文，用来验证跨源转载（Guid 相同）不会被算作作者改稿。";

    private const string EditedBodyOld =
        "第一段：结论是 90% 的情况清缓存就好。\n\n第二段：这段两版都一样，用来确保 diff 只报变了的行。";

    private const string EditedBodyNew =
        "第一段：结论是 90% 的情况清缓存就好。\n\n第二段：更正——多半是 split-horizon 与本地 resolver 不一致，清缓存没用。";

    private const string DupBody =
        "这是一段足够长的正文，用来做段落重合度检测；它在两个不同的源里重复出现，重合度应当超过阈值。\n\n第二段同样逐字一致，确保整体重合度高于 0.8。";

    // 真实 RSS 的样子：整篇正文在一行里，段落靠 <p> 分隔、行内**没有换行**。
    // 按行 diff 这种输入 = 一行变一行 = "整篇删除 + 整篇插入"，所以要按段落比。
    public const string HtmlBodyOld =
        "<p>开头这段两版一模一样，用来确认 diff 没把整篇当成重写。</p><p>第二段：结论是清缓存就好。</p><p>结尾这段也一样。</p>";

    public const string HtmlBodyNew =
        "<p>开头这段两版一模一样，用来确认 diff 没把整篇当成重写。</p><p>第二段：更正——是 DNS split-horizon，清缓存没用。</p><p>结尾这段也一样。</p>";

    public WebFeaturesServer() : base(passwordMode: true)
    {
        var s = Instance;
        s.InsertFeed(FeedA, "源 A", "http://a.example/feed.xml");
        s.InsertFeed(FeedB, "源 B", "http://b.example/feed.xml");

        // ① 跨源转载：同一个 Guid 落在两个源里（各一版）
        s.InsertItem(CrossFeedItemA, FeedA, "共享文章", "http://a.example/1", SharedBody, "g-shared");
        s.InsertItem(CrossFeedItemB, FeedB, "共享文章（转载）", "http://b.example/1", SharedBody, "g-shared");

        // ② 同源改稿：同 (FeedId, Guid) 两版，旧版归档、新版 active
        s.InsertItem(EditedV1, FeedA, "被改过的文章", "http://a.example/2", EditedBodyOld, "g-edited");
        s.Exec("UPDATE Items SET Version=1, Status='archived', ArchivedAt=@now WHERE Id=@id",
            ("@now", DateTime.Now.AddHours(-2).ToString("O")), ("@id", EditedV1));
        s.InsertItem(EditedV2, FeedA, "被改过的文章", "http://a.example/2", EditedBodyNew, "g-edited");
        s.Exec("UPDATE Items SET Version=2 WHERE Id=@id", ("@id", EditedV2));

        // ③ 跨源重复（正文逐字一致）—— 去重扫描要能聚成一簇
        s.InsertItem(DupItemA, FeedA, "重复内容 A", "http://a.example/3", DupBody, "g-dup-a");
        s.InsertItem(DupItemB, FeedB, "重复内容 B", "http://b.example/3", DupBody, "g-dup-b");

        // ④ 同源改稿，但正文是「一行 HTML」（RSS 常态）—— 差异该落在段落上，不是整篇
        s.InsertItem(HtmlV1, FeedA, "一行 HTML 的改稿", "http://a.example/4", HtmlBodyOld, "g-html");
        s.Exec("UPDATE Items SET Version=1, Status='archived', ArchivedAt=@now WHERE Id=@id",
            ("@now", DateTime.Now.AddHours(-2).ToString("O")), ("@id", HtmlV1));
        s.InsertItem(HtmlV2, FeedA, "一行 HTML 的改稿", "http://a.example/4", HtmlBodyNew, "g-html");
        s.Exec("UPDATE Items SET Version=2 WHERE Id=@id", ("@id", HtmlV2));

        // 去重扫描只看窗口内的**发布时间**：InsertItem 没写 PublishDate，这里补上（否则整批被窗口滤掉）
        string now = DateTime.Now.ToString("O");
        s.Exec("UPDATE Items SET PublishDate=@now WHERE Id IN (101,102,201,202,301,302,401,402)", ("@now", now));
    }
}

public class WebFeaturesTests : IClassFixture<WebFeaturesServer>
{
    private readonly WebFeaturesServer _srv;

    public WebFeaturesTests(WebFeaturesServer srv) => _srv = srv;

    private async Task<HttpClient> ClientAsync()
    {
        var c = _srv.NewClient(out var jar, "sip-test/features");
        using var res = await _srv.LoginAsync(c, jar);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return c;
    }

    private static async Task<string> Body(HttpResponseMessage res) => await res.Content.ReadAsStringAsync();

    // ── CSP / 静态资源 ───────────────────────────────────────────

    [Fact]
    public async Task AppPage_HasStrictCsp_AndNoInlineScriptOrHandlers()
    {
        using var client = await ClientAsync();
        using var res = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.True(res.Headers.TryGetValues("Content-Security-Policy", out var csp), "必须有 CSP 头");
        string policy = string.Join(" ", csp);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("object-src 'none'", policy);

        string html = await Body(res);
        // 内联脚本与内联事件属性都是 CSP 会拦的东西，页面上不该再有
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("onclick=", html);
        Assert.DoesNotContain("oninput=", html);
        Assert.Contains("<script src=\"/app.js\" defer></script>", html);
    }

    [Fact]
    public async Task StaticAssets_AreServed()
    {
        using var client = await ClientAsync();

        using (var js = await client.GetAsync("/app.js"))
        {
            Assert.Equal(HttpStatusCode.OK, js.StatusCode);
            Assert.Contains("javascript", js.Content.Headers.ContentType?.MediaType ?? "");
            Assert.Contains("data-act", await Body(js));   // 全站事件委托确实在这个文件里
        }
        using (var login = await client.GetAsync("/login.js"))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var sw = await client.GetAsync("/sw.js"))
            Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        using (var manifest = await client.GetAsync("/manifest.webmanifest"))
        {
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            Assert.Contains("standalone", await Body(manifest));
        }
        using (var icon = await client.GetAsync("/icon.svg"))
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
    }

    [Fact]
    public async Task LanguageFiles_AreServed_Not404()
    {
        // 这一条修的是"多语言形同虚设"：前端一直请求 /languages/xx.json，
        // 而服务端此前没有任何静态路由 → 永远 404 → 永远回落到 20 个键的内置表。
        using var client = await ClientAsync();
        using var res = await client.GetAsync("/languages/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string body = await Body(res);
        Assert.Contains("\"Today\"", body);
        Assert.True(body.Length > 1000, "语言文件应该是一整份词典，而不是一个空对象");
    }

    [Fact]
    public async Task LanguagePathTraversal_IsRejected()
    {
        // /languages/ 只接受「字母/数字/连字符/下划线」组成的文件名 —— 不做路径拼接
        using var client = await ClientAsync();
        using var res = await client.GetAsync("/languages/..%2F..%2Fsip_settings.json");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    // ── 改稿追踪 ────────────────────────────────────────────────

    [Fact]
    public async Task Edits_ListsSameFeedRevisions_ButNotCrossFeedReposts()
    {
        using var client = await ClientAsync();
        using var res = await client.GetAsync("/api/edits");
        string body = await Body(res);
        Assert.True(res.StatusCode == HttpStatusCode.OK, body);

        Assert.Contains("\"itemId\":202", body);                 // 同源改稿 → 在列表里
        Assert.DoesNotContain("\"itemId\":101", body);           // 跨源转载 → 不在列表里
        Assert.DoesNotContain("\"itemId\":102", body);
    }

    [Fact]
    public async Task Versions_AreScopedToOwnFeed()
    {
        // 版本链按 (FeedId, Guid) 隔离：跨源转载的同一 Guid 不能混进同一条历史，
        // 否则版本号会重复，"看 v1→v2" 可能拿 B 源的 v1 去比 A 源的 v2。
        using var client = await ClientAsync();
        using var res = await client.GetAsync($"/api/articles/{WebFeaturesServer.CrossFeedItemA}/versions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await Body(res));
        var versions = doc.RootElement.GetProperty("data").GetProperty("versions");
        Assert.Equal(1, versions.GetArrayLength());
    }

    [Fact]
    public async Task Diff_ReportsChangedParagraphs()
    {
        using var client = await ClientAsync();
        using var res = await client.GetAsync($"/api/articles/{WebFeaturesServer.EditedV2}/diff");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await Body(res));
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("from").GetInt32());
        Assert.Equal(2, data.GetProperty("to").GetInt32());
        Assert.True(data.GetProperty("added").GetInt32() >= 1);
        Assert.True(data.GetProperty("removed").GetInt32() >= 1);
        Assert.Contains("更正", await Body(res));
    }

    [Fact]
    public async Task Diff_OneLineHtml_ComparesParagraphs_NotWholeArticle()
    {
        // 回归：RSS 的 Content 常常整篇只有一行 HTML。按行 diff 会变成
        // "整篇删除 + 整篇插入"（页面上就是一大块红接一大块绿），
        // 正确的行为是：没改的段落仍是 Unchanged，只报改掉的那一段。
        using var client = await ClientAsync();
        using var res = await client.GetAsync($"/api/articles/{WebFeaturesServer.HtmlV2}/diff");
        string body = await Body(res);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("from").GetInt32());
        Assert.Equal(2, data.GetProperty("to").GetInt32());

        var changes = data.GetProperty("changes").EnumerateArray()
            .Select((x) => (Type: x.GetProperty("type").GetString(), Text: x.GetProperty("text").GetString()))
            .ToList();

        // ① 两版共有的段落必须留在 Unchanged 里（不是被删了再插一遍）
        Assert.Contains(changes, (c) => c.Type == "Unchanged" && c.Text!.Contains("开头这段两版一模一样"));
        Assert.Contains(changes, (c) => c.Type == "Unchanged" && c.Text!.Contains("结尾这段也一样"));
        Assert.DoesNotContain(changes, (c) => c.Text!.Contains("<p>"));   // 标签不该出现在差异文本里

        // ② 改动集中在第二段：删/插都只涉及这一段，量级不是整篇
        var deleted = changes.Where((c) => c.Type == "Deleted").ToList();
        var inserted = changes.Where((c) => c.Type == "Inserted").ToList();
        Assert.NotEmpty(deleted);
        Assert.NotEmpty(inserted);
        Assert.Contains("清缓存就好", string.Join(" ", deleted.Select((c) => c.Text)));
        Assert.Contains("split-horizon", string.Join(" ", inserted.Select((c) => c.Text)));
        Assert.True(deleted.Count <= 3 && inserted.Count <= 3,
            $"只该报第二段，实际 -{deleted.Count} / +{inserted.Count}");
        Assert.True(data.GetProperty("added").GetInt32() + data.GetProperty("removed").GetInt32() <= 4);
        // 整篇长度级的"假差异"：改动文本不该逼近全文
        Assert.True(deleted.Sum((c) => c.Text!.Length) < WebFeaturesServer.HtmlBodyOld.Length / 2,
            "删除的行加起来接近全文长度 —— 又变成整篇重写了");
    }

    [Fact]
    public async Task Article_CanBeReadAtAnOldVersion()
    {
        using var client = await ClientAsync();
        using (var now = await client.GetAsync($"/api/articles/{WebFeaturesServer.EditedV2}"))
        {
            using var doc = JsonDocument.Parse(await Body(now));
            var d = doc.RootElement.GetProperty("data");
            Assert.Equal(2, d.GetProperty("version").GetInt32());
            Assert.True(d.GetProperty("hasHistory").GetBoolean());
        }
        using (var old = await client.GetAsync($"/api/articles/{WebFeaturesServer.EditedV2}?version=1"))
        {
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);
            using var doc = JsonDocument.Parse(await Body(old));
            var d = doc.RootElement.GetProperty("data");
            Assert.Equal(1, d.GetProperty("version").GetInt32());
            Assert.Equal(WebFeaturesServer.EditedV1, d.GetProperty("shownItemId").GetInt32());
            // 旧版正文必须真的是旧的（不是拿最新版糊弄）
            string html = d.GetProperty("bodyHtml").GetString() ?? "";
            Assert.Contains("清缓存就好", html);
            Assert.DoesNotContain("更正", html);
        }
    }

    [Fact]
    public async Task Article_UnknownVersion_Is404()
    {
        using var client = await ClientAsync();
        using var res = await client.GetAsync($"/api/articles/{WebFeaturesServer.EditedV2}?version=99");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Export_ReturnsMarkdownAttachment()
    {
        using var client = await ClientAsync();
        using var res = await client.GetAsync($"/api/articles/{WebFeaturesServer.EditedV2}/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
        string md = await Body(res);
        Assert.Contains("被改过的文章", md);
    }

    // ── 跨源去重 ────────────────────────────────────────────────

    [Fact]
    public async Task DedupScan_FindsClusters_AndHideIsReversible()
    {
        using var client = await ClientAsync();

        using (var scan = await client.PostAsync("/api/dedup/scan", new StringContent("{\"window\":48}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
            string body = await Body(scan);
            Assert.Contains("重复内容 A", body);
            Assert.Contains("\"clusters\"", body);
        }

        // 段落级 diff：两篇正文逐字一致 → 0 增 0 删
        using (var diff = await client.GetAsync($"/api/dedup/diff?a={WebFeaturesServer.DupItemA}&b={WebFeaturesServer.DupItemB}"))
        {
            Assert.Equal(HttpStatusCode.OK, diff.StatusCode);
            using var doc = JsonDocument.Parse(await Body(diff));
            var d = doc.RootElement.GetProperty("data");
            Assert.Equal(0, d.GetProperty("added").GetInt32());
            Assert.Equal(0, d.GetProperty("removed").GetInt32());
            Assert.True(d.GetProperty("overlap").GetDouble() > 80);
        }

        // 同一篇跟自己比 → 400（而不是给出一份"完全一致"的假报告）
        using (var self = await client.GetAsync($"/api/dedup/diff?a={WebFeaturesServer.DupItemA}&b={WebFeaturesServer.DupItemA}"))
            Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        // 隐藏 B（以 A 为代表元）
        using (var hide = await client.PostAsync("/api/dedup/hide",
            new StringContent($"{{\"hiddenId\":{WebFeaturesServer.DupItemB},\"canonicalId\":{WebFeaturesServer.DupItemA}}}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        Assert.Equal("dedup", _srv.Instance.ItemStatus(WebFeaturesServer.DupItemB));

        // 隐藏不是删除：还能撤销，撤销后回到 active
        using (var list = await client.GetAsync("/api/dedup"))
        {
            string body = await Body(list);
            Assert.Contains("\"hidden\"", body);
            // dedup.json 的键是 "<hiddenFeedId>:<link>"（与 CLI 同一键空间）
            Assert.Contains($"{WebFeaturesServer.FeedB}:http://b.example/3", body);
        }
        string key = $"{WebFeaturesServer.FeedB}:http://b.example/3";
        using (var undo = await client.PostAsync("/api/dedup/undo",
            new StringContent(JsonSerializer.Serialize(new { key }), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal("active", _srv.Instance.ItemStatus(WebFeaturesServer.DupItemB));
    }

    // ── 源规则 ──────────────────────────────────────────────────

    [Fact]
    public async Task Policies_CanBeAddedListedAndRemoved()
    {
        using var client = await ClientAsync();

        using (var add = await client.PostAsync("/api/policies",
            new StringContent(JsonSerializer.Serialize(new { feedId = WebFeaturesServer.FeedA, action = "tag", tag = "#ai", note = "只看 AI 相关" }), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        using (var list = await client.GetAsync("/api/policies"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            string body = await Body(list);
            Assert.Contains("\"action\":\"tag\"", body);
            Assert.Contains("\"tag\":\"ai\"", body);            // 前导 # 会被去掉，与 CLI 一致
            Assert.Contains("\"createdBy\":\"user\"", body);    // AI 永不自动写
        }

        // 非法频率：必须当场拒（否则终端读不懂网页写下的值）
        using (var bad = await client.PostAsync("/api/policies",
            new StringContent(JsonSerializer.Serialize(new { feedId = WebFeaturesServer.FeedA, action = "lower_frequency", schedule = "每天" }), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("BAD_SCHEDULE", await Body(bad));
        }

        // 未知动作
        using (var unknown = await client.PostAsync("/api/policies",
            new StringContent(JsonSerializer.Serialize(new { feedId = WebFeaturesServer.FeedA, action = "explode" }), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // 不存在的源
        using (var missing = await client.PostAsync("/api/policies",
            new StringContent(JsonSerializer.Serialize(new { feedId = 9999, action = "keep" }), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using (var del = await client.DeleteAsync($"/api/policies/{WebFeaturesServer.FeedA}"))
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        using (var list2 = await client.GetAsync("/api/policies"))
            Assert.DoesNotContain("\"feedId\":1", await Body(list2));
    }

    [Fact]
    public async Task FeedSchedule_RejectsGarbage_AcceptsPreset()
    {
        using var client = await ClientAsync();
        using (var bad = await client.PostAsync($"/api/feeds/{WebFeaturesServer.FeedA}/schedule",
            new StringContent("{\"expr\":\"随时\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using (var ok = await client.PostAsync($"/api/feeds/{WebFeaturesServer.FeedA}/schedule",
            new StringContent("{\"expr\":\"1h\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("1h", _srv.Instance.QueryScalar("SELECT Schedule FROM Feeds WHERE Id=1"));

        // 空串 = 只手动更新（与 CLI 同一语义）
        using (var manual = await client.PostAsync($"/api/feeds/{WebFeaturesServer.FeedA}/schedule",
            new StringContent("{\"expr\":\"\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        Assert.Equal("", _srv.Instance.QueryScalar("SELECT Schedule FROM Feeds WHERE Id=1"));
    }

    // ── 本地导入 / 电子书 ───────────────────────────────────────

    [Fact]
    public async Task Import_UploadListReadDelete_RoundTrip()
    {
        using var client = await ClientAsync();
        string tmp = Path.Combine(Path.GetTempPath(), "sip-feature-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(tmp, "# 一章\n\n这是导入后的正文，用来验证网页端能读回来。", Encoding.UTF8);
        long itemId = 0;
        try
        {
            using (var up = await client.PostAsync("/api/imports?name=" + Uri.EscapeDataString(Path.GetFileName(tmp)),
                new ByteArrayContent(await File.ReadAllBytesAsync(tmp))))
            {
                Assert.Equal(HttpStatusCode.OK, up.StatusCode);
                using var doc = JsonDocument.Parse(await Body(up));
                itemId = doc.RootElement.GetProperty("data").GetProperty("itemId").GetInt64();
                Assert.True(itemId > 0);
            }

            using (var list = await client.GetAsync("/api/imports"))
            {
                string body = await Body(list);
                Assert.Contains($"\"itemId\":{itemId}", body);
                Assert.Contains("\"type\":\"md\"", body);
            }

            using (var text = await client.GetAsync($"/api/imports/{itemId}/text"))
            {
                Assert.Equal(HttpStatusCode.OK, text.StatusCode);
                string body = await Body(text);
                Assert.Contains("导入后的正文", body);
                Assert.DoesNotContain("<script", body);
            }

            // 删除：库里的行与该文件都要消失
            string? stored = _srv.Instance.QueryScalar("SELECT Link FROM Items WHERE Id=@id", ("@id", itemId));
            Assert.True(stored != null && File.Exists(stored), "导入时必须把文件复制进数据目录");
            using (var del = await client.DeleteAsync($"/api/imports/{itemId}"))
                Assert.Equal(HttpStatusCode.OK, del.StatusCode);
            Assert.Null(_srv.Instance.QueryScalar("SELECT Id FROM Items WHERE Id=@id", ("@id", itemId)));
            Assert.False(File.Exists(stored), "删除导入项时要连落地的那份文件一起删");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task Import_RejectsUnsupportedType()
    {
        using var client = await ClientAsync();
        using var res = await client.PostAsync("/api/imports?name=evil.exe", new ByteArrayContent(new byte[] { 1, 2, 3 }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("UNSUPPORTED_FORMAT", await Body(res));
    }

    [Fact]
    public async Task ImportAsset_CannotEscapeTheImportFolder()
    {
        using var client = await ClientAsync();
        string outside = Path.Combine(_srv.Instance.DataDir, "sip_settings.json");
        using var res = await client.GetAsync($"/api/imports/1/asset?path={Uri.EscapeDataString(outside)}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── 治理面 ──────────────────────────────────────────────────

    [Fact]
    public async Task Config_ReportsFactsWithoutSecrets()
    {
        using var client = await ClientAsync();
        using var res = await client.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string body = await Body(res);
        Assert.Contains("\"dataDir\"", body);
        Assert.Contains("\"passwordSet\":true", body);
        Assert.Contains("\"agentGate\"", body);
        Assert.Contains("\"telemetry\"", body);
        // 只报"存没存"，绝不回显 Key 本身
        Assert.Contains("keySet", body);
        Assert.DoesNotContain("api_key", body);
    }

    [Fact]
    public async Task Telemetry_TogglesAndExports()
    {
        using var client = await ClientAsync();

        using (var on = await client.PostAsync("/api/telemetry", new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        using (var status = await client.GetAsync("/api/telemetry"))
        {
            string body = await Body(status);
            Assert.Contains("\"enabled\":true", body);
        }
        using (var export = await client.GetAsync("/api/telemetry/export"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("exportedAt", await Body(export));
        }
        // 关回去（后面同一批用例还在跑，别把状态留给别人）
        using (var off = await client.PostAsync("/api/telemetry", new StringContent("{\"enabled\":false}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    [Fact]
    public async Task IndexStatus_IsReadable_AndRunningWithoutAiIsRefused()
    {
        using var client = await ClientAsync();
        using (var st = await client.GetAsync("/api/index"))
        {
            Assert.Equal(HttpStatusCode.OK, st.StatusCode);
            string body = await Body(st);
            Assert.Contains("\"configured\":false", body);   // 夹具里没有 ai_config.json
            Assert.Contains("\"vectors\"", body);
        }
        // 没配 AI 就跑索引 = 白跑一遍超时；接口必须**先拒绝**并给出下一步
        using (var run = await client.PostAsync("/api/index", new StringContent("{\"reindex\":false}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
            string body = await Body(run);
            Assert.Contains("AI_NOT_CONFIGURED", body);
            Assert.Contains("sip --init", body);
        }
    }

    [Fact]
    public async Task BulkSummaries_WithoutAi_IsRefusedWithHint()
    {
        using var client = await ClientAsync();
        using var res = await client.PostAsync("/api/summaries", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("AI_NOT_CONFIGURED", await Body(res));
    }

    [Fact]
    public async Task InsightsInterval_RejectsGarbage()
    {
        using var client = await ClientAsync();
        using (var bad = await client.PostAsync("/api/insights/interval", new StringContent("{\"interval\":\"每月\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var ok = await client.PostAsync("/api/insights/interval", new StringContent("{\"interval\":\"off\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // ── 命令面板：只读白名单 ────────────────────────────────────

    [Fact]
    public async Task CommandPalette_RunsReadOnly_AndRefusesAnythingElse()
    {
        using var client = await ClientAsync();

        async Task<(HttpStatusCode Code, string Body)> Run(string line) =>
            await Post(client, "/api/command", JsonSerializer.Serialize(new { line }));

        var status = await Run("status");
        Assert.Equal(HttpStatusCode.OK, status.Code);
        Assert.Contains("data", status.Body);

        var list = await Run("list");
        Assert.Equal(HttpStatusCode.OK, list.Code);
        Assert.Contains("源 A", list.Body);

        // 大小写不敏感（与 TUI 命令栏同一约定）
        Assert.Equal(HttpStatusCode.OK, (await Run("LIST")).Code);

        // 写命令一条都不许执行
        foreach (string bad in new[] { "--import /etc/passwd", "delete 1", "sync", "telemetry enable", "simon level 1" })
        {
            var r = await Run(bad);
            Assert.Equal(HttpStatusCode.BadRequest, r.Code);
            Assert.Contains("UNKNOWN_COMMAND", r.Body);
        }
    }

    [Fact]
    public async Task CommandPalette_UnknownCommand_ListsWhatIsAllowed()
    {
        using var client = await ClientAsync();
        var r = await Post(client, "/api/command", JsonSerializer.Serialize(new { line = "frobnicate" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.Code);
        Assert.Contains("grep", r.Body);        // hint 里要给出可用的命令
    }

    // ── 阅读位置 ────────────────────────────────────────────────

    [Fact]
    public async Task ReadingProgress_RoundTrips()
    {
        using var client = await ClientAsync();
        using (var set = await client.PostAsync("/api/reading-progress",
            new StringContent(JsonSerializer.Serialize(new { itemId = WebFeaturesServer.EditedV2, position = 420 }), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using (var get = await client.GetAsync("/api/reading-progress"))
        {
            string body = await Body(get);
            Assert.Contains($"\"{WebFeaturesServer.EditedV2}\":420", body);
        }
    }

    private static async Task<(HttpStatusCode Code, string Body)> Post(HttpClient client, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await client.PostAsync(path, content);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// 挡位这一轴单独一个实例：升档会改变这个进程的写策略，
/// 和别的用例共用夹具会互相污染（而且挡位**降不下来** —— 那正是设计意图）。
/// </summary>
public sealed class WebSimonServer : SipWebServer
{
    public WebSimonServer() : base(passwordMode: true)
    {
        Instance.InsertFeed(1, "源 A", "http://a.example/feed.xml");
    }
}

public class WebSimonTests : IClassFixture<WebSimonServer>
{
    private readonly WebSimonServer _srv;

    public WebSimonTests(WebSimonServer srv) => _srv = srv;

    [Fact]
    public async Task TighteningIsAllowed_LooseningIsNot()
    {
        var client = _srv.NewClient(out var jar, "sip-test/simon");
        using (var login = await _srv.LoginAsync(client, jar))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using (var st = await client.GetAsync("/api/simon"))
        {
            Assert.Equal(HttpStatusCode.OK, st.StatusCode);
            string body = await st.Content.ReadAsStringAsync();
            Assert.Contains("\"canDisable\":false", body);       // 默认开启、无法关闭
            Assert.Contains("\"level\":1", body);
            Assert.Contains("\"agentGate\"", body);
        }

        // 升档（收紧）：任意通道放行 —— 发现异常时要能立刻收紧
        using (var up = await client.PostAsync("/api/simon/level", new StringContent("{\"level\":2}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
            Assert.Contains("\"level\":2", await up.Content.ReadAsStringAsync());
        }

        // 升档之后，写操作被挡（与 CLI 同一挡位语义）
        using (var write = await client.PostAsync("/api/policies", new StringContent("{\"feedId\":1,\"action\":\"keep\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.Contains("SIMON_BLOCKED", await write.Content.ReadAsStringAsync());
        }
        // 但读照常
        using (var read = await client.GetAsync("/api/feeds"))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // 降档（放宽）：浏览器不是人工通道 —— 必须拒绝并告诉人去终端
        using (var down = await client.PostAsync("/api/simon/level", new StringContent("{\"level\":1}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, down.StatusCode);
            string body = await down.Content.ReadAsStringAsync();
            Assert.Contains("SIMON_LOOSEN_REQUIRES_TERMINAL", body);
            Assert.Contains("sip simon level 1", body);
        }

        // 非法挡位
        using (var bad = await client.PostAsync("/api/simon/level", new StringContent("{\"level\":9}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
