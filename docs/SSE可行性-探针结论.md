# SSE 可行性 · 探针结论（t9）

> ## ✅ 最终结论：**增量流式成立，`/ask` 的 SSE 没问题**（2026-09-25 实测）
>
> 下面 §1 与两版"更正"都**作废**，包括我自己写的那版"写出去 ≠ 增量到达"。真实情况是：
> **`HttpListener` + `SendChunked` + 每帧 `Flush()` 能逐帧增量到达。**
>
> **证据 1（最小探针，裸 socket 客户端，绕开一切 HTTP 封装）**：
> ```
> 服务端自报写出:  2ms  315ms  628ms  939ms 1253ms 1564ms
> 客户端收到时刻: 27ms  339ms  653ms  964ms 1277ms 1589ms   ← 逐帧，延迟 27ms
> 结论: ✅ 增量成立
> ```
> 探针脚本：`tests/verification/sse_mini_probe.mjs`（可重跑）。
>
> **证据 2**：`SseStreamingTests` 三条**全绿**（`ChunkedResponse_ReachesClientIncrementally` /
> `ClientDisconnect_SurfacesAsWriteFailure` / `OpenSseStream_DoesNotBlockOtherRequests`）。
>
> ### 那两版"红"是怎么来的：**夹具的时间间隔算成了 0**
> 测试服务器里原本写的是：
> ```csharp
> for (int k = 0; k < FrameGapMs / 100; k++) Thread.Sleep(100);   // FrameGapMs = 50
> ```
> `50 / 100 == 0`（**整数除法**）→ 一次都不睡 → 服务端瞬间写完 40 帧。
> 于是客户端"40 帧全在同一毫秒到齐"——**不是平台不流，是夹具根本没留时间间隔**。
> 改成 `Thread.Sleep(FrameGapMs)` 后立刻全绿。
>
> **这个坑值得留着当教训**：一个整数除法让"测试自己写太快"，我据此得出
> "HttpListener 不能增量流式"的结论，还把交付说明写成"逐字流式尚未证明"。
> **当时该先做一个最小探针把服务端与客户端分开验**——`sse_mini_probe.mjs` 就是这么写的，
> 而它 10 秒就把问题定死了。**别拿一个复合用例的失败去推断平台行为。**
>
> ### 下面 §1 与历史更正的定位
> 保留为**过程记录**，不是结论。§1 的"环境起不来"同样是当时探针程序的运行方式问题
> （`sip --start` 在本沙箱确实报"拒绝访问"，但测试夹具里的 `HttpListener` 能起来 ——
> 这个矛盾至今没查清，**但它不影响产品**：产品在用户机器上跑 `--start` 是正常的）。


> **这份文档的诚实边界**：`HttpListener` **在本执行环境里起不来**（证据见 §1），
> 所以"逐字流式"这条**没有拿到真跑证据**。文档里凡是我读代码确认的，标【已核对】；
> 凡是需要真跑才能落地的，标【待真跑】。**没有任何一条是"我猜应该可以"**。
>
> 探针与复现程序一律建在**系统临时目录**，未碰 `readwithhotsoup/`、未改产品代码，
> 跑完已撤干净（仓库零残留）。

---

## 1. 为什么没跑成：环境把传输层本身挡住了

| 现象 | 证据 |
|---|---|
| `new HttpListener().Start()` 直接失败 | `System.Net.HttpListenerException (6): 句柄无效。`，抛在 `HttpListener.SetupV2Config`。**5 行的独立程序即复现** |
| 产品自己也起不来 | 系统 temp 隔离实例里跑 `sip.exe --start --no-open` → 退出码 1，stderr `Web server failed to start: 句柄无效`（端口是空的，非占用） |
| `dotnet test` 跑不起来 | testhost 启动阶段崩：`Win32Exception(5) 拒绝访问` @ `ProcessManager.OpenProcess ← ProcessHelper.SetExitCallback`。**用现成 `LangParityTests` 同样复现**，与本轮改动无关 |
| 根因 | 沙箱不允许对**非子代**进程做句柄操作（实测：开自己句柄 Access denied，开子进程可以）。http.sys 要建请求队列句柄、testhost 要监听父进程退出，两条路都被挡 |
| **无绕道** | 运行时 `System.Net.HttpListener.dll`（10.0.9）里**没有** `UseManagedImplementation`/`DisableManagedImplementation` 开关，只有 `SetupV2Config` + `HttpCreateRequestQueue` —— Windows 上 HttpListener 只能走 http.sys，**没有托管回退** |

