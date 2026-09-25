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

    // sip（无参数）与 sip --start 共用这一条启动路径：
    // 逃生口 → 首次向导 → 起服务。两处各写一遍迟早只剩一处被改到。
    //
    // 返回**有没有真的跑起来**：绑定失败时返回 false，双击路径据此停下来让人看清错误
    // （控制台窗口在进程退出的瞬间就会关掉，不暂停的话那几行提示一闪而过）。
    static async Task<bool> StartWebFromCli(string dbPath, bool noOpen)
    {
        HandleWebAuthResetFile();       // 逃生口：存在 web_auth.reset 则清密码
        FirstRunWebPasswordSetup();     // 首次且真实终端：询问是否设密码
        // 自动开浏览器只在**真终端**里做，且可以在设置里关掉（sip_settings.json 的
        // WebOpenBrowser）或命令行 --no-open 关掉；测试/脚本环境 stdout 被重定向，
        // HasInteractiveConsole() 为假，不会弹出浏览器。
        bool open = !noOpen && LoadSettings().WebOpenBrowser && HasInteractiveConsole();
        return await StartWebServer(dbPath, openBrowser: open);
    }

    /// <summary>双击路径专用：起不来就**停下来让人看清错误**再退出。
    /// 双击开的控制台窗口会在进程退出的瞬间关掉，不暂停的话那几行提示一闪而过 ——
    /// 用户只看到"窗口闪了一下"，然后什么都不知道。</summary>
    static async Task StartWebFromCliOrPause(string dbPath, bool noOpen)
    {
        if (await StartWebFromCli(dbPath, noOpen)) return;
        SetExit();
        if (!HasInteractiveConsole()) return;
        Console.WriteLine();
        Console.Write(Lang.T("Press Enter to close this window…"));
        try { Console.ReadLine(); } catch { }
    }

    /// <summary>把浏览器开到指定 URL。失败只提示、不抛 —— 打不开浏览器不该让服务起不来。</summary>
    static void OpenInBrowser(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = true };
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = url;                 // Windows 用 shell 打开 URL
            }
            else
            {
                psi.FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                psi.ArgumentList.Add(url);
                psi.UseShellExecute = false;
            }
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.T("(could not open the browser: {0} — open the link above manually)", ex.Message));
        }
    }

    // sip --start / 无参数  前台阻塞；Ctrl+C 退出
    // 绑定/端口来自 sip_settings.json（首次交互初始化写入）；未配置则 127.0.0.1:8777
    //
    // openBrowser：把浏览器**直接开到那条带引导令牌的链接**上。无密码模式下尤其重要 ——
    // 令牌只印在终端里，不自动开浏览器的话，双击的人还得自己复制粘贴一次。
    //
    // 返回 true = 真的进了接受循环（Ctrl+C 之后从这里返回）；false = 没起来（绑定失败等）。
    static async Task<bool> StartWebServer(string dbPath, bool openBrowser = false)
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
            return false;
        }

        bool loopback = IsLoopbackHost(host);
        // 无密码模式：把引导令牌印在 URL 里。这是唯一的带外发放点。
        string uiUrl = displayUrl.TrimEnd('/');
        string openUrl = WebPasswordIsSet() ? uiUrl : $"{uiUrl}/?t={EnsureWebBootToken()}";

        Console.WriteLine("========================================================");
        Console.WriteLine(Lang.T("  🍲 sip web · taste it slow"));
        Console.WriteLine(Lang.T("  Web UI     : {0}", openUrl));
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
        // 无参数启动现在默认就是 Web（见 sipcore.cs 入口），所以这里要告诉人终端界面还在。
        Console.WriteLine(Lang.T("  Terminal UI: {0}", "sip tui"));
        Console.WriteLine(Lang.T("  Ctrl+C to stop"));
        Console.WriteLine("========================================================");
        if (!loopback && !WebPasswordIsSet())
        {
            Console.WriteLine(Lang.T("No password and non-local bind — run sip webpass now, or switch back to 127.0.0.1."));
        }
        if (openBrowser)
        {
            Console.WriteLine(Lang.T("Opening the browser… (disable with --no-open, or WebOpenBrowser=false in sip_settings.json)"));
            OpenInBrowser(openUrl);
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
        return true;
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
            res.Headers["X-Frame-Options"] = "DENY";
            // ── CSP：第二道防线（第一道是服务端净化，见 ToSafeBodyHtml）──
            // 为什么现在才上：`script-src 'self'` 要求页面里**没有内联脚本、没有 onclick=**，
            // 所以前端先拆成 web/index.html（只有标记）+ web/app.js（全部逻辑）。
            // 这一条同时掐死"正文里混进 <script>"与"事件属性被激活"两条路：
            // 就算哪天净化器漏了一个标签，浏览器也不会执行它。
            //   default-src 'none'      —— 没写的一律不许（白名单思路，与净化器一致）
            //   script-src 'self'       —— 只有我们自己的 /app.js、/login.js
            //   style-src  允许 inline  —— 页内 <style> 与 style="" 是排版手段，不构成脚本执行面
            //   img-src    放行 http(s) —— 正文里的图是订阅源给的**绝对**地址（净化器只放行绝对 URL）
            //   connect-src 'self'      —— 前端只能打本机 API
            //   frame-ancestors 'none'  —— 与 XFO 重复是有意的：老浏览器认 XFO，新的认 CSP
            res.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: http: https:; media-src http: https:; font-src 'self'; " +
                "connect-src 'self'; form-action 'self'; frame-ancestors 'none'; " +
                "base-uri 'none'; object-src 'none'";

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

            // ── 静态资源（前端脚本、语言文件、PWA 清单）──
            // 放在认证检查**之前**：它们本身不含你的数据，而登录页正需要 /login.js 与背景图。
            // （此前 /languages/*.json 没有路由，于是界面永远回落到 20 个键的内置表 ——
            //   "多语言"看着有、实际没生效。）
            if (method is "GET" or "HEAD" && TryServeStatic(res, path)) return;

            // ── 导入文件的「临时链接」出口：/api/imports/{id}/file?k=<临时令牌> ──
            // 必须在下面那道"/api/* 一律要会话"之前：这条链接的凭据**就是那个令牌**
            // （进程内、只对一份文件、10 分钟过期），而不是浏览器 cookie ——
            // 否则"拿去别的标签页 / 别的 PDF 阅读器里打开"根本无从谈起。
            // 没带令牌或令牌不对时，仍然按会话认证走（所以它也是"登录后用浏览器直接打开"的接口）。
            if (method is "GET" or "HEAD"
                && TryMatch(path, "/api/imports/", out int fileItemId, out string fileRest)
                && fileRest == "/file")
            {
                HandleImportFile(req, res, fileItemId);
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
            if (method == "GET" && path == "/api/reading-progress") { HandleReadingProgressGet(res); return; }
            if (method == "POST" && path == "/api/reading-progress") { HandleReadingProgressSet(req, res); return; }
            if (method == "POST" && path == "/api/feeds/sync") { HandleSync(res, onlyDue: true); return; }
            if (method == "POST" && path == "/api/feeds/update-all") { HandleSync(res, onlyDue: false); return; }

            if (TryMatch(path, "/api/feeds/", out int feedId, out string rest))
            {
                if (method == "GET" && rest == "/articles") { HandleFeedArticles(res, feedId); return; }
                if (method == "GET" && rest == "/info") { HandleFeedInfo(res, feedId); return; }
                if (method == "POST" && rest == "/update") { HandleFeedUpdate(res, feedId); return; }
                if (method == "POST" && rest == "/archive") { HandleFeedArchive(res, feedId, archive: true); return; }
                if (method == "POST" && rest == "/unarchive") { HandleFeedArchive(res, feedId, archive: false); return; }
                if (method == "POST" && rest == "/schedule") { HandleFeedSchedule(req, res, feedId); return; }
                if (method == "DELETE" && rest == "") { HandleFeedDelete(res, feedId); return; }
            }

            // ── articles ──
            if (TryMatch(path, "/api/articles/", out int itemId, out string ar))
            {
                if (method == "GET" && ar == "") { HandleArticleGet(req, res, itemId); return; }
                if (method == "GET" && ar == "/versions") { HandleArticleVersions(res, itemId); return; }
                if (method == "GET" && ar == "/diff") { HandleArticleDiff(req, res, itemId); return; }
                if (method == "GET" && ar == "/export") { HandleArticleExport(res, itemId); return; }
                if (method == "POST" && ar == "/like") { HandleArticleLike(res, itemId); return; }
                if (method == "POST" && ar == "/fulltext") { HandleArticleFulltext(req, res, itemId); return; }
                if (method == "GET" && ar == "/summary") { HandleArticleSummary(res, itemId, generate: false); return; }
                if (method == "POST" && ar == "/summary") { HandleArticleSummary(res, itemId, generate: true); return; }
            }

            // ── today / likes / search ──
            if (method == "GET" && path == "/api/today") { HandleToday(req, res, refresh: false); return; }
            if (method == "POST" && path == "/api/today/refresh") { HandleToday(req, res, refresh: true); return; }
            if (method == "GET" && path == "/api/likes") { HandleLikes(res); return; }
            if (method == "GET" && path == "/api/grep") { HandleGrep(req, res); return; }
            if (method == "GET" && path == "/api/search") { HandleSearch(req, res); return; }
            if (method == "GET" && path == "/api/insights") { HandleInsights(req, res); return; }

            // ══════════ v2.0.0：补齐的 Web 功能 ══════════
            // 改稿追踪（列表）
            if (method == "GET" && path == "/api/edits") { HandleEdits(req, res); return; }

            // 跨源去重
            if (method == "GET" && path == "/api/dedup") { HandleDedupList(req, res); return; }
            if (method == "POST" && path == "/api/dedup/scan") { HandleDedupScan(req, res); return; }
            if (method == "POST" && path == "/api/dedup/hide") { HandleDedupHide(req, res); return; }
            if (method == "POST" && path == "/api/dedup/hide-cluster") { HandleDedupHideCluster(req, res); return; }
            if (method == "POST" && path == "/api/dedup/undo") { HandleDedupUndo(req, res); return; }
            if (method == "GET" && path == "/api/dedup/diff") { HandleDedupDiff(req, res); return; }

            // 源规则（用户确认的处理规则）
            if (method == "GET" && path == "/api/policies") { HandlePolicyList(res); return; }
            if (method == "POST" && path == "/api/policies") { HandlePolicySet(req, res); return; }
            if (TryMatch(path, "/api/policies/", out int polFeedId, out string polRest) && polRest == "")
            {
                if (method == "DELETE") { HandlePolicyRemove(res, polFeedId); return; }
            }

            // 本地导入 / 电子书
            if (method == "GET" && path == "/api/imports") { HandleImportList(res); return; }
            if (method == "POST" && path == "/api/imports") { HandleImportUpload(req, res); return; }
            if (TryMatch(path, "/api/imports/", out int impId, out string impRest))
            {
                if (method == "GET" && impRest == "") { HandleImportDetail(res, impId); return; }
                if (method == "GET" && impRest == "/text") { HandleImportText(req, res, impId); return; }
                if (method == "GET" && impRest == "/asset") { HandleImportAsset(req, res, impId); return; }
                if (method == "POST" && impRest == "/link") { HandleImportLink(res, impId); return; }
                if (method == "GET" && impRest.StartsWith("/page/", StringComparison.Ordinal))
                {
                    if (int.TryParse(impRest[6..], out int pageNo)) { HandleImportPage(res, impId, pageNo); return; }
                }
                if (method == "DELETE" && impRest == "") { HandleImportDelete(res, impId); return; }
            }

            // 向量索引
            if (method == "GET" && path == "/api/index") { HandleIndexStatus(res); return; }
            if (method == "POST" && path == "/api/index") { HandleIndexRun(req, res); return; }

            // 治理面：挡位 / Agent 门 / 遥测 / 配置 / 推荐源（onboarding）
            if (method == "GET" && path == "/api/simon") { HandleSimonStatus(res); return; }
            if (method == "POST" && path == "/api/simon/level") { HandleSimonLevel(req, res); return; }
            if (method == "GET" && path == "/api/telemetry") { HandleTelemetryStatus(res); return; }
            if (method == "POST" && path == "/api/telemetry") { HandleTelemetrySet(req, res); return; }
            if (method == "GET" && path == "/api/telemetry/export") { HandleTelemetryExport(res); return; }
            if (method == "GET" && path == "/api/config") { HandleConfig(res); return; }
            if (method == "GET" && path == "/api/onboarding") { HandleOnboardingList(res); return; }
            if (method == "POST" && path == "/api/onboarding/add") { HandleOnboardingAdd(req, res); return; }
            if (method == "POST" && path == "/api/insights/interval") { HandleInsightsInterval(req, res); return; }
            if (method == "POST" && path == "/api/summaries") { HandleSummaryAll(req, res); return; }
            if (method == "POST" && path == "/api/purge-fulltext") { HandlePurgeFulltext(req, res); return; }

            // 命令面板（只读白名单，不执行任意 CLI）
            if (method == "POST" && path == "/api/command") { HandleCommand(req, res); return; }

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

    /// <summary>带**非 ASCII 文件名**的下载（文章标题常常是中文）。
    /// HTTP 头只能是 ASCII，所以同时给 <c>filename</c>（回落用）与
    /// <c>filename*=UTF-8''…</c>（RFC 5987，现代浏览器优先用它）——
    /// 只给前者的话，中文标题会被压成乱码甚至整条头被拒。</summary>
    static void WriteDownloadEncoded(HttpListenerResponse res, string contentType, string asciiFilename, string utf8Filename, string body)
    {
        byte[] buf = Encoding.UTF8.GetBytes(body);
        res.StatusCode = 200;
        res.ContentType = contentType;
        string encoded = Uri.EscapeDataString(utf8Filename).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        res.Headers["Content-Disposition"] = $"attachment; filename=\"{asciiFilename}\"; filename*=UTF-8''{encoded}";
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

    // ══════════ 静态资源 ══════════
    // 前端脚本与语言文件都来自**内嵌资源**（单文件 exe 不依赖外部目录），
    // 找不到时回落到源码树/输出目录里的同名文件 —— 开发态改完不用重新打包。
    //
    // 为什么把 JS 拆成独立文件而不是留在 <style>/<script> 里：
    // CSP 的 `script-src 'self'` 不允许内联脚本。拆出去之后，
    // 「正文里混进 <script>」这条 XSS 路径在浏览器层面就是死的 —— 净化器漏一个标签也不会执行。
    static readonly Dictionary<string, (string Resource, string ContentType)> WebStaticFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/app.js"] = ("sip-web.app.js", "application/javascript; charset=utf-8"),
            ["/login.js"] = ("sip-web.login.js", "application/javascript; charset=utf-8"),
            ["/manifest.webmanifest"] = ("sip-web.manifest", "application/manifest+json; charset=utf-8"),
            ["/sw.js"] = ("sip-web.sw.js", "application/javascript; charset=utf-8"),
            ["/icon.svg"] = ("sip-web.icon.svg", "image/svg+xml; charset=utf-8"),
        };

    /// <summary>命中并写出一个静态资源；没命中返回 false（调用方继续走后面的路由）。</summary>
    static bool TryServeStatic(HttpListenerResponse res, string path)
    {
        if (WebStaticFiles.TryGetValue(path, out var asset))
            return WriteStatic(res, asset.Resource, asset.ContentType, Path.GetFileName(path));

        // /languages/zh-CN.json —— 界面语言词典（与 sip Lang.T 同一批键）。
        // 只接受 `字母/连字符` 组成的文件名：这里**不做路径拼接**，免得给目录穿越留口子。
        const string langPrefix = "/languages/";
        if (path.StartsWith(langPrefix, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            string code = path[langPrefix.Length..^5];
            if (code.Length is > 0 and <= 24 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return WriteStatic(res, "sip-lang." + code + ".json", "application/json; charset=utf-8", code + ".json");
        }
        return false;
    }

    static bool WriteStatic(HttpListenerResponse res, string resourceName, string contentType, string diskName)
    {
        byte[]? buf = null;
        using (var rs = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
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
            // 开发态：输出目录里的 web/ 或 languages/（两种都试，路径由文件名拼出，不接受调用方给的路径）
            string[] candidates =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", diskName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "languages", diskName)
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) { buf = File.ReadAllBytes(c); break; } } catch { }
            }
        }
        if (buf == null) return false;

        res.StatusCode = 200;
        res.ContentType = contentType;
        res.ContentLength64 = buf.Length;
        if (res.OutputStream.CanWrite) res.OutputStream.Write(buf, 0, buf.Length);
        res.OutputStream.Close();
        return true;
    }

    static void WriteLoginBg(HttpListenerResponse res)
    {        var asm = System.Reflection.Assembly.GetExecutingAssembly();
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

    // ── 阅读位置记忆（reading_progress.json，与 TUI 共用同一个文件）──
    // 刻意**不过** WebWriteAllowed：这是界面状态（读到哪儿了），不是对库的改动。
    // 挡位 2 的本意是拦住"改库"的动作；把滚动位置也拦掉，只会让人没法接着读。
    static void HandleReadingProgressGet(HttpListenerResponse res)
    {
        var map = LoadReadingProgress();
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                positions = map.ToDictionary(k => k.Key.ToString(), v => v.Value)
            }
        });
    }

    static async void HandleReadingProgressSet(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body = await ReadBodyAsync(req);
        long itemId = BodyInt(body, "itemId");
        int position = BodyInt(body, "position", -1);
        if (itemId <= 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "itemId required" } });
            return;
        }
        var map = LoadReadingProgress();
        if (position < 0) map.Remove(itemId);
        else map[itemId] = position;
        SaveReadingProgress(map);
        WriteJson(res, 200, new { success = true, data = new { itemId, position = map.TryGetValue(itemId, out int p) ? p : -1 } });
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

    // 更新计划（CLI: `sip --schedule <id> <expr>`；TUI: schedule 命令）。
    // 与它们共用 SetFeedSchedule —— 表达式解析、清空语义、遥测留痕都只有一份。
    static async void HandleFeedSchedule(HttpListenerRequest req, HttpListenerResponse res, int feedRealId)
    {
        if (!WebWriteAllowed(res, "schedule")) return;
        string body = await ReadBodyAsync(req);
        string expr = BodyString(body, "expr").Trim();
        if (!FeedExistsReal(feedRealId))
        {
            WriteJson(res, 404, new { success = false, error = new { code = "FEED_NOT_FOUND", message = Lang.T("Feed number not found") } });
            return;
        }
        // 空串 / manual 都表示"只手动更新"（与 CLI 同一语义）
        if (expr.Length == 0) expr = "manual";
        if (!expr.Equals("manual", StringComparison.OrdinalIgnoreCase) && TryParseSchedule(expr) == null)
        {
            WriteJson(res, 400, new
            {
                success = false,
                error = new
                {
                    code = "BAD_SCHEDULE",
                    message = Lang.T("Invalid schedule expression: {0}; e.g. 30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual", expr)
                }
            });
            return;
        }
        SetFeedSchedule(GetDisplayNum(feedRealId, webDbPath).ToString(), expr, webDbPath);
        WriteJson(res, 200, new
        {
            success = true,
            data = new { feedId = feedRealId, schedule = expr.Equals("manual", StringComparison.OrdinalIgnoreCase) ? "" : expr.ToLowerInvariant() }
        });
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

    static string ToSafeBodyHtml(string body, long importItemId = 0)
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
            // 导入的电子书里，图片是**本机 file:// 绝对路径**（EPUB/DOCX 抽取时改写的）。
            // 净化器只放行绝对 http(s)/data: 地址，于是这些图会被整个丢掉 ——
            // 所以先把它们改写成我们自己的资产接口（那条接口把路径围在 imported/ 之内）。
            if (importItemId > 0)
            {
                foreach (var img in doc.DocumentNode.Descendants("img").ToList())
                {
                    string src = img.GetAttributeValue("src", "");
                    string? local = LocalPathOf(src);
                    if (local != null)
                        img.SetAttributeValue("src",
                            $"/api/imports/{importItemId}/asset?path={Uri.EscapeDataString(local)}");
                }
            }
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
        // 唯一的相对路径例外：我们自己的导入资产接口。
        // 它由 ToSafeBodyHtml 从 file:// 改写而来（见那里的注释），
        // 服务端会把路径围在 readwithhotsoup/imported/ 之内 —— 不是任意本地文件读取口。
        if (allowData && u.StartsWith("/api/imports/", StringComparison.Ordinal)) return true;
        return false;
    }

    static void HandleArticleGet(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        int? wantVersion = int.TryParse(QueryParam(req, "version"), out int qv) ? qv : null;

        string title, content, desc, link, pub, author, feed, summary, guid, status, archivedAt;
        int? pageCount;
        int version, feedId, versionCount;
        int wantId = itemId;
        bool imported = false;   // 属于「本地导入」源（界面据此把它交给阅读器）

        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var head = conn.CreateCommand();
            head.CommandText = "SELECT Guid, FeedId, Version FROM Items WHERE Id = @id";
            head.Parameters.AddWithValue("@id", itemId);
            using (var hr = head.ExecuteReader())
            {
                if (!hr.Read())
                {
                    WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
                    return;
                }
                guid = hr.IsDBNull(0) ? "" : hr.GetString(0);
                feedId = hr.GetInt32(1);
                version = hr.GetInt32(2);
            }

            // ?version=N = 看**历史版本**的正文（TUI 里按 V 选一版等价物）。
            // 版本链按 (Guid, FeedId) 隔离 —— 不同源可能转载同一篇（Guid 相同），
            // 只按 Guid 取会让两个源的历史混在一起，版本号还会重复。
            if (wantVersion != null && wantVersion != version && guid.Length > 0)
            {
                var vc = conn.CreateCommand();
                vc.CommandText = "SELECT Id FROM Items WHERE Guid = @g AND FeedId = @f AND Version = @v LIMIT 1";
                vc.Parameters.AddWithValue("@g", guid);
                vc.Parameters.AddWithValue("@f", feedId);
                vc.Parameters.AddWithValue("@v", wantVersion.Value);
                object? hit = vc.ExecuteScalar();
                if (hit == null)
                {
                    WriteJson(res, 404, new { success = false, error = new { code = "VERSION_NOT_FOUND", message = "version not found" } });
                    return;
                }
                wantId = Convert.ToInt32(hit);
                version = wantVersion.Value;
            }

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT i.Title, i.Content, i.Description, i.Link, i.PublishDate, i.Author, f.Title,
                       i.PageCount, i.Summary, i.Status, i.ArchivedAt, f.FeedUrl
                FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
                WHERE i.Id = @id";
            cmd.Parameters.AddWithValue("@id", wantId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
                return;
            }
            title = r.GetString(0);
            content = r.IsDBNull(1) ? "" : r.GetString(1);
            desc = r.IsDBNull(2) ? "" : r.GetString(2);
            link = r.IsDBNull(3) ? "" : r.GetString(3);
            pub = r.IsDBNull(4) ? "" : r.GetString(4);
            author = r.IsDBNull(5) ? "" : r.GetString(5);
            feed = r.IsDBNull(6) ? "" : r.GetString(6);
            pageCount = r.IsDBNull(7) ? null : r.GetInt32(7);
            summary = r.IsDBNull(8) ? "" : r.GetString(8);
            status = r.GetString(9);
            archivedAt = r.IsDBNull(10) ? "" : r.GetString(10);
            // 「本地导入」的文件也在这条接口上（它就是一个普通源）。界面据此把它交给**阅读器**：
            // 那边按节分页、图片也改写好了；文章视图对整本书既分不了页、图还会被净化器丢掉。
            imported = !r.IsDBNull(11) && r.GetString(11) == "local://import";
            r.Close();

            var vcount = conn.CreateCommand();
            vcount.CommandText = guid.Length == 0
                ? "SELECT 1"
                : "SELECT COUNT(*) FROM Items WHERE Guid = @g AND FeedId = @f";
            vcount.Parameters.AddWithValue("@g", guid);
            vcount.Parameters.AddWithValue("@f", feedId);
            versionCount = guid.Length == 0 ? 1 : Convert.ToInt32(vcount.ExecuteScalar() ?? 1);
        }

        string articleContent = string.IsNullOrEmpty(content) ? desc : content;
        string? fulltext = null;
        string ftPath = FulltextPath(wantId);
        if (File.Exists(ftPath))
        {
            try { fulltext = File.ReadAllText(ftPath); } catch { }
        }
        var sig = GetSignal(itemId);

        // 有全文缓存就用全文（Markdown → 先渲染成 HTML），否则用源里的正文/摘要（本来就是 HTML）。
        // **只返回净化后的 HTML**：原先的 content/fulltext 是原始 HTML，前端 innerHTML 直接注入，
        // 等于任何订阅源都能在本机页面上执行脚本（同源 fetch 带 cookie → 把 API 交给订阅源作者）。
        //
        // 导入项要带上 importItemId：它的图片是本机 file:// 路径，净化器只放行绝对 http(s)/data:，
        // 不改写就会被**整个丢掉** —— 用户看到的就是"电子书里没有图"。
        string bodyHtml = ToSafeBodyHtml(
            string.IsNullOrEmpty(fulltext) ? articleContent : MarkdownToHtml(fulltext),
            importItemId: imported ? wantId : 0);

        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                itemId,
                shownItemId = wantId,
                version,
                versionCount,
                hasHistory = versionCount > 1,
                status,
                archivedAt,
                title,
                feed,
                feedId,
                imported,
                guid,
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
        gCmd.CommandText = "SELECT Guid, Title, FeedId FROM Items WHERE Id = @id";
        gCmd.Parameters.AddWithValue("@id", itemId);
        using var gr = gCmd.ExecuteReader();
        if (!gr.Read())
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
            return;
        }
        string guid = gr.IsDBNull(0) ? "" : gr.GetString(0);
        string title = gr.GetString(1);
        int feedId = gr.GetInt32(2);
        gr.Close();

        string feed = "";
        var fCmd = conn.CreateCommand();
        fCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
        fCmd.Parameters.AddWithValue("@id", feedId);
        feed = fCmd.ExecuteScalar()?.ToString() ?? "";

        var versions = new List<object>();
        if (!string.IsNullOrEmpty(guid))
        {
            // 版本链按 (Guid, FeedId) 隔离：跨源转载不该混进同一条历史
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Version, Status, ArchivedAt, Title,
                       LENGTH(COALESCE(NULLIF(Content,''), Description, ''))
                FROM Items WHERE Guid = @g AND FeedId = @f ORDER BY Version DESC";
            cmd.Parameters.AddWithValue("@g", guid);
            cmd.Parameters.AddWithValue("@f", feedId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long vid = r.GetInt64(0);
                versions.Add(new
                {
                    id = vid,
                    version = r.GetInt32(1),
                    status = r.GetString(2),
                    archivedAt = r.IsDBNull(3) ? "" : r.GetString(3),
                    title = r.GetString(4),
                    length = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    current = vid == itemId
                });
            }
        }
        WriteJson(res, 200, new { success = true, data = new { itemId, title, feed, feedId, versions } });
    }

    static void HandleArticleDiff(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        int? from = int.TryParse(QueryParam(req, "from"), out var f) ? f : null;
        int? to = int.TryParse(QueryParam(req, "to"), out var t) ? t : null;

        using var conn = OpenDb(webDbPath);
        conn.Open();
        var head = conn.CreateCommand();
        head.CommandText = "SELECT Guid, FeedId FROM Items WHERE Id = @id";
        head.Parameters.AddWithValue("@id", itemId);
        string guid; int feedId;
        using (var hr = head.ExecuteReader())
        {
            if (!hr.Read())
            {
                WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
                return;
            }
            guid = hr.IsDBNull(0) ? "" : hr.GetString(0);
            feedId = hr.GetInt32(1);
        }

        if (string.IsNullOrEmpty(guid))
        {
            WriteJson(res, 200, new { success = true, data = new { article = itemId, from = 0, to = 0, added = 0, removed = 0, changes = Array.Empty<object>() } });
            return;
        }

        var rows = new List<(int Ver, string Body, string Title)>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Version, COALESCE(NULLIF(Content,''), Description, ''), Title
            FROM Items WHERE Guid = @g AND FeedId = @f ORDER BY Version";
        cmd.Parameters.AddWithValue("@g", guid);
        cmd.Parameters.AddWithValue("@f", feedId);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                rows.Add((r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2)));
        }

        if (rows.Count < 2)
        {
            WriteJson(res, 200, new { success = true, data = new { article = itemId, from = rows.Count > 0 ? rows[0].Ver : 0, to = rows.Count > 0 ? rows[0].Ver : 0, added = 0, removed = 0, changes = Array.Empty<object>() } });
            return;
        }

        // 选了哪两版就比哪两版；没选默认**最后两版**（rows 按 Version 升序）
        var older = rows[^2];
        var newer = rows[^1];
        if (from != null)
        {
            var m = rows.FirstOrDefault(x => x.Ver == from.Value);
            if (m.Body != null || m.Title != null) older = m;
        }
        if (to != null)
        {
            var m = rows.FirstOrDefault(x => x.Ver == to.Value);
            if (m.Body != null || m.Title != null) newer = m;
        }

        // 按段落比（DiffParagraphs 内部先 DiffNormalize）：RSS 的正文常常整篇只有一行 HTML，
        // 直接按行 diff 会得到"整篇删除 + 整篇插入"，看着像作者重写了全文 —— 那是假象。
        var (added, removed, changes) = DiffParagraphs(older.Body ?? "", newer.Body ?? "");

        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                article = itemId,
                from = older.Ver,
                to = newer.Ver,
                titleOld = older.Title,
                titleNew = newer.Title,
                titleChanged = older.Title != newer.Title,
                added,
                removed,
                changes
            }
        });
    }

    // 导出单篇 Markdown：**与 CLI `sip --export <id>` 共用 BuildArticleMarkdown**，
    // 不另写一套渲染 —— 否则网页下载的文件和终端里的迟早不一样。
    static void HandleArticleExport(HttpListenerResponse res, int itemId)
    {
        if (!ArticleExists(itemId, webDbPath))
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
            return;
        }
        string md;
        string fileName = $"sip-{itemId}.md";
        try
        {
            md = BuildArticleMarkdown(itemId, true, webDbPath, 90);
            string title = ItemTitle(itemId);
            if (!string.IsNullOrWhiteSpace(title))
                fileName = "sip-" + new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim() + ".md";
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "EXPORT_FAILED", message = ex.Message } });
            return;
        }
        // 文件名可能很长，且必须是**纯 ASCII 安全的** header 值：非 ASCII 交给 filename* 的 RFC 5987 形式
        string ascii = new string(fileName.Where(c => c < 128 && c != '"' && c != '\\').ToArray());
        if (string.IsNullOrWhiteSpace(ascii) || ascii.Length < 4) ascii = $"sip-{itemId}.md";
        WriteDownloadEncoded(res, "text/markdown; charset=utf-8", ascii, fileName, md);
    }

    static string ItemTitle(int itemId)
    {
        try
        {
            using var conn = OpenDb(webDbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Title FROM Items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", itemId);
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        catch { return ""; }
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

    static void HandleToday(HttpListenerRequest req, HttpListenerResponse res, bool refresh)
    {
        try
        {
            var (done, target, tracking) = TodayProgress(webDbPath);
            var list = GetTodayList(webDbPath, 5, refresh, out string generatedAt);
            // 「今日变化」摘要（新增/被改/可能同文）默认**不算**：它要跑一次 48 小时窗口的
            // 跨源重复检测（上万篇正文读进来做段落比对）。首屏不该为它等几秒，
            // 所以界面用 ?digest=1 单独要一次。
            object? digest = null;
            if (QueryInt(req, "digest", 0, 0, 1) == 1)
            {
                var d = BuildTodayDigest(webDbPath, 48);
                digest = new
                {
                    newTotal = d.NewTotal,
                    sourceCount = d.SourceCount,
                    newBySource = d.NewBySource.Select(s => new { source = s.Source, count = s.Count, flood = s.Flood }),
                    modified = d.Modified.Select(m => new
                    {
                        itemId = m.ItemId,
                        title = m.Title,
                        source = m.Source,
                        titleChanged = m.TitleChanged,
                        addedLines = m.AddedLines,
                        removedLines = m.RemovedLines,
                        wordDelta = m.WordDelta
                    }),
                    dedups = d.Dedups.Select(c => new
                    {
                        size = c.Size,
                        representativeId = c.RepresentativeId,
                        title = c.Title,
                        source = c.Source,
                        minOverlap = c.MinOverlap,
                        members = c.Members
                    })
                };
            }
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
                    digest,
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

    // ══════════════════════════════════════════════════════════════════════
    // v2.0.0 · Web 功能对齐：改稿追踪 / 跨源去重 / 源规则 / 本地导入 / 电子书 /
    //          向量索引 / 治理面（挡位·遥测·配置）/ 导出 / 只读命令面板
    //
    // 三条不变的原则（与既有 handler 一致）：
    //   ① 进程内直接调**核心函数**，不 shell 出 CLI、不解析子进程 stdout。
    //      尤其**不调 `*Cli` 包装器**：它们往服务器自己的 stdout 打印进度，
    //      还会用 SetExit() 改**进程级**退出码 —— 那是给一次性命令行进程用的，
    //      放进长期运行的 HTTP 服务里会让"某个请求失败"污染整个进程。
    //   ② 与 CLI 共用同一份事实（同一个 FindDuplicateClusters / HideAsDedup /
    //      LoadSourcePolicy / BuildInsights …），绝不写第二套算法：
    //      同一个数字在终端和网页上不一致，比没有这个功能更糟。
    //   ③ 写操作一律先过 WebWriteAllowed（与 CLI 同一套挡位语义）。
    // ══════════════════════════════════════════════════════════════════════

    // ── 小工具：请求体 / 查询串的取值 ──
    static int QueryInt(HttpListenerRequest req, string key, int def, int min, int max)
        => int.TryParse(QueryParam(req, key), out int v) ? Math.Clamp(v, min, max) : def;

    static JsonElement? BodyProp(string body, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(prop, out var e))
                return e.Clone();   // 必须 Clone：doc 在 using 结束时释放
        }
        catch { }
        return null;
    }

    static string BodyString(string body, string prop)
    {
        var e = BodyProp(body, prop);
        if (e == null) return "";
        return e.Value.ValueKind == JsonValueKind.String ? (e.Value.GetString() ?? "") : e.Value.ToString();
    }

    static int BodyInt(string body, string prop, int def = 0)
    {
        var e = BodyProp(body, prop);
        if (e == null) return def;
        if (e.Value.ValueKind == JsonValueKind.Number && e.Value.TryGetInt32(out int n)) return n;
        if (e.Value.ValueKind == JsonValueKind.String && int.TryParse(e.Value.GetString(), out int s)) return s;
        return def;
    }

    static bool BodyBool(string body, string prop)
    {
        var e = BodyProp(body, prop);
        if (e == null) return false;
        if (e.Value.ValueKind == JsonValueKind.True) return true;
        if (e.Value.ValueKind == JsonValueKind.False) return false;
        return e.Value.ValueKind == JsonValueKind.String
            && bool.TryParse(e.Value.GetString(), out bool b) && b;
    }

    /// <summary>文章「列表用」摘要（不含正文）。去重成员卡片、导入书单都用它。
    /// ids 是我们自己从库里读出来的整数，拼进 IN 列表是安全的。</summary>
    static Dictionary<int, object> ItemBriefs(IEnumerable<int> ids)
    {
        var map = new Dictionary<int, object>();
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return map;
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.Id, i.Title, i.FeedId, f.Title, i.Link, i.PublishDate, i.Status, i.Version,
                   LENGTH(COALESCE(NULLIF(i.Content,''), i.Description, ''))
            FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Id IN (" + string.Join(",", list) + ")";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int id = r.GetInt32(0);
            map[id] = new
            {
                itemId = id,
                title = r.GetString(1),
                feedId = r.GetInt32(2),
                feed = r.IsDBNull(3) ? "" : r.GetString(3),
                link = r.IsDBNull(4) ? "" : r.GetString(4),
                published = r.IsDBNull(5) ? "" : r.GetString(5),
                status = r.GetString(6),
                version = r.GetInt32(7),
                length = r.IsDBNull(8) ? 0 : r.GetInt32(8)
            };
        }
        return map;
    }

    /// <summary>一篇文章的标题 + 原始正文（Content 空则回落到 Description）—— 与
    /// 去重算法、CLI diff 用的是同一个回落规则。</summary>
    static (string Title, string Body)? ItemBodyRaw(int itemId)
    {
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, COALESCE(NULLIF(Content,''), Description, '') FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
    }

    /// <summary>两段正文的**段落级**行 diff（先去 HTML 再按段切）。
    /// 用 NormalizeParagraphs 与去重算法同一套切分：显示的就是算法实际在比的东西。</summary>
    static (int Added, int Removed, List<object> Lines) DiffParagraphs(string a, string b)
    {
        string ta = string.Join("\n", NormalizeParagraphs(a));
        string tb = string.Join("\n", NormalizeParagraphs(b));
        var model = new DiffPlex.DiffBuilder.InlineDiffBuilder(new DiffPlex.Differ()).BuildDiffModel(ta, tb);
        int added = 0, removed = 0;
        var lines = new List<object>();
        foreach (var l in model.Lines)
        {
            string kind = l.Type.ToString();
            if (kind == "Inserted") added++;
            else if (kind == "Deleted") removed++;
            if (kind is "Unchanged" or "Inserted" or "Deleted" or "Modified")
                lines.Add(new { type = kind, text = l.Text ?? "" });
        }
        return (added, removed, lines);
    }

    // ══════════ 改稿追踪（跨源之外的那条轴：同一个源里作者改稿）══════════
    // 列表 = 「哪些文章有历史版本」。详情用既有的 /versions 与 /diff，不重复实现。
    static void HandleEdits(HttpListenerRequest req, HttpListenerResponse res)
    {
        int limit = QueryInt(req, "limit", 50, 1, 200);
        var rows = new List<object>();
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            // 版本链按 (FeedId, Guid) 分组 —— 与 ShowDiff 的归档范围、/versions 的查询一致。
            // 不带 FeedId 的话，两个源转载同一篇（Guid 相同）会被算成「这篇改过稿」。
            cmd.CommandText = @"
                WITH g AS (
                    SELECT FeedId, Guid, COUNT(*) AS VerCount, MIN(Version) AS MinVer, MAX(Version) AS MaxVer,
                           MAX(ArchivedAt) AS LastArch
                    FROM Items
                    WHERE Guid IS NOT NULL AND Guid <> ''
                    GROUP BY FeedId, Guid
                    HAVING COUNT(*) > 1
                )
                SELECT g.FeedId, g.Guid, g.VerCount, g.MinVer, g.MaxVer, g.LastArch,
                       (SELECT i2.Id FROM Items i2
                         WHERE i2.FeedId = g.FeedId AND i2.Guid = g.Guid AND i2.Status = 'active'
                         ORDER BY i2.Version DESC LIMIT 1)                                   AS ActiveId,
                       (SELECT i3.Id FROM Items i3
                         WHERE i3.FeedId = g.FeedId AND i3.Guid = g.Guid
                         ORDER BY i3.Version DESC, i3.Id DESC LIMIT 1)                       AS LatestId,
                       (SELECT i4.Title FROM Items i4
                         WHERE i4.FeedId = g.FeedId AND i4.Guid = g.Guid
                         ORDER BY i4.Version DESC, i4.Id DESC LIMIT 1)                       AS Title,
                       (SELECT f.Title FROM Feeds f WHERE f.Id = g.FeedId)                   AS FeedTitle
                FROM g
                ORDER BY g.LastArch DESC, g.MaxVer DESC
                LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long? activeId = r.IsDBNull(6) ? null : r.GetInt64(6);
                rows.Add(new
                {
                    itemId = activeId ?? r.GetInt64(7),
                    feedId = r.GetInt32(0),
                    feed = r.IsDBNull(9) ? "" : r.GetString(9),
                    title = r.GetString(8),
                    from = r.GetInt32(3),
                    to = r.GetInt32(4),
                    versions = r.GetInt32(2),
                    lastChangedAt = r.IsDBNull(5) ? "" : r.GetString(5),
                    stillActive = activeId != null
                });
            }
        }
        WriteJson(res, 200, new { success = true, data = new { count = rows.Count, items = rows } });
    }

    // ══════════ 跨源去重 ══════════
    // 「检测」与「处理」分开：检测只读（可反复跑），处理才有副作用，
    // 而且**只隐藏、不删除** —— 隐藏是可撤销的，删除不是。
    static List<object> DedupClusterPayload(List<DedupCluster> clusters)
    {
        var briefs = ItemBriefs(clusters.SelectMany(c => c.Members));
        var outp = new List<object>();
        foreach (var c in clusters)
        {
            outp.Add(new
            {
                id = c.RepresentativeId,
                representativeId = c.RepresentativeId,
                title = c.Title,
                source = c.Source,
                size = c.Size,
                minOverlap = c.MinOverlap,
                members = c.Members.Select(m => briefs.TryGetValue(m, out var b) ? b : (object)new { itemId = m }).ToList()
            });
        }
        return outp;
    }

    static void WriteDedupState(HttpListenerResponse res, int window, bool scanned)
    {
        var clusters = FindDuplicateClusters(webDbPath, window);
        var hidden = ListHiddenDedup(webDbPath)
            .Select(h => new { itemId = h.Id, title = h.Title, source = h.Source, key = h.Key })
            .ToList();
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                scanned,
                windowHours = window,
                threshold = LoadSettings().DedupThreshold,
                clusters = DedupClusterPayload(clusters),
                hidden
            }
        });
    }

    static void HandleDedupList(HttpListenerRequest req, HttpListenerResponse res)
        => WriteDedupState(res, QueryInt(req, "window", 48, 1, 24 * 30), scanned: false);

    // 扫描 = 同一份检测，只是明确告诉界面「这是一次主动扫描」。
    // 仍然加并发护栏：检测要把窗口内上万篇正文读进来跑段落比对，
    // 连点几下就是几倍的 CPU 与内存（同「同步」按钮的道理）。
    static async void HandleDedupScan(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body = await ReadBodyAsync(req);
        int window = BodyInt(body, "window", QueryInt(req, "window", 48, 1, 24 * 30));
        window = Math.Clamp(window, 1, 24 * 30);
        string key = "dedup:scan";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        try
        {
            ProgBegin("dedup");
            _progCurrent = Lang.T("扫描跨源重复…");
            WriteDedupState(res, window, scanned: true);
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "DEDUP_FAILED", message = ex.Message } });
        }
        finally { ProgEnd(); EndDownload(key); }
    }

    static async void HandleDedupHide(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "dedup")) return;
        string body = await ReadBodyAsync(req);
        int hiddenId = BodyInt(body, "hiddenId");
        int canonicalId = BodyInt(body, "canonicalId");
        string? err = HideAsDedup(webDbPath, hiddenId, canonicalId);
        if (err != null)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "DEDUP_INVALID", message = err } });
            return;
        }
        WriteJson(res, 200, new { success = true, data = new { hiddenId, canonicalId, ok = true } });
    }

    static async void HandleDedupHideCluster(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "dedup")) return;
        string body = await ReadBodyAsync(req);
        int repId = BodyInt(body, "representativeId");
        if (repId <= 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "representativeId required" } });
            return;
        }
        // 与 CLI `--dedup hide-cluster` 同一套判定：先找到簇，再以代表元为准逐个隐藏
        var cluster = FindDuplicateClusters(webDbPath, 48)
            .FirstOrDefault(c => c.RepresentativeId == repId || c.Members.Contains(repId));
        if (cluster == null)
        {
            WriteJson(res, 404, new { success = false, error = new { code = "CLUSTER_NOT_FOUND", message = "cluster not found (scan first?)" } });
            return;
        }
        int rep = cluster.Members.Contains(repId) ? repId : cluster.RepresentativeId;
        int hidden = 0;
        var fails = new List<string>();
        foreach (int m in cluster.Members)
        {
            if (m == rep) continue;
            string? err = HideAsDedup(webDbPath, m, rep);
            if (err == null) hidden++;
            else fails.Add(err);
        }
        WriteJson(res, 200, new { success = true, data = new { representative = rep, hidden, fails } });
    }

    static async void HandleDedupUndo(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "dedup")) return;
        string body = await ReadBodyAsync(req);
        string key = BodyString(body, "key").Trim();
        if (key.Length == 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "key required" } });
            return;
        }
        bool ok = UndoDedup(webDbPath, key);
        WriteJson(res, ok ? 200 : 404, new
        {
            success = ok,
            data = new { key, ok },
            error = ok ? null : new { code = "KEY_NOT_FOUND", message = "no such hidden rule" }
        });
    }

    // 并排比较：代表元 vs 某个成员。两篇的 Guid 不同（是转载，不是改稿），
    // 所以不能复用 /diff —— 那个比的是同一篇的两个版本。
    static void HandleDedupDiff(HttpListenerRequest req, HttpListenerResponse res)
    {
        int a = QueryInt(req, "a", 0, 0, int.MaxValue);
        int b = QueryInt(req, "b", 0, 0, int.MaxValue);
        if (a <= 0 || b <= 0 || a == b)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "a and b must be two different item ids" } });
            return;
        }
        var left = ItemBodyRaw(a);
        var right = ItemBodyRaw(b);
        if (left == null || right == null)
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "article not found" } });
            return;
        }
        var (added, removed, lines) = DiffParagraphs(left.Value.Body, right.Value.Body);
        double overlap = ParagraphOverlap(
            NormalizeParagraphs(left.Value.Body), NormalizeParagraphs(right.Value.Body));
        var briefs = ItemBriefs(new[] { a, b });
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                a = briefs.GetValueOrDefault(a),
                b = briefs.GetValueOrDefault(b),
                titleA = left.Value.Title,
                titleB = right.Value.Title,
                overlap = Math.Round(overlap * 100, 0),
                threshold = Math.Round(LoadSettings().DedupThreshold * 100, 0),
                added,
                removed,
                lines
            }
        });
    }

    // ══════════ 源规则（source_policy.json；createdBy 永远 user）══════════
    static readonly string[] PolicyActions = { "lower_frequency", "archive", "keep", "tag", "unsubscribe" };

    static void HandlePolicyList(HttpListenerResponse res)
    {
        var map = LoadSourcePolicy();
        var titles = new Dictionary<int, string>();
        var schedules = new Dictionary<int, string>();
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Title, Schedule FROM Feeds";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                titles[r.GetInt32(0)] = r.GetString(1);
                schedules[r.GetInt32(0)] = r.IsDBNull(2) ? "" : r.GetString(2);
            }
        }
        var rows = map.OrderBy(k => k.Key).Select(kv => (object)new
        {
            feedId = kv.Key,
            feed = titles.GetValueOrDefault(kv.Key, Lang.T("(feed deleted)")),
            action = kv.Value.Action,
            schedule = kv.Value.Schedule,
            tag = kv.Value.Tag,
            note = kv.Value.Note,
            createdBy = kv.Value.CreatedBy,
            updatedAt = kv.Value.UpdatedAt,
            currentSchedule = schedules.GetValueOrDefault(kv.Key, "")
        }).ToList();
        WriteJson(res, 200, new { success = true, data = new { count = rows.Count, actions = PolicyActions, policies = rows } });
    }

    static async void HandlePolicySet(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "policy")) return;
        string body = await ReadBodyAsync(req);
        int feedId = BodyInt(body, "feedId");
        string action = BodyString(body, "action").Trim().ToLowerInvariant();
        string schedule = BodyString(body, "schedule").Trim();
        string tag = BodyString(body, "tag").Trim().TrimStart('#');
        string note = BodyString(body, "note").Trim();

        if (feedId <= 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "feedId required" } });
            return;
        }
        if (!PolicyActions.Contains(action))
        {
            WriteJson(res, 400, new
            {
                success = false,
                error = new { code = "UNKNOWN_ACTION", message = Lang.T("未知动作: {0}", action), allowed = PolicyActions }
            });
            return;
        }
        // 与 CLI 的 `set` 一样：先确认这个源真的存在（用真实 Id，不是显示编号）
        if (!FeedExistsReal(feedId))
        {
            WriteJson(res, 404, new { success = false, error = new { code = "FEED_NOT_FOUND", message = Lang.T("Feed number not found") } });
            return;
        }

        // lower_frequency 的取值必须与 CLI 认的表达式一致 —— 这里先用**同一个** TryParseSchedule 校验，
        // 免得网页收下一个终端读不懂的频率，两个通道对同一份配置给出不同结果。
        if (action == "lower_frequency" && (schedule.Length == 0 || TryParseSchedule(schedule) == null))
        {
            WriteJson(res, 400, new
            {
                success = false,
                error = new { code = "BAD_SCHEDULE", message = Lang.T("Invalid schedule: {0}", schedule), hint = "30m / 1h / daily@10:00 / weekly@Mon 08:00" }
            });
            return;
        }

        var map = LoadSourcePolicy();
        map.TryGetValue(feedId, out var rule);
        rule ??= new SourcePolicyRule();
        rule.Action = action;
        rule.CreatedBy = "user";                         // AI 永不自动写规则：网页也只记 user
        rule.UpdatedAt = DateTime.Now.ToString("O");

        switch (action)
        {
            case "lower_frequency":
                // 复用 CLI 同一条落地路径（它会写 Feeds.Schedule 并记遥测）
                SetFeedSchedule(GetDisplayNum(feedId, webDbPath).ToString(), schedule, webDbPath);
                rule.Schedule = schedule.ToLowerInvariant();
                if (note.Length > 0) rule.Note = note;
                break;
            case "archive":
                // 「归档」= 给源标题加时间戳后缀（与 CLI/TUI 的 A 键完全同一个动作）
                AddTimestampForRealId(feedId, webDbPath);
                if (note.Length > 0) rule.Note = note;
                break;
            case "tag":
                if (tag.Length == 0)
                {
                    WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "tag required" } });
                    return;
                }
                rule.Tag = tag;
                rule.Note = note;
                break;
            default:                                      // keep / unsubscribe
                rule.Note = note;
                break;
        }

        map[feedId] = rule;
        SaveSourcePolicy(map);
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                feedId,
                action = rule.Action,
                schedule = rule.Schedule,
                tag = rule.Tag,
                note = rule.Note,
                createdBy = rule.CreatedBy,
                updatedAt = rule.UpdatedAt
            }
        });
    }

    static void HandlePolicyRemove(HttpListenerResponse res, int feedId)
    {
        if (!WebWriteAllowed(res, "policy")) return;
        var map = LoadSourcePolicy();
        bool had = map.Remove(feedId);
        SaveSourcePolicy(map);
        WriteJson(res, had ? 200 : 404, new
        {
            success = had,
            data = new { feedId, ok = had },
            error = had ? null : new { code = "POLICY_NOT_FOUND", message = "this feed has no rule" }
        });
    }

    static bool FeedExistsReal(int realId)
    {
        try
        {
            using var conn = OpenDb(webDbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", realId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
        }
        catch { return false; }
    }

    // ══════════ 本地导入 / 电子书阅读 ══════════
    // 导入 = 把文件**复制进数据目录**再抽正文（不是原地引用）：原文件你随时可以删，
    // 库里的那份不受影响。Web 走的是「字节 → 临时文件 → ImportFileCore」，
    // 与 CLI `sip --import` 完全同一条实现。
    //
    // 电子书：**没有章节模型**。EPUB/DOCX/MOBI/TXT/MD 抽出来的是一整篇 HTML/Markdown；
    // PDF 则从不解析文本（ReadPdfFile 只放一句占位），真正的"第 N 页"来自
    // RenderPdfPages 的逐页栅格化。所以这里的阅读模型就是这两条，不假装有章节。

    static void HandleImportList(HttpListenerResponse res)
    {
        var rows = new List<object>();
        long feedId = 0;
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var fid = conn.CreateCommand();
            // 「本地导入」源只有一个，用 FeedUrl 标记（Feeds 表没有别的标记列）
            fid.CommandText = "SELECT Id FROM Feeds WHERE FeedUrl = 'local://import' LIMIT 1";
            object? f = fid.ExecuteScalar();
            if (f != null) feedId = Convert.ToInt64(f);
            if (feedId == 0)
            {
                WriteJson(res, 200, new { success = true, data = new { feedId = 0, items = rows } });
                return;
            }
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Title, Link, Description, PublishDate, PageCount, LENGTH(Content)
                FROM Items WHERE FeedId = @f ORDER BY Id DESC";
            cmd.Parameters.AddWithValue("@f", feedId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                string link = r.IsDBNull(2) ? "" : r.GetString(2);
                // 类型与体积都从**落地的那份文件**读：库里只存路径，不存冗余元数据
                string ext = "";
                try { ext = Path.GetExtension(link).TrimStart('.').ToLowerInvariant(); } catch { }
                long size = 0;
                try { if (link.Length > 0 && File.Exists(link)) size = new FileInfo(link).Length; } catch { }
                rows.Add(new
                {
                    itemId = id,
                    title = r.GetString(1),
                    type = ext,
                    size,
                    description = r.IsDBNull(3) ? "" : r.GetString(3),
                    importedAt = r.IsDBNull(4) ? "" : r.GetString(4),
                    pages = r.IsDBNull(5) ? (int?)null : r.GetInt32(5),
                    chars = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    isPdf = ext == "pdf"
                });
            }
        }
        WriteJson(res, 200, new { success = true, data = new { feedId, items = rows } });
    }

    // 上传：原始字节 + `?name=<文件名>`。用原始 body 而不是 multipart ——
    // multipart 需要解析边界，多写一段易错的解析器换不来任何东西。
    static async void HandleImportUpload(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "import")) return;

        string name = (QueryParam(req, "name") ?? "").Trim();
        // 只取文件名，不接受任何路径成分：这里**不做路径拼接**，Directory 穿越就无从谈起
        try { name = Path.GetFileName(name); } catch { name = ""; }
        if (name.Length == 0) name = "upload.txt";
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".txt" or ".md" or ".markdown" or ".pdf" or ".epub" or ".mobi" or ".docx"))
        {
            WriteJson(res, 400, new
            {
                success = false,
                error = new { code = "UNSUPPORTED_FORMAT", message = Lang.T("Unsupported file type: {0}. Supported: txt, md, pdf, epub, mobi, docx", ext) }
            });
            return;
        }
        const long maxBytes = 512L * 1024 * 1024;
        if (req.ContentLength64 > maxBytes)
        {
            WriteJson(res, 413, new { success = false, error = new { code = "TOO_LARGE", message = "文件过大（上限 512 MB）" } });
            return;
        }

        string key = "import";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        string? tmp = null;
        try
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "sip-web-upload");
            Directory.CreateDirectory(tmpDir);
            tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ext);
            await using (var fs = File.Create(tmp))
                await req.InputStream.CopyToAsync(fs);

            ProgBegin("import");
            _progCurrent = name;
            var r = ImportFileCore(tmp, Path.GetFileNameWithoutExtension(name), webDbPath);
            if (!r.Ok)
            {
                WriteJson(res, 400, new { success = false, error = new { code = r.Code, message = r.Message } });
                return;
            }
            WriteJson(res, 200, new
            {
                success = true,
                data = new
                {
                    itemId = r.ItemId,
                    title = r.Title,
                    file = Path.GetFileName(r.DestPath),
                    type = ext.TrimStart('.'),
                    bytes = new FileInfo(tmp).Length
                }
            });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "IMPORT_ERROR", message = ex.Message } });
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
            ProgEnd();
            EndDownload(key);
        }
    }

    /// <summary>本地文件导入项的元信息（含 PDF 页数）。找不到 / 不属于导入源都返回 null。</summary>
    static (long FeedId, string Title, string Link, string Ext, int? Pages)? ImportItemInfo(long itemId)
    {
        using var conn = OpenDb(webDbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.FeedId, i.Title, i.Link, i.PageCount
            FROM Items i JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Id = @id AND f.FeedUrl = 'local://import'";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string link = r.IsDBNull(2) ? "" : r.GetString(2);
        string ext = "";
        try { ext = Path.GetExtension(link).TrimStart('.').ToLowerInvariant(); } catch { }
        return (r.GetInt64(0), r.GetString(1), link, ext, r.IsDBNull(3) ? null : r.GetInt32(3));
    }

    static void HandleImportDetail(HttpListenerResponse res, int itemId)
    {
        var info = ImportItemInfo(itemId);
        if (info == null)
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "imported item not found" } });
            return;
        }
        var v = info.Value;
        // PDF 的页数：以库里的 PageCount 为准，缺失时现场问一次 pdfium
        int? pages = v.Pages;
        if (v.Ext == "pdf" && pages == null)
        {
            try { pages = GetPdfPageCount(v.Link); } catch { }
        }
        long size = 0;
        try { if (File.Exists(v.Link)) size = new FileInfo(v.Link).Length; } catch { }
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                itemId,
                title = v.Title,
                feedId = v.FeedId,
                type = v.Ext,
                size,
                pages,
                isPdf = v.Ext == "pdf",
                file = Path.GetFileName(v.Link)
            }
        });
    }

    // 正文：与 /api/articles/{id} 一样走净化器，唯一区别是**把 file:// 图片改写成
    // 我们自己的资产接口**（否则 EPUB/DOCX 里的图会被净化器整个丢掉 —— 相对路径/
    // file 协议一律不放行）。
    static void HandleImportText(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        var info = ImportItemInfo(itemId);
        if (info == null)
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "imported item not found" } });
            return;
        }
        var v = info.Value;
        string content = "";
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(NULLIF(Content,''), Description, '') FROM Items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", itemId);
            content = cmd.ExecuteScalar()?.ToString() ?? "";
        }
        if (v.Ext == "pdf")
        {
            // PDF 从来就没有文本层可读（ReadPdfFile 只写一句占位）。
            // 与其把占位句当正文给出来，不如直接说"这一份要按页看"。
            WriteJson(res, 200, new
            {
                success = true,
                data = new
                {
                    itemId,
                    title = v.Title,
                    type = v.Ext,
                    isPdf = true,
                    pages = v.Pages,
                    bodyHtml = "",
                    note = Lang.T("PDF 按页栅格化阅读，没有文本层")
                }
            });
            return;
        }
        string html = LooksLikeHtml(content) ? content : MarkdownToHtml(content);
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                itemId,
                title = v.Title,
                type = v.Ext,
                isPdf = false,
                pages = (int?)null,
                bodyHtml = ToSafeBodyHtml(html, importItemId: itemId)
            }
        });
    }

    // 资产（EPUB/DOCX 抽出来的图）：**必须限定在 ImportedDir() 之内**。
    // 没有这道围栏，正文里一个 src 就能让浏览器读走机器上任意文件。
    static void HandleImportAsset(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        string raw = (QueryParam(req, "path") ?? "").Trim();
        if (raw.Length == 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "path required" } });
            return;
        }
        string? full = LocalPathOf(raw);
        string root;
        try { root = Path.GetFullPath(ImportedDir()); } catch { root = ""; }
        if (full == null || root.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(res, 403, new { success = false, error = new { code = "FORBIDDEN", message = "asset path outside the import folder" } });
            return;
        }
        if (!File.Exists(full))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        try
        {
            byte[] buf = File.ReadAllBytes(full);
            res.StatusCode = 200;
            res.ContentType = ContentTypeForPath(full);
            res.ContentLength64 = buf.Length;
            res.OutputStream.Write(buf, 0, buf.Length);
            res.OutputStream.Close();
        }
        catch
        {
            res.StatusCode = 500;
            res.Close();
        }
    }

    // PDF 第 N 页（1 起）：复用 CLI 的 RenderPdfPages（150 DPI、带注释/表单，
    // 已渲染过的页会命中磁盘缓存，翻页不必重渲染）。
    static void HandleImportPage(HttpListenerResponse res, int itemId, int pageNo)
    {
        var info = ImportItemInfo(itemId);
        if (info == null || info.Value.Ext != "pdf")
        {
            WriteJson(res, 404, new { success = false, error = new { code = "NOT_A_PDF", message = "item is not an imported PDF" } });
            return;
        }
        string pdf = info.Value.Link;
        int? pages = info.Value.Pages;
        if (pages == null)
        {
            try { pages = GetPdfPageCount(pdf); } catch { }
        }
        if (pages != null && (pageNo < 1 || pageNo > pages.Value))
        {
            WriteJson(res, 404, new { success = false, error = new { code = "PAGE_NOT_FOUND", message = $"page {pageNo} of {pages}" } });
            return;
        }
        try
        {
            var rendered = RenderPdfPages(pdf, pageNo.ToString());
            if (rendered.Count == 0 || !File.Exists(rendered[0]))
            {
                WriteJson(res, 500, new { success = false, error = new { code = "RENDER_FAILED", message = "pdf page render failed" } });
                return;
            }
            byte[] buf = File.ReadAllBytes(rendered[0]);
            res.StatusCode = 200;
            res.ContentType = "image/png";
            res.Headers["Cache-Control"] = "private, max-age=3600";
            res.ContentLength64 = buf.Length;
            res.OutputStream.Write(buf, 0, buf.Length);
            res.OutputStream.Close();
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "RENDER_FAILED", message = ex.Message } });
        }
    }

    static void HandleImportDelete(HttpListenerResponse res, int itemId)
    {
        if (!WebWriteAllowed(res, "import")) return;
        var (ok, code, message) = ImportItemDelete(itemId, webDbPath);
        if (!ok)
        {
            WriteJson(res, code == "ITEM_NOT_FOUND" ? 404 : 400, new { success = false, error = new { code, message } });
            return;
        }
        WriteJson(res, 200, new { success = true, data = new { itemId, deleted = true } });
    }

    // 删除一个导入项（CLI 的 `--import-rm` 与 Web 的 DELETE 共用）：
    // 删库里的行 + 删落地的那份文件；assets/<guid>/ 不删（可能被别的项共享，
    // 且它只是一堆图，留着比误删安全）。
    static (bool Ok, string Code, string Message) ImportItemDelete(long realId, string dbPath)
    {
        string link = "";
        bool imported;
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT i.Link, f.FeedUrl FROM Items i JOIN Feeds f ON i.FeedId = f.Id WHERE i.Id = @id";
            c.Parameters.AddWithValue("@id", realId);
            using var r = c.ExecuteReader();
            if (!r.Read()) return (false, "ITEM_NOT_FOUND", Lang.T("Article {0} not found", realId));
            link = r.IsDBNull(0) ? "" : r.GetString(0);
            imported = (r.IsDBNull(1) ? "" : r.GetString(1)) == "local://import";
        }
        if (!imported) return (false, "NOT_IMPORTED", Lang.T("Article {0} is not an imported file", realId));

        if (link.Length > 0 && File.Exists(link))
        {
            try { File.Delete(link); } catch { }
        }
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", realId);
            cmd.ExecuteNonQuery();
        }
        return (true, "", "");
    }

    // ══════════ 导入原文件：浏览器直接打开 + 临时链接 ══════════
    // 为什么需要它：PDF 在网页里是**逐页栅格化**看的（没有文本层、也不能选字、不能搜）。
    // 但浏览器自带的 PDF 阅读器（或系统的 PDF 程序）比我们做得好 —— 那就别挡着，
    // 把原文件**原样**递出去，用 `Content-Disposition: inline` 让浏览器自己决定怎么渲染。
    //
    // 「临时链接」解决的另一个问题是**会话**：网页的钥匙是"一个浏览器一把、活到进程结束"，
    // 所以 /api/imports/{id}/file 直接从页面里点开没问题，但把链接贴到别的标签页、
    // 别的阅读器、手机上看就会 401。于是给一个**进程内、只对一份文件、10 分钟过期**的令牌。
    // 它比会话 cookie 窄得多：只读、只一份、会过期、重启即失效。

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long ItemId, DateTime ExpiresUtc)> webFileTickets = new(StringComparer.Ordinal);
    static readonly TimeSpan WebFileTicketTtl = TimeSpan.FromMinutes(10);

    static string IssueWebFileTicket(long itemId)
    {
        // 顺手清过期项：这个表只会因为"点了多少次链接"而增长，不清就是慢性泄漏
        DateTime now = DateTime.UtcNow;
        foreach (var kv in webFileTickets)
            if (kv.Value.ExpiresUtc <= now) webFileTickets.TryRemove(kv.Key, out _);

        var buf = new byte[16];
        RandomNumberGenerator.Fill(buf);
        string ticket = Convert.ToHexString(buf).ToLowerInvariant();
        webFileTickets[ticket] = (itemId, now + WebFileTicketTtl);
        return ticket;
    }

    /// <summary>令牌必须**同时**对得上文件和时效：令牌本身不带文件身份，
    /// 所以"拿 A 的令牌去读 B"也必须被拒（itemId 比对就是干这个的）。</summary>
    static bool WebFileTicketOk(string? ticket, long itemId)
        => !string.IsNullOrEmpty(ticket)
           && webFileTickets.TryGetValue(ticket, out var v)
           && v.ItemId == itemId
           && v.ExpiresUtc > DateTime.UtcNow;

    static void HandleImportLink(HttpListenerResponse res, int itemId)
    {
        var info = ImportItemInfo(itemId);
        if (info == null)
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "imported item not found" } });
            return;
        }
        string ticket = IssueWebFileTicket(itemId);
        // 相对路径 + 令牌：前端拼成绝对地址（它知道当前 origin），这样换绑定/端口也不写死
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                itemId,
                url = $"/api/imports/{itemId}/file?k={ticket}",
                expiresInSeconds = (int)WebFileTicketTtl.TotalSeconds,
                type = info.Value.Ext,
                file = Path.GetFileName(info.Value.Link),
                inline = info.Value.Ext is "pdf"
            }
        });
    }

    static void HandleImportFile(HttpListenerRequest req, HttpListenerResponse res, int itemId)
    {
        // 两条凭据任选其一：临时令牌（可贴到别处）或浏览器会话（页面里直接点开）
        if (!WebFileTicketOk(QueryParam(req, "k"), itemId) && !WebRequestIsAuthenticated(req))
        {
            WriteJson(res, 401, new { success = false, error = new { code = "UNAUTHORIZED", message = "ticket expired or missing, and no session" } });
            return;
        }
        var info = ImportItemInfo(itemId);
        if (info == null)
        {
            WriteJson(res, 404, new { success = false, error = new { code = "ITEM_NOT_FOUND", message = "imported item not found" } });
            return;
        }
        string path = info.Value.Link;
        if (path.Length == 0 || !File.Exists(path))
        {
            WriteJson(res, 404, new { success = false, error = new { code = "FILE_MISSING", message = "the stored copy is gone" } });
            return;
        }
        WriteFileInline(req, res, path, Path.GetFileName(path));
    }

    /// <summary>原样递出本地文件，支持单段 Range —— 浏览器的 PDF 阅读器会按需拉取区间，
    /// 几十 MB 的扫描件不必先整份下载完才能翻第一页。</summary>
    static void WriteFileInline(HttpListenerRequest req, HttpListenerResponse res, string path, string fileName)
    {
        var fi = new FileInfo(path);
        long total = fi.Length;
        long start = 0, end = total > 0 ? total - 1 : 0;
        bool partial = false;

        string? range = req.Headers["Range"];
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && total > 0)
        {
            string spec = range[6..].Split(',')[0].Trim();     // 只认第一段：多段 Range 的响应体格式复杂，浏览器很少真用
            int dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                string a = spec[..dash], b = spec[(dash + 1)..];
                bool okA = a.Length == 0 || long.TryParse(a, out _);
                if (okA)
                {
                    if (a.Length == 0 && long.TryParse(b, out long suffix)) { start = Math.Max(0, total - suffix); end = total - 1; }
                    else if (long.TryParse(a, out long s))
                    {
                        start = Math.Max(0, s);
                        end = b.Length > 0 && long.TryParse(b, out long e) ? Math.Min(total - 1, e) : total - 1;
                    }
                    if (start > end || start >= total)
                    {
                        res.StatusCode = 416;
                        res.Headers["Content-Range"] = $"bytes */{total}";
                        res.Close();
                        return;
                    }
                    partial = true;
                }
            }
        }

        res.StatusCode = partial ? 206 : 200;
        res.ContentType = ContentTypeForPath(path);
        res.Headers["Accept-Ranges"] = "bytes";
        // inline：**不要**触发下载，交给浏览器自己决定（PDF 用它自带的阅读器）
        string ascii = new string(fileName.Where(c => c < 128 && c != '"' && c != '\\').ToArray());
        if (string.IsNullOrWhiteSpace(ascii)) ascii = "imported-file";
        res.Headers["Content-Disposition"] =
            $"inline; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        if (partial) res.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
        res.ContentLength64 = end - start + 1;

        using var fs = File.OpenRead(path);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[64 * 1024];
        long remain = end - start + 1;
        while (remain > 0)
        {
            int want = (int)Math.Min(buf.Length, remain);
            int got = fs.Read(buf, 0, want);
            if (got <= 0) break;
            res.OutputStream.Write(buf, 0, got);
            remain -= got;
        }
        res.OutputStream.Close();
    }

    /// <summary>把 <c>file:///C:/x/y.png</c> 或裸路径变成绝对本地路径；不是本地路径则 null。</summary>
    static string? LocalPathOf(string raw)
    {
        try
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
                s = u.LocalPath;
            }
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(s);
        }
        catch { return null; }
    }

    static string ContentTypeForPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            // ⚠️ .pdf 必须是 application/pdf：给成 octet-stream 的话，浏览器会**下载**它，
            // 而不是用它自带的阅读器打开 —— 而"用原生阅读器打开"正是这个接口存在的理由。
            ".pdf" => "application/pdf",
            ".epub" => "application/epub+zip",
            ".mobi" => "application/x-mobipocket-ebook",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt" or ".md" or ".markdown" => "text/plain; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".css" => "text/css; charset=utf-8",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream"
        };
    }

    // ══════════ 向量索引 ══════════
    // 为什么不用 IndexArticlesCli / ReindexCli：它们都会 Console.ReadLine() 问编号与确认，
    // 在服务器里读到的是 EOF → 直接当作取消。这里照 TUI 的做法重写循环
    // （EnsureModel → SafeEmbed → SaveVector → EmbedItemChunks），算的是同一件事。
    static void HandleIndexStatus(HttpListenerResponse res)
    {
        int vectors = 0, chunks = 0, active = 0;
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            vectors = ScalarInt(conn, "SELECT COUNT(*) FROM Vectors");
            chunks = ScalarInt(conn, "SELECT COUNT(*) FROM VectorsChunks");
            active = ScalarInt(conn, "SELECT COUNT(*) FROM Items WHERE Status = 'active'");
        }
        var cfg = LoadConfig(webDbPath);
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                // 「配置过了吗」的判据与 TUI 一致：ai_config.json 是否存在。
                // 没有这个文件就索引等于白跑（SafeEmbed 会一个个超时）。
                configured = File.Exists(ConfigPath(webDbPath)),
                provider = cfg.Embedding.Provider,
                model = cfg.Embedding.Model,
                dimensions = cfg.Embedding.Dimensions,
                endpoint = cfg.Embedding.ApiEndpoint,
                searchThreshold = cfg.Embedding.SearchThreshold,
                currentModelId = CurrentEmbeddingModelId(webDbPath),
                vectors,
                chunks,
                active,
                progress = new { active = _progKind.Length > 0, kind = _progKind, done = _progDone, total = _progTotal, current = _progCurrent }
            }
        });
    }

    static int ScalarInt(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    static async void HandleIndexRun(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "index")) return;
        string body = await ReadBodyAsync(req);
        bool reindex = BodyBool(body, "reindex");

        if (!File.Exists(ConfigPath(webDbPath)))
        {
            WriteJson(res, 409, new
            {
                success = false,
                error = new
                {
                    code = "AI_NOT_CONFIGURED",
                    message = Lang.T("AI is not configured yet."),
                    hint = "在真实终端运行 sip --init（API Key 只从终端输入，不进网页）"
                }
            });
            return;
        }

        string key = "index";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        try
        {
            var cfg = LoadConfig(webDbPath);
            var targets = new List<(int Id, int FeedId, string Title)>();
            using (var conn = OpenDb(webDbPath))
            {
                conn.Open();
                if (reindex)
                {
                    using var del1 = conn.CreateCommand();
                    del1.CommandText = "DELETE FROM Vectors";
                    del1.ExecuteNonQuery();
                    using var del2 = conn.CreateCommand();
                    del2.CommandText = "DELETE FROM VectorsChunks";
                    del2.ExecuteNonQuery();
                }
                var cmd = conn.CreateCommand();
                cmd.CommandText = reindex
                    ? "SELECT Id, FeedId, Title FROM Items WHERE Status = 'active'"
                    : @"SELECT Id, FeedId, Title FROM Items
                        WHERE Status = 'active'
                          AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = Items.Id)";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    targets.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
            }

            int ok = 0, fail = 0;
            ProgBegin("index");
            _progTotal = targets.Count;
            int modelId = EnsureModel(webDbPath, cfg.Embedding);
            foreach (var t in targets)
            {
                _progDone = ok + fail;
                _progCurrent = t.Title;
                var vec = await SafeEmbed(t.Title, cfg, json: false, articleId: t.Id, sourceId: t.FeedId);
                if (vec == null) { fail++; continue; }
                // 维度变了（换了嵌入模型）就跟着改配置 —— 与 CLI/TUI 的自动纠正同一行为
                if (vec.Length != cfg.Embedding.Dimensions)
                {
                    cfg.Embedding.Dimensions = vec.Length;
                    SaveConfig(webDbPath, cfg);
                }
                SaveVector(webDbPath, t.FeedId, t.Id, modelId, vec);
                await EmbedItemChunks(webDbPath, t.FeedId, t.Id, modelId, cfg);
                ok++;
            }

            // 收尾：与 CLI 一致，补全文 sidecar 与分块向量（失败不影响主结果）
            int sidecars = 0, backfilled = 0;
            try
            {
                var pairs = targets.Select(t => (t.Id, t.FeedId)).ToList();
                sidecars = BackfillFulltextSidecars(webDbPath, pairs);
                backfilled = await BackfillChunks(webDbPath, targets.Select(t => t.FeedId).Distinct().ToList(), modelId, cfg);
            }
            catch { }

            WriteJson(res, 200, new
            {
                success = true,
                data = new { reindex, total = targets.Count, ok, fail, sidecars, backfilled, dimensions = cfg.Embedding.Dimensions }
            });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "INDEX_FAILED", message = ex.Message } });
        }
        finally { ProgEnd(); EndDownload(key); }
    }

    // ══════════ 治理面：孟思琳挡位 / Agent 门 ══════════
    static void HandleSimonStatus(HttpListenerResponse res)
    {
        var events = SimonLoadEvents();
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                name = "孟思琳(simon)",
                level = CurrentSimonLevel(),
                canDisable = false,                  // 默认开启、无法关闭，只能调挡位
                agentGate = AgentModeOn(),
                // 降档为什么不在网页做：见 HandleSimonLevel
                loosenHint = "sip simon level <1|2|3>（真实终端 + Web 口令）",
                events = events.Take(20).Select(e => new { ts = e.Ts, type = e.Type, level = e.Level, detail = e.Detail }).ToList()
            }
        });
    }

    static async void HandleSimonLevel(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body = await ReadBodyAsync(req);
        int level = BodyInt(body, "level", 0);
        if (level is < 1 or > 3)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "level must be 1, 2 or 3" } });
            return;
        }
        int cur = CurrentSimonLevel();
        if (level < cur)
        {
            // **降档 = 放宽保护**，按设计走人工通道：真实交互终端 + Web 口令。
            // 浏览器不是那个通道 —— 「口令只在终端里输入」正是这条门的意义所在；
            // 允许网页降档，等于任何拿到会话 cookie 的程序都能把保护关掉。
            // 升档（收紧）反过来任意通道放行：Agent 发现异常时要能立刻收紧。
            WriteJson(res, 403, new
            {
                success = false,
                error = new
                {
                    code = "SIMON_LOOSEN_REQUIRES_TERMINAL",
                    level = cur,
                    message = Lang.T("Lowering the Simon level needs a real terminal and the Web password."),
                    hint = $"sip simon level {level}"
                }
            });
            return;
        }
        if (level == cur)
        {
            WriteJson(res, 200, new { success = true, data = new { level = cur, previous = cur, changed = false } });
            return;
        }
        if (!SimonLevelSet(level))
        {
            WriteJson(res, 500, new
            {
                success = false,
                error = new { code = "LEVEL_SAVE_FAILED", level = cur, message = Lang.T("The OS credential store rejected the write; the level was NOT changed.") }
            });
            return;
        }
        SimonRecord("level_change", $"web {cur} → {level}", level);
        WriteJson(res, 200, new { success = true, data = new { level = CurrentSimonLevel(), previous = cur, changed = true } });
    }

    // ══════════ 遥测（默认关；开关与导出都在本机）══════════
    static void HandleTelemetryStatus(HttpListenerResponse res)
    {
        var stats = TelemetryService.Stats();
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                enabled = TelemetryService.IsEnabled,
                consent = TelemetryService.Consent,
                events = stats.Count,
                first = stats.First,
                last = stats.Last,
                db = Path.Combine(dataDir, "telemetry.db")
            }
        });
    }

    static async void HandleTelemetrySet(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "telemetry")) return;
        string body = await ReadBodyAsync(req);
        bool on = BodyBool(body, "enabled");
        TelemetryService.SetConsent(on ? "enabled" : "disabled");
        WriteJson(res, 200, new
        {
            success = true,
            data = new { enabled = TelemetryService.IsEnabled, consent = TelemetryService.Consent }
        });
    }

    // 导出 = 把本机记录原样交给你（换机器、备份、或者干脆自己看）。
    // 只读，不需要挡位放行；文件只经本机 HTTP 回到你自己的浏览器。
    static void HandleTelemetryExport(HttpListenerResponse res)
    {
        var events = TelemetryService.AllEvents();
        var payload = JsonSerializer.Serialize(new
        {
            exportedAt = DateTime.Now.ToString("O"),
            events = events.Select(e => new
            {
                id = e.Id,
                timestamp = e.Timestamp,
                sessionId = e.SessionId,
                type = e.Type,
                articleId = e.ArticleId,
                sourceId = e.SourceId,
                versionId = e.VersionId,
                surface = e.Surface,
                position = e.Position,
                data = e.DataJson
            })
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        WriteDownload(res, "application/json; charset=utf-8", "sip-telemetry.json", payload);
    }

    // ══════════ 配置总览（只读）══════════
    // 只读：**改配置的路仍然只在终端**（sip_settings.json 手改、sip webpass、
    // sip aikey、sip --init、sip simon）。网页给出的事实要够用，够用来让人
    // 知道下一步该敲哪条命令 —— 而不是把危险开关搬进浏览器。
    static void HandleConfig(HttpListenerResponse res)
    {
        var st = LoadSettings();
        var cfg = LoadConfig(webDbPath);
        string host = string.IsNullOrWhiteSpace(st.WebHost) ? "127.0.0.1" : st.WebHost;
        int feeds = 0, items = 0, likes = 0, fulltextFiles = 0;
        using (var conn = OpenDb(webDbPath))
        {
            conn.Open();
            feeds = ScalarInt(conn, "SELECT COUNT(*) FROM Feeds");
            items = ScalarInt(conn, "SELECT COUNT(*) FROM Items");
        }
        try
        {
            likes = LoadSignals().Count;
            if (Directory.Exists(FulltextDir()))
                fulltextFiles = Directory.GetFiles(FulltextDir(), "*.md").Length;
        }
        catch { }

        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?",
                dataDir,
                dbPath = webDbPath,
                web = new
                {
                    host,
                    port = st.WebPort,
                    passwordSet = WebPasswordIsSet(),
                    networkReachable = !IsLoopbackHost(host),
                    sessionScope = "process"
                },
                ai = new
                {
                    configured = File.Exists(ConfigPath(webDbPath)),
                    configFile = ConfigPath(webDbPath),
                    embedding = new
                    {
                        provider = cfg.Embedding.Provider,
                        model = cfg.Embedding.Model,
                        dimensions = cfg.Embedding.Dimensions,
                        endpoint = cfg.Embedding.ApiEndpoint,
                        searchThreshold = cfg.Embedding.SearchThreshold,
                        keySet = AiKeyGet(embedding: true) != null
                    },
                    llm = new
                    {
                        provider = cfg.Llm.Provider,
                        model = cfg.Llm.Model,
                        endpoint = cfg.Llm.ApiEndpoint,
                        keySet = AiKeyGet(embedding: false) != null
                    },
                    allowPrivateNet = cfg.AllowPrivateNet
                },
                simon = CurrentSimonLevel(),
                agentGate = AgentModeOn(),
                telemetry = new { enabled = TelemetryService.IsEnabled, consent = TelemetryService.Consent },
                insights = new { interval = st.InsightsInterval, lastAt = st.LastInsightsAt },
                thresholds = new
                {
                    dedup = st.DedupThreshold,
                    dedupSemantic = st.DedupSemanticThreshold,
                    changeGradePolish = st.ChangeGradePolish,
                    changeGradeReverse = st.ChangeGradeReverse,
                    groupMatch = st.GroupMatchThreshold,
                    floodPerDay = st.FloodThresholdPerDay
                },
                counts = new { feeds, items, likes, fulltext = fulltextFiles }
            }
        });
    }

    static async void HandleInsightsInterval(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "insights-interval")) return;
        string body = await ReadBodyAsync(req);
        string raw = BodyString(body, "interval").Trim();
        if (raw.Length == 0) raw = "off";
        // 与 CLI 同一个解析器：网页收下的值，终端必须也认
        bool valid = raw.Equals("off", StringComparison.OrdinalIgnoreCase) || TryParseInsightsInterval(raw) != null;
        if (!valid)
        {
            WriteJson(res, 400, new
            {
                success = false,
                error = new { code = "BAD_INTERVAL", message = Lang.T("无效间隔，应为 7d / 30d / off 之一"), value = raw }
            });
            return;
        }
        var s = LoadSettings();
        s.InsightsInterval = raw.ToLowerInvariant();
        SaveSettings(s);
        WriteJson(res, 200, new { success = true, data = new { interval = s.InsightsInterval } });
    }

    // ══════════ 批量摘要（等价于 sip --summary-all，但不会卡在 stdin 上）══════════
    static async void HandleSummaryAll(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "summaries")) return;

        if (!File.Exists(ConfigPath(webDbPath)) || AiKeyGet(embedding: false) == null)
        {
            WriteJson(res, 409, new
            {
                success = false,
                error = new
                {
                    code = "AI_NOT_CONFIGURED",
                    message = Lang.T("AI is not configured yet."),
                    hint = "在真实终端运行 sip --init，或用 sip aikey set 存 Key（只从终端输入）"
                }
            });
            return;
        }

        string key = "summaries";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        try
        {
            var todo = new List<(int Id, string Title)>();
            using (var conn = OpenDb(webDbPath))
            {
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT Id, Title FROM Items
                                    WHERE Status = 'active' AND (Summary IS NULL OR Summary = '')";
                using var r = cmd.ExecuteReader();
                while (r.Read()) todo.Add((r.GetInt32(0), r.GetString(1)));
            }
            if (todo.Count == 0)
            {
                WriteJson(res, 200, new { success = true, data = new { total = 0, ok = 0, fail = 0, message = Lang.T("All active articles already have summaries") } });
                return;
            }

            int ok = 0, fail = 0;
            ProgBegin("summaries");
            _progTotal = todo.Count;
            foreach (var t in todo)
            {
                _progCurrent = t.Title;
                try
                {
                    // quiet:true —— 别把每篇的进度写进服务器终端；json:false —— 结果我们自己序列化
                    var (good, _) = await SummarizeItem(webDbPath, t.Id, json: false, quiet: true);
                    if (good) ok++; else fail++;
                }
                catch { fail++; }
                _progDone = ok + fail;
            }
            WriteJson(res, 200, new { success = true, data = new { total = todo.Count, ok, fail } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "SUMMARY_FAILED", message = ex.Message } });
        }
        finally { ProgEnd(); EndDownload(key); }
    }

    // ══════════ 全文缓存清理（sip --purge-fulltext）══════════
    static async void HandlePurgeFulltext(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "purge-fulltext")) return;
        string body = await ReadBodyAsync(req);
        int itemId = BodyInt(body, "itemId");
        try
        {
            if (itemId > 0)
            {
                string p = FulltextPath(itemId);
                bool had = File.Exists(p);
                if (had) File.Delete(p);
                RemoveFulltextVecs(new List<int> { itemId });
                WriteJson(res, 200, new { success = true, data = new { itemId, cleared = had } });
                return;
            }
            string dir = FulltextDir();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            WriteJson(res, 200, new { success = true, data = new { itemId = 0, cleared = true, scope = "all" } });
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { success = false, error = new { code = "PURGE_FAILED", message = ex.Message } });
        }
    }

    // ══════════ 推荐源（sip --onboarding）══════════
    static void HandleOnboardingList(HttpListenerResponse res)
    {
        var t = LoadTemplates();
        WriteJson(res, 200, new
        {
            success = true,
            data = new
            {
                categories = t.Select(kv => new
                {
                    category = kv.Key,
                    sources = kv.Value.Select(x => new { name = x.Name, url = x.Url }).ToList()
                }).ToList()
            }
        });
    }

    static async void HandleOnboardingAdd(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!WebWriteAllowed(res, "onboarding")) return;
        string body = await ReadBodyAsync(req);
        string category = BodyString(body, "category").Trim();
        string pick = BodyString(body, "index").Trim().ToLowerInvariant();
        if (pick.Length == 0) pick = "all";

        var templates = LoadTemplates();
        if (!templates.TryGetValue(category, out var list) || list.Count == 0)
        {
            WriteJson(res, 404, new
            {
                success = false,
                error = new { code = "CATEGORY_NOT_FOUND", message = Lang.T("未找到分类 {0}", category), categories = templates.Keys.ToList() }
            });
            return;
        }
        List<SourceTemplate> chosen;
        if (pick == "all") chosen = list;
        else if (int.TryParse(pick, out int idx) && idx >= 1 && idx <= list.Count) chosen = new List<SourceTemplate> { list[idx - 1] };
        else
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_INDEX", message = $"索引无效（1~{list.Count} 或 all）" } });
            return;
        }

        string key = "onboarding";
        string? busy = TryBeginDownload(key);
        if (busy != null)
        {
            WriteJson(res, 409, new { success = false, error = new { code = "ALREADY_RUNNING", message = busy } });
            return;
        }
        int ok = 0, fail = 0;
        var errors = new List<string>();
        try
        {
            ProgBegin("onboarding");
            _progTotal = chosen.Count;
            foreach (var t in chosen)
            {
                _progCurrent = t.Url;
                try
                {
                    await DownloadAndSaveToDb(t.Url, webDbPath, interactive: false);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    if (errors.Count < 20) errors.Add($"{t.Name} ({t.Url}) — {ex.Message}");
                }
                _progDone = ok + fail;
            }
            WriteJson(res, 200, new { success = true, data = new { category, ok, fail, errors } });
        }
        finally { ProgEnd(); EndDownload(key); }
    }

    // ══════════ 命令面板：**只读白名单** ══════════
    // 与 TUI 命令栏的用途等价（"我知道命令，但懒得点"），但**刻意不是**通用 CLI 执行器：
    // 网页里开一条任意命令通道，等于把 CLI 的全部写能力塞进浏览器。
    // 这里每个命令都映射到已有的只读内部函数，返回纯文本行。
    static void HandleCommand(HttpListenerRequest req, HttpListenerResponse res)
    {
        string line;
        try
        {
            string body = ReadBodyAsync(req).GetAwaiter().GetResult();
            line = BodyString(body, "line").Trim();
        }
        catch { line = (QueryParam(req, "line") ?? "").Trim(); }

        if (line.Length == 0)
        {
            WriteJson(res, 400, new { success = false, error = new { code = "BAD_REQUEST", message = "line required" } });
            return;
        }
        var (ok, lines, code) = RunReadOnlyCommand(line);
        WriteJson(res, ok ? 200 : 400, new
        {
            success = ok,
            data = new { line, lines },
            error = ok ? null : new { code, message = Lang.T("Unknown or non-read-only command: {0}", line), hint = CommandPaletteHelp() }
        });
    }

    static string CommandPaletteHelp() =>
        "status · list · today · grep <关键词> · show <id> · versions <id> · edits · dedup · hidden · policy · simon · telemetry · index · config";

    static (bool Ok, List<string> Lines, string Code) RunReadOnlyCommand(string line)
    {
        var outp = new List<string>();
        // 命令名大小写不敏感；参数原样交给对应函数（与 TUI 命令栏同一约定）
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string rest = line.Length > cmd.Length ? line[(cmd.Length + 1)..].Trim() : "";

        // **参数个数也要管**：`simon level 1`、`telemetry enable` 这类"
        // 拿一个只读命令名当挡箭牌、后面跟写意图"的写法必须整条拒掉，
        // 而不是把多余参数丢掉后照样返回一份只读结果 ——
        // 那样用户会以为自己执行的是写命令（而面板说它成功了）。
        bool NoArgs() => rest.Length == 0;

        switch (cmd)
        {
            case "status":
            case "about":
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                outp.Add("sip v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?"));
                outp.Add("data    " + dataDir);
                outp.Add("db      " + webDbPath);
                outp.Add("simon   level " + CurrentSimonLevel() + " · agent gate " + (AgentModeOn() ? "on" : "off"));
                outp.Add("auth    " + (WebPasswordIsSet() ? "password" : "local-token"));
                outp.Add("telemetry " + TelemetryService.Consent);
                return (true, outp, "");

            case "list":
            case "feeds":
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                using (var conn = OpenDb(webDbPath))
                {
                    conn.Open();
                    var c = conn.CreateCommand();
                    c.CommandText = @"
                        SELECT f.Id, f.Title, f.Schedule,
                               (SELECT COUNT(*) FROM Items WHERE FeedId = f.Id AND Status = 'active') AS n
                        FROM Feeds f ORDER BY f.Id";
                    using var r = c.ExecuteReader();
                    while (r.Read())
                        outp.Add($"#{r.GetInt32(0),-4} {r.GetString(1)}  [{r.GetInt32(3)} 篇 · {(r.IsDBNull(2) ? "manual" : r.GetString(2))}]");
                }
                if (outp.Count == 0) outp.Add("(还没有订阅源)");
                return (true, outp, "");

            case "today":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var (done, target, tracking) = TodayProgress(webDbPath);
                var list = GetTodayList(webDbPath, 5, refresh: false, out string generatedAt);
                outp.Add($"今日哈汤 · 目标 {target} · 已读 {(tracking ? done.ToString() : "—（遥测关闭）")} · 生成于 {generatedAt}");
                foreach (var t in list)
                    outp.Add($"  #{t.ItemId,-5} {t.Title}  [{t.Source} · {t.Reason} · ≈{t.Minutes:0.#} 分]");
                if (list.Count == 0) outp.Add("  (今天还没有值得读的)");
                return (true, outp, "");
            }

            case "grep":
            case "search":
            case "s":
            {
                if (rest.Length == 0) return (false, outp, "BAD_REQUEST");
                var hits = DoGrep(rest, webDbPath, 20, null);
                if (hits == null) return (false, outp, "BAD_REQUEST");
                outp.Add($"全文 · {rest} · {hits.Count} 篇");
                foreach (var h in hits)
                    outp.Add($"  #{h.ItemId,-5} {h.Title}  [{h.FeedTitle}]");
                return (true, outp, "");
            }

            case "show":
            case "content":
            {
                if (!int.TryParse(rest, out int id)) return (false, outp, "BAD_REQUEST");
                var body = ItemBodyRaw(id);
                if (body == null) { outp.Add($"文章 #{id} 不存在"); return (true, outp, ""); }
                outp.Add(body.Value.Title);
                outp.Add("");
                foreach (var p in NormalizeParagraphs(body.Value.Body).Take(40)) outp.Add(p);
                return (true, outp, "");
            }

            case "versions":
            case "history":
            {
                if (!int.TryParse(rest, out int id)) return (false, outp, "BAD_REQUEST");
                using var conn = OpenDb(webDbPath);
                conn.Open();
                var head = conn.CreateCommand();
                head.CommandText = "SELECT Guid, FeedId, Title FROM Items WHERE Id = @id";
                head.Parameters.AddWithValue("@id", id);
                string guid; int feedId; string title;
                using (var hr = head.ExecuteReader())
                {
                    if (!hr.Read()) { outp.Add($"文章 #{id} 不存在"); return (true, outp, ""); }
                    guid = hr.IsDBNull(0) ? "" : hr.GetString(0);
                    feedId = hr.GetInt32(1);
                    title = hr.GetString(2);
                }
                outp.Add($"{title}  的版本历史");
                if (guid.Length == 0) { outp.Add("  (这篇没有 Guid，不参与版本追踪)"); return (true, outp, ""); }
                var c = conn.CreateCommand();
                c.CommandText = "SELECT Id, Version, Status FROM Items WHERE Guid = @g AND FeedId = @f ORDER BY Version DESC";
                c.Parameters.AddWithValue("@g", guid);
                c.Parameters.AddWithValue("@f", feedId);
                using var r = c.ExecuteReader();
                while (r.Read())
                    outp.Add($"  v{r.GetInt32(1),-3} #{r.GetInt64(0),-6} {r.GetString(2)}");
                return (true, outp, "");
            }

            case "edits":
            case "diffs":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                using var conn = OpenDb(webDbPath);
                conn.Open();
                var c = conn.CreateCommand();
                c.CommandText = @"
                    WITH g AS (
                        SELECT FeedId, Guid, COUNT(*) AS n, MIN(Version) AS mn, MAX(Version) AS mx
                        FROM Items WHERE Guid IS NOT NULL AND Guid <> ''
                        GROUP BY FeedId, Guid HAVING COUNT(*) > 1)
                    SELECT g.mn, g.mx, g.n,
                           (SELECT i2.Title FROM Items i2 WHERE i2.FeedId = g.FeedId AND i2.Guid = g.Guid
                             ORDER BY i2.Version DESC LIMIT 1)
                    FROM g ORDER BY g.mx DESC LIMIT 50";
                using var r = c.ExecuteReader();
                while (r.Read())
                    outp.Add($"  v{r.GetInt32(0)} → v{r.GetInt32(1)}（{r.GetInt32(2)} 版） {r.GetString(3)}");
                if (outp.Count == 0) outp.Add("(还没有被改过稿的文章)");
                return (true, outp, "");
            }

            case "dedup":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var clusters = FindDuplicateClusters(webDbPath, 48);
                outp.Add($"48 小时窗口内发现 {clusters.Count} 组跨源重复（阈值 {LoadSettings().DedupThreshold:0.00}）");
                foreach (var c in clusters.Take(20))
                    outp.Add($"  [{string.Join(",", c.Members)}] {c.Title}  [{c.Source} · 重合 {c.MinOverlap:0}%]");
                return (true, outp, "");
            }

            case "hidden":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var hidden = ListHiddenDedup(webDbPath);
                outp.Add($"已隐藏 {hidden.Count} 篇");
                foreach (var h in hidden) outp.Add($"  #{h.Id,-5} {h.Title}  [{h.Source}]  key={h.Key}");
                return (true, outp, "");
            }

            case "policy":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var map = LoadSourcePolicy();
                outp.Add($"源规则 {map.Count} 条");
                foreach (var kv in map.OrderBy(x => x.Key))
                    outp.Add($"  feed {kv.Key}: {kv.Value.Action} {(kv.Value.Schedule.Length > 0 ? kv.Value.Schedule : kv.Value.Tag)} {kv.Value.Note}");
                return (true, outp, "");
            }

            case "simon":
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                outp.Add($"孟思琳(simon) 挡位 {CurrentSimonLevel()}（默认开启、无法关闭）");
                outp.Add($"Agent 外部调用门：{(AgentModeOn() ? "开" : "关")}（开：sip --agentok）");
                foreach (var e in SimonLoadEvents().Take(10))
                    outp.Add($"  {e.Ts}  {e.Type}  {e.Detail}");
                return (true, outp, "");

            case "telemetry":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var stats = TelemetryService.Stats();
                outp.Add($"遥测：{TelemetryService.Consent}（{(TelemetryService.IsEnabled ? "记录中" : "未记录")}）");
                outp.Add($"事件 {stats.Count} 条" + (stats.First != null ? $" · 最早 {stats.First} · 最近 {stats.Last}" : ""));
                return (true, outp, "");
            }

            case "index":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var cfg = LoadConfig(webDbPath);
                int vectors, chunks, active;
                using (var conn = OpenDb(webDbPath))
                {
                    conn.Open();
                    vectors = ScalarInt(conn, "SELECT COUNT(*) FROM Vectors");
                    chunks = ScalarInt(conn, "SELECT COUNT(*) FROM VectorsChunks");
                    active = ScalarInt(conn, "SELECT COUNT(*) FROM Items WHERE Status = 'active'");
                }
                outp.Add($"嵌入模型 {cfg.Embedding.Model}（{cfg.Embedding.Dimensions} 维）@ {cfg.Embedding.ApiEndpoint}");
                outp.Add($"已配置：{(File.Exists(ConfigPath(webDbPath)) ? "是" : "否（先 sip --init）")}");
                outp.Add($"向量 {vectors} 条 · 分块 {chunks} 条 · 活跃文章 {active} 篇");
                return (true, outp, "");
            }

            case "config":
            {
                if (!NoArgs()) return (false, outp, "UNKNOWN_COMMAND");
                var st = LoadSettings();
                outp.Add($"data folder : {dataDir}");
                outp.Add($"db          : {webDbPath}");
                outp.Add($"web         : {st.WebHost}:{st.WebPort}  密码 {(WebPasswordIsSet() ? "已设" : "未设")}");
                outp.Add($"ai config   : {ConfigPath(webDbPath)}  ({(File.Exists(ConfigPath(webDbPath)) ? "存在" : "缺失")})");
                outp.Add($"simon       : level {CurrentSimonLevel()}");
                outp.Add($"telemetry   : {TelemetryService.Consent}");
                outp.Add($"insights    : {st.InsightsInterval}");
                outp.Add($"dedup 阈值  : {st.DedupThreshold}");
                return (true, outp, "");
            }

            default:
                return (false, outp, "UNKNOWN_COMMAND");
        }
    }

}