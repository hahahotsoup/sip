using System.Net;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 登录 / 会话的契约（黑盒：真起 <c>sip --start</c> + 真 HTTP + 真 cookie 罐）。
///
/// 这套设计就三条规矩，每条都有用例钉着：
///   ① **会话只活到进程结束** —— <see cref="Restart_ForcesLoginAgain_EvenWithTheSameCookie"/>：
///      重启后同一串 cookie 必须被拒，并且登录页要说明原因。
///   ② **每个浏览器一把钥匙** —— <see cref="FreshBrowser_MustLogInAgain"/> 与
///      <see cref="CookieCopiedToAnotherBrowser_IsRefused_AndRevoked"/>。
///   ③ **一次一换** —— <see cref="ReloginInTheSameBrowser_RotatesTheKey"/>。
///
/// 为什么每条都要真起进程：这些都是「跨请求、跨进程」的状态问题，
/// 只测函数等于把最关键的那一环（状态存在哪里）测掉了。
/// </summary>
public class WebLoginTests : IClassFixture<WebLoginServer>
{
    private readonly WebLoginServer _srv;

    public WebLoginTests(WebLoginServer srv) => _srv = srv;

    private const string BrowserA = "sip-test/browser-A";
    private const string BrowserB = "sip-test/browser-B";