**这不是产品缺陷**：用户不受沙箱约束，Web UI 本来就能跑。受影响的只有"在沙箱内验证"。

---

## 2. 对照矩阵

三种模式的区别**不在 API 名字上，而在三个开关**：`SendChunked`、`ContentLength64`、`Flush()`。

| 模式 | 写法 | 客户端观感 | 判定 |
|---|---|---|---|
| **A. 逐字流式（要的）** | `SendChunked=true`；**不设** `ContentLength64`；每帧 `Write` 后 `Flush()` | 每帧到达时间不同，首帧远早于总耗时 | 【待真跑】 |
| **B. 攒够再发（退化成非流式）** | 设了 `ContentLength64`，或写完不 `Flush` | 全部内容在**同一时刻**到齐；`timeToFirstDelta ≈ totalDuration` | 【已核对】`HttpListener` 会缓冲到处理函数返回才发；这正是"我用了 SSE" ≠ "字是流出来的" |
| **C. 报错/降级** | 流开始前出错 | 正常 HTTP 状态码 + JSON（401/404/409/400） | 【已核对】契约 §5.5 要求：**流开始后**改不了状态码，只能发 `event: error` |

**关键判据（照契约 §5.8，三条全都要过）**
1. ≥2 个 `delta` 帧，且**到达时间差 > 20ms**；
2. `timeToFirstDelta < 0.5 × totalDuration`（B 模式必然 ≈ 1.0）；
3. 客户端**在 `done` 之前就已渲染出文字**。

### 断连信号【待真跑】
客户端断开后，服务端下一次 `Write` 是否抛 `IOException`/`HttpListenerException` —— 这是契约 §5.6
「取消上游、别烧 token」**唯一可靠**的信号。我把它写成了可执行判据（见 §4），但**没跑过**：
如果真跑发现 http.sys 不立刻报错，就**不能**只靠写异常，需要补别的探测（例如把
`CancellationToken` 也挂到 `ctx.Response.OutputStream` 或加心跳写）。

### 30 秒长连接【待真跑】
契约 §5.3 要求每 15s 无输出时发一次 `: ping` 注释帧，防浏览器/中间层掐连接。
**这与"能否流式"是两个独立问题**：即使流式成立，长时间无帧仍可能被掐。真跑时要专门等 30s+ 看连接是否存活。

---

## 3. 读代码就能确定的事实【已核对】—— harness-eng 可直接依赖

- **不会队头阻塞**：`Web.cs:167` 是 `_ = Task.Run(() => HandleWebContext(ctx));`
  —— 请求被甩到线程池，一条 SSE 长连接**不会**卡住 accept 循环。
  （谁把它改成同步调用，表现就是"一提问整站卡住"。）
- **SSE 处理器不能复用现有输出助手**：`WriteJson`/`WriteHtml`/`WritePng` 等**全部**先设
  `res.ContentLength64 = buf.Length` 再 `OutputStream.Close()`
  （`Web.cs` 的 518/528/541/557/731/769/3457/3501/3689 等处）。
  设了 `ContentLength64` 就**不是流**了 —— 必须走独立分支。
- **没人替 SSE 收尾**：`HandleWebContext` 只有 `try/catch`，**没有** `finally { res.Close(); }`
  （`Web.cs:465-469`）。处理器自己要负责关闭，否则连接悬着。
- **全局响应头对 SSE 无害**：`Cache-Control: no-store`、CSP、`X-Frame-Options` 在分发前就设好了
  （`Web.cs:185-205`）。契约 §5.4 要求保持 `no-store`（**别改 `no-cache`**），与现状一致。
