using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// Web 正文净化的端到端黑盒测试：真起一个 <c>sip --start</c> 进程，经 HTTP 取
/// <c>/api/articles/{id}</c>，断言「不可信的订阅源正文永远不会以活标记的形式到达浏览器」。
///
/// 为什么要走进程 + 真 HTTP，而不是直接调 ToSafeBodyHtml：
///   净化的价值全在**边界**上 —— 谁把未净化内容塞进 JSON、认证是否真拦住匿名请求、
///   纯文本分支是否漏编码。这些只有把整条链路跑起来才测得到；单元级白盒测试
///   会跟着实现一起改，等于把 bug 抄进断言里。
///
/// 依赖（已由产品代码确认）：
///   · <c>sip --start</c> 前台阻塞，横幅里打印 <c>http://127.0.0.1:&lt;port&gt;/?t=&lt;token&gt;</c>；
///   · 裸 GET / 只给提示页，必须带引导令牌换 <c>sip_local</c> cookie 才能访问 /api/*；
///   · <c>data.bodyHtml</c> 是服务端已净化好的 HTML（原始 content/fulltext 不再外泄）。
/// </summary>
public class WebSanitizeTests : IClassFixture<WebSanitizeFixture>
{
    private readonly WebSanitizeFixture _fx;

    public WebSanitizeTests(WebSanitizeFixture fx) => _fx = fx;

    /// <summary>XSS 语料必须被中和：危险构造一个不剩，同时正常结构不能被一起清空。</summary>
    [Fact]
    public async Task XssPayload_IsNeutralized()
    {
        using var client = _fx.NewClient(out var jar);
        await _fx.ConsumeBootTokenAsync(client, jar);

        string body = await _fx.GetBodyHtmlAsync(client, WebSanitizeFixture.XssItemId);

        // 只断言「属性形态 / 标签形态」，绝不断言裸词。
        // 语料里的可见文字本身就含 onclick、javascript:（是给读者看的文本，不是标记），
        // 断言裸词只会得到一个假失败。
        string[] forbidden =
        [
            "onclick=", "onerror=", "onload=", "href=\"javascript:", "style=",
            "<iframe", "<script", "<svg", "<form", "<object", "<marquee",
            "src=\"x\"", "src=\"/api/feeds\"", "<input", "<button",
        ];
        foreach (string bad in forbidden)
            Assert.DoesNotContain(bad, body, StringComparison.OrdinalIgnoreCase);

        // 净化 ≠ 清空：白名单内的结构和属性必须原样保留，否则「安全」是靠删光正文换来的
        string[] required =
        [
            "<figure", "<figcaption", "colspan",
            "href=\"http://ok.example/fine\"", "rel=\"noopener noreferrer\"", "loading=\"lazy\"",
            "&lt;script&gt;", "<table",
        ];
        foreach (string good in required)
            Assert.Contains(good, body, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("<p>", body);
    }

    /// <summary>响应里不许再有原始字段（content/fulltext），整包也不许漏出未净化的原文。</summary>
    [Fact]
    public async Task Response_DoesNotLeakRawFields()
    {
        using var client = _fx.NewClient(out var jar);
        await _fx.ConsumeBootTokenAsync(client, jar);

        string json = await _fx.GetArticleJsonAsync(client, WebSanitizeFixture.XssItemId);
        using var doc = JsonDocument.Parse(json);

        foreach (var p in doc.RootElement.EnumerateObject())
            Assert.False(p.Name is "content" or "fulltext", $"顶层多出原始正文字段：{p.Name}");

        var data = doc.RootElement.GetProperty("data");
        foreach (var p in data.EnumerateObject())
            Assert.False(p.Name is "content" or "fulltext", $"data 里多出原始正文字段：{p.Name}");

        Assert.True(data.TryGetProperty("bodyHtml", out var bodyHtmlEl), "缺少 data.bodyHtml");
        string body = bodyHtmlEl.GetString() ?? "";
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);

        // 整包（不只是 bodyHtml）都不许漏出「原始标记形态」。
        // 只挑语料里真实存在、且净化后必然消失的**属性/标签形态**：
        // 语料的可见文字本来就含 onclick 和 alert(（那是文本，不是标记），断言裸词只会得到假失败。
        string[] rawOnly =
        [
            "onclick=", "onerror=", "onload=", "<iframe", "<svg", "<object", "<form", "/api/feeds",
        ];
        foreach (string bad in rawOnly)
        {
            Assert.Contains(bad, WebSanitizeFixture.XssPayload, StringComparison.OrdinalIgnoreCase);   // 语料里确有 → 断言不空过
            Assert.DoesNotContain(bad, json, StringComparison.OrdinalIgnoreCase);                      // 响应里确无
        }
    }

