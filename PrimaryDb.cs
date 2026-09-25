// ══════════ 主数据库：跨位置的「哪一份才是真的」══════════
// sip 是绿色的：exe 拷到哪儿都能跑，数据目录（readwithhotsoup/）跟着 exe 走。
// 于是同一个用户很容易攒出好几份库 —— 桌面一份、U 盘一份、发布目录一份、测试目录一份 ——
// 谁也说不清哪份是"真的"。这个文件回答的就是这一个问题，办法是**系统凭据库里的一条记录**：
//
//   · 第一次写入的人认领：当前数据目录被记为主库，并当场告知（stderr）
//   · 之后在任何位置启动，只要不是主库，就提示主库在哪、怎么改、怎么合
//   · **它只提示，不当门**：在别处照样能用那份库（真正的门是孟思琳挡位与 Agent 门）。
//     这里若一票否决，绿色程序最常见的用法（拷一份出去试）就废了。
//
// 为什么不带数据目录作用域：挡位/Agent 门/AI Key 的凭据名都带 SimonScopeHash()，
// 那是"每个副本互不影响"的语义 —— 而这条记录要的恰恰相反：**跟着用户走、跨所有副本**。
// 两者只共享同一套测试隔离手法：SIP_SIMON_KEY_NAME 换命名空间（测试并发跑不串味）。
//
// 为什么打 stderr：`--json` 的 stdout 必须干净（脚本要解析），而这条提示是给人看的诊断。

using System.Text.Json;
using Microsoft.Data.Sqlite;

public partial class Program
{
    // 凭据键名：生产下固定；测试用 SIP_SIMON_KEY_NAME 换命名空间（与挡位/Agent 门同一套规矩）
    static string PrimaryDbKey()
    {
        string? t = Environment.GetEnvironmentVariable("SIP_SIMON_KEY_NAME");
        return string.IsNullOrEmpty(t) ? "primary_db" : "primary_db_" + t;
    }

    static string? PrimaryDbPath()
    {
        string? p = CredGet(PrimaryDbKey());
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    static bool PrimaryDbSet(string dir)
    {
        try { CredSet(PrimaryDbKey(), Path.GetFullPath(dir)); return true; }
        catch { return false; }
    }

    // 路径比较：Windows 不区分大小写，尾部斜杠也不该算成不同目录
    static bool SameDataDir(string a, string b)
    {
        try
        {
            string x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            string y = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(x, y, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>启动检查：没认领过就认领，认领过且不是这一份就提示。**只打印，不返回可否继续。**</summary>
    static void PrimaryDbStartupCheck()
    {
        string cur = Path.GetFullPath(dataDir);
        string? primary = PrimaryDbPath();
        if (primary == null)
        {
            if (PrimaryDbSet(cur))
                Console.Error.WriteLine(Lang.T("已将 {0} 作为主数据库（第一次运行即认领，以后在别处打开会提示你）", cur));
            return;
        }
        if (SameDataDir(primary, cur)) return;
        Console.Error.WriteLine(Lang.T("已将 {0} 作为主数据库", primary));
        Console.Error.WriteLine(Lang.T("当前打开的是另一份：{0}", cur));
        Console.Error.WriteLine(Lang.T("更改或合并请使用 sip db 指令：sip db status | sip db set <目录> | sip db merge [目录]"));
    }

    // ══════════ CLI: sip db [status|set|merge] ══════════
    static void CliDb(string[] args, string dbPath)
    {
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        string sub = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status" or "show": DbStatusCli(json); return;
            case "set" or "use": DbSetCli(args.Skip(1).ToArray(), json); return;
            case "merge": DbMergeCli(args.Skip(1).ToArray(), json); return;
            default:
                SetExit();
                if (json) JsonOut(new { success = false, error = new { code = "USAGE", message = "sip db status | set [<dir>|--here] | merge [<dir>] [--yes]" } });
                else Console.WriteLine(Lang.T("用法: sip db status | set [<目录>|--here] | merge [<目录>] [--yes]"));
                return;
        }
    }

    static void DbStatusCli(bool json)
    {
        string cur = Path.GetFullPath(dataDir);
        string? primary = PrimaryDbPath();
        bool match = primary != null && SameDataDir(primary, cur);
        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    primaryDb = primary,
                    currentDir = cur,
                    match,
                    claimed = primary != null,
                    credentialKey = PrimaryDbKey()
                }
            });
            return;
        }
        Console.WriteLine(Lang.T("主数据库   : {0}", primary ?? Lang.T("（还没认领：下一次运行会把当前目录记为主库）")));
        Console.WriteLine(Lang.T("当前目录   : {0}", cur));
        Console.WriteLine(Lang.T("是否一致   : {0}", match ? Lang.T("是") : Lang.T("否")));
        Console.WriteLine(Lang.T("凭据键名   : {0}", PrimaryDbKey()));
        if (!match && primary != null)
            Console.WriteLine(Lang.T("提示：更改用 sip db set <目录>；把这一份合进主库用 sip db merge --yes"));
    }

    static void DbSetCli(string[] args, bool json)
    {
        bool here = args.Length == 0 || args.Any(a => a.Equals("--here", StringComparison.OrdinalIgnoreCase));
        string target = here ? dataDir : args.First(a => !a.StartsWith('-'));
        string full;
        try { full = Path.GetFullPath(target); }
        catch (Exception ex)
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "BAD_PATH", message = ex.Message } });
            else Console.WriteLine(Lang.T("路径无效：{0}", target));
            return;
        }
        if (!Directory.Exists(full))
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "NO_SUCH_DIR", message = full } });
            else Console.WriteLine(Lang.T("目录不存在：{0}", full));
            return;
        }
        bool hasDb = File.Exists(Path.Combine(full, "rss.db"));
        if (!PrimaryDbSet(full))
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "CRED_WRITE_FAILED", message = PrimaryDbKey() } });
            else Console.WriteLine(Lang.T("写入凭据失败（系统凭据库拒绝了写入），主数据库未改变"));
            return;
        }
        if (json)
        {
            JsonOut(new { success = true, data = new { primaryDb = full, hasDb, credentialKey = PrimaryDbKey() } });
            return;
        }
        Console.WriteLine(Lang.T("已将 {0} 设为主数据库", full));
        if (!hasDb) Console.WriteLine(Lang.T("注意：该目录下还没有 rss.db（第一次在那里运行时会建）"));
        if (!SameDataDir(full, dataDir))
            Console.WriteLine(Lang.T("当前目录仍是 {0}（下次启动会提示它不是主库）", Path.GetFullPath(dataDir)));
    }

    static void DbMergeCli(string[] args, bool json)
    {
        bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        string? srcArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        string srcDir = Path.GetFullPath(srcArg ?? dataDir);
        string? primary = PrimaryDbPath();
        if (primary == null)
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "NO_PRIMARY", message = "sip db set <dir>" } });
            else Console.WriteLine(Lang.T("还没有主数据库：先运行 sip db set <目录>（或直接在目标目录里跑一次 sip）"));
            return;
        }
        string dstDir = Path.GetFullPath(primary);

        string srcDb = Path.Combine(srcDir, "rss.db");
        string dstDb = Path.Combine(dstDir, "rss.db");
        if (SameDataDir(srcDir, dstDir))
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "SAME_DB", message = dstDir } });
            else Console.WriteLine(Lang.T("来源与主库是同一份（{0}），没有可合并的东西", dstDir));
            return;
        }
        if (!File.Exists(srcDb))
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "NO_SOURCE_DB", message = srcDb } });
            else Console.WriteLine(Lang.T("来源里没有 rss.db：{0}", srcDb));
            return;
        }
        if (!File.Exists(dstDb))
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "NO_TARGET_DB", message = dstDb } });
            else Console.WriteLine(Lang.T("主库里没有 rss.db：{0}（先在那里运行一次 sip）", dstDb));
            return;
        }
        if (!yes && !HasInteractiveConsole())
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "NEEDS_YES", message = "--yes" } });
            else Console.WriteLine(Lang.T("这是写操作：确认请加 --yes（{0} → {1}）", srcDir, dstDir));
            return;
        }

        try
        {
            var r = MergeLibraryInto(srcDir, dstDir);
            if (json)
            {
                JsonOut(new
                {
                    success = true,
                    data = new
                    {
                        source = srcDir,
                        target = dstDir,
                        feedsAdded = r.FeedsAdded,
                        feedsReused = r.FeedsReused,
                        itemsAdded = r.ItemsAdded,
                        itemsSkipped = r.ItemsSkipped,
                        signalsMerged = r.SignalsMerged,
                        progressMerged = r.ProgressMerged
                    }
                });
                return;
            }
            Console.WriteLine(Lang.T("已合并：{0} → {1}", srcDir, dstDir));
            Console.WriteLine(Lang.T("  订阅源：新增 {0}，复用已有 {1}", r.FeedsAdded, r.FeedsReused));
            Console.WriteLine(Lang.T("  文章  ：新增 {0}，已存在跳过 {1}", r.ItemsAdded, r.ItemsSkipped));
            Console.WriteLine(Lang.T("  标记/进度：收藏 {0} 条，阅读进度 {1} 条", r.SignalsMerged, r.ProgressMerged));
            Console.WriteLine(Lang.T("  未合并：向量索引（在主库重跑 sip --index 即可）、源规则/去重规则/健康状态/导入资产/全文缓存"));
        }
        catch (Exception ex)
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "MERGE_FAILED", message = ex.Message } });
            else Console.WriteLine(Lang.T("合并失败：{0}", ex.Message));
        }
    }

    // ── 合并实现 ──────────────────────────────────────────────
    readonly record struct MergeResult(int FeedsAdded, int FeedsReused, int ItemsAdded, int ItemsSkipped, int SignalsMerged, int ProgressMerged);

    /// <summary>把 <paramref name="srcDir"/> 的库合并进 <paramref name="dstDir"/>。
    /// 三条规矩：
    ///   ① 只读来源、只写目标（两个连接，不用 ATTACH —— 跨库事务的语义坑不值得踩）
    ///   ② 去重按订阅源 FeedUrl、文章 Guid（无 Guid 退 Link，再无退 标题+时间）
    ///   ③ 幂等：同一份合两次，第二次全是"已存在跳过"</summary>
    static MergeResult MergeLibraryInto(string srcDir, string dstDir)
    {
        string srcDb = Path.Combine(srcDir, "rss.db");
        string dstDb = Path.Combine(dstDir, "rss.db");
        using var src = new SqliteConnection($"Data Source={srcDb};Mode=ReadOnly;Pooling=False");
        using var dst = new SqliteConnection($"Data Source={dstDb};Pooling=False");
        src.Open();
        dst.Open();

        var feedCols = SharedColumns(src, dst, "Feeds");
        var itemCols = SharedColumns(src, dst, "Items");
        var feedMap = new Dictionary<long, long>();
        var itemMap = new Dictionary<long, long>();
        int feedsAdded = 0, feedsReused = 0, itemsAdded = 0, itemsSkipped = 0;

        using (var tx = dst.BeginTransaction())
        {
            // ── 订阅源：FeedUrl 相同即视为同一源 ──
            var urlIdx = feedCols.IndexOf("FeedUrl");
            using (var read = src.CreateCommand())
            {
                read.CommandText = SelectSql("Feeds", feedCols);
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    long srcFeedId = r.GetInt64(0);
                    string url = urlIdx >= 0 ? AsText(r, urlIdx + 1) : "";
                    long? dstFeedId = null;
                    if (!string.IsNullOrWhiteSpace(url))
                        dstFeedId = ScalarLong(dst, tx, "SELECT Id FROM Feeds WHERE FeedUrl = @u LIMIT 1", ("@u", url));

                    if (dstFeedId == null)
                    {
                        var vals = ReadValues(r, feedCols);
                        // Title 是 NOT NULL：源里为空时兜一个占位，别让整次合并在约束上炸掉
                        int tIdx = feedCols.IndexOf("Title");
                        if (tIdx >= 0 && string.IsNullOrWhiteSpace(vals[tIdx] as string)) vals[tIdx] = "(未命名订阅源)";
                        dstFeedId = InsertRow(dst, tx, "Feeds", feedCols, vals);
                        feedsAdded++;
                    }
                    else feedsReused++;
                    feedMap[srcFeedId] = dstFeedId.Value;
                }
            }

            // ── 文章：同源内 Guid 相同即视为同一篇 ──
            var guidIdx = itemCols.IndexOf("Guid");
            var linkIdx = itemCols.IndexOf("Link");
            var titleIdx = itemCols.IndexOf("Title");
            var dateIdx = itemCols.IndexOf("PublishDate");
            using (var read = src.CreateCommand())
            {
                read.CommandText = SelectSql("Items", itemCols, "FeedId");
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    long srcItemId = r.GetInt64(0);
                    if (!feedMap.TryGetValue(r.GetInt64(1), out long dstFeedId)) continue;   // 没映射到源：跳过

                    string guid = guidIdx >= 0 ? AsText(r, guidIdx + 2) : "";
                    string link = linkIdx >= 0 ? AsText(r, linkIdx + 2) : "";
                    long? existing = null;
                    if (!string.IsNullOrWhiteSpace(guid))
                        existing = ScalarLong(dst, tx, "SELECT Id FROM Items WHERE FeedId = @f AND Guid = @g LIMIT 1", ("@f", dstFeedId), ("@g", guid));
                    if (existing == null && !string.IsNullOrWhiteSpace(link))
                        existing = ScalarLong(dst, tx, "SELECT Id FROM Items WHERE FeedId = @f AND Link = @l LIMIT 1", ("@f", dstFeedId), ("@l", link));
                    if (existing == null && string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(link))
                    {
                        string title = titleIdx >= 0 ? AsText(r, titleIdx + 2) : "";
                        string date = dateIdx >= 0 ? AsText(r, dateIdx + 2) : "";
                        if (title.Length > 0 || date.Length > 0)
                            existing = ScalarLong(dst, tx, "SELECT Id FROM Items WHERE FeedId = @f AND IFNULL(Title,'') = @t AND IFNULL(PublishDate,'') = @d LIMIT 1",
                                ("@f", dstFeedId), ("@t", title), ("@d", date));
                    }

                    if (existing != null)
                    {
                        itemsSkipped++;
                        itemMap[srcItemId] = existing.Value;
                        continue;
                    }
                    var vals = ReadValues(r, itemCols, offset: 2);
                    // FeedId 必须指向目标库里那条源
                    int fIdx = itemCols.IndexOf("FeedId");
                    if (fIdx >= 0) vals[fIdx] = dstFeedId;
                    long newId = InsertRow(dst, tx, "Items", itemCols, vals);
                    itemMap[srcItemId] = newId;
                    itemsAdded++;
                }
            }
            tx.Commit();
        }

        // ── 侧挂文件：收藏标记 + 阅读进度（按 id 映射搬过去；目标已有的以目标为准）──
        int signals = MergeSignals(srcDir, dstDir, itemMap);
        int progress = MergeProgress(srcDir, dstDir, itemMap);
        return new MergeResult(feedsAdded, feedsReused, itemsAdded, itemsSkipped, signals, progress);
    }

    static string SelectSql(string table, List<string> cols, string extraLeading = "")
    {
        var parts = new List<string> { "Id" };
        if (extraLeading.Length > 0) parts.Add(extraLeading);
        parts.AddRange(cols);
        return $"SELECT {string.Join(", ", parts)} FROM {table}";
    }

    /// <summary>两张表共有的列（排除 Id：目标库自己分配）。源库可能是旧版本，缺列是常态。</summary>
    static List<string> SharedColumns(SqliteConnection src, SqliteConnection dst, string table)
    {
        var s = TableColumns(src, table);
        var d = TableColumns(dst, table);
        return d.Where(c => c != "Id" && s.Contains(c)).ToList();
    }

    static HashSet<string> TableColumns(SqliteConnection conn, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(1));
        return set;
    }

    static object?[] ReadValues(SqliteDataReader r, List<string> cols, int offset = 1)
    {
        var v = new object?[cols.Count];
        for (int i = 0; i < cols.Count; i++)
        {
            int idx = offset + i;
            v[i] = r.IsDBNull(idx) ? null : r.GetValue(idx);
        }
        return v;
    }

    static string AsText(SqliteDataReader r, int idx)
        => r.IsDBNull(idx) ? "" : (r.GetValue(idx)?.ToString() ?? "");

    static long InsertRow(SqliteConnection conn, SqliteTransaction tx, string table, List<string> cols, object?[] vals)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var names = string.Join(", ", cols);
        var ph = string.Join(", ", cols.Select((_, i) => "@p" + i));
        cmd.CommandText = $"INSERT INTO {table} ({names}) VALUES ({ph}); SELECT last_insert_rowid();";
        for (int i = 0; i < cols.Count; i++)
            cmd.Parameters.AddWithValue("@p" + i, vals[i] ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    static long? ScalarLong(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        object? o = cmd.ExecuteScalar();
        return o == null || o is DBNull ? null : Convert.ToInt64(o);
    }

    // ── 侧挂文件（按 id 映射搬运）────────────────────────────
    static int MergeSignals(string srcDir, string dstDir, Dictionary<long, long> itemMap)
    {
        try
        {
            var mine = LoadJsonMap<SignalEntry>(Path.Combine(srcDir, "article_signals.json"));
            if (mine.Count == 0) return 0;
            string dstPath = Path.Combine(dstDir, "article_signals.json");
            var theirs = LoadJsonMap<SignalEntry>(dstPath);
            int n = 0;
            foreach (var (k, v) in mine)
            {
                if (!long.TryParse(k, out long srcId) || !itemMap.TryGetValue(srcId, out long newId)) continue;
                string key = newId.ToString();
                if (theirs.ContainsKey(key)) continue;      // 目标已有的以目标为准
                theirs[key] = v;
                n++;
            }
            if (n > 0) SaveJsonMap(dstPath, theirs);
            return n;
        }
        catch { return 0; }
    }

    static int MergeProgress(string srcDir, string dstDir, Dictionary<long, long> itemMap)
    {
        try
        {
            var mine = LoadJsonMap<int>(Path.Combine(srcDir, "reading_progress.json"));
            if (mine.Count == 0) return 0;
            string dstPath = Path.Combine(dstDir, "reading_progress.json");
            var theirs = LoadJsonMap<int>(dstPath);
            int n = 0;
            foreach (var (k, v) in mine)
            {
                if (!long.TryParse(k, out long srcId) || !itemMap.TryGetValue(srcId, out long newId)) continue;
                string key = newId.ToString();
                if (theirs.ContainsKey(key)) continue;
                theirs[key] = v;
                n++;
            }
            if (n > 0) SaveJsonMap(dstPath, theirs);
            return n;
        }
        catch { return 0; }
    }

    static Dictionary<string, T> LoadJsonMap<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path)) ?? new();
        }
        catch { return new(); }
    }

    static void SaveJsonMap<T>(string path, Dictionary<string, T> map)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