- **鉴权在流之前**：`/api/*` 未认证时回 401 JSON（`Web.cs:341`）—— 契约 §5.4 要求
  **未认证时必须回 401 JSON，不能开始流**。这条天然成立，但**别把 SSE 路由挪到鉴权那一行之前**。

---

## 4. 可照抄的最小骨架

```csharp
// 只列要点；正式接口归 harness-eng，这里不落产品代码。
static void HandleAsk(HttpListenerRequest req, HttpListenerResponse res, int itemId)
{
    // ── 流开始【之前】：一切会失败的检查都要在这里做完 ──
    // 401 / 404 / 409 / 400 走普通 JSON（WriteJson），此时状态码还能改。
    if (!WebRequestIsAuthenticated(req)) { WriteJson(res, 401, ...); return; }   // 已在 Web.cs:341 统一处理
    // ...ITEM_NOT_FOUND / AI_NOT_CONFIGURED / ALREADY_RUNNING / EMPTY_QUERY...

    // ── 开始流 ──
    res.StatusCode = 200;
    res.ContentType = "text/event-stream; charset=utf-8";
    res.SendChunked = true;                 // 关键 1：分块
    // 关键 2：**绝不设** res.ContentLength64（设了就不是流）
    try
    {
        Send(res, "session", ...);          // 契约 §5.2：session 必须是第一帧
        Send(res, "anchor", ...);
        Send(res, "snapshot", ...);
        foreach (var chunk in llmStream)    // 上游逐块吐
            Send(res, "delta", ...);
        Send(res, "cites", ...);
        Send(res, "done", ...);
        res.Close();
    }
    catch (Exception ex)                    // 客户端断开 → 写失败
    {
        // B2：不许把 ex.Message 回显给前端；细节只进服务端日志。
        Console.Error.WriteLine($"[ask] client gone or write failed: {ex}");
        cts.Cancel();                       // 契约 §5.6：取消上游，别继续烧 token
        try { res.Close(); } catch { }
    }
}

static void Send(HttpListenerResponse res, string ev, string json)
{
    byte[] b = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {json}\n\n");
    res.OutputStream.Write(b, 0, b.Length);
    res.OutputStream.Flush();               // 关键 3：不 Flush，会攒到处理函数返回才发
}
```

**四个必须记住的点**
1. `SendChunked = true`；
2. **不设** `ContentLength64`；
3. 每帧 `Flush()`；
4. 结束时 `res.Close()`（没人替你关）。

---

## 5. 怎么把它真跑通（留给你/验证方）

我加了 3 条可执行用例：`tests/Sip.Tests/SseStreamingTests.cs`。

- 它在**起不了 HttpListener 的环境里显式 SKIP 并打印原因**（`Xunit.Sdk.SkipException.ForSkip`），
  **不是静默通过** —— 所以"全绿"里不会有它假装跑过。本沙箱实测：`通过 0 · 失败 0 · 跳过 3`，退出码 0。
- 在 http.sys 可用的机器上，它会给出 §2 三条判据 + "SSE 开着时其它请求仍 <2s 返回" + "断开后写是否抛异常"。

**要在本环境之外补的验证**（这些是 t5/t6 的活，不是 t4 的）：
1. `dotnet test --filter FullyQualifiedName~SseStreaming` 在有 http.sys 的环境跑一次；
2. 30s 长连接 + 15s 心跳的存活验证；
3. 断连后**上游是否真的被取消**（不能只看异常有没有抛，要看模型侧连接是否断、token 是否停）。

---

## 6. 结论一句话

**机制本身没有发现障碍**（`Task.Run` 分发已确认、四个 API 要点清楚、没有已知的 HttpListener 流式缺陷），
**但"逐字流出"这件事在本环境里没有被实证过**。`t4` 可以按 §4 的骨架实现，
但**在有人于 http.sys 可用的环境里跑通 §5 之前，不应声称"流式已验收"** ——
这正是契约 §5.8 说的"最容易自欺"的一条。