    /// <summary>纯文本正文（没有任何标签样文本）走编码分支：转义 + 按空行包 &lt;p&gt;，绝不原样注入。</summary>
    [Fact]
    public async Task PlainTextBody_IsEncodedAndWrapped()
    {
        using var client = _fx.NewClient(out var jar);
        await _fx.ConsumeBootTokenAsync(client, jar);

        string body = await _fx.GetBodyHtmlAsync(client, WebSanitizeFixture.PlainItemId);

        Assert.Contains("<p>", body);
        Assert.Contains("&amp;", body);   // 裸 & 必须转义
        Assert.Contains("&lt;", body);    // 裸 < 必须转义
        Assert.DoesNotContain("& ", body);            // 没有漏网的裸 &
        Assert.DoesNotContain("< ", body);            // 没有漏网的裸 <
        Assert.Contains("第二段同样没有标签", body);   // 第二段没被吃掉
    }

    /// <summary>认证契约：没换过票的 /api/* 一律 401，换到 sip_local 之后才 200。</summary>
    [Fact]
    public async Task Api_RequiresBootTokenCookie()
    {
        int id = WebSanitizeFixture.XssItemId;
        using var client = _fx.NewClient(out var jar);

        using (var anon = await client.GetAsync($"/api/articles/{id}"))
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // 裸访问 / 不是应用，也不下发凭据（引导令牌只走终端这条带外信道）
        using (var bare = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
            Assert.False(bare.Headers.Contains("Set-Cookie"), "裸访问 / 不该下发 sip_local");
        }

        using (var boot = await _fx.ConsumeBootTokenAsync(client, jar))
        {
            Assert.Equal(HttpStatusCode.Found, boot.StatusCode);
            Assert.Equal("/", boot.Headers.Location?.OriginalString);
            Assert.True(boot.Headers.Contains("Set-Cookie"), "换票响应必须带 Set-Cookie");
        }
        Assert.True(jar.Count > 0, "sip_local 没有被复制进 cookie 容器");

        using (var authed = await client.GetAsync($"/api/articles/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
            Assert.Contains("<p>", await WebSanitizeFixture.ReadBodyHtmlAsync(authed));
        }
    }

    /// <summary>空正文只能给出空 bodyHtml —— 任何「兜底模板」都可能成为注入点。</summary>
    [Fact]
    public async Task EmptyBody_YieldsEmptyBodyHtml()
    {
        using var client = _fx.NewClient(out var jar);
        await _fx.ConsumeBootTokenAsync(client, jar);

        string body = await _fx.GetBodyHtmlAsync(client, WebSanitizeFixture.EmptyItemId);

        Assert.Equal("", body);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>无密码模式下，<c>POST /api/login</c> **不能**换到任何凭据。
    /// 它能换到的话，本机任意进程 POST 一个空 body 就进了门 —— 终端里那条 <c>?t=</c>
    /// 引导链接（唯一的带外信道）也就白设了。</summary>
    [Fact]
    public async Task NoPasswordMode_LoginEndpoint_DoesNotMintAnything()
    {
        using var client = _fx.NewClient(out var jar);

        using var res = await client.PostAsync("/api/login",
            new StringContent("""{"password":"whatever"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        Assert.Contains("bootTokenRequired", body);
        Assert.False(res.Headers.Contains("Set-Cookie"), "无密码模式下 POST /api/login 不该下发任何 cookie");
        Assert.Equal(0, jar.Count);

        using var stillAnon = await client.GetAsync($"/api/articles/{WebSanitizeFixture.XssItemId}");
        Assert.Equal(HttpStatusCode.Unauthorized, stillAnon.StatusCode);
    }

    /// <summary>换浏览器 = 手里没有 <c>sip_local</c> → 必须重新打开终端里那条引导链接。
    /// 这与密码模式的「换浏览器要重新输密码」是同一条规矩的两种形态。</summary>
    [Fact]
    public async Task NoPasswordMode_AnotherBrowser_StillNeedsTheBootLink()
    {
        // 浏览器 A：换票成功，进得去
        using var a = _fx.NewClient(out var jarA);
        await _fx.ConsumeBootTokenAsync(a, jarA);
        using (var ok = await a.GetAsync($"/api/articles/{WebSanitizeFixture.XssItemId}"))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // 浏览器 B：什么都没带 —— 接口 401，页面是「请用终端里打印的链接打开」那张说明页
        using var b = _fx.NewClient(out _);
        using (var anon = await b.GetAsync($"/api/articles/{WebSanitizeFixture.XssItemId}"))
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        string html = await b.GetStringAsync("/");
        Assert.True(SipWebServer.IsBootHintPage(html), "换浏览器后 GET / 应该给「去终端复制链接」的说明页");
        Assert.False(SipWebServer.IsAppPage(html), "没换票不该直接给应用页");
    }
}

/// <summary>
/// 一个跑着 <c>sip --start</c> 的隔离实例（含 HTTP 客户端工厂）。
///
/// 三个「为什么」：
///   · 为什么自己 <see cref="Process.Start(ProcessStartInfo)"/> 而不用 <see cref="SipInstance.Run"/>：
///     Run 会把 stdout 读到底 —— 而 --start 永不退出，永远读不完。必须逐行读、读到横幅就停。
///   · 为什么每个用例各自一个 HttpClient + CookieContainer：
///     cookie 是认证状态本身，共用就等于让用例互相污染（401 用例会被换过票的用例搞成假绿）。
///   · 为什么手工把 Set-Cookie 复制进容器：不依赖 HttpClientHandler 是否顺带收录
///     302 响应上的 cookie（那是实现细节，收紧/改动都不该让测试变红）。
/// </summary>
public sealed class WebSanitizeFixture : IDisposable
{
    public const int XssFeedId = 1;
    public const int XssItemId = 1;
    public const int PlainItemId = 2;
    public const int EmptyItemId = 3;

    /// <summary>XSS 语料：把「标签白名单 + 属性白名单」的每条边界都摆一遍。</summary>
    public const string XssPayload = """<h2>标题</h2><p>正常段落</p><script>alert('s')</script><p onclick="alert('p')">带 onclick 的段落</p><img src="x" onerror="alert('i')"><img src="https://ok.example/keep.png" alt="keep" onerror="alert('x')"><a href="javascript:alert('j')">js 链接</a><a href="http://ok.example/fine" onclick="alert('a')">正常链接</a><iframe src="http://evil.example/f"></iframe><svg onload="alert('v')"><circle r="10"/></svg><style>body{display:none}</style><form action="http://evil.example/p"><input name="x"><button>go</button></form><object data="http://evil.example/o"></object><marquee>未知标签</marquee><div style="position:fixed">遮罩</div><figure><img src="https://ok.example/fig.png" onerror="alert('f')"><figcaption>图注</figcaption></figure><table><thead><tr><th colspan="2">表头</th></tr></thead><tbody><tr><td>甲</td><td>乙</td></tr></tbody></table><p>实体：&lt;script&gt;alert(1)&lt;/script&gt; 与 &amp;amp; 与 &gt;</p><p>相对图 <img src="/api/feeds"></p>""";

    /// <summary>纯文本语料：<c>&lt;[a-zA-Z]</c> 一个都没有，因此走 HtmlEncode 分支。中间空行 = 两段。</summary>
    public const string PlainPayload = """
        第一段没有尖括号，但有 & 和 < 字符。

        第二段同样没有标签。
        """;

    private readonly SipInstance _sip = new();
    private readonly StringBuilder _log = new();
    private Process? _proc;

    public string BaseUrl { get; private set; } = "";
    public string BootToken { get; private set; } = "";

    public WebSanitizeFixture()
    {
        _sip.EnsureDatabase();
        // fixture 必须在起服务之前插好：虽然服务每次请求都重开库，
        // 但「先有数据再开门」读起来才是确定的
        _sip.InsertFeed(XssFeedId, "净化测试源", "http://example.com/feed.xml");
        _sip.InsertItem(XssItemId, XssFeedId, "XSS 语料", "http://example.com/a1", XssPayload, "g-xss");
        _sip.InsertItem(PlainItemId, XssFeedId, "纯文本正文", "http://example.com/a2", PlainPayload, "g-plain");
        _sip.InsertItem(EmptyItemId, XssFeedId, "空正文", "http://example.com/a3", "", "g-empty");

        StartServer();
    }

    // ── HTTP 客户端 ────────────────────────────────────────────

    /// <summary>每个用例一个新客户端（自带空 cookie 罐），避免认证状态串味。</summary>
    public HttpClient NewClient(out CookieContainer jar)
    {
        jar = new CookieContainer();
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,   // 302 要自己看，正是「换票」这一步的断言对象
            CookieContainer = jar,
            UseCookies = true,
        })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>换票：GET <c>/?t=&lt;boot token&gt;</c> → 302 + Set-Cookie: sip_local。</summary>
    public async Task<HttpResponseMessage> ConsumeBootTokenAsync(HttpClient client, CookieContainer jar)
    {
        var res = await client.GetAsync($"/?t={BootToken}");
        if (res.Headers.TryGetValues("Set-Cookie", out var vals))
            foreach (string raw in vals) CopyCookie(jar, raw);
        return res;
    }

    /// <summary>只搬 name=value；Path/HttpOnly/SameSite/Max-Age 由我们按同源根路径重设
    /// （SameSite、Max-Age 这些原文 CookieContainer 并不都认，交给它解析可能整条丢掉）。</summary>
    private void CopyCookie(CookieContainer jar, string setCookie)
    {
        string pair = setCookie.Split(';')[0];
        int eq = pair.IndexOf('=');
        if (eq <= 0) return;
        string name = pair[..eq].Trim();
        string value = pair[(eq + 1)..].Trim();
        if (name.Length == 0 || value.Length == 0) return;
        jar.Add(new Uri(BaseUrl), new Cookie(name, value) { Path = "/", HttpOnly = true });
    }

    public async Task<string> GetArticleJsonAsync(HttpClient client, int itemId)
    {
        using var res = await client.GetAsync($"/api/articles/{itemId}");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    public async Task<string> GetBodyHtmlAsync(HttpClient client, int itemId)
        => await ReadBodyHtmlAsync(await client.GetAsync($"/api/articles/{itemId}"));

    /// <summary>取出 data.bodyHtml。这里必须真的解析 JSON：
    /// 直接对响应文本做子串匹配会被转义序列骗过去（假绿）。</summary>
    public static async Task<string> ReadBodyHtmlAsync(HttpResponseMessage res)
    {
        res.EnsureSuccessStatusCode();
        string json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("bodyHtml").GetString() ?? "";
    }

    // ── 起服务 / 收尸 ──────────────────────────────────────────

    private void StartServer()
    {
        Exception? last = null;
        // 端口是从「临时占一下再放掉」拿到的，理论上存在被别人抢走的窗口；
        // 抢走只会表现为起不来 → 换一个端口重试，别把偶发当成产品缺陷
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            int port = FreePort();
            WriteSettings(port);
            try
            {
                _proc = Launch(port, TimeSpan.FromSeconds(30), out string token);
                BaseUrl = $"http://127.0.0.1:{port}";
                BootToken = token;
                WaitUntilResponsive();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                KillServer();
            }
        }
        throw new InvalidOperationException(
            $"sip --start 重试 3 次仍未就绪：{last?.Message}\n--- sip 输出 ---\n{Log()}", last);
    }

    /// <summary>端口必须自己挑：默认 8777 会和开发者本机正在跑的那个撞车。</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>首次交互初始化在无真实终端时会提问 —— 预置 WebSetupDone 让 --start 直接照办。</summary>
    private void WriteSettings(int port)
    {
        Directory.CreateDirectory(_sip.DataDir);
        File.WriteAllText(Path.Combine(_sip.DataDir, "sip_settings.json"),
            $"{{ \"WebHost\": \"127.0.0.1\", \"WebPort\": {port}, \"WebSetupDone\": true }}");
    }

    private Process Launch(int port, TimeSpan timeout, out string token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_sip.Root, OperatingSystem.IsWindows() ? "sip.exe" : "sip"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--start");
        psi.Environment["SIP_SIMON_KEY_NAME"] = _sip.KeyName;   // 与 TestHost 同一隔离凭据作用域

        var proc = Process.Start(psi)!;
        // stderr 必须一直抽干：管道缓冲写满后子进程会卡住，而它写的东西正是启动失败的原因
        _ = Task.Run(async () =>
        {
            try { string s = await proc.StandardError.ReadToEndAsync(); lock (_log) _log.Append(s); }
            catch { /* 进程被杀时会抛，正常 */ }
        });

        var sw = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan remain = timeout - sw.Elapsed;
            if (remain <= TimeSpan.Zero)
                throw new TimeoutException($"等 Web UI 横幅超时（{timeout.TotalSeconds:0} 秒）");

            string? line;
            // 同步等一行：Launch 被构造函数直接调用，这里没有 async 上下文（xunit 无同步上下文，不会死锁）
            try { line = proc.StandardOutput.ReadLineAsync().WaitAsync(remain).GetAwaiter().GetResult(); }
            catch (TimeoutException) { throw new TimeoutException($"等 Web UI 横幅超时（{timeout.TotalSeconds:0} 秒）"); }
            if (line == null)
                throw new InvalidOperationException("sip --start 提前退出，没打印 Web UI 地址");
            lock (_log) _log.AppendLine(line);

            // 只认 URL 本身，不认标签文案：标签会随语言文件变，URL 不会
            var m = Regex.Match(line, @"http://127\.0\.0\.1:(?<port>\d+)/\?t=(?<token>[0-9a-fA-F]+)");
            if (!m.Success) continue;

            int got = int.Parse(m.Groups["port"].Value);
            if (got != port)
                throw new InvalidOperationException($"Web 端口不符：期望 {port}，实际 {got}");
            token = m.Groups["token"].Value;
            return proc;
        }
    }

    /// <summary>横幅只证明它打算监听；真正能收请求才算就绪（HttpListener 注册是异步生效的）。</summary>
    private void WaitUntilResponsive()
    {
        using var client = NewClient(out _);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var res = client.GetAsync("/api/status").GetAwaiter().GetResult();
                return;   // 401/200 都行：有 HTTP 响应就说明监听已生效
            }
            catch (HttpRequestException) { Thread.Sleep(150); }
            catch (TaskCanceledException) { Thread.Sleep(150); }
        }
        throw new TimeoutException("Web 服务器 20 秒内没有响应 /api/status");
    }

    private void KillServer()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(10_000); } catch { }
        try { p.Dispose(); } catch { }
    }

    private string Log()
    {
        lock (_log) return _log.ToString();
    }

    public void Dispose()
    {
        KillServer();          // 先收进程：server 还开着库文件时删目录会失败
        _sip.Dispose();
    }
}
