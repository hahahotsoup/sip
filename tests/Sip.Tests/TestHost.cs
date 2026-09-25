using System.Diagnostics;
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;
using Microsoft.Data.Sqlite;

namespace Sip.Tests;

/// <summary>
/// 进程级黑盒测试宿主。
/// 主程序数据目录固定在 exe 同级 readwithhotsoup/,因此把 sip 构建输出复制到
/// 独立临时目录再运行,数据天然隔离,测完即删,绝不碰真实数据。
/// 临时根目录优先取环境变量 SIP_TEST_TMP(本机 C 盘 TEMP 可能空间不足,测试输出目录默认在项目盘)。
/// </summary>
public sealed class SipInstance : IDisposable
{
    // 每实例唯一凭据 key 名:子进程经 ProcessStartInfo.Environment 继承,
    // 测试之间绝不共享系统凭据(挡位/密钥完全隔离,不污染彼此与真实用户)。
    // 需要两个实例共享同一命名空间时(「主数据库」要跨位置比较,见 PrimaryDbTests)用 keyName 显式传入。
    public string KeyName { get; }

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "readwithhotsoup");
    public string DbPath => Path.Combine(DataDir, "rss.db");

    private static readonly object TemplateLock = new();
    private static string? _template;

    public SipInstance(bool openAgentGate = true, string? keyName = null)
    {
        KeyName = string.IsNullOrEmpty(keyName) ? "simon_db_key_test_" + Guid.NewGuid().ToString("N")[..10] : keyName;
        Root = Path.Combine(TempRoot(), "sip-" + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(EnsureTemplate(), Root);
        if (openAgentGate) OpenAgentGate();
    }

    /// <summary>测试宿主本身就是「非交互调用」，而 Agent 门默认关闭 —— 不打开的话所有用例都会被拦。
    /// 这里写的就是 `sip --agentok` 自己写的那条凭据（<c>hotsoupreader</c> 服务下的 agent_ok_*），
    /// 只是借 SIP_SIMON_KEY_NAME 落在本实例独享的作用域里，用完即删（见 DeleteTestCredentials）。
    /// **产品本身没有任何旁路**：2026-09-12 删掉了原来的 agent_mode.json 兜底文件 ——
    /// 那个文件在「凭据库还没有值」（也就是全新安装的默认状态）时就会被采信，
    /// 等于任何程序写个 JSON 就能把这道门打开，与「只有 --agentok 能开」矛盾。
    /// 想验证这道门本身，用 <c>new SipInstance(openAgentGate: false)</c> 建实例；
    /// 也可以事后调 <see cref="OpenAgentGate"/> 做「关门 → 开门」的反向对照。</summary>
    public void OpenAgentGate()
    {
        string gateKey = "agent_ok_" + KeyName;
        try
        {
            Directory.CreateDirectory(DataDir);
            var store = CredentialStoreFactory.CreateDefault("hotsoupreader");
            var cache = new ktsu.CredentialCache.CredentialCache(store);
            cache.AddOrReplace(new PersonaGUID { WeakString = gateKey },
                new CredentialWithToken { Token = new CredentialToken { WeakString = "on" } });

            // **写完回读确认**（2026-09-12 补）。凭据库写失败时如果只是 catch 掉，
            // 症状是**几十个用例各自报 AGENT_BLOCKED**，真正的原因（门没打开）被埋在噪音里 ——
            // 实测就撞上过一次偶发。宁可在建实例时当场炸掉，把这件事说清楚。
            if (!cache.TryGet(new PersonaGUID { WeakString = gateKey }, out var back)
                || back is not CredentialWithToken ct || ct.Token.WeakString != "on")
                throw new InvalidOperationException(
                    $"打不开 Agent 门：凭据库写入后回读不到 {gateKey}。多半是系统凭据库写不进去了" +
                    "（本机历史上出现过 ERROR_NOT_ENOUGH_MEMORY，症状是安全设置集体静默失效，见 CHANGELOG）。");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // 把"写不进去"和"这个平台根本没有凭据库"都变成同一条明确信息
            throw new InvalidOperationException(
                $"打不开 Agent 门：写系统凭据库失败 —— {ex.GetType().Name}: {ex.Message}。" +
                "测试套件要求系统凭据库可写（产品已无文件兜底，见 simon.cs 的注释）。", ex);
        }
    }

    /// <summary>任何 CLI 命令都会触发 InitDatabase,跑一次 --help 即建好空库。</summary>
    public void EnsureDatabase() => Run("--help");

    public (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var p = Process.Start(CreatePsi(args))!;
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } }
        return (p.ExitCode, so, se);
    }

    /// <summary>带 stdin 输入调用(ingest --stdin / --evidence --stdin 测试用)。
    /// 先写 stdin 再读 stdout,避免大输入时管道缓冲死锁。</summary>
    public (int ExitCode, string Stdout, string Stderr) RunWithInput(string input, params string[] args)
    {
        using var p = Process.Start(CreatePsi(args))!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } }
        return (p.ExitCode, so, se);
    }

    private ProcessStartInfo CreatePsi(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Root, OperatingSystem.IsWindows() ? "sip.exe" : "sip"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 重定向 stdin → 子进程 Console.IsInputRedirected=true,稳定模拟「非交互(脚本/Agent)调用」
            RedirectStandardInput = true,
            UseShellExecute = false,
            // 程序统一 UTF-8 输出;不显式指定会按系统默认(GBK)解码导致乱码
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SIP_SIMON_KEY_NAME"] = KeyName;   // 每实例隔离凭据命名空间
        return psi;
    }

    /// <summary>直接对隔离库执行 SQL(构造 fixture / 断言 DB 状态)。
    /// Pooling=False:连接用完即真正关闭,不占文件句柄——否则池连接会让
    /// sip 子进程无法替换 rss.db(例如完整性自愈时 File.Move 报"被其他进程占用")</summary>
    public void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public string? QueryScalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar()?.ToString();
    }

    public void Dispose()
    {
        DeleteTestCredentials();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }

    /// <summary>清掉本实例可能写进系统凭据库的条目。
    /// <para>历史教训（2026-09-12 实测）：旧版加密路径每跑一个测试实例就往凭据库写一把随机密钥，
    /// 且**从不清理**。500+ 个实例之后把用户的 Windows 凭据库顶到阈值，
    /// `CredWrite` / `cmdkey` 开始报 `ERROR_NOT_ENOUGH_MEMORY` —— 于是
    /// **改挡位、存 AI Key、建 Web 会话密钥集体静默失效**（都只表现为"改不动"，不报错）。
    /// 清理时实测：756 条 sip 条目里 746 条是测试垃圾，删掉后写入立刻恢复。</para>
    /// 名字隔离不够，必须清理。</summary>
    private void DeleteTestCredentials()
    {
        // 这就是本实例可能写进凭据库的全部条目:SimonDbKey(旧)、SimonLevelKey、Agent 门开关、主数据库认领
        foreach (var key in new[] { KeyName, KeyName + "_level", "agent_ok_" + KeyName, "primary_db_" + KeyName })
        {
            // "删一遍就算"不够：cmdkey 失败在这里是被吞掉的，而后果非常重 ——
            // 历史教训是**泄漏**（746 条测试垃圾把凭据库顶满，挡位/密钥/Web 会话集体静默失效）。
            // 所以删完回查一次，还在就再删一遍；仍然在也不抛（Dispose 抛异常会污染测试结论）。
            for (int pass = 0; pass < 2 && CredentialExists(key); pass++)
                RunCmdKey("/delete:hotsoupreader:" + key);
        }
    }

    /// <summary>用 cmdkey 独立回查（不经过 ktsu，避免"用可能坏掉的同一套机制验证自己"）。</summary>
    private static bool CredentialExists(string key)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmdkey",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("/list:hotsoupreader:" + key);
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(5_000);
            return outp.Contains("hotsoupreader:" + key, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // 查不了就当没有，不因为清理失败而影响测试结论
    }

    private static void RunCmdKey(string arg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmdkey",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(5_000);
        }
        catch { /* 清理失败不影响测试结论 */ }
    }

    // ── fixture 辅助 ────────────────────────────────────────────

    public void InsertFeed(int id, string title, string url)
        => Exec("INSERT INTO Feeds (Id, Title, FeedUrl, LastCheckedAt) VALUES (@id, @t, @u, NULL)",
            ("@id", id), ("@t", title), ("@u", url));

    public void InsertItem(int id, int feedId, string title, string link, string content, string guid)
        => Exec("""
                INSERT INTO Items (Id, FeedId, Title, Link, Description, Content, Guid, Status, Version)
                VALUES (@id, @fid, @t, @link, '', @c, @g, 'active', 1)
                """,
            ("@id", id), ("@fid", feedId), ("@t", title), ("@link", link), ("@c", content), ("@g", guid));

    public string? ItemStatus(int id)
        => QueryScalar("SELECT Status FROM Items WHERE Id = @id", ("@id", id));

    // ── 内部 ────────────────────────────────────────────────────

    private static string TempRoot()
        => Environment.GetEnvironmentVariable("SIP_TEST_TMP") ?? Path.Combine(AppContext.BaseDirectory, "test-tmp");

    private static string SipOutputDir() => Path.Combine(AppContext.BaseDirectory, "sip");

    private static string EnsureTemplate()
    {
        lock (TemplateLock)
        {
            if (_template != null) return _template;
            SweepStaleInstances(TempRoot());
            var root = Path.Combine(TempRoot(), "template");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            CopyDirectory(SipOutputDir(), root);
            // 去掉 bin 输出里可能带上的开发数据,保证模板是干净空库
            var devData = Path.Combine(root, "readwithhotsoup");
            if (Directory.Exists(devData)) Directory.Delete(devData, recursive: true);
            _template = root;
            return root;
        }
    }

    /// <summary>清掉上次残留的实例目录。
    /// <para>为什么必须清：进程**被强杀**时 Dispose 不会执行，每个死在半路的实例都留下一份拷贝的残骸
    /// （实测攒到过 **527 个目录 / 1.7 GB**）。而模板本来就是每个测试进程重新拷一份，所以旧模板也纯属占地方。</para>
    /// <para>只删**超过 6 小时没动过**的：并行跑的另一个测试进程（例如同时跑 Debug 与 Release）
    /// 刚建的目录 mtime 是新的，不会被误删。</para></summary>
    private static void SweepStaleInstances(string tmpRoot)
    {
        try
        {
            if (!Directory.Exists(tmpRoot)) return;
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var dir in Directory.GetDirectories(tmpRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                    Directory.Delete(dir, recursive: true);
                }
                catch { /* 单个删不掉（被占用等）不影响测试 */ }
            }
        }
        catch { /* 清理失败不影响测试结论 */ }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(d.Replace(src, dst));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(src, dst), overwrite: true);
    }
}
