using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// t9：**敲实 SSE 可行性** —— HttpListener 到底能不能"逐字流式输出"。
///
/// 为什么值得单独一个文件：契约 §5.8 把"我用了 SSE"与"字是流出来的"分开，
/// 并要求给出**时序证据**。而"用了 SSE"本身不保证任何事 ——
/// `HttpListenerResponse` 只要被设了 `ContentLength64`，或者写完后不 `Flush()`，
/// 响应就会一直攒到处理函数返回才一次性发给客户端（那就退化成非流式，看起来却完全正常）。
///
/// 这组用例跑的是**真实传输层**（真的 HttpListener + 真的 http.sys + 真的 HttpClient），
/// 不是 mock：本机 Web 服务用的就是同一套 API（Web.cs:90 建 listener、:164 GetContextAsync、
/// :167 Task.Run 分发、:518 WriteJson 设 ContentLength64）。
///
/// 复现：`dotnet test tests/Sip.Tests/Sip.Tests.csproj -c Release --filter FullyQualifiedName~SseStreaming`
/// </summary>
public class SseStreamingTests
{
    private const int Frames = 20;
    private const int FrameGapMs = 50;

    /// <summary>
    /// 起 HttpListener 的前置探测。
    ///
    /// 为什么要有它：**在受限的执行环境里 `HttpListener.Start()` 会直接失败**
    /// （实测为 `HttpListenerException (6): 句柄无效`，抛在 `HttpListener.SetupV2Config`），
    /// 而这与产品代码无关 —— 同一环境下 `sip --start` 也报
    /// 「Web server failed to start: 句柄无效」。那种环境下这几条用例**无法证明任何事**，
    /// 与其红着让人误以为 SSE 有 bug，不如显式 Skip 并把原因写出来。
    ///
    /// 在普通桌面环境（http.sys 可用）里，探测为真，三条用例照常全跑。
    /// </summary>
    private static readonly Lazy<(bool Ok, string Why)> ListenerPreflight = new(() =>
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{FreePort()}/");
            l.Start();
            bool listening = l.IsListening;
            l.Close();
            return (listening, "");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    });

    /// <summary>环境起不了 HttpListener 时**显式跳过**（不是静默通过）：跳过原因会打印出来，
    /// 所以"全绿"里不会有它假装跑过；而 http.sys 可用的机器上它照常全跑。</summary>
    private static void RequireHttpListener()
    {
        var (ok, why) = ListenerPreflight.Value;
        if (!ok)
            throw Xunit.Sdk.SkipException.ForSkip(
                "这个环境起不了 HttpListener，SSE 传输层无法验证 " +
                $"（{why}）—— 同一环境下 `sip --start` 也报「Web server failed to start: 句柄无效」。" +
                "这类用例需要在 http.sys 可用的普通桌面环境下跑。");
    }

    private static int FreePort() => SseTestServer.FreePort();

    /// <summary>
    /// 核心用例：服务端每写一帧就 Flush，客户端**必须**在服务端还没写完时就开始收到帧。
    ///
    /// 判据（照契约 §5.8 的 1、2 两条）：
    ///   1. 至少收到 2 个帧，且**到达时间不同**（差值 &gt; 20ms）—— 证明不是一次性到齐；
    ///   2. `首帧时刻 &lt; 0.5 × 总耗时` —— 逐块流式的实现自然满足；"先整段生成再一次性发出"必然 ≈1.0。
    ///
    /// ⚠️ **本轮这条是红的，而且它红得有信息**（2026-09-25 实测）：
    /// 服务端写完 40 帧、每帧都 `Flush()`，客户端侧**全部记到同一个毫秒**（81ms 到齐），
    /// 即"逐块写出"没有变成"逐块到达"。它与 t9 探针的担忧一致，只是这次有可复现的观测。
    /// **不把它改成 SKIP、也不放宽断言** —— 它就是"SSE 到底流不流"的判据；
    /// 红了说明这套 HttpListener 用法在真实环境下**尚未证明能逐字流式**。
    /// 要它变绿，得先把"为什么 Flush 了还攒"查清（候选：响应缓冲设置、chunked 写入路径），
    /// 那是一项独立的排查，不该靠调测试阈值掩盖过去。
    /// </summary>
    [Fact]
    public async Task ChunkedResponse_ReachesClientIncrementally()
    {
        RequireHttpListener();
        await using var server = await SseTestServer.StartAsync();
        using var client = NewClient();

        var sw = Stopwatch.StartNew();
        using var res = await client.GetAsync(server.BaseUrl + "/sse",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // 分块传输的标志：**不能**有 Content-Length，否则就不是流
        Assert.Null(res.Content.Headers.ContentLength);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);

        var arrivals = new List<(string Ev, long Ms)>();
        await foreach (var (ev, _) in ReadSseAsync(res))
            arrivals.Add((ev, sw.ElapsedMilliseconds));
        long total = sw.ElapsedMilliseconds;
        long first = arrivals.Count == 0 ? -1 : arrivals[0].Ms;

        // 1. 帧数与"到达时间不同"
        var deltas = arrivals.Where(a => a.Ev == "delta").ToList();
        Assert.True(deltas.Count >= 2,
            $"只收到 {deltas.Count} 个 delta 帧（期望 ≥2）。到达记录：{Dump(arrivals)}");
        long spread = deltas[^1].Ms - deltas[0].Ms;
        Assert.True(spread > 20,
            $"delta 帧的到达时间几乎相同（跨度 {spread}ms）—— 说明响应被攒着一次性发出了。到达记录：{Dump(arrivals)}");

        // 2. 首帧明显早于整体结束
        Assert.True(first >= 0, $"一个帧都没收到。到达记录：{Dump(arrivals)}");
        Assert.True(first < total * 0.5,
            $"首帧来得太晚（首帧 {first}ms / 总共 {total}ms，比值 {(double)first / total:F2} ≥ 0.5）—— " +
            "这正是「先整段生成、再一次性发出」的特征。" +
            $"到达记录：{Dump(arrivals)}");

        // 3. 事件类型按契约顺序（§5.2）：session 先来，delta 一批，done 收尾
        Assert.Equal("session", arrivals[0].Ev);
        Assert.Equal("delta", arrivals[1].Ev);
        Assert.Equal("done", arrivals[^1].Ev);
    }

    /// <summary>
    /// 一条 SSE 长连接**不得**把整个服务堵死。
    /// 这一条能成立，靠的是 Web.cs:167 的 `_ = Task.Run(() => HandleWebContext(ctx))` ——
    /// 请求被甩到线程池，而不是在 accept 循环里串行处理。
    /// 如果哪天有人把它改成同步调用，这条用例会红（而且真实表现是"一提问整站卡住"）。
    /// </summary>
    [Fact]
    public async Task OpenSseStream_DoesNotBlockOtherRequests()
    {
        RequireHttpListener();
        await using var server = await SseTestServer.StartAsync();

        // 先开一条 SSE 并读到第一帧，确保它**正处于流式中**
        using var sseClient = NewClient();
        using var sse = await sseClient.GetAsync(server.BaseUrl + "/sse",
            HttpCompletionOption.ResponseHeadersRead);
        await using (var e = ReadSseAsync(sse).GetAsyncEnumerator())
        {
            Assert.True(await e.MoveNextAsync(), "SSE 流没有吐出任何帧");
            Assert.Equal("session", e.Current.Ev);

            // 另开一条普通请求：必须很快拿到完整响应
            using var plainClient = NewClient();
            var sw = Stopwatch.StartNew();
            using var json = await plainClient.GetAsync(server.BaseUrl + "/ping");
            string body = await json.Content.ReadAsStringAsync();
            sw.Stop();

            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            Assert.Contains("\"ok\":true", body, StringComparison.Ordinal);
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"SSE 流开着时另一条请求花了 {sw.ElapsedMilliseconds}ms —— 请求可能被串行处理了");
        }
    }

    /// <summary>
    /// 客户端**断开**后，服务端继续写会抛异常 —— 这是 §5.6「取消上游、别烧 token」唯一可靠的信号。
    ///
    /// 这一条测的不是"我们想不想要"，而是"http.sys 到底告不告诉我们"。结论如实写进断言：
    /// 只要能抛出，实现就可以靠 `try { Write } catch { cts.Cancel(); }` 收到取消。
    /// </summary>
    [Fact]
    public async Task ClientDisconnect_SurfacesAsWriteFailure()
    {
        RequireHttpListener();
        await using var server = await SseTestServer.StartAsync();
        using (var client = NewClient())
        {
            using var res = await client.GetAsync(server.BaseUrl + "/sse",
                HttpCompletionOption.ResponseHeadersRead);
            await using var e = ReadSseAsync(res).GetAsyncEnumerator();
            Assert.True(await e.MoveNextAsync(), "SSE 流没有吐出任何帧");
        }   // 这里把连接整个扔掉 = 客户端断开

        // 服务端在写失败时会把这个 Task 标记完成
        var failed = await Task.WhenAny(server.WriteFailureDetected, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(failed == server.WriteFailureDetected,
            "客户端断开后 10 秒内服务端仍未察觉写失败 —— " +
            "那么 §5.6 的「客户端断开就取消上游」就不能只靠写异常，需要别的探测手段");
        Assert.NotNull(server.WriteFailureException);
    }

    // ── 客户端读 SSE ────────────────────────────────────────────

    private static HttpClient NewClient() => new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>按 SSE 帧格式逐帧读：`event: X` / `data: {...}` / 空行结束。
    /// 用流式读（ResponseHeadersRead + 逐段 ReadAsync），不是读完整 body —— 否则测不出时序。</summary>
    private static async IAsyncEnumerable<(string Ev, string Data)> ReadSseAsync(HttpResponseMessage res)
    {
        await using var stream = await res.Content.ReadAsStreamAsync();
        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (true)
        {
            int n = await stream.ReadAsync(buf);
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            // 帧分隔：空行。逐个切出完整帧
            while (true)
            {
                string all = sb.ToString();
                int sep = all.IndexOf("\n\n", StringComparison.Ordinal);
                if (sep < 0) break;
                string frame = all[..sep];
                sb.Remove(0, sep + 2);
                string ev = "", data = "";
                foreach (var line in frame.Split('\n'))
                {
                    if (line.StartsWith("event:", StringComparison.Ordinal)) ev = line[6..].Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
                }
                if (ev.Length == 0 && data.Length == 0) continue;   // 心跳注释帧
                yield return (ev, data);
            }
        }
    }

    private static string Dump(List<(string Ev, long Ms)> a)
        => "[" + string.Join(", ", a.Select(x => $"{x.Ev}@{x.Ms}ms")) + "]";

    // ── 被测服务端：与 Web.cs 同构的最小实现 ─────────────────────

    /// <summary>
    /// 一个只做两件事的 HttpListener：
    ///   GET /sse   —— 逐帧写、逐帧 Flush、**不设 ContentLength64**（这三个是 SSE 的全部要点）
    ///   GET /ping  —— 普通 JSON（对照用，证明别的请求还能跑）
    /// accept 循环照抄 Web.cs:161-168（含 `Task.Run` 分发）。
    /// </summary>
    private sealed class SseTestServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        // 必须是 TCS：用 Task.CompletedTask 初始化会让「断开检测」用例**天然通过**，
        // 那是这条用例最容易自欺的地方（它要证明的恰恰是"失败信号会不会来"）。
        private readonly TaskCompletionSource _writeFailed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string BaseUrl { get; private set; } = "";
        public Task WriteFailureDetected => _writeFailed.Task;
        public Exception? WriteFailureException { get; private set; }

        public static async Task<SseTestServer> StartAsync()
        {
            var s = new SseTestServer();
            int port = FreePort();
            s.BaseUrl = $"http://127.0.0.1:{port}";
            s._listener.Prefixes.Add(s.BaseUrl + "/");
            s._listener.Start();
            _ = Task.Run(s.LoopAsync);
            return await Task.FromResult(s);
        }

        public static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));   // 同 Web.cs:167
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/ping")
            {
                var buf = Encoding.UTF8.GetBytes("{\"ok\":true}");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                ctx.Response.OutputStream.Close();
                return;
            }

            // ── SSE ──
            var res = ctx.Response;
            res.Headers["Cache-Control"] = "no-store";   // 与 Web.cs:187 的全局头一致
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.SendChunked = true;                      // 关键 1：分块，且**不设** ContentLength64
            try
            {
                Write(res, "session", "{\"sessionId\":\"chat:s1\",\"turnIndex\":1}");

                // 写**足量**的帧再收尾。
                // 两个数都要够大，各为一个用例服务：
                //   · 「逐块到达」用例要 ≥2 个 delta 且跨度 >20ms（这里给 8 帧 × 50ms ≈ 400ms）；
                //   · 「断开会被察觉」用例要流在客户端断开**之后仍在写**（这里给 2 秒）。
                // 早先那版只写 20 帧 × 50ms ≈ 1 秒且收尾很快，于是断开时流早就写完了 ——
                // 测到的其实是"写完了当然不报错"，而不是"断开会不会被察觉"。
                for (int i = 0; i < 40 && !_cts.IsCancellationRequested; i++)
                {
                    Write(res, "delta", $"{{\"text\":\"chunk{i}\"}}");
                    // ⚠️ 这里**必须**真的睡 FrameGapMs。
                    // 第一版写成 `for (k = 0; k < FrameGapMs / 100; k++) Thread.Sleep(100)`，
                    // 而 FrameGapMs = 50 → `50 / 100 == 0`（整数除法）→ 一次都不睡 →
                    // 服务端瞬间写完 40 帧。症状是"客户端 40 帧全在同一毫秒到齐"，
                    // 看起来像平台不流，其实是**夹具根本没有留出时间间隔**。
                    // 这个坑值得留着：它差点被写成"HttpListener 不能增量流式"的结论。
                    Thread.Sleep(FrameGapMs);
                    // 静默期写注释行当心跳 —— 与产品 AiReading 的做法一致：
                    // 没有写点就没有失败信号，断开就无从发现。
                    // 长度必须由字符串自己算：写死 15 而实际是 14，会多吃掉下一帧的首字节。
                    var hb = Encoding.UTF8.GetBytes(": keep-alive\n\n");
                    res.OutputStream.Write(hb, 0, hb.Length);
                    res.OutputStream.Flush();
                }
                Write(res, "done", "{\"fullLength\":120}");
                res.Close();
            }
            catch (Exception ex)
            {
                // 客户端断开 → 写失败。这正是实现要挂"取消上游"的地方。
                WriteFailureException = ex;
                _writeFailed.TrySetResult();
                try { res.Close(); } catch { }
            }
        }

        private static void Write(HttpListenerResponse res, string ev, string data)
        {
            byte[] buf = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {data}\n\n");
            res.OutputStream.Write(buf, 0, buf.Length);
            res.OutputStream.Flush();   // 关键 2：不 Flush，HttpListener 会攒到结束才发
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            await Task.CompletedTask;
        }
    }
}
