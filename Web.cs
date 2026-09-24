// ══════════ Web：sip --start 的全部服务端 ══════════
// 这个文件 = Web 这一件事：进程内 HTTP 服务、路由、各接口处理器、正文净化，
// 以及网页登录（口令哈希 / 会话 cookie / 无密码模式的引导令牌）与 sip webpass + 首次向导。
// 前端资源在 web/（index.html / login.html / login-bg.png），编译时作为 EmbeddedResource 内嵌。
// 进程内直读数据库与内部方法：不 shell 出 CLI，也不解析子进程 stdout。
// 默认只监听 127.0.0.1；写操作受孟思琳挡位约束（与 CLI 同一策略源）。

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public partial class Program
{
    static readonly object WebWriteGate = new();
    static HttpListener? webListener;
    static string webDbPath = "";

    // sip --start  前台阻塞；Ctrl+C 退出
    // 绑定/端口来自 sip_settings.json（首次交互初始化写入）；未配置则 127.0.0.1:8777
    static async Task StartWebServer(string dbPath)
    {
        webDbPath = dbPath;
        var st = LoadSettings();
        string host = string.IsNullOrWhiteSpace(st.WebHost) ? "127.0.0.1" : st.WebHost.Trim();
        int port = st.WebPort is > 0 and < 65535 ? st.WebPort : 8777;
        // HttpListener 前缀：0.0.0.0 / * 需写成 http://+:port/
        string listenHost = (host is "0.0.0.0" or "*" or "+") ? "+" : host;
        string prefix = $"http://{listenHost}:{port}/";
        string displayUrl = $"http://{(listenHost == "+" ? "127.0.0.1" : host)}:{port}/";
        webListener = new HttpListener();
        webListener.Prefixes.Add(prefix);
        try
        {
            webListener.Start();
        }
        catch (Exception ex)
        {
            SetExit();
            Console.Error.WriteLine(Lang.T("Web server failed to start: {0}", ex.Message));
            // 非回环绑定在 Windows 上需要 http.sys 的 URL 保留（urlacl），
            // 否则**非管理员进程一律「拒绝访问」** —— 实测绑 0.0.0.0 和绑具体局域网 IP 都是如此。
            // 这条必须说清楚，否则用户会照着「端口被占用」的提示白折腾。
            bool denied = ex is System.Net.HttpListenerException hle && hle.ErrorCode == 5;
            if (denied && !IsLoopbackHost(host))
            {
                Console.Error.WriteLine(Lang.T("Binding to {0} needs a one-time URL reservation. Run this once in an ELEVATED prompt:", host));
                Console.Error.WriteLine($"  netsh http add urlacl url={prefix} user=Everyone");
                Console.Error.WriteLine(Lang.T("…or run sip --start as administrator. Binding 127.0.0.1 needs neither."));
            }
            else
            {
                Console.Error.WriteLine(Lang.T("Hint: make sure {0} is free, or re-run sip --start to change bind/port.", prefix.TrimEnd('/')));
            }
            return;
        }

        bool loopback = IsLoopbackHost(host);
        Console.WriteLine("========================================================");
        Console.WriteLine(Lang.T("  🍲 sip web · taste it slow"));
        // 无密码模式：把引导令牌印在 URL 里。这是唯一的带外发放点。
        string uiUrl = displayUrl.TrimEnd('/');
        Console.WriteLine(Lang.T("  Web UI     : {0}", WebPasswordIsSet() ? uiUrl : $"{uiUrl}/?t={EnsureWebBootToken()}"));
        if (!WebPasswordIsSet())
            Console.WriteLine(Lang.T("               (no password: open the link above — a bare visit is refused on purpose)"));
        Console.WriteLine(Lang.T("  Data folder: {0}", dataDir));
        Console.WriteLine(Lang.T("  Password   : {0}", WebPasswordIsSet() ? Lang.T("set") : Lang.T("not set (open access; sip webpass to set)")));
        // 会话是**进程内**的（见 WebSession 段）：横幅里说明白，省得两种误会 ——
        // 「每次开页面都要输密码？」和「重启后还能免密码进去？」。
        Console.WriteLine(Lang.T("  Session    : {0}", Lang.T(WebPasswordIsSet() ? SessionLinePassword : SessionLineBootLink)));
        Console.WriteLine(Lang.T("  Bind       : {0}  {1}", host, loopback ? Lang.T("(this machine only)") : Lang.T("reachable from network")));
        // 阅读报告全部来自本机遥测，而遥测默认关闭 —— 不提醒的话，用户打开网页只会看到
        // 「没有数据」却不知道该做什么。终端是唯一能给出可执行下一步的地方。
        if (!TelemetryService.IsEnabled)
            Console.WriteLine(Lang.T("  Telemetry  : off · the reading report will have no data (turn on: sip telemetry enable)"));
        Console.WriteLine(Lang.T("  Ctrl+C to stop"));
        Console.WriteLine("========================================================");
        if (!loopback && !WebPasswordIsSet())
        {
            Console.WriteLine(Lang.T("No password and non-local bind — run sip webpass now, or switch back to 127.0.0.1."));
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            try { webListener.Stop(); } catch { }
        };

        try
        {
            while (!cts.IsCancellationRequested && webListener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await webListener.GetContextAsync(); }
                catch (Exception) when (cts.IsCancellationRequested || !webListener.IsListening) { break; }
                catch { continue; }
                _ = Task.Run(() => HandleWebContext(ctx));
            }
        }
        finally
        {
            try { webListener.Stop(); } catch { }
            try { webListener.Close(); } catch { }
            webListener = null;
        }
    }

    static void HandleWebContext(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Cache-Control"] = "no-store";
            // 防点击劫持：本机 UI 不该被任何页面套进 iframe
            // （CSP 的 frame-ancestors 'none' 留到上 CSP 那一步，这里先用覆盖更广的 XFO）
            res.Headers["X-Frame-Options"] = "DENY";

            string path = req.Url?.AbsolutePath ?? "/";
            string method = (req.HttpMethod ?? "GET").ToUpperInvariant();

            // 只服务本机回环前缀下的路径；拒绝奇怪的绝对路径遍历
            if (path.Contains("..") || path.Contains('\\'))
            {
                WriteJson(res, 400, new { success = false, error = new { code = "BAD_PATH", message = "invalid path" } });
                return;
            }

            // 写请求：Origin/Referer 必须是本机回环（防本地恶意页 CSRF）
            if (method is not ("GET" or "HEAD" or "OPTIONS"))
            {
                if (!WebOriginOk(req))
                {
                    WriteJson(res, 403, new { success = false, error = new { code = "BAD_ORIGIN", message = "cross-origin blocked" } });
                    return;
                }
            }

            // ── 认证路由 ──
            if (method == "GET" && path == "/api/auth/status")
            {
                bool need = WebPasswordIsSet();
                var s = need
                    ? LookupWebSession(req, WebSessionCookieName, local: false)
                    : LookupWebSession(req, WebLocalCookieName, local: true);
                WriteJson(res, 200, new
                {
                    success = true,
                    data = new
                    {
                        passwordSet = need,
                        authenticated = s != null,
                        // 会话只活到进程结束：这三项是给前端/测试看的**契约**，不是装饰
                        sessionScope = "process",
                        restartRequiresLogin = true,
                        bootTokenRequired = !need && s == null,
                        sessionIssuedAt = s?.CreatedUtc.ToString("O")
                    }
                });
                return;
            }
            if (method == "POST" && path == "/api/login")
            {
                HandleLogin(req, res);
                return;
            }
            if (method == "POST" && path == "/api/logout")
            {
                // 登出 = **服务端吊销 + 清 cookie**。只清 cookie 的话，那把钥匙在进程里还活着，
                // 谁抄走过这串值谁就还能用（审计 §9.5 只提了 sip_local，这里是同一件事的两半）。
                RevokePresentedSession(req, WebSessionCookieName);
                RevokePresentedSession(req, WebLocalCookieName);
                ClearSessionCookie(res, WebSessionCookieName);
                ClearSessionCookie(res, WebLocalCookieName);
                WriteJson(res, 200, new { success = true });
                return;
            }

            // 打开页面。
            //   有密码  → 未认证给登录页（不变）
            //   无密码  → **必须带启动时打印到终端里的引导令牌**，否则只给一张说明页
            // 为什么不再无条件下发 sip_local：那样本机任意进程 GET 一次就拿到凭据，
            // 等于把整个库经 HTTP 敞开。引导令牌只存在于终端输出 —— 进程读不到的带外信道。
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                if (WebPasswordIsSet())
                {
                    if (LookupWebSession(req, WebSessionCookieName, local: false) == null)
                    {
                        // 带着一把已经不作数的钥匙（上一次运行留下的，或从别处抄来的）
                        // → 明确告诉人「为什么又要输密码」，再把那把废钥匙从浏览器里清掉
                        bool stale = !string.IsNullOrEmpty(req.Cookies[WebSessionCookieName]?.Value);
                        if (stale) ClearSessionCookie(res, WebSessionCookieName);
                        WriteHtml(res, LoadLoginHtml(stale ? Lang.T(LoginNoticeRestarted) : null));
                    }
                    else WriteHtml(res, LoadWebHtml());
                    return;
                }

                // 已有 sip_local（换过一次了）→ 刷新、收藏、前进后退都照常
                if (LookupWebSession(req, WebLocalCookieName, local: true) != null)
                {
                    WriteHtml(res, LoadWebHtml());
                    return;
                }

                // 换浏览器 / 重启进程后，旧的 sip_local 一律不作数 —— 得重新打开终端里那条链接
                bool staleLocal = !string.IsNullOrEmpty(req.Cookies[WebLocalCookieName]?.Value);

                if (WebBootTokenOk(QueryParam(req, "t")))
                {
                    // 换票成功：IssueWebSession 会把新钥匙写进同名 cookie（覆盖旧的），
                    // 所以这里**不能**再补一条清除 —— 两条 Set-Cookie 的先后就成了隐式契约
                    IssueWebSession(req, res, WebLocalCookieName, local: true);
                    // 换完立刻跳回干净 URL：别让令牌留在地址栏和浏览历史里
                    res.StatusCode = 302;
                    res.Headers["Location"] = "/";
                    res.Close();
                    return;
                }

                if (staleLocal) ClearSessionCookie(res, WebLocalCookieName);
                WriteHtml(res, LoadBootHintHtml(staleLocal));
                return;
            }

            if (method == "GET" && path == "/login-bg.png")
            {
                WriteLoginBg(res);
                return;
            }

            // 其余 /api/*：必须已认证（密码会话 或 本机 token）
            if (path.StartsWith("/api/", StringComparison.Ordinal) && !WebRequestIsAuthenticated(req))
            {
                WriteJson(res, 401, new { success = false, error = new { code = "UNAUTHORIZED", message = "open the web UI in a browser first, or login" } });
                return;
            }

            if (method == "GET" && path == "/api/status")
            {
                WriteJson(res, 200, new
                {
                    success = true,
                    data = new
                    {
                        sip = "sip v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?"),
                        web = "builtin",
                        auth = WebPasswordIsSet() ? "password" : "local-token",
                        simon = CurrentSimonLevel(),
                        time = DateTime.Now.ToString("O")
                    }
                });
                return;
            }

            // ── feeds ──
            if (method == "GET" && path == "/api/feeds") { HandleFeedsList(res); return; }
            if (method == "POST" && path == "/api/feeds") { HandleFeedAdd(req, res); return; }
            if (method == "POST" && path == "/api/feeds/opml") { HandleOpmlImport(req, res); return; }
            if (method == "GET" && path == "/api/feeds/opml") { HandleOpmlExport(res); return; }
            if (method == "GET" && path == "/api/progress") { HandleProgress(res); return; }
            if (method == "POST" && path == "/api/feeds/sync") { HandleSync(res, onlyDue: true); return; }
            if (method == "POST" && path == "/api/feeds/update-all") { HandleSync(res, onlyDue: false); return; }

            if (TryMatch(path, "/api/feeds/", out int feedId, out string rest))
            {
                if (method == "GET" && rest == "/articles") { HandleFeedArticles(res, feedId); return; }
                if (method == "GET" && rest == "/info") { HandleFeedInfo(res, feedId); return; }
                if (method == "POST" && rest == "/update") { HandleFeedUpdate(res, feedId); return; }
                if (method == "POST" && rest == "/archive") { HandleFeedArchive(res, feedId, archive: true); return; }
                if (method == "POST" && rest == "/unarchive") { HandleFeedArchive(res, feedId, archive: false); return; }
                if (method == "DELETE" && rest == "") { HandleFeedDelete(res, feedId); return; }
            }

            // ── articles ──
            if (TryMatch(path, "/api/articles/", out int itemId, out string ar))
            {
                if (method == "GET" && ar == "") { HandleArticleGet(res, itemId); return; }
                if (method == "GET" && ar == "/versions") { HandleArticleVersions(res, itemId); return; }
                if (method == "GET" && ar == "/diff") { HandleArticleDiff(req, res, itemId); return; }
                if (method == "POST" && ar == "/like") { HandleArticleLike(res, itemId); return; }
                if (method == "POST" && ar == "/fulltext") { HandleArticleFulltext(req, res, itemId); return; }
                if (method == "GET" && ar == "/summary") { HandleArticleSummary(res, itemId, generate: false); return; }
                if (method == "POST" && ar == "/summary") { HandleArticleSummary(res, itemId, generate: true); return; }
            }

            // ── today / likes / search ──
            if (method == "GET" && path == "/api/today") { HandleToday(res, refresh: false); return; }
            if (method == "POST" && path == "/api/today/refresh") { HandleToday(res, refresh: true); return; }
            if (method == "GET" && path == "/api/likes") { HandleLikes(res); return; }
            if (method == "GET" && path == "/api/grep") { HandleGrep(req, res); return; }
            if (method == "GET" && path == "/api/search") { HandleSearch(req, res); return; }
            if (method == "GET" && path == "/api/insights") { HandleInsights(req, res); return; }

            WriteJson(res, 404, new { success = false, error = new { code = "NOT_FOUND", message = path } });
        }
        catch (Exception ex)
        {
            try { WriteJson(res, 500, new { success = false, error = new { code = "INTERNAL", message = ex.Message } }); }
            catch { try { res.StatusCode = 500; res.Close(); } catch { } }
        }
    }

    // ── route helpers ──
    static bool TryMatch(string path, string prefix, out int id, out string rest)
    {
        id = 0; rest = "";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string tail = path.Substring(prefix.Length);
        int slash = tail.IndexOf('/');
        string idPart = slash < 0 ? tail : tail[..slash];
        rest = slash < 0 ? "" : tail[slash..];
        return int.TryParse(idPart, out id);
    }

    static bool WebWriteAllowed(HttpListenerResponse res, string what)
    {
        int level = CurrentSimonLevel();
        // 与 CLI **同一语义**：挡位 2 起拦一切写操作（CLI 侧是 !SimonIsReadOnly）。
        //
        // 原先这里只拦 delete/sync/update/update-all/add，于是挡位 2 下 Web 仍可
        // archive / unarchive / like / fulltext / summary —— 同一个操作仅因走的通道不同
        // 就裁决相反，实际成了一条绕过 CLI 限制的写通道（审计 §2.3）。
        if (level >= 2)
        {
            SimonRecord("blocked_cmd", $"web:{what}", level);
            WriteJson(res, 403, new
            {
                success = false,
                error = new
                {
                    code = "SIMON_BLOCKED",
                    level,
                    message = Lang.T("Simon blocked this web write (level {0}). Reading still works; lower the level from a real terminal.", level)
                }
            });
            return false;
        }
        return true;
    }

    static void WriteJson(HttpListenerResponse res, int status, object body)
    {
        byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.OutputStream.Close();
    }

    static void WriteHtml(HttpListenerResponse res, string html)
    {
        byte[] buf = Encoding.UTF8.GetBytes(html);
        res.StatusCode = 200;
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.OutputStream.Close();
    }

    // 原始文本下载（OPML 导出）。必须显式给 Content-Disposition：
    // 不给的话浏览器会把它当页面渲染，用户看到一坨 XML 而不是"下载了一个文件"。
    static void WriteDownload(HttpListenerResponse res, string contentType, string filename, string body)
    {
        byte[] buf = Encoding.UTF8.GetBytes(body);
        res.StatusCode = 200;
        res.ContentType = contentType;
        res.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.OutputStream.Close();
    }

    static string LoadWebHtml()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var rs = asm.GetManifestResourceStream("sip-web.index.html");
        if (rs != null)
        {
            using var sr = new StreamReader(rs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        // 开发时未嵌入：从源码树旁路读取
        string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "index.html");
        if (File.Exists(fallback)) return File.ReadAllText(fallback);
        return "<!doctype html><meta charset=utf-8><title>sip</title><body style='font-family:serif;padding:40px'>web ui missing</body>";
    }

    /// <summary><paramref name="notice"/> 非空时插进 `&lt;!--SIP_LOGIN_NOTICE--&gt;` 占位处，
    /// 用来解释「为什么要重新输密码」（进程重启 / 这把钥匙属于别的浏览器）。
    /// 文案是我们自己的字面量，不含用户输入 —— 这里不需要再转义一次。</summary>
    static string LoadLoginHtml(string? notice = null)
    {
        string html = LoadLoginHtmlRaw();
        if (string.IsNullOrEmpty(notice)) return html.Replace(LoginNoticeSlot, "");
        return html.Replace(LoginNoticeSlot, $"<div class=\"notice\">{notice}</div>");
    }

    /// <summary>登录页里的提示占位符；<c>web/login.html</c> 与下面的兜底壳都要有这一行。</summary>
    const string LoginNoticeSlot = "<!--SIP_LOGIN_NOTICE-->";

    static string LoadLoginHtmlRaw()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var rs = asm.GetManifestResourceStream("sip-web.login.html");
        if (rs != null)
        {
            using var sr = new StreamReader(rs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "login.html"),
            Path.Combine(dataDir, "..", "web", "login.html")
        };
        foreach (var c in candidates)
        {
            try
            {
                string p = Path.GetFullPath(c);
                if (File.Exists(p)) return File.ReadAllText(p);
            }
            catch { }
        }
        // 最小登录壳（无装饰图时仍可用）
        return """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>sip · 登录</title>
<style>
body{margin:0;height:100vh;display:grid;place-items:center;background:#F7F3EB;font-family:system-ui,"PingFang SC",sans-serif;color:#1C1917}
form{width:min(340px,90vw);display:flex;flex-direction:column;gap:14px}
h1{font-size:22px;letter-spacing:.2em;margin:0 0 8px}
input,button{padding:12px;border-radius:10px;border:1px solid rgba(28,25,23,.15);font:inherit}
button{background:#A16207;border:0;color:#fff;letter-spacing:.3em;cursor:pointer}
.err{color:#B91C1C;font-size:13px;min-height:1.2em}
.notice{background:rgba(161,98,7,.10);border:1px solid rgba(161,98,7,.25);color:#78350F;font-size:13px;line-height:1.7;padding:10px 12px;border-radius:10px}
</style></head><body>
<form id="f"><h1>sip</h1>
<!--SIP_LOGIN_NOTICE-->
<input id="p" type="password" placeholder="密码" autocomplete="current-password" required>
<div class="err" id="e"></div>
<button type="submit">登 录</button></form>
<script>
document.getElementById('f').onsubmit=async(e)=>{e.preventDefault();
const r=await fetch('/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:document.getElementById('p').value})});
const j=await r.json().catch(()=>({}));
if(j.success){location.reload()}else{document.getElementById('e').textContent=j.error&&j.error.message||'登录失败'}};
</script></body></html>
""";
    }

    // 无密码模式下的说明页。
    // ⚠️ **绝不在这里回显引导令牌** —— 那等于把它交给任何能 GET 本页的进程，
    // 带外信道就白设了。这里只告诉人去哪儿看。
    static string LoadBootHintHtml(bool stale = false) => """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>sip · 需要引导链接</title>
<style>
body{margin:0;height:100vh;display:grid;place-items:center;background:#F7F3EB;font-family:system-ui,"PingFang SC",sans-serif;color:#1C1917}
.box{width:min(520px,90vw);line-height:1.9}
h1{font-size:20px;letter-spacing:.12em;margin:0 0 14px}
code{background:rgba(28,25,23,.06);padding:2px 6px;border-radius:6px}
.hint{color:#78716C;font-size:13px;margin-top:18px}
.stale{background:rgba(161,98,7,.10);border:1px solid rgba(161,98,7,.25);color:#78350F;font-size:13.5px;line-height:1.7;padding:10px 12px;border-radius:10px;margin:0 0 16px}
</style></head><body>
<div class="box">
<h1>请用终端里打印的链接打开</h1>
{{STALE}}
<p>为了不让本机其他程序直接把你的库读走，<strong>无密码模式下不再无条件放行本页</strong>。</p>
<p>回到你运行 <code>sip --start</code> 的那个终端窗口，把它打印的
<code>Web UI</code> 那一行（末尾带 <code>?t=…</code>）复制到浏览器打开。</p>
<p class="hint">这张通行证是<strong>每个浏览器各一张</strong>，而且<strong>重启 sip 后就作废</strong>：
换浏览器、或重启过程序，都要回来重新复制一次链接（终端里那条链接每次启动都是新的）。</p>
<p class="hint">想省掉这一步：在终端运行 <code>sip webpass</code> 设一个 Web 密码，
之后直接打开本页、输密码登录即可。</p>
</div></body></html>
""".Replace("{{STALE}}", stale
        ? "<p class=\"stale\">sip 重启过了（或这张通行证属于另一个浏览器），上一次的链接已经作废 —— 请用终端里新打印的那条。</p>"
        : "");

    static void WriteLoginBg(HttpListenerResponse res)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        byte[]? buf = null;
        using (var rs = asm.GetManifestResourceStream("sip-web.login-bg.png"))
        {
            if (rs != null)
            {
                using var ms = new MemoryStream();
                rs.CopyTo(ms);
                buf = ms.ToArray();
            }
        }
        if (buf == null)
        {
            string[] candidates =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "login-bg.png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "login-bg.png")
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) { buf = File.ReadAllBytes(c); break; } } catch { }
            }
        }
        if (buf == null)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        res.StatusCode = 200;
        res.ContentType = "image/png";
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.OutputStream.Close();
    }

    // Origin/Referer 校验（写请求防 CSRF）。两层判定，缺一不可：
    //   ① Origin 的 host 必须等于本次请求的 Host —— 挡 evil.com 直接跨站打本机
    //   ② Origin 的 host 必须是「我们本来就认的名字」：
    //        回环（localhost/127.0.0.1/::1）一律认；
    //        否则**只认 IP 字面量**，且需等于配置的绑定地址（绑 0.0.0.0/* 时放宽为任意 IP 字面量）
    //
    // ② 是防 **DNS rebinding** 的关键，不是随手写的白名单：
    //   攻击者把 evil.com 解析到 127.0.0.1，浏览器同源请求本机端口时
    //   Host 与 Origin 都是 evil.com —— ① 会通过，只有「域名一律不认」才挡得住。
    //   代价是局域网访问只能用 IP 字面量。这是刻意取舍，不是疏漏。
    static bool WebOriginOk(HttpListenerRequest req)
    {
        string host = req.Url?.Host ?? "127.0.0.1";
        string origin = req.Headers["Origin"] ?? req.Headers["Referer"] ?? "";
        if (string.IsNullOrEmpty(origin)) return true; // curl / 同源无 Origin
        try
        {
            var u = new Uri(origin);
            if (!string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase)) return false;
            if (IsLoopbackHost(u.Host)) return true;

            // 非回环：只接受 IP 字面量（域名可能是 rebinding）
            if (!System.Net.IPAddress.TryParse(u.Host.Trim('[', ']'), out _)) return false;

            var st = LoadSettings();
            string bind = (st.WebHost ?? "").Trim();
            // 绑全网卡时，访问地址就是各网卡的 IP，此处无法预先枚举 → 接受任意 IP 字面量
            if (bind.Length == 0 || bind is "0.0.0.0" or "*" or "+") return true;
            return string.Equals(u.Host, bind, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static async void HandleLogin(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebPasswordIsSet())
        {
            // 无密码模式：这里**刻意什么都不发**。
            // 通行证只能由终端里那条 `?t=` 链接换取 —— 如果 POST /api/login 也能换到一把钥匙，
            // 引导令牌这道「只有终端能给的带外信道」就白设了（本机任意进程都能 POST 一个空 body）。
            WriteJson(res, 200, new { success = true, data = new { passwordSet = false, bootTokenRequired = true } });
            return;
        }
        string body = await ReadBodyAsync(req);
        string password = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString() ?? "";
        }
        catch { }

        // 限流：**5 分钟滑动窗口内出错 20 次**才拦。
        // 原实现是一个只增不减的进程级计数器（只在登录成功时清零），于是长期运行的进程里
        // 累计错过 20 次之后就进入「错一次锁一次」的状态 —— 对记错密码的人过于苛刻，
        // 而对真正在爆破的人也没有更强（他等 5 分钟就恢复）。窗口式两头都对。
        if (WebLoginBlocked(out int retryAfter))
        {
            res.Headers["Retry-After"] = retryAfter.ToString();
            WriteJson(res, 429, new { success = false, error = new { code = "RATE_LIMIT", message = "too many attempts, try later" } });
            return;
        }

        if (!VerifyWebPassword(password))
        {
            WebLoginRecordFailure();
            WriteJson(res, 401, new { success = false, error = new { code = "BAD_PASSWORD", message = "password incorrect" } });
            return;
        }
        WebLoginRecordSuccess();
        IssueWebSession(req, res, WebSessionCookieName, local: false);
        // 不再顺带下发 sip_local：有密码时 WebRequestIsAuthenticated 只认 sip_web，
        // 那个 cookie 在这里是死重量，还会让人误以为"无密码通道"是登录的一部分。
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                passwordSet = true,
                authenticated = true,
                sessionScope = "process",
                restartRequiresLogin = true
            }
        });
    }

    // ── 口令失败限流（滑动窗口；只在成功登录时清空）──
    static readonly Queue<DateTime> webLoginFails = new();
    const int WebLoginFailLimit = 20;
    static readonly TimeSpan WebLoginFailWindow = TimeSpan.FromMinutes(5);

    static bool WebLoginBlocked(out int retryAfterSeconds)
    {
        lock (webLoginFails)
        {
            DateTime cutoff = DateTime.UtcNow - WebLoginFailWindow;
            while (webLoginFails.Count > 0 && webLoginFails.Peek() < cutoff) webLoginFails.Dequeue();
            if (webLoginFails.Count >= WebLoginFailLimit)
            {
                // 窗口里最早那次失败滑出去时，才能再试 —— 给客户端一个准确的等待秒数
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((webLoginFails.Peek() + WebLoginFailWindow - DateTime.UtcNow).TotalSeconds));
                return true;
            }
            retryAfterSeconds = 0;
            return false;
        }
    }

    static void WebLoginRecordFailure()
    {
        lock (webLoginFails) webLoginFails.Enqueue(DateTime.UtcNow);
    }

    static void WebLoginRecordSuccess()
    {
        lock (webLoginFails) webLoginFails.Clear();
    }

    static string? QueryParam(HttpListenerRequest req, string key)
    {
        return req.QueryString[key];
    }

    // ── 下载护栏：同一个源不并发下两次 ──────────────────────────
    // 为什么必须有：按钮可以被连点，也可以两个标签页同时点 —— 每个请求都会**真的再下一次**，
    // 结果是同一个源并发下载 + 并发写库。客户端禁用按钮只是礼貌，服务端这道才是保证。
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _downloadsInFlight = new();

    /// <summary>抢到了返回 null（调用方必须在 finally 里 EndDownload）；已有同样的下载在跑则返回原因。</summary>
    static string? TryBeginDownload(string key)
        => _downloadsInFlight.TryAdd(key, 0)
            ? null
            : Lang.T("这个下载已经在跑了，等它结束再试 —— 同一个源不会同时下载两次。");

    static void EndDownload(string key) => _downloadsInFlight.TryRemove(key, out _);

    // 下载进度：界面要能说"3/21 · 正在抓 xxx"，而不是干等一条不会变的提示。
    // 只用于显示，不参与并发控制（那是 _downloadsInFlight 的职责）。
    static volatile string _progKind = "";
    static volatile string _progCurrent = "";
    static int _progDone, _progTotal;
    static void ProgBegin(string kind){ _progKind = kind; _progDone = 0; _progTotal = 0; _progCurrent = ""; }
    static void ProgEnd(){ _progKind = ""; _progCurrent = ""; _progDone = 0; _progTotal = 0; }

    static void HandleProgress(HttpListenerResponse res)
    {
        bool active = _progKind.Length > 0 || !_downloadsInFlight.IsEmpty;
        WriteJson(res, 200, new
        {
            success = true,
            data = new { active, kind = _progKind, done = _progDone, total = _progTotal, current = _progCurrent }
        });
    }

    /// <summary>某个源当前有多少篇文章。用来给出「新增 N 篇」这种真实反馈 ——
    /// 光说"已更新"，用户没法判断到底是抓到了新内容还是白跑一趟。</summary>
    static int FeedItemCount(int feedId)
    {
        try
        {
            using var conn = OpenDb(webDbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE FeedId = @f";
            cmd.Parameters.AddWithValue("@f", feedId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { return 0; }
    }

    static int FeedIdByUrl(string url)
    {
        try
        {
            using var conn = OpenDb(webDbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Feeds WHERE FeedUrl = @u LIMIT 1";
            cmd.Parameters.AddWithValue("@u", url);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { return 0; }
    }

    static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        if (!req.HasEntityBody) return "";
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        return await sr.ReadToEndAsync();
    }

    // ── handlers：全部进程内 ──
    static void HandleFeedsList(HttpListenerResponse res)
    {
        var rows = new List<object>();
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Title,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'active')   AS ActiveCount,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'archived') AS ArchiveCount,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'deleted')  AS DeleteCount,
                       ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum,
                       LastCheckedAt, Schedule, FeedUrl
                FROM Feeds";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new
                {
                    id = r.GetInt32(0),
                    displayNum = r.GetInt32(5),
                    title = r.GetString(1),
                    active = r.GetInt32(2),
                    archived = r.GetInt32(3),
                    deleted = r.GetInt32(4),
                    schedule = r.IsDBNull(7) ? "" : r.GetString(7),
                    url = r.IsDBNull(8) ? "" : r.GetString(8),
                    lastChecked = r.IsDBNull(6) ? null : TryParseIso(r.GetString(6))
                });
            }
        }
        WriteJson(res, 200, new { success = true, data = new { feeds = rows } });
    }

    static void HandleFeedArticles(HttpListenerResponse res, int feedRealId)
    {
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var titleCmd = conn.CreateCommand();
        titleCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
        titleCmd.Parameters.AddWithValue("@id", feedRealId);
        string feedTitle = titleCmd.ExecuteScalar()?.ToString() ?? "";

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, Version,
                   ROW_NUMBER() OVER (ORDER BY PublishDate DESC, Id DESC) AS DisplayNum,
                   VersionCount, ArchivedCount,
                   LENGTH(Content) AS ContentLen, LENGTH(Description) AS DescLen,
                   PublishDate
            FROM (
                SELECT i.Id, i.Title, i.Version, i.Guid, i.Content, i.Description, i.PublishDate,
                       CASE WHEN i.Guid = '' THEN 1
                            ELSE COUNT(*) OVER (PARTITION BY i.Guid) END AS VersionCount,
                       CASE WHEN i.Guid = '' THEN 0
                            ELSE COUNT(*) FILTER (WHERE i.Status = 'archived') OVER (PARTITION BY i.Guid) END AS ArchivedCount,
                       ROW_NUMBER() OVER (PARTITION BY i.Guid ORDER BY i.Version DESC) AS rn
                FROM Items i
                WHERE i.FeedId = @fid AND i.Guid IS NOT NULL AND i.Status != 'dedup'
            )
            WHERE Guid = '' OR rn = 1
            -- 按**发布时间**倒序，与 CLI（`ListArticlesFromDb` / grep）一致。
            -- 原来是 ORDER BY Id DESC —— 那是入库顺序，于是网页把最旧的一篇排在最前面
            -- （实测源 1 第一条是 2025-06 的文章，而最新的排在末尾）。同一个库，
            -- 终端和网页给出不同顺序，本身就是缺陷。
            -- PublishDate 是定宽 UTC ISO（…Z），字符串序即时间序；空值在 DESC 下自然排最后。
            ORDER BY PublishDate DESC, Id DESC
            LIMIT 500";
        cmd.Parameters.AddWithValue("@fid", feedRealId);
        var signals = LoadSignals();
        var items = new List<object>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                int realId = r.GetInt32(0);
                signals.TryGetValue(realId.ToString(), out var sig);
                int contentLen = r.IsDBNull(6) ? 0 : r.GetInt32(6);
                int descLen = r.IsDBNull(7) ? 0 : r.GetInt32(7);
                items.Add(new
                {
                    itemId = realId,
                    displayNum = r.GetInt32(3),
                    title = r.GetString(1),
                    hasHistory = r.GetInt32(5) > 0,
                    quality = ContentQualityByLen(contentLen, descLen),
                    liked = sig?.UserLike ?? false,
                    aiLiked = sig?.AiLike ?? false,
                    published = r.IsDBNull(8) ? null : r.GetString(8)
                });
            }
        }
        WriteJson(res, 200, new { success = true, data = new { feedId = feedRealId, feedTitle, articles = items } });
    }

    static void HandleFeedInfo(HttpListenerResponse res, int feedRealId)
    {
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, FeedUrl, LastCheckedAt, Schedule FROM Feeds WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", feedRealId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            WriteJson(res, 404, new { success = false, error = new { code = "FEED_NOT_FOUND", message = "feed not found" } });
            return;
        }
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                feedId = feedRealId,
                title = r.GetString(0),
                url = r.IsDBNull(1) ? "" : r.GetString(1),
                lastChecked = r.IsDBNull(2) ? null : TryParseIso(r.GetString(2)),
                schedule = r.IsDBNull(3) ? "" : r.GetString(3)
            }
        });
    }

    // 导出 OPML：**与 CLI 共用 BuildOpml**（不写第二套生成逻辑）。
    // 这是只读操作，任何挡位都放行；前端用一个 <a download> 直接指向它。
    static void HandleOpmlExport(HttpListenerResponse res)
        => WriteDownload(res, "text/x-opml; charset=utf-8", "sip-feeds.opml", BuildOpml(webDbPath));

    // OPML 导入：**与 CLI 共用同一实现**（ImportOpmlCore），不做第二套解析。
    // 入口就在「添加订阅源」面板里 —— 订阅源只有一个入口，OPML 是它的另一种填法。
    static async void HandleOpmlImport(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "opml")) return;
        const int maxBytes = 2 * 1024 * 1024;
        if (req.ContentLength64 > maxBytes)
        {
            WriteJson(res, 413, new { success = false, error = new { code = "TOO_LARGE", message = "OPML 文件过大（上限 2 MB）" } });
            return;
        }
        string xml = await ReadBodyAsync(req);
        if (xml.Length > maxBytes)
        {
            WriteJson(res, 413, new { success = false, error = new { code = "TOO_LARGE", message = "OPML 文件过大（上限 2 MB）" } });
            return;
        }
        if (string.IsNullOrWhiteSpace(xml))
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "空请求体：请把 OPML 文件内容作为请求体发来" } });
            return;
        }
        string key = "opml";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        try
        {
            ProgBegin("opml");
            var (ok, skip, fail, parseFailed, fatal, errors) = ImportOpmlCore(xml, webDbPath, (done, total, url) =>
            {
                _progDone = done; _progTotal = total; _progCurrent = url;
            });
            if (parseFailed)
            {
                WriteJson(res, 400, new { success = false, error = new { code = "OPML_PARSE_FAILED", message = fatal } });
                return;
            }
            if (fatal != null)
            {
                WriteJson(res, 400, new { success = false, error = new { code = "NO_FEEDS", message = fatal } });
                return;
            }
            WriteJson(res, 200, new { success = true, data = new { imported = ok, skipped = skip, failed = fail, errors } });
        }
        finally { EndDownload(key); ProgEnd(); }
    }

    static async void HandleFeedAdd(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "add")) return;
        string body = await ReadBodyAsync(req);
        string url = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString() ?? "";
        }
        catch { }
        url = (url ?? "").Trim();
        if (url.Length == 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "url required" } });
            return;
        }
        string key = "url:" + url.ToLowerInvariant();
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        try
        {
            await DownloadAndSaveToDb(url, webDbPath, interactive: false);
            int fid = FeedIdByUrl(url);
            int items = fid > 0 ? FeedItemCount(fid) : 0;
            WriteJson(res, 200, new { success = true, data = new { url, feedId = fid, items } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 502, new { success = false, error = new { code = "DOWNLOAD_FAILED", message = ex.Message } });
        }
        finally { EndDownload(key); }
    }

    static async void HandleFeedUpdate(HttpListenerResponse res, int feedRealId)
    {
        if (!WebWriteAllowed(res, "update")) return;
        // 连点/多标签页会各发一次请求 —— 没有这道护栏就会并发下同一个源
        string key = "feed:" + feedRealId;
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        int before = FeedItemCount(feedRealId);
        try
        {
            using var conn = OpenDb(webDbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Title, FeedUrl FROM Feeds WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", feedRealId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                WriteJson(res, 404, new { success = false, error = new { code = "FEED_NOT_FOUND", message = "feed not found" } });
                return;
            }
            string title = r.GetString(0);
            string url = r.GetString(1);
            r.Close();
            if (IsArchived(title))
            {
                WriteJson(res, 400, new { success = false, error = new { code = "ARCHIVED", message = "feed is archived" } });
                return;
            }
            await DownloadAndSaveToDb(url, webDbPath, interactive: false);
            RecordFeedSuccess(feedRealId);
            int added = Math.Max(0, FeedItemCount(feedRealId) - before);
            WriteJson(res, 200, new { success = true, data = new { feedId = feedRealId, updated = true, added, items = before + added } });
        }
        catch (Exception ex)
        {
            RecordFeedFailure(feedRealId, ex.Message);
            WriteJson(res, 502, new { success = false, error = new { code = "UPDATE_FAILED", message = ex.Message } });
        }
        finally { EndDownload(key); }
    }

    static async void HandleSync(HttpListenerResponse res, bool onlyDue)
    {
        if (!WebWriteAllowed(res, onlyDue ? "sync" : "update-all")) return;
        // 同上：连点「同步」会并发跑整轮下载。整轮只允许一个在跑。
        string key = "sync";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        int ok = 0, fail = 0, addedTotal = 0;
        var results = new List<object>();
        try
        {
            List<(int Id, string Title, string Url, DateTime? LastChecked)> targets = new();
            using (var conn = OpenDb(webDbPath))
            {
                conn.Open();
                var cmd = conn.CreateCommand();
                // 两个分支本来就是同一条查询（原来的三元写法让人以为"只有到期源才查库"，
                // 而 due 与否只影响下面 FeedIsDue 的过滤）。
                cmd.CommandText = "SELECT Id, Title, FeedUrl, LastCheckedAt, Schedule FROM Feeds";
                using var r = cmd.ExecuteReader();
                var now = DateTime.Now;
                while (r.Read())
                {
                    int id = r.GetInt32(0);
                    string title = r.GetString(1);
                    string url = r.GetString(2);
                    DateTime? lc = r.IsDBNull(3) ? null : TryParseIso(r.GetString(3));
                    string schedule = r.IsDBNull(4) ? "" : r.GetString(4);
                    if (IsArchived(title)) continue;
                    if (onlyDue && !FeedIsDue(schedule, lc, now)) continue;
                    targets.Add((id, title, url, lc));
                }
            }
            foreach (var f in targets)
            {
                int before = FeedItemCount(f.Id);
                try
                {
                    await DownloadAndSaveToDb(f.Url, webDbPath, interactive: false);
                    RecordFeedSuccess(f.Id);
                    int added = Math.Max(0, FeedItemCount(f.Id) - before);
                    addedTotal += added;
                    ok++;
                    results.Add(new { feedId = f.Id, title = f.Title, ok = true, added });
                }
                catch (Exception ex)
                {
                    RecordFeedFailure(f.Id, ex.Message);
                    fail++;
                    results.Add(new { feedId = f.Id, title = f.Title, ok = false, added = 0, error = ex.Message });
                }
            }
            WriteJson(res, 200, new { success = true, data = new { feeds = results, ok, fail, added = addedTotal, due = onlyDue } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "SYNC_FAILED", message = ex.Message } });
        }
        finally { EndDownload(key); }
    }

    static bool FeedIsDue(string schedule, DateTime? lastChecked, DateTime now)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(schedule) || schedule.Equals("manual", StringComparison.OrdinalIgnoreCase))
                return false;
            var next = FeedNextDue(schedule, lastChecked, now);
            return next == null || next <= now;
        }
        catch { return false; }
    }

    static void HandleFeedArchive(HttpListenerResponse res, int feedRealId, bool archive)
    {
        if (!WebWriteAllowed(res, archive ? "archive" : "unarchive")) return;
        try
        {
            if (archive) AddTimestampForRealId(feedRealId, webDbPath);
            else RemoveTimestampForRealId(feedRealId, webDbPath);
            WriteJson(res, 200, new { success = true, data = new { feedId = feedRealId, archived = archive } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "FAILED", message = ex.Message } });
        }
    }

    static void HandleFeedDelete(HttpListenerResponse res, int feedRealId)
    {
        if (!WebWriteAllowed(res, "delete")) return;
        try
        {
            DeleteFeedByRealId(feedRealId, webDbPath);
            WriteJson(res, 200, new { success = true, data = new { feedId = feedRealId, deleted = true } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "FAILED", message = ex.Message } });
        }
    }

    // ══════════ 正文净化：把第三方 HTML 变成「可安全注入浏览器的 DOM」══════════
    // 为什么在服务端做：
    //   ① 一处做对，所有消费方受益（将来原生 App / 别的前端不会漏）
    //   ② 「前端记得净化」靠不住 —— 渲染点只会越加越多
    //   ③ CSP 是第二道防线；第一道必须是「不可信内容不许变成标记」
    //
    // 策略：**标签白名单 + 属性白名单**，其余一律丢弃。
    //   黑名单永远漏（on* 一大串、新标签层出不穷），白名单漏不了。
    //   不保留 style/class/id：它们可用于 CSS 数据外泄，也能把正文排版成
    //   "删除订阅源"按钮的样子来骗点击。
    static readonly HashSet<string> SafeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p","br","hr","div","span","small","sub","sup",
        "h1","h2","h3","h4","h5","h6",
        "ul","ol","li","dl","dt","dd",
        "blockquote","pre","code","kbd","samp","var",
        "strong","b","em","i","u","s","del","ins","mark","q","cite","abbr","time",
        "a","img","figure","figcaption","picture",
        "table","thead","tbody","tfoot","tr","td","th","caption",
        "video","audio","source",
        "article","section","main","aside","header","footer","nav","address","wbr"
    };

    // 这些标签**连内容一起丢**（内容本身可能有害，或会污染布局）
    static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script","style","iframe","object","embed","form","input","button","select","textarea",
        "svg","math","link","meta","base","noscript","template","applet","frame","frameset"
    };

    static readonly HashSet<string> SafeAttrs = new(StringComparer.OrdinalIgnoreCase)
    { "href","src","alt","title","colspan","rowspan","datetime","cite" };

    static bool LooksLikeHtml(string s)
        => System.Text.RegularExpressions.Regex.IsMatch(s, "<[a-zA-Z][\\s\\S]*>");

    // 统一出口：不管原文是 HTML 还是纯文本，都给出「可直接注入」的已净化 HTML。
    // 这样前端**不需要也不该**再判断 isHtml —— 判断点越少，漏点越少。
    // Markdown -> HTML。
    // 全文缓存是 **Markdown**（抓取时就把网页转好了，保留标题/段落/列表/图片），
    // 而源里的 Content 是 HTML —— 两个来源必须都先变成 HTML 再进净化器。
    // 少了这一步，Markdown 会被当成纯文本转义，# / ** / ![]() 原样显示：
    // 那就是"抓全文后排版很乱、图片全没了"的直接原因（2026-09-12 用户反馈）。
    // 放在净化器旁边：这一步和净化都是"网页正文的输出处理"。
    static string MarkdownToHtml(string md)
    {
        try { return Markdig.Markdown.ToHtml(md ?? ""); }
        catch { return ""; }   // 渲染失败不抛：宁可正文少显示，也不能让接口整体挂掉
    }

    static string ToSafeBodyHtml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        if (!LooksLikeHtml(body))
        {
            var sb = new StringBuilder();
            foreach (var para in body.Replace("\r\n", "\n").Split("\n\n"))
            {
                string t = para.Trim();
                if (t.Length == 0) continue;
                sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(t).Replace("\n", "<br>")).Append("</p>");
            }
            return sb.ToString();
        }
        try
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(body);
            var cleaned = SanitizeList(doc.DocumentNode.ChildNodes.ToList());
            doc.DocumentNode.RemoveAllChildren();
            foreach (var n in cleaned) doc.DocumentNode.AppendChild(n);
            return doc.DocumentNode.InnerHtml;
        }
        catch
        {
            // 净化本身失败时**宁可什么都不显示**，也不能把未净化的 HTML 放出去
            return "<p>(正文无法安全显示)</p>";
        }
    }

    // 纯函数式改写：每个节点只判定一次；不认识的标签「脱壳」（保留净化后的子节点），
    // 免得把 <article>/<figure> 这类结构标签连同正文一起吃掉。
    static List<HtmlAgilityPack.HtmlNode> SanitizeList(List<HtmlAgilityPack.HtmlNode> nodes)
    {
        var outp = new List<HtmlAgilityPack.HtmlNode>();
        foreach (var n in nodes)
        {
            if (n.NodeType == HtmlAgilityPack.HtmlNodeType.Comment) continue;
            if (n.NodeType != HtmlAgilityPack.HtmlNodeType.Element) { outp.Add(n); continue; }

            string tag = n.Name.ToLowerInvariant();
            if (DropWithContent.Contains(tag)) continue;

            if (!SafeTags.Contains(tag))
            {
                outp.AddRange(SanitizeList(n.ChildNodes.ToList()));
                continue;
            }

            foreach (var attr in n.Attributes.ToList())
            {
                string name = attr.Name.ToLowerInvariant();
                if (!SafeAttrs.Contains(name)) { n.Attributes.Remove(attr); continue; }
                if (name == "href")
                {
                    if (!IsSafeUrl(attr.Value ?? "", allowData: false)) n.Attributes.Remove(attr);
                    else n.SetAttributeValue("rel", "noopener noreferrer");
                }
                else if (name == "src")
                {
                    if (!IsSafeUrl(attr.Value ?? "", allowData: true)) n.Attributes.Remove(attr);
                    else if (tag == "img") n.SetAttributeValue("loading", "lazy");
                }
            }

            // src 被删掉的 img（相对路径/非法 scheme 一律删）只剩一个破图图标，
            // 留着只会在正文里撒一地碎图 —— 整个节点去掉。
            if (tag == "img" && n.Attributes["src"] == null) continue;

            var kids = SanitizeList(n.ChildNodes.ToList());
            n.RemoveAllChildren();
            foreach (var k in kids) n.AppendChild(k);
            outp.Add(n);
        }
        return outp;
    }

    // 只放行**绝对** URL：相对路径会以本机服务为基准，等于让正文去探我们的接口
    static bool IsSafeUrl(string url, bool allowData)
    {
        string u = (url ?? "").Trim();
        if (u.Length == 0) return false;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (!allowData && u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return true;
        if (allowData && u.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void HandleArticleGet(HttpListenerResponse res, int itemId)
    {
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.Title, i.Content, i.Description, i.Link, i.PublishDate, i.Author, f.Title, i.PageCount, i.Summary
            FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
            return;
        }
        string title = r.GetString(0);
        string content = r.IsDBNull(1) ? "" : r.GetString(1);
        string desc = r.IsDBNull(2) ? "" : r.GetString(2);
        string link = r.IsDBNull(3) ? "" : r.GetString(3);
        string pub = r.IsDBNull(4) ? "" : r.GetString(4);
        string author = r.IsDBNull(5) ? "" : r.GetString(5);
        string feed = r.IsDBNull(6) ? "" : r.GetString(6);
        int? pageCount = r.IsDBNull(7) ? null : r.GetInt32(7);
        string summary = r.IsDBNull(8) ? "" : r.GetString(8);
        r.Close();

        string articleContent = string.IsNullOrEmpty(content) ? desc : content;
        string? fulltext = null;
        string ftPath = FulltextPath(itemId);
        if (File.Exists(ftPath))
        {
            try { fulltext = File.ReadAllText(ftPath); } catch { }
        }
        var sig = GetSignal(itemId);

        // 有全文缓存就用全文（Markdown → 先渲染成 HTML），否则用源里的正文/摘要（本来就是 HTML）。
        // **只返回净化后的 HTML**：原先的 content/fulltext 是原始 HTML，前端 innerHTML 直接注入，
        // 等于任何订阅源都能在本机页面上执行脚本（同源 fetch 带 cookie → 把 API 交给订阅源作者）。
        string bodyHtml = ToSafeBodyHtml(string.IsNullOrEmpty(fulltext) ? articleContent : MarkdownToHtml(fulltext));

        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                itemId,
                title,
                feed,
                link,
                published = pub,
                author,
                quality = ContentQuality(content, desc),
                liked = sig?.UserLike ?? false,
                aiLiked = sig?.AiLike ?? false,
                bodyHtml,
                hasFulltext = !string.IsNullOrEmpty(fulltext),
                summary,
                pageCount
            }
        });
    }

    static void HandleArticleVersions(HttpListenerResponse res, int itemId)
    {
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var gCmd = conn.CreateCommand();
        gCmd.CommandText = "SELECT Guid, Title FROM Items WHERE Id = @id";
        gCmd.Parameters.AddWithValue("@id", itemId);
        using var gr = gCmd.ExecuteReader();
        if (!gr.Read())
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
            return;
        }
        string guid = gr.IsDBNull(0) ? "" : gr.GetString(0);
        string title = gr.GetString(1);
        gr.Close();

        var versions = new List<object>();
        if (!string.IsNullOrEmpty(guid))
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Version, Status, ArchivedAt, Title FROM Items WHERE Guid = @g ORDER BY Version DESC";
            cmd.Parameters.AddWithValue("@g", guid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                versions.Add(new
                {
                    id = r.GetInt64(0),
                    version = r.GetInt32(1),
                    status = r.GetString(2),
                    archivedAt = r.IsDBNull(3) ? "" : r.GetString(3),
                    title = r.GetString(4)
                });
            }
        }
        WriteJson(res, 200, new { success = true, data = new { itemId, title, versions } });
    }

    static void HandleArticleDiff(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        int? from = int.TryParse(QueryParam(req, "from"), out var f) ? f : null;
        int? to = int.TryParse(QueryParam(req, "to"), out var t) ? t : null;

        using var conn = OpenDb(webDbPath);
        conn.Open();
        var gCmd = conn.CreateCommand();
        gCmd.CommandText = "SELECT Guid FROM Items WHERE Id = @id";
        gCmd.Parameters.AddWithValue("@id", itemId);
        var guid = gCmd.ExecuteScalar()?.ToString() ?? "";
        if (string.IsNullOrEmpty(guid))
        {
            WriteJson(res, 200, new { success = true, article = itemId, from = 0, to = 0, changes = Array.Empty<object>() });
            return;
        }

        var cmd = conn.CreateCommand();
        cmd.CommandText = from != null
            ? "SELECT Id, Version, Content, Description, Title FROM Items WHERE Guid = @g AND Version = @v"
            : "SELECT Id, Version, Content, Description, Title FROM Items WHERE Guid = @g ORDER BY Version DESC LIMIT 2";
        cmd.Parameters.AddWithValue("@g", guid);
        if (from != null) cmd.Parameters.AddWithValue("@v", from);
        var list = new List<(long Id, int Ver, string Content, string Title)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                string c = r.IsDBNull(2) ? "" : r.GetString(2);
                if (string.IsNullOrEmpty(c) && !r.IsDBNull(3)) c = r.GetString(3);
                list.Add((r.GetInt64(0), r.GetInt32(1), c, r.GetString(4)));
            }
        }

        if (to != null && from != null)
        {
            var c2 = conn.CreateCommand();
            c2.CommandText = "SELECT Id, Version, Content, Description, Title FROM Items WHERE Guid = @g AND Version = @v";
            c2.Parameters.AddWithValue("@g", guid);
            c2.Parameters.AddWithValue("@v", to);
            using var r2 = c2.ExecuteReader();
            if (r2.Read())
            {
                string c = r2.IsDBNull(2) ? "" : r2.GetString(2);
                if (string.IsNullOrEmpty(c) && !r2.IsDBNull(3)) c = r2.GetString(3);
                list.Clear();
                list.Add((r2.GetInt64(0), r2.GetInt32(1), c, r2.GetString(4)));
            }
        }

        if (list.Count < 2)
        {
            WriteJson(res, 200, new { success = true, article = itemId, from = list.Count > 0 ? list[0].Ver : 0, to = list.Count > 0 ? list[0].Ver : 0, changes = Array.Empty<object>() });
            return;
        }

        // 默认 last two：list[0]=较新，list[1]=较旧
        var older = list.Count >= 2 && from == null ? list[1] : list[^1];
        var newer = from == null ? list[0] : list[0];
        if (from != null && to != null && list.Count >= 1)
        {
            // already filtered
        }

        var diffBuilder = new DiffPlex.DiffBuilder.InlineDiffBuilder(new DiffPlex.Differ());
        var model = diffBuilder.BuildDiffModel(older.Content, newer.Content);
        var changes = model.Lines
            .Select(l => new
            {
                type = l.Type.ToString(),
                text = l.Text ?? ""
            })
            .Where(x => x.type is "Unchanged" or "Inserted" or "Deleted" or "Modified")
            .ToList();

        WriteJson(res, 200, new
        {
            success = true,
            article = itemId,
            from = older.Ver,
            to = newer.Ver,
            titleOld = older.Title,
            titleNew = newer.Title,
            changes
        });
    }

    static async void HandleArticleLike(HttpListenerResponse res, int itemId)
    {
        if (!WebWriteAllowed(res, "like")) return;
        if (!ArticleExists(itemId, webDbPath))
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
            return;
        }
        bool liked = ToggleSignal(itemId, ai: false, reason: null, webDbPath);
        await Task.CompletedTask;
        WriteJson(res, 200, new { success = true, data = new { itemId, liked } });
    }

    // 抓全文 = 把别人的文章存到自己机器上，CLI 有一道一次性同意门（要手打短语）。
    // 原先 Web 直接传 `yes: true`：**既跳过同意门，又替用户把同意写进 fulltext_consent.txt**
    // —— 一次点击就等于永久同意，用户从没看过那段免责声明（2026-09-12 用户反馈）。
    // 现在 Web 复用同一份同意标记与同一段声明：没同意过就回 FULLTEXT_NEEDS_CONSENT +
    // 声明原文，前端展示后由用户点"同意并继续"，带 consent:true 再来一次才记录。
    // 同意是**一次性**的：记录之后 CLI 与网页都不再问。
    static async void HandleArticleFulltext(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        if (!WebWriteAllowed(res, "fulltext")) return;

        bool consent = false;
        try
        {
            string body = await ReadBodyAsync(req);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("consent", out var c) && c.ValueKind == JsonValueKind.True)
                    consent = true;
            }
        }
        catch { /* 请求体不是 JSON：按"未同意"处理 */ }

        if (!HasFulltextConsent())
        {
            if (!consent)
            {
                WriteJson(res, 428, new
                {
                    success = false,
                    error = new
                    {
                        code = "FULLTEXT_NEEDS_CONSENT",
                        message = Lang.T("Fetching the full text stores someone else's article on your machine. Please read and confirm first."),
                        disclaimer = FulltextDisclaimer()
                    }
                });
                return;
            }
            WriteFulltextConsent();
        }

        // 到这里同意已经成立（早先同意过，或刚刚在本页点过）→ yes:true 只用于跳过
        // 那个面向终端的二次确认；同意门本身已经在上面把过了。
        var (text, code, err) = FetchFulltext(webDbPath, itemId, yes: true);
        if (text != null)
            WriteJson(res, 200, new { success = true, data = new { itemId, content = text, bytes = text.Length } });
        else
            WriteJson(res, 502, new { success = false, error = new { code = "FULLTEXT_FAILED", message = err ?? "failed" } });
    }

    static async void HandleArticleSummary(HttpListenerResponse res, int itemId, bool generate)
    {
        // 只读取缓存摘要不该受写挡位限制（原先 GET /summary 也被当成写拦掉，见审计 §9.5）
        if (generate && !WebWriteAllowed(res, "summary")) return;
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Title, Summary FROM Items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", itemId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
                return;
            }
            string title = r.GetString(0);
            string existing = r.IsDBNull(1) ? "" : r.GetString(1);
            r.Close();
            if (!string.IsNullOrEmpty(existing))
            {
                WriteJson(res, 200, new { success = true, data = new { itemId, title, summary = existing, cached = true } });
                return;
            }
            if (!generate)
            {
                WriteJson(res, 200, new { success = true, data = new { itemId, title, summary = "", cached = false } });
                return;
            }
        }

        try
        {
            var (ok, summary) = await SummarizeItem(webDbPath, itemId, json: false, quiet: true);
            if (ok && summary != null)
                WriteJson(res, 200, new { success = true, data = new { itemId, summary, cached = false } });
            else
                WriteJson(res, 502, new { success = false, error = new { code = "SUMMARY_FAILED", message = "LLM summary failed" } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 502, new { success = false, error = new { code = "SUMMARY_FAILED", message = ex.Message } });
        }
    }

    // 阅读报告：与 CLI `sip --insights` **同一份事实**（直接调同一个 BuildInsights），
    // 不另起一套算法 —— 同一个数字在终端和网页上不一致，比没有这个功能更糟。
    //
    // 遥测默认关闭。此时**明确回 TELEMETRY_OFF**，让页面说清"没有数据"，
    // 而不是像以前那样渲染一组编造的统计数字冒充你的阅读情况。
    //
    // 与 CLI 的唯一区别：这里**不记** settings.LastInsightsAt。那个时间戳是给 TUI 判断
    // "报告到期该提醒了"用的；GET 请求不该顺手写配置（挡位 2 起写操作本来也是被拦的）。
    static void HandleInsights(HttpListenerRequest req, HttpListenerResponse res)
    {
        int window = 30;
        if (int.TryParse(QueryParam(req, "window"), out int w) && w > 0)
            window = Math.Clamp(w, 1, 365);

        if (!TelemetryService.IsEnabled)
        {
            WriteJson(res, 409, new
            {
                success = false,
                error = new
                {
                    code = "TELEMETRY_OFF",
                    message = Lang.T("阅读情况报告需要先开启遥测"),
                    hint = Lang.T("运行 sip telemetry enable 后重试")
                }
            });
            return;
        }

        var list = BuildInsights(webDbPath, window);
        var (total, ok, fail, llm, emb) = TelemetryService.GlobalAiCallStats(window);
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                windowDays = window,
                generatedAt = DateTime.Now.ToString("O"),
                aiCalls = new { total, success = ok, fail, llm, embedding = emb },
                feeds = list.Select(x => new
                {
                    id = x.FeedId,
                    title = x.Title,
                    schedule = x.Schedule,
                    active = x.Active,
                    backlog = x.Backlog,
                    opened = x.Opened,
                    completed = x.Completed,
                    skipped = x.Skipped,
                    completionRate = x.Opened > 0 ? Math.Round(100.0 * x.Completed / x.Opened, 0) : 0,
                    userLikes = x.UserLikes,
                    aiLikes = x.AiLikes,
                    llmCalls = x.LlmCalls,
                    embeddingCalls = x.EmbeddingCalls,
                    status = x.Status,
                    reasons = x.Reasons
                })
            }
        });
    }

    static void HandleToday(HttpListenerResponse res, bool refresh)
    {
        try
        {
            var (done, target, tracking) = TodayProgress(webDbPath);
            var list = GetTodayList(webDbPath, 5, refresh, out string generatedAt);
            WriteJson(res, 200, new
            {
                success = true,
                data = new
                {
                    date = DateTime.Now.ToString("yyyy-MM-dd"),
                    generatedAt,
                    refreshed = refresh,
                    target,
                    done,
                    tracking,
                    items = list.Select(i => new
                    {
                        itemId = i.ItemId,
                        title = i.Title,
                        source = i.Source,
                        reason = i.Reason,
                        minutes = i.Minutes,
                        score = i.Score
                    })
                }
            });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "TODAY_FAILED", message = ex.Message } });
        }
    }

    static void HandleLikes(HttpListenerResponse res)
    {
        var map = LoadSignals();
        var ids = map.Keys.Where(k => int.TryParse(k, out _)).Select(int.Parse).OrderBy(x => x).ToList();
        var titles = new Dictionary<int, string>();
        var feeds = new Dictionary<int, string>();
        if (ids.Count > 0)
        {
            using var conn = OpenDb(webDbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT i.Id, i.Title, f.Title
                FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
                WHERE i.Id IN (" + string.Join(",", ids) + ")";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                titles[r.GetInt32(0)] = r.GetString(1);
                feeds[r.GetInt32(0)] = r.IsDBNull(2) ? "" : r.GetString(2);
            }
        }
        var signals = ids.Select(id =>
        {
            map.TryGetValue(id.ToString(), out var e);
            return new
            {
                itemId = id,
                title = titles.GetValueOrDefault(id, ""),
                feed = feeds.GetValueOrDefault(id, ""),
                liked = e?.UserLike ?? false,
                aiLiked = e?.AiLike ?? false,
                reason = e?.AiReason ?? ""
            };
        }).Where(s => s.liked || s.aiLiked).ToList();

        WriteJson(res, 200, new { success = true, data = new { signals } });
    }

    static void HandleGrep(HttpListenerRequest req, HttpListenerResponse res)
    {
        string q = (QueryParam(req, "q") ?? "").Trim();
        int limit = int.TryParse(QueryParam(req, "limit"), out var lm) ? Math.Clamp(lm, 1, 200) : 50;
        if (q.Length == 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "q required" } });
            return;
        }
        var hits = DoGrep(q, webDbPath, limit, null);
        if (hits == null)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "empty query" } });
            return;
        }
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                query = q,
                hits = hits.Select(h => new
                {
                    itemId = h.ItemId,
                    title = h.Title,
                    feed = h.FeedTitle,
                    snippet = FirstSnippet(h, q),
                    link = h.Link
                })
            }
        });
    }

    static string FirstSnippet(GrepHit h, string q)
    {
        string[] parts = { h.Summary ?? "", h.Content ?? "", h.Description ?? "" };
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            int i = p.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (i < 0) i = p.IndexOf(q, StringComparison.Ordinal);
            if (i < 0) continue;
            int start = Math.Max(0, i - 24);
            int len = Math.Min(p.Length - start, q.Length + 56);
            string s = p.Substring(start, len).Replace('\n', ' ').Replace('\r', ' ');
            return (start > 0 ? "…" : "") + s + (start + len < p.Length ? "…" : "");
        }
        return "";
    }

    static void HandleSearch(HttpListenerRequest req, HttpListenerResponse res)
    {
        string q = (QueryParam(req, "q") ?? "").Trim();
        if (q.Length == 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "q required" } });
            return;
        }
        float? thr = float.TryParse(QueryParam(req, "threshold"), out var t) ? t : null;
        var hits = DoSearch(q, webDbPath, null, thr, json: false);
        if (hits == null)
        {
            WriteJson(res, 502, new { success = false, error = new { code = "SEARCH_FAILED", message = "semantic search unavailable (run sip --init)" } });
            return;
        }
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                query = q,
                hits = hits.Select(h => new
                {
                    itemId = h.ItemId,
                    title = h.Title,
                    feed = h.FeedTitle,
                    score = h.Score,
                    snippet = (h.Description ?? "").Replace('\n', ' ')
                })
            }
        });
    }

    // ══════════ Web 登录 / 会话 / 口令（原 WebAuth.cs）══════════
    // ── 路径 / 凭据名 ──
    static string WebAuthPath() => Path.Combine(dataDir, "web_auth.json");
    static string WebAuthResetPath() => Path.Combine(dataDir, "web_auth.reset");

    class WebAuthFile
    {
        public string Algo { get; set; } = "pbkdf2-sha256";
        public int Iterations { get; set; } = 100_000;
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    static WebAuthFile? LoadWebAuth()
    {
        try
        {
            string p = WebAuthPath();
            if (!File.Exists(p)) return null;
            var f = JsonSerializer.Deserialize<WebAuthFile>(File.ReadAllText(p));
            if (f == null || string.IsNullOrEmpty(f.Hash) || string.IsNullOrEmpty(f.Salt)) return null;
            return f;
        }
        catch { return null; }
    }

    static void SaveWebAuth(WebAuthFile f)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(WebAuthPath(), JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void ClearWebAuth()
    {
        try { if (File.Exists(WebAuthPath())) File.Delete(WebAuthPath()); } catch { }
    }

    static bool WebPasswordIsSet() => LoadWebAuth() != null;

    static string HashWebPassword(string password, byte[] salt, int iterations)
    {
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(hash);
    }

    static bool VerifyWebPassword(string password)
    {
        var f = LoadWebAuth();
        if (f == null) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(f.Salt);
            string h = HashWebPassword(password, salt, f.Iterations);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(h), Encoding.UTF8.GetBytes(f.Hash));
        }
        catch { return false; }
    }

    static void SetWebPassword(string password)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        const int iter = 100_000;
        string hash = HashWebPassword(password, salt, iter);
        SaveWebAuth(new WebAuthFile
        {
            Algo = "pbkdf2-sha256",
            Iterations = iter,
            Salt = Convert.ToBase64String(salt),
            Hash = hash,
            CreatedAt = DateTime.Now.ToString("O")
        });
    }

    // ══════════ 会话：一次性密钥，活到进程结束 ══════════
    // 三条规矩（这套设计的全部）：
    //   ① **进程内**：会话表只在内存里，没有第二份持久副本 → 重启 sip = 所有浏览器重新登录。
    //      旧实现是「HMAC 签名的 exp.MAC + 系统凭据库里的长期密钥」：签名密钥跨重启不变，
    //      于是那把 token 重启后照样有效、抄进别的浏览器也照样有效 —— 与这两条要求正好相反。
    //   ② **每浏览器一把钥匙**：登录成功时现场随机生成，并绑这次请求的浏览器指纹（UA）。
    //      换浏览器 = 手里没有这把钥匙；就算把值抄过去，也过不了指纹这一关 → 必须重新登录。
    //   ③ **一次一换**：同一浏览器再登录一次，旧钥匙当场作废，cookie 里换成新的。
    public const string WebSessionCookieName = "sip_web";
    // 无密码模式走同一套：终端里那条 `?t=` 引导链接换来的钥匙存在 sip_local 里
    public const string WebLocalCookieName = "sip_local";

    // cookie 在浏览器里存 30 天。**它只是一串进程内的钥匙**，进程一退就作废，浏览器里留久点无害；
    // 反过来，若做成会话 cookie（关掉浏览器即失效），就变成「重启浏览器也要再登一次」，
    // 与「重启程序前不必再输密码」的要求不符。
    const int WebCookieMaxAgeSeconds = 30 * 24 * 3600;

    // 这三条文案同时出现在终端横幅、登录页/说明页上，抽成常量：改一处不会漏另一处。
    // 键就是英文原文，zh-CN / zh-Moe 里给出译文；英文用户直接看到键本身。
    const string SessionLinePassword = "one login per browser, valid until sip exits · a restart asks for the password again";
    const string SessionLineBootLink = "one boot link per browser, valid until sip exits · a restart needs the new link";
    const string LoginNoticeRestarted = "sip was restarted, or this key belongs to another browser — enter the password again.";

    sealed class WebSession
    {
        public string Fingerprint = "";
        public bool Local;                 // true = 无密码模式下经引导链接换来的
        public DateTime CreatedUtc;
        public DateTime LastSeenUtc;       // 只用于裁剪排序；并发下偶尔读到旧值无害
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WebSession> webSessions = new(StringComparer.Ordinal);
    // 上限只为兜住内存：正常用量等于「浏览器数量」，几十个顶天。
    // 刻意**不按空闲时间清**：会话的有效期就是进程的寿命，挂着一下午也不该被踢下线。
    const int WebSessionMax = 128;

    static string WebClientFingerprint(HttpListenerRequest req)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.UserAgent ?? "")));

    static string IssueWebSession(HttpListenerRequest req, HttpListenerResponse res, string cookieName, bool local)
    {
        RevokePresentedSession(req, cookieName);        // ③ 旧钥匙作废
        var buf = new byte[32];
        RandomNumberGenerator.Fill(buf);
        string id = Convert.ToHexString(buf);
        webSessions[id] = new WebSession
        {
            Fingerprint = WebClientFingerprint(req),
            Local = local,
            CreatedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        PruneWebSessions();
        SetSessionCookie(res, cookieName, id);
        return id;
    }

    /// <summary>校验并续期（滑动）会话：**查表命中 + 指纹一致**才算通过。
    /// 查表本身就是这套设计的关键 —— 表里没有这把钥匙，就说明进程重启过（或它来自别的进程/别的浏览器）。</summary>
    static WebSession? LookupWebSession(HttpListenerRequest req, string cookieName, bool local)
    {
        string? id = req.Cookies[cookieName]?.Value;
        if (string.IsNullOrEmpty(id)) return null;
        if (!webSessions.TryGetValue(id, out var s)) return null;
        if (s.Local != local) return null;
        byte[] expect = Encoding.UTF8.GetBytes(s.Fingerprint);
        byte[] actual = Encoding.UTF8.GetBytes(WebClientFingerprint(req));
        if (expect.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expect, actual))
        {
            // 换浏览器（或 UA 变了）：这把钥匙不属于当前浏览器 —— 吊销，而不是留着
            webSessions.TryRemove(id, out _);
            return null;
        }
        s.LastSeenUtc = DateTime.UtcNow;
        return s;
    }

    static void RevokePresentedSession(HttpListenerRequest req, string cookieName)
    {
        string? id = req.Cookies[cookieName]?.Value;
        if (!string.IsNullOrEmpty(id)) webSessions.TryRemove(id, out _);
    }

    static void PruneWebSessions()
    {
        if (webSessions.Count <= WebSessionMax) return;
        foreach (var kv in webSessions.OrderBy(k => k.Value.LastSeenUtc).Take(webSessions.Count - WebSessionMax))
            webSessions.TryRemove(kv.Key, out _);
    }

    // 手写 Set-Cookie：HttpListener 的 Cookie 类对 SameSite 支持差，且部分环境 Expires 解析怪
    static void SetSessionCookie(HttpListenerResponse res, string name, string value)
    {
        try { res.Headers.Add("Set-Cookie", $"{name}={value}; Path=/; HttpOnly; SameSite=Lax; Max-Age={WebCookieMaxAgeSeconds}"); }
        catch { }
    }

    static void ClearSessionCookie(HttpListenerResponse res, string name)
    {
        try { res.Headers.Add("Set-Cookie", $"{name}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0"); }
        catch { }
    }

    // ── 无密码模式的**引导令牌**（每进程一次；只打印到终端，绝不写进页面）──
    // 为什么需要它：原先 GET / 会无条件下发 sip_local，于是**本机任意进程 GET 一次就拿到凭据**，
    // 整个订阅库通过 HTTP 敞开。引导令牌只出现在 `sip --start` 的终端输出里 ——
    // 那是别的进程读不到的带外信道（和"密码只在终端输入"是同一个思路）。
    //
    // 与 sip_local 的分工：
    //   引导令牌 = "门在哪" 的凭证，只在终端，用来换 cookie
    //   sip_local = "进过门" 的凭证，进程内随机，换到之后浏览器刷新照常
    static string? webBootToken;

    public static string EnsureWebBootToken()
    {
        if (string.IsNullOrEmpty(webBootToken))
        {
            var buf = new byte[16];
            RandomNumberGenerator.Fill(buf);
            webBootToken = Convert.ToHexString(buf).ToLowerInvariant();
        }
        return webBootToken;
    }

    static bool WebBootTokenOk(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        byte[] a = Encoding.UTF8.GetBytes(supplied);
        byte[] b = Encoding.UTF8.GetBytes(EnsureWebBootToken());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    static bool WebRequestIsAuthenticated(HttpListenerRequest req)
    {
        // 有密码：只认登录换来的 sip_web；无密码：只认引导链接换来的 sip_local。
        // 两条路都必须是**本进程签发的、绑当前浏览器的那把钥匙**（见 WebSession 段的 ① ②）。
        return WebPasswordIsSet()
            ? LookupWebSession(req, WebSessionCookieName, local: false) != null
            : LookupWebSession(req, WebLocalCookieName, local: true) != null;
    }

    static void HandleWebAuthResetFile()
    {
        try
        {
            if (!File.Exists(WebAuthResetPath())) return;
            bool had = WebPasswordIsSet();
            ClearWebAuth();
            File.Delete(WebAuthResetPath());
            Console.WriteLine(had
                ? Lang.T("Detected web_auth.reset: Web login password cleared. Run sip webpass to set a new one.")
                : Lang.T("Detected web_auth.reset: no Web password was set; marker deleted."));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Lang.T("Failed to handle web_auth.reset: {0}", ex.Message));
        }
    }

    // ══════════ CLI: sip webpass ══════════
    static void CliWebPass(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "set";

        if (sub is "status" or "show")
        {
            bool on = WebPasswordIsSet();
            Console.WriteLine(on
                ? Lang.T("Web login password: set")
                : Lang.T("Web login password: not set (--start goes straight in)"));
            Console.WriteLine(Lang.T("Forgot password: create web_auth.reset in the data folder, then run sip --start"));
            Console.WriteLine(Lang.T("Data folder: {0}", dataDir));
            return;
        }

        if (sub is "clear" or "off" or "disable")
        {
            RequireInteractiveTty("webpass clear");
            if (!WebPasswordIsSet())
            {
                Console.WriteLine(Lang.T("Web login password is not set"));
                return;
            }
            Console.Write(Lang.T("Clear Web login password? (y/n) "));
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(Lang.T("Cancelled"));
                return;
            }
            ClearWebAuth();
            Console.WriteLine(Lang.T("Web login password cleared"));
            return;
        }

        RequireInteractiveTty("webpass");
        PromptAndSetWebPassword();
    }

    static void PromptAndSetWebPassword()
    {
        Console.WriteLine(WebPasswordIsSet()
            ? Lang.T("Change Web login password")
            : Lang.T("Set Web login password (browser will ask for it; local only by default)"));
        Console.Write(Lang.T("New password: "));
        string p1 = ReadSecret();
        if (p1.Length < 4)
        {
            SetExit();
            Console.WriteLine(Lang.T("Password too short (min 4 characters)"));
            return;
        }
        Console.Write(Lang.T("Type again: "));
        string p2 = ReadSecret();
        if (p1 != p2)
        {
            SetExit();
            Console.WriteLine(Lang.T("Passwords do not match"));
            return;
        }
        SetWebPassword(p1);
        Console.WriteLine(Lang.T("Saved. Hash is in web_auth.json; session secret is in the OS credential store."));
    }

    // 首次 --start 初始化（真实 TTY）：密码 + 绑定地址/端口
    static void FirstRunWebPasswordSetup()
    {
        if (!HasInteractiveConsole()) return;   // 隐藏窗口/服务/脚本：不提问

        var st0 = LoadSettings();
        bool setupDone = st0.WebSetupDone;
        if (WebPasswordIsSet()) return;         // 已设密码：没有要问的
        // 没设密码也**不再每次启动都问**（用户反馈：每次开 web 都被问一遍，很烦）。
        // 只在"非本机绑定 + 无口令"这个真正危险的组合下才提醒 —— 那时 OpenClaw 的教训才适用；
        // 仅本机可访问时，裸链接 + 终端里的引导令牌本来就是设计好的用法。
        if (setupDone && IsLoopbackHost(string.IsNullOrWhiteSpace(st0.WebHost) ? "127.0.0.1" : st0.WebHost))
            return;

        Console.WriteLine();
        Console.WriteLine(Lang.T("==== sip Web setup ===="));

        if (!WebPasswordIsSet())
        {
            Console.WriteLine(Lang.T("Set a Web login password now?"));
            Console.WriteLine(Lang.T("  · Yes: the browser will require the password"));
            Console.WriteLine(Lang.T("  · Forgot later: create web_auth.reset in the data folder, then sip --start"));
            Console.Write(Lang.T("Set password now? [y/N] "));
            string? ans = Console.ReadLine()?.Trim();
            if (ans == null)
            {
                // stdin 到了 EOF（脚本 / 服务 / 重定向启动）：**不要**替用户设密码。
                // 这里原来会继续走到下面的"设密码"分支，配合 EOF 就可能写下一个
                // 没人知道的口令并把 WebSetupDone 置真 —— 也就是静默锁死。
                Console.WriteLine(Lang.T("No input available; skipping Web password setup."));
            }
            else if (IsYes(ans))
            {
                PromptAndSetWebPassword();
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(Lang.T("Are you sure?"));
                Console.WriteLine(Lang.T("OpenClaw left many hosts exposed on the public internet because it defaulted to no password."));
                Console.WriteLine(Lang.T("No password + non-local bind = anyone who can reach the address can read/write your library."));
                Console.Write(Lang.T("Type skip to continue without a password, or any other key to set one: "));
                string? confirm = Console.ReadLine()?.Trim();
                if (confirm == null)
                {
                    Console.WriteLine(Lang.T("No input available; skipping Web password setup."));
                }
                else if (!string.Equals(confirm, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    PromptAndSetWebPassword();
                }
                else
                {
                    Console.WriteLine(Lang.T("Password skipped. You can run sip webpass later."));
                }
            }
        }

        var st = LoadSettings();
        string defHost = string.IsNullOrWhiteSpace(st.WebHost) ? "127.0.0.1" : st.WebHost;
        int defPort = st.WebPort is > 0 and < 65535 ? st.WebPort : 8777;

        if (!setupDone)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.T("Bind which IP?"));
            Console.WriteLine(Lang.T("  127.0.0.1  this machine only (recommended)"));
            Console.WriteLine(Lang.T("  0.0.0.0    LAN/public reachable (set a password first)"));
            Console.Write(Lang.T("IP [{0}]: ", defHost));
            string hostIn = (Console.ReadLine()?.Trim() ?? "");
            string host = hostIn.Length == 0 ? defHost : hostIn;

            Console.Write(Lang.T("Port [{0}]: ", defPort));
            string portIn = (Console.ReadLine()?.Trim() ?? "");
            int port = defPort;
            if (portIn.Length > 0)
            {
                if (!int.TryParse(portIn, out port) || port is < 1 or > 65535)
                {
                    Console.WriteLine(Lang.T("Invalid port, using default {0}", defPort));
                    port = defPort;
                }
            }

            bool loopback = IsLoopbackHost(host);
            if (!loopback && !WebPasswordIsSet())
            {
                Console.WriteLine();
                Console.WriteLine(Lang.T("Binding {0} with no password opens your reading library.", host));
                Console.WriteLine(Lang.T("OpenClaw lesson: default no password + public reachability = mass exposure."));
                Console.Write(Lang.T("Type I-UNDERSTAND to continue, or any other key to set a password: "));
                string? ack = Console.ReadLine()?.Trim();
                if (ack == null)
                {
                    // 没人应答（EOF）：带着"非回环地址 + 无口令"启动是最危险的状态。
                    // 问不到人就不做这个危险选择 —— 退回默认的回环绑定。
                    Console.WriteLine(Lang.T("No input available; keeping the safe default bind."));
                    host = defHost;
                }
                else if (!string.Equals(ack, "I-UNDERSTAND", StringComparison.OrdinalIgnoreCase))
                {
                    PromptAndSetWebPassword();
                }
            }

            st.WebHost = host;
            st.WebPort = port;
            st.WebSetupDone = true;
            SaveSettings(st);
            Console.WriteLine(Lang.T("Saved bind {0}:{1} (sip_settings.json)", host, port));
        }
    }

    static bool IsYes(string? s)
        => string.Equals(s, "y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);

    static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().ToLowerInvariant();
        if (h is "127.0.0.1" or "localhost" or "::1" or "[::1]") return true;
        if (System.Net.IPAddress.TryParse(h, out var ip))
            return System.Net.IPAddress.IsLoopback(ip);
        return false;
    }

}