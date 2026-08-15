using System.Diagnostics;
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
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "readwithhotsoup");
    public string DbPath => Path.Combine(DataDir, "rss.db");

    private static readonly object TemplateLock = new();
    private static string? _template;

    public SipInstance()
    {
        Root = Path.Combine(TempRoot(), "sip-" + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(EnsureTemplate(), Root);
    }

    /// <summary>任何 CLI 命令都会触发 InitDatabase,跑一次 --help 即建好空库。</summary>
    public void EnsureDatabase() => Run("--help");

    public (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
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
        using var p = Process.Start(psi)!;
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } }
        return (p.ExitCode, so, se);
    }

    /// <summary>直接对隔离库执行 SQL(构造 fixture / 断言 DB 状态)。</summary>
    public void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public string? QueryScalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar()?.ToString();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
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

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(d.Replace(src, dst));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(src, dst), overwrite: true);
    }
}