    [Fact]
    public async Task LoginOnce_ThenThisBrowserStaysIn()
    {
        var client = _srv.NewClient(out var jar, BrowserA);

        // 没登录过：接口 401，页面是登录页（并且**没有**「重启过」那句提示 —— 全新浏览器没有旧 cookie）
        using (var anon = await client.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        string before = await client.GetStringAsync("/");
        Assert.True(SipWebServer.IsLoginPage(before), "未登录时 GET / 应该给登录页");
        Assert.False(SipWebServer.HasRestartNotice(before), "全新浏览器不该看到「重启过」的提示");

        using (var res = await _srv.LoginAsync(client, jar))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(res.Headers.Contains("Set-Cookie"), "登录成功必须下发会话 cookie");
        }

        using (var st = await client.GetAsync("/api/auth/status"))
        {
            Assert.Equal(HttpStatusCode.OK, st.StatusCode);
            string body = await st.Content.ReadAsStringAsync();
            // 会话的**范围**是这次契约的核心：进程内、重启即失效
            Assert.Contains("\"authenticated\":true", body);
            Assert.Contains("\"sessionScope\":\"process\"", body);
            Assert.Contains("\"restartRequiresLogin\":true", body);
        }

        // 之后同一个浏览器刷新 / 调接口都不再要密码
        using (var ok = await client.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(SipWebServer.IsAppPage(await client.GetStringAsync("/")), "登录后 GET / 应该给应用页");

        client.Dispose();
    }

    [Fact]
    public async Task FreshBrowser_MustLogInAgain()
    {
        var a = _srv.NewClient(out var jarA, BrowserA);
        using (var res = await _srv.LoginAsync(a, jarA))
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // 换一个浏览器 = 另一个 cookie 罐（浏览器之间本来就不共享 cookie）
        var b = _srv.NewClient(out _, BrowserB);
        using (var anon = await b.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.True(SipWebServer.IsLoginPage(await b.GetStringAsync("/")), "换浏览器必须重新看到登录页");

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task CookieCopiedToAnotherBrowser_IsRefused_AndRevoked()
    {
        var a = _srv.NewClient(out var jarA, BrowserA);
        using (var res = await _srv.LoginAsync(a, jarA))
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string key = _srv.CookieValue(jarA, "sip_web");
        Assert.False(string.IsNullOrEmpty(key), "登录后 cookie 罐里应该有 sip_web");

        // 把这串值抄进另一个浏览器：钥匙本身是真的，但它不属于这个浏览器
        var b = _srv.NewClient(out var jarB, BrowserB);
        _srv.InjectCookie(jarB, "sip_web", key);
        using (var refused = await b.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // 指纹对不上 = 这把钥匙已泄漏 → 服务端当场吊销，原浏览器也必须重新登录
        var a2 = _srv.NewClient(out var jarA2, BrowserA);
        _srv.InjectCookie(jarA2, "sip_web", key);
        using (var revoked = await a2.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        a.Dispose();
        b.Dispose();
        a2.Dispose();
    }

    [Fact]
    public async Task ReloginInTheSameBrowser_RotatesTheKey()
    {
        var client = _srv.NewClient(out var jar, BrowserA);
        using (var first = await _srv.LoginAsync(client, jar))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string oldKey = _srv.CookieValue(jar, "sip_web");

        using (var second = await _srv.LoginAsync(client, jar))
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        string newKey = _srv.CookieValue(jar, "sip_web");

        Assert.NotEqual(oldKey, newKey);          // 一次一换：每次登录都换一把新钥匙
        using (var ok = await client.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);   // 新钥匙可用

        // 旧钥匙当场作废（即使指纹对得上）
        var stale = _srv.NewClient(out var staleJar, BrowserA);
        _srv.InjectCookie(staleJar, "sip_web", oldKey);
        using (var refused = await stale.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        client.Dispose();
        stale.Dispose();
    }

    [Fact]
    public async Task Logout_RevokesTheKeyOnTheServer()
    {
        var client = _srv.NewClient(out var jar, BrowserA);
        using (var res = await _srv.LoginAsync(client, jar))
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string key = _srv.CookieValue(jar, "sip_web");

        using (var bye = await client.PostAsync("/api/logout", new StringContent("")))
            Assert.Equal(HttpStatusCode.OK, bye.StatusCode);

        // 只清 cookie 是不够的：抄走这串值的人必须一起失效
        var after = _srv.NewClient(out var afterJar, BrowserA);
        _srv.InjectCookie(afterJar, "sip_web", key);
        using (var refused = await after.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        client.Dispose();
        after.Dispose();
    }

    /// <summary>核心用例：**重启程序后，同一个浏览器手里的 cookie 必须失效**，
    /// 而且登录页要说清楚为什么（而不是莫名其妙又弹一个密码框）。</summary>
    [Fact]
    public async Task Restart_ForcesLoginAgain_EvenWithTheSameCookie()
    {
        using var srv = new SipWebServer(passwordMode: true);
        var client = srv.NewClient(out var jar, BrowserA);

        using (var res = await srv.LoginAsync(client, jar))
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using (var ok = await client.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        srv.Restart();   // 同一个数据目录、同一个端口，全新进程；客户端手里还攥着旧 cookie

        using (var after = await client.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        string html = await client.GetStringAsync("/");
        Assert.True(SipWebServer.IsLoginPage(html), "重启后 GET / 应该回到登录页");
        Assert.True(SipWebServer.HasRestartNotice(html), "登录页应该说明「sip 重启过了」");

        // 重新输一次密码就能继续用（会话重新签发）
        using (var again = await srv.LoginAsync(client, jar))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var ok2 = await client.GetAsync("/api/status"))
            Assert.Equal(HttpStatusCode.OK, ok2.StatusCode);
        Assert.True(SipWebServer.IsAppPage(await client.GetStringAsync("/")));

        client.Dispose();
    }

    /// <summary>限流用独立实例：它会把 5 分钟窗口填满，落在共享实例上会误伤别的用例
    /// （限流发生在验密码**之前**，正确密码也一样被拦）。</summary>
    [Fact]
    public async Task TooManyBadPasswords_BlocksEvenTheRightPassword()
    {
        using var srv = new SipWebServer(passwordMode: true);
        var client = srv.NewClient(out var jar, BrowserA);

        for (int i = 0; i < 20; i++)
        {
            using var bad = await srv.LoginAsync(client, jar, "definitely-wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        using (var blocked = await srv.LoginAsync(client, jar))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.True(blocked.Headers.Contains("Retry-After"), "被限流时应告诉客户端等多久");
        }

        client.Dispose();
    }
}

/// <summary>共享的**密码模式**实例：除「重启」「限流」两个用例各自建实例外，其余都在它上面跑。</summary>
public sealed class WebLoginServer : SipWebServer
{
    public WebLoginServer() : base(passwordMode: true) { }
}
