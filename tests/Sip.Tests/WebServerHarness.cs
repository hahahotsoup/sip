using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sip.Tests;

/// <summary>
/// 一个跑着 <c>sip --start</c> 的隔离实例：起服务、停、**重启**（同一数据目录、同一端口），
/// 外加 HTTP 客户端工厂与「登录一次」的辅助方法。
///
/// 与 <see cref="WebSanitizeFixture"/> 里那套起服务代码的关系：那套是正文净化测试的夹具
/// （无密码模式 + XSS 语料）。本类是为「登录 / 会话」契约抽出来的通用小工具，
/// **刻意不去改那个已经在跑的夹具** —— 改夹具的风险比这里重复几十行大得多。
///
/// 每个 <see cref="SipInstance"/> 自带一套隔离数据目录与凭据作用域，用完即删。
/// </summary>
public class SipWebServer : IDisposable
{
    public const string DefaultPassword = "hotsoup-login-2026";

    private readonly SipInstance _sip = new();
    private readonly StringBuilder _log = new();
    private Process? _proc;
    private int _port;

    public bool PasswordMode { get; }
    public string Password { get; }
    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>无密码模式下的引导令牌（终端横幅里那条 <c>?t=</c>）；密码模式为空串。</summary>
    public string BootToken { get; private set; } = "";

    public SipWebServer(bool passwordMode = true, string? password = null)
    {
        PasswordMode = passwordMode;
        Password = password ?? DefaultPassword;
        _sip.EnsureDatabase();
        // fixture 必须在起服务之前把口令/设置写好：服务每次请求都重开这些文件，
        // 但「先有凭据再开门」读起来才是确定的
        if (passwordMode) WritePasswordFile();
        try { Start(); }
        catch
        {
            // 构造失败时 xunit 不会 Dispose 这个夹具 → 没人清理那份 700 MB 的实例目录。
            // 自己收尸，别把失败变成一个装满垃圾的磁盘。
            _sip.Dispose();
            throw;
        }
    }

    /// <summary>重启：**同一个数据目录、同一个端口**，全新的进程 —— 用来验证「会话活不过重启」。</summary>
    public void Restart()
    {
        Kill();
        Start();
    }

    public string Log()
    {
        lock (_log) return _log.ToString();
    }

    // ── HTTP 客户端 ────────────────────────────────────────────

    /// <summary>每个用例一个新客户端（自带空 cookie 罐），避免认证状态串味。
    /// <paramref name="userAgent"/> 就是「浏览器指纹」——换一个 UA 等于换一个浏览器。</summary>
    public HttpClient NewClient(out CookieContainer jar, string userAgent = "")
    {
        jar = new CookieContainer();
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,   // 302 要自己看，正是「换票」这一步的断言对象
            CookieContainer = jar,
            UseCookies = true,
        })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (userAgent.Length > 0) client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>把响应里的 Set-Cookie 搬进 cookie 罐。不依赖 HttpClientHandler 是否顺带收录 ——
    /// 那是实现细节，收紧/改动都不该让测试变红。</summary>
    public void CollectCookies(CookieContainer jar, HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var vals)) return;
        foreach (string raw in vals) CopyCookie(jar, raw);
    }

    /// <summary>只搬 name=value；Path/HttpOnly/SameSite/Max-Age 由我们按同源根路径重设
    /// （SameSite、Max-Age 这些原文 CookieContainer 并不都认，交给它解析可能整条丢掉）。
    /// <c>Max-Age=0</c>（清除）按删除处理，否则「登出」在罐里清不掉。</summary>
    private void CopyCookie(CookieContainer jar, string setCookie)
    {
        string pair = setCookie.Split(';')[0];
        int eq = pair.IndexOf('=');
        if (eq <= 0) return;
        string name = pair[..eq].Trim();
        string value = pair[(eq + 1)..].Trim();
        bool clearing = setCookie.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0) return;
        if (value.Length == 0 || clearing)
        {
            try { jar.SetCookies(new Uri(BaseUrl), name + "="); } catch { }
            return;
        }
        jar.Add(new Uri(BaseUrl), new Cookie(name, value) { Path = "/", HttpOnly = true });
    }

    /// <summary>登录一次（顺手把 cookie 收进罐里）。失败也返回响应，让用例自己断言。</summary>
    public async Task<HttpResponseMessage> LoginAsync(HttpClient client, CookieContainer jar, string? password = null)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { password = password ?? Password }),
            Encoding.UTF8, "application/json");
        var res = await client.PostAsync("/api/login", content);
        CollectCookies(jar, res);
        return res;
    }

    /// <summary>读出罐里某个 cookie 的值（没有就返回空串）。</summary>
    public string CookieValue(CookieContainer jar, string name)
        => jar.GetCookies(new Uri(BaseUrl))[name]?.Value ?? "";

    /// <summary>把一串 cookie 值塞进另一个客户端的罐里 —— 模拟「cookie 被抄到了别的浏览器」。</summary>
    public void InjectCookie(CookieContainer jar, string name, string value)
        => jar.Add(new Uri(BaseUrl), new Cookie(name, value) { Path = "/", HttpOnly = true });

    // ── 页面形态 ───────────────────────────────────────────────

    /// <summary>登录页：只有 <c>web/login.html</c> 里有 <c>id="pass"</c>。</summary>
    public static bool IsLoginPage(string html) => html.Contains("id=\"pass\"", StringComparison.Ordinal);

    /// <summary>应用页：只有 <c>web/index.html</c> 里有 <c>id="app"</c>。</summary>
    public static bool IsAppPage(string html) => html.Contains("id=\"app\"", StringComparison.Ordinal);

    /// <summary>无密码模式的说明页（服务端硬编码中文，与语言设置无关）。</summary>
    public static bool IsBootHintPage(string html)
        => html.Contains("请用终端里打印的链接打开", StringComparison.Ordinal);

    /// <summary>登录页上那句「为什么要重新输密码」的提示。用 class 判定，不依赖界面语言。</summary>
    public static bool HasRestartNotice(string html) => html.Contains("class=\"notice\"", StringComparison.Ordinal);

    // ── 起服务 / 收尸 ──────────────────────────────────────────

    private void Start()
    {
        Exception? last = null;
        // 端口是「临时占一下再放掉」拿到的，存在被别人抢走的窗口；
        // 抢走只会表现为起不来 → 换一个端口重试，别把偶发当成产品缺陷
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (_port == 0 || attempt > 1) _port = FreePort();
            WriteSettings(_port);
            try
            {
                _proc = Launch(_port, TimeSpan.FromSeconds(30), out string token);
                BootToken = token;
                WaitUntilResponsive();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Kill();
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

    /// <summary>直接按 <c>web_auth.json</c> 的格式写口令哈希（与 Web.cs 的 SetWebPassword 同参数：
    /// PBKDF2-SHA256 / 100,000 次 / 16 字节盐 / 32 字节输出 / Base64）。
    /// 不走 <c>sip webpass</c>：那条路要真实 TTY，测试环境里没有。</summary>
    private void WritePasswordFile()
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        const int iter = 100_000;
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(Password), salt, iter, HashAlgorithmName.SHA256, 32);
        var json = new
        {
            Algo = "pbkdf2-sha256",
            Iterations = iter,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            CreatedAt = DateTime.Now.ToString("O")
        };
        File.WriteAllText(Path.Combine(_sip.DataDir, "web_auth.json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>起进程 + 等到横幅里那一行。**失败时自己收尸**：这个进程已经占住了端口、
    /// 还攥着我们的输出管道（实测泄漏过一次：三次重试留下三个 `sip --start`，
    /// 而它们握着管道会让 `dotnet test` 永远等不到 EOF，整个测试任务看起来"卡住"）。</summary>
    private Process Launch(int port, TimeSpan timeout, out string token)
    {
        var proc = StartProcess();
        try
        {
            token = WaitForBanner(proc, port, timeout);
            return proc;
        }
        catch
        {
            KillProcess(proc);
            throw;
        }
    }

    private Process StartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_sip.Root, OperatingSystem.IsWindows() ? "sip.exe" : "sip"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            // 程序统一 UTF-8 输出;不显式指定会按系统默认(GBK)解码导致乱码
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
        return proc;
    }

    private string WaitForBanner(Process proc, int port, TimeSpan timeout)
    {
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

            // 只认 URL 本身，不认标签文案：标签会随语言文件变，URL 不会。
            // 密码模式印的是裸 URL（`http://127.0.0.1:PORT`，**末尾没有斜杠**）；
            // 无密码模式末尾还带一次性引导令牌（`…:PORT/?t=…`）。两种都要认。
            var m = Regex.Match(line, @"http://127\.0\.0\.1:(?<port>\d+)(?:/\?t=(?<token>[0-9a-fA-F]+))?");
            if (!m.Success)
            {
                // 这一行里就有地址却解析不出端口 = 横幅格式变了。与其干等 30 秒超时
                // （实测踩过一次：密码模式的 URL 末尾没有斜杠），不如当场把原文摆出来。
                if (line.Contains("http://127.0.0.1:", StringComparison.Ordinal))
                    throw new InvalidOperationException($"认不出 Web UI 那一行（横幅格式可能变了）：{line.Trim()}");
                continue;
            }

            int got = int.Parse(m.Groups["port"].Value);
            if (got != port)
                throw new InvalidOperationException($"Web 端口不符：期望 {port}，实际 {got}");
            return m.Groups["token"].Success ? m.Groups["token"].Value : "";
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

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        KillProcess(p);
    }

    private static void KillProcess(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(10_000); } catch { }
        try { p.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Kill();          // 先收进程：server 还开着库文件时删目录会失败
        _sip.Dispose();
    }
}
