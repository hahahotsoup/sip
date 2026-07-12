// ===== 引用外部包 =====
// using 相当于"导入工具箱"，每个包提供不同的工具
// System.* 是 C# 自带的（网络、文件、文字处理）
// CodeHollow.FeedReader 是第三方包，专门解析 RSS/Atom
// Microsoft.Data.Sqlite 是微软提供的轻量数据库
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CodeHollow.FeedReader;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Data.Sqlite;

// 工作目录 = exe 所在文件夹（Mac/Linux/Windows 都适用）
string workDir = AppDomain.CurrentDomain.BaseDirectory;
string dbPath = Path.Combine(workDir, "rss.db");
InitDatabase(dbPath);

// ═══════════ CLI 模式 ═══════════
if (args.Length > 0)
{
    RunCli(args, dbPath);
    return 0;
}

Console.WriteLine($"工作目录：{workDir}");

// ═══════════ 主循环 ═══════════
// while(true) 是死循环，程序一直跑、等你输入命令
while (true)
{
    Console.WriteLine("今天要来点rss嘛？A 看看已有订阅 | B 下载新RSS源 | 随意输入什么退出");
    var a = Console.ReadLine();

    if (a == "A")
    {
        while (true)
        {
            // --- 先列出所有订阅源 ---
            ListFeedsFromDb(dbPath);

            // --- 子菜单 ---
            // 输入数字 → U → 更新rss | T 编号 → 归档化 | R 编号 → 去归档化 | D 编号 → 删除 | L 编号 → 列出指定订阅源文章 |随意输入什么退出
            Console.Write("编号=更新 | T=归档化 | R=去归档化 | D=删除 | L=列出指定订阅源文章：| 随意输入什么退出");
            string input = Console.ReadLine()!;

            if (input.StartsWith("T", StringComparison.OrdinalIgnoreCase))
            {
                // === 加时间戳 ===
                if (!int.TryParse(input[1..].Trim(), out int tid))
                {
                    Console.WriteLine("格式错误。正确：T 1");
                    continue;
                }
                AddTimestamp(tid, dbPath);
            }
            else if (input.StartsWith("R", StringComparison.OrdinalIgnoreCase))
            {
                // === 去时间戳 ===
                if (!int.TryParse(input[1..].Trim(), out int rid))
                {
                    Console.WriteLine("格式错误。正确：R 1");
                    continue;
                }
                RemoveTimestamp(rid, dbPath);
            }
            else if (input.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                // === 删除 ===
                if (!int.TryParse(input[1..].Trim(), out int did))
                {
                    Console.WriteLine("格式错误。正确：D 1");
                    continue;
                }
                DeleteFeed(did, dbPath);
            }
            else if (input.StartsWith("U", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(input[1..].Trim(), out int displayNum))
                {
                    Console.WriteLine("格式错误。正确：U 1");
                    continue;
                }
                await UpdateFeed(displayNum, dbPath);
            }
            else if (input.StartsWith("L", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(input[1..].Trim(), out int lNum))
                {
                    Console.WriteLine("格式错误。正确：L 1");
                    continue;
                }
                // L 进入文章管理子循环
                int feedRealId = GetRealId(lNum, dbPath);
                if (feedRealId == 0) { Console.WriteLine("没找到这个编号"); continue; }
                ManageArticles(feedRealId, lNum, dbPath);
            }
            else
            {
                if (!int.TryParse(input, out int displayNum))
                {
                    break;
                }
            }
        }
    }
    else if (a == "B")
    {
        // B → 输入一个 RSS 链接，下载并存入数据库
        Console.WriteLine("请输入 RSS 链接：");
        string url = Console.ReadLine()!;

        try
        {
            // await = "等这个网络操作完成，期间程序不会卡死"
            await DownloadAndSaveToDb(url, dbPath);
        }
        // 下面是三种不同类型的错误，分别处理
        catch (TaskCanceledException cancelEx)  // 超时了
        {
            Console.WriteLine($"下太久了 是不是下错了？ {cancelEx.Message}");
        }
        catch (HttpRequestException httpEx)  // 网络本身的问题
        {
            Console.WriteLine($"网络错误：{httpEx.Message}");
        }
        catch (Exception ex)  // 兜底：所有上面没列出的错误
        {
            Console.WriteLine($"发生错误：{ex.Message}");
        }
    }
    else
    {
        // 输入了 A/B 以外的字符
        Console.WriteLine("怪东西 爬");
        return 0;
    }
}

// ═══════════════════════════════════════════════════
// 以下是所有方法，按调用顺序排列
// ═══════════════════════════════════════════════════

// ═══════════ CLI 参数处理 ═══════════
void RunCli(string[] args, string dbPath)
{
    var cmd = args[0].ToLower();

    if (cmd is "-h" or "--help")
    {
        PrintHelp();
        return;
    }

    if (cmd is "-l" or "--list")
    {
        ListFeedsFromDb(dbPath);
        return;
    }

    if (args.Length < 2)
    {
        Console.WriteLine($"缺少参数。用法: rssreader {cmd} <值>");
        return;
    }

    switch (cmd)
    {
        case "-u" or "--update":
            if (!int.TryParse(args[1], out int aNum)) { Console.WriteLine("编号必须是数字"); return; }
            UpdateFeed(aNum, dbPath).Wait();
            break;
        case "-d" or "--download":
            DownloadCli(args[1], dbPath);
            break;
        case "-a" or "--archive":
            if (!int.TryParse(args[1], out int tNum)) { Console.WriteLine("编号必须是数字"); return; }
            AddTimestamp(tNum, dbPath);
            break;
        case "-una" or "--unarchive":
            if (!int.TryParse(args[1], out int uNum)) { Console.WriteLine("编号必须是数字"); return; }
            RemoveTimestamp(uNum, dbPath);
            break;
        case "-r" or "--remove":
            if (!int.TryParse(args[1], out int dNum)) { Console.WriteLine("编号必须是数字"); return; }
            DeleteFeed(dNum, dbPath);
            break;
        default:
            Console.WriteLine($"未知命令: {cmd}");
            PrintHelp();
            break;
    }
}

void PrintHelp()
{
    Console.WriteLine(@"
用法: rssreader <命令> [参数]

命令:
  -l, --list       列出所有订阅源
  -u, --update     更新指定订阅源（编号）
  -d, --download   下载新的 RSS 源（URL）
  -a, --archive    归档（加时间戳）
  -una, --unarchive 去归档
  -r, --remove     删除订阅源
  -h, --help       显示此帮助

示例:
  rssreader -l
  rssreader -d https://example.com/rss
  rssreader -u 1
  rssreader -a 1
  rssreader -r 1
");
}

// ═══════════ 更新指定订阅源（A 菜单和 CLI 共用）═══════════
async Task UpdateFeed(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine("没找到这个编号"); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, FeedUrl FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    using var r = cmd.ExecuteReader();
    r.Read();
    string title = r.GetString(0);
    string url = r.GetString(1);
    r.Close();

    if (IsArchived(title)) { Console.WriteLine($" {title} 已归档，不能更新"); return; }

    try { await DownloadAndSaveToDb(url, dbPath); Console.WriteLine("更新完成"); }
    catch (TaskCanceledException) { Console.WriteLine("下载超时，请检查网络或链接是否有效"); }
    catch (HttpRequestException) { Console.WriteLine("网络请求失败，链接可能已失效"); }
    catch (SqliteException ex) { Console.WriteLine($"数据库出错：{ex.Message}"); }
    catch (Exception ex) { Console.WriteLine($"未知错误：{ex.Message}"); }
}

// CLI 模式下载（同步等待异步方法）
void DownloadCli(string url, string dbPath)
{
    try { DownloadAndSaveToDb(url, dbPath).Wait(); Console.WriteLine("下载完成"); }
    catch (Exception ex) { Console.WriteLine($"出错: {ex.Message}"); }
}

// ═══════════ 建表方法 ═══════════
// 只在程序启动时调用一次。IF NOT EXISTS 保证不会覆盖已有数据。
// 两张表的关系：Feeds 是"班级"，Items 是"学生"，FeedId 就是学生属于哪个班级。
void InitDatabase(string dbPath)
{
    // $ 开头是"字符串插值"：把 {dbPath} 替换成实际路径
    // using 保证连接用完会自动关闭，不占资源
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();  // 打开连接

    var cmd = conn.CreateCommand();  // 创建一个"命令对象"
    // 先开外键约束，再建表
    cmd.CommandText = "PRAGMA foreign_keys = ON;";
    cmd.ExecuteNonQuery();

    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Feeds ( --管理rss链接
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Title       TEXT    NOT NULL,    -- 订阅源标题
            FeedUrl     TEXT,               -- 下载链接（唯一标识，用来去重）
            Link        TEXT,               -- 博客首页网址
            Description TEXT,               -- 一句话简介
            LastFetched TEXT,               -- 上次抓取时间
            RawXml      TEXT                -- 原始XML，留着以后做diff
        );

        CREATE TABLE IF NOT EXISTS Items ( --管理rss文章
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            FeedId      INTEGER NOT NULL,   -- 外键：指向 Feeds 表的 Id
            Title       TEXT,               -- 文章标题
            Link        TEXT,               -- 文章链接
            Description TEXT,               -- 文章摘要
            Author      TEXT,               -- 作者
            PublishDate TEXT,               -- 发布时间
            Content     TEXT,               -- 正文
            Guid        TEXT,               -- 文章唯一标识（同Guid可有多版本）
            Status      TEXT    DEFAULT 'active',  -- active/archived/deleted
            Version     INTEGER DEFAULT 1,         -- 同一Guid的第几版
            ArchivedAt  TEXT,                      -- 归档时间戳
            FOREIGN KEY (FeedId) REFERENCES Feeds(Id)  -- 需配合 PRAGMA
        );
    ";
    cmd.ExecuteNonQuery();
}
// ═══════════ 文章管理子循环 ═══════════
void ManageArticles(int feedRealId, int feedDisplayNum, string dbPath)
{
    while (true)
    {
        ListArticlesFromDb(feedRealId, feedDisplayNum, dbPath);

        Console.Write("  D 编号=删除文章 | Q=返回上级：");
        string input = Console.ReadLine()!;

        if (input.StartsWith("D", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(input[1..].Trim(), out int artNum))
            {
                Console.WriteLine("格式错误。正确：D 1");
                continue;
            }
            DeleteArticle(feedRealId, artNum, dbPath);
        }
        else if (input.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
        {
            break;  // 返回 A 菜单
        }
        else
        {
            Console.WriteLine("未知命令，D=删除 Q=返回");
        }
    }
}

// ═══════════ 列出指定源的所有文章（含 ROW_NUMBER 显示编号） ═══════════
void ListArticlesFromDb(int feedRealId, int feedDisplayNum, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 查 Feed 标题
    var titleCmd = conn.CreateCommand();
    titleCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    titleCmd.Parameters.AddWithValue("@id", feedRealId);
    string feedTitle = titleCmd.ExecuteScalar()!.ToString()!;
    Console.WriteLine($"── [{feedDisplayNum}] {feedTitle} 的文章列表 ──");

    // 用 ROW_NUMBER 给文章编显示号（删后自动继位）
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Guid, Title, Status, Version,
               ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum
        FROM Items
        WHERE FeedId = @fid
        ORDER BY Id
    ";
    cmd.Parameters.AddWithValue("@fid", feedRealId);
    using var reader = cmd.ExecuteReader();
    if (!reader.HasRows)
    {
        Console.WriteLine("  这个源还没有文章");
        return;
    }
    while (reader.Read())
    {
        int displayNum = reader.GetInt32(5);    // 第5列 DisplayNum
        string status  = reader.GetString(3);   // 第3列 Status
        string title   = reader.GetString(2);   // 第2列 Title
        int version    = reader.GetInt32(4);    // 第4列 Version

        string tag = status switch
        {
            "active"   => "[现]",
            "archived" => "[旧]",
            "deleted"  => "[删]",
            _          => "[?]"
        };
        Console.WriteLine($"  [{displayNum}] {tag} v{version} | {title}");
    }
}

// ═══════════ 真删文章（物理删除，不可恢复） ═══════════
void DeleteArticle(int feedRealId, int articleDisplayNum, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 显示编号 → 真实 Id（只查当前 Feed 的文章）
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title FROM (
            SELECT Id, Title, ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum
            FROM Items WHERE FeedId = @fid
        ) WHERE DisplayNum = @n
    ";
    cmd.Parameters.AddWithValue("@fid", feedRealId);
    cmd.Parameters.AddWithValue("@n", articleDisplayNum);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) { Console.WriteLine("没找到这篇文章"); return; }
    long artRealId = reader.GetInt64(0);
    string artTitle = reader.GetString(1);
    reader.Close();

    Console.Write($"确定永久删除《{artTitle}》？此操作不可恢复！(y/n)：");
    if (Console.ReadLine()!.ToLower() != "y") { Console.WriteLine("已取消"); return; }

    cmd.CommandText = "DELETE FROM Items WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", artRealId);
    cmd.ExecuteNonQuery();

    Console.WriteLine($"《{artTitle}》已永久删除");
}

// ═══════════ 列表方法：显示数据库中所有订阅源 ═══════════
// ROW_NUMBER() 保证显示出来永远是 1, 2, 3 连续编号（不管中间有没有删过）
// 但操作（更新/时间戳/删除）仍然用真实的 Id，因为 Items 表靠它关联
void ListFeedsFromDb(string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title,
               (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'active')   AS ActiveCount,
               (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'archived') AS ArchiveCount,
               (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'deleted')  AS DeleteCount,
               ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum
        FROM Feeds
    ";
    // 六列：[真实Id, 标题, 活跃数, 旧版数, 已删数, 显示编号]

    using var reader = cmd.ExecuteReader();
    if (!reader.HasRows)
    {
        Console.WriteLine("数据库里还没有订阅源");
        return;
    }

    while (reader.Read())
    {
        int active = reader.GetInt32(2);
        int archive = reader.GetInt32(3);
        int deleted = reader.GetInt32(4);

        // 拼出显示文本：只显示非零的状态
        var parts = new List<string>();
        if (active > 0)  parts.Add($"现存{active+deleted}篇");
        if (archive > 0) parts.Add($"其中有{archive} 篇发生了更改");
        if (deleted > 0) parts.Add($"{deleted} 篇被作者删掉了，但是我们已经帮你存档了");
        string stats = string.Join(", ", parts);

        Console.WriteLine($"[{reader.GetInt32(5)}] {reader.GetString(1)} — {stats}");
    }
}

// ═══════════ 核心方法：下载 RSS → 解析 → 去重 → 写入数据库 ═══════════
async Task DownloadAndSaveToDb(string url, string dbPath)
{
    // --- 第 1 步：下载 RSS 原始 XML ---
    // 不加 User-Agent 有些服务器会返回 403 拒绝
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
    Console.WriteLine("正在下载...");
    string rawXml = await client.GetStringAsync(url);

    // --- 第 2 步：解析 ---
    var feed = FeedReader.ReadFromString(rawXml);

    // --- 第 3 步：打开数据库 ---
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // --- 第 4 步：检查是否已存在同名且未归档的订阅源 ---
    // 已归档的（标题带时间戳）不参与比对，直接当新源处理
    string? oldXml = GetActiveRawXml(feed.Title, conn);
    long feedId;

    bool isNewFeed;  // 新源还是更新已有源

    if (oldXml != null)
    {
        // 同名未归档源存在！先用文本 diff 比对 Feed 级别变化
        isNewFeed = false;
        Console.WriteLine($"订阅源{feed.Title}已存在，正在比对...");
        bool hasChanges = ShowFeedXmlDiff(oldXml, rawXml);

        if (hasChanges)
        {
            var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = @"
                UPDATE Feeds SET RawXml = @rawXml, LastFetched = @fetched
                WHERE Title = @title
            ";
            updateCmd.Parameters.AddWithValue("@rawXml", rawXml);
            updateCmd.Parameters.AddWithValue("@fetched", DateTime.Now.ToString("O"));
            updateCmd.Parameters.AddWithValue("@title", feed.Title);
            updateCmd.ExecuteNonQuery();
            Console.WriteLine("内容有变化，已更新订阅源。");
        }
        else
        {
            Console.WriteLine("内容无变化，跳过更新。");
        }

        var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT Id FROM Feeds WHERE Title = @title";
        idCmd.Parameters.AddWithValue("@title", feed.Title);
        feedId = (long)idCmd.ExecuteScalar()!;
    }
    else
    {
        // 新订阅源 → 插入（不含归档源的冲突）
        isNewFeed = true;
        var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO Feeds (Title, FeedUrl, Link, Description, LastFetched, RawXml)
            VALUES (@title, @url, @link, @desc, @fetched, @rawXml)
        ";
        insertCmd.Parameters.AddWithValue("@title", feed.Title);
        insertCmd.Parameters.AddWithValue("@url", url);
        insertCmd.Parameters.AddWithValue("@link", feed.Link ?? "");
        insertCmd.Parameters.AddWithValue("@desc", feed.Description ?? "");
        insertCmd.Parameters.AddWithValue("@fetched", DateTime.Now.ToString("O"));
        insertCmd.Parameters.AddWithValue("@rawXml", rawXml);
        insertCmd.ExecuteNonQuery();

        insertCmd.CommandText = "SELECT last_insert_rowid()";
        feedId = (long)insertCmd.ExecuteScalar()!;
    }

    // --- 第 5 步：ShowDiff 负责检测文章变化 + 输出 + 执行归档/插入/标记删除 ---
    // 新源 → 全量插入不过滤；旧源 → 逐篇比对
    ShowDiff(feed, feedId, conn, isNewFeed);

    Console.WriteLine($"{feed.Title} 写入完成");
}

// ═══════════ 辅助方法：按标题查未归档源的旧 RawXml ═══════════
// 只匹配无时间戳后缀的源，已归档的（带 _yyyymmdd_hhmmss）不参与比对
// 返回 null = 没找到或全是归档源 → 当作新源处理
string? GetActiveRawXml(string title, SqliteConnection conn)
{
    // 先查出所有同名源的 RawXml 和 Title，用 C# IsArchived 过滤
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, RawXml FROM Feeds WHERE Title = @title OR Title LIKE @title || '\\_%' ESCAPE '\\'";
    cmd.Parameters.AddWithValue("@title", title);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        string t = reader.GetString(0);
        if (!IsArchived(t))  // 只返回未归档的
            return reader.GetString(1);
    }
    return null;  // 没找到或全是归档源
}

// ═══════════ 判断标题是否有时间戳后缀（即是否已被归档） ═══════════
bool IsArchived(string title)
{
    return Regex.IsMatch(title, @"_\d{8}_\d{6}$");
}


// ═══════════ 显示编号 → 真实 Id ═══════════
// 列表显示用了 ROW_NUMBER()，用户输入的是显示编号（1,2,3...）
// 这个方法把显示编号转换成数据库里真实的 Id（可能是 1,3,5...有断档）
// 返回 0 表示找不到
int GetRealId(int displayNum, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id FROM (
            SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum
            FROM Feeds
        ) WHERE DisplayNum = @n
    ";
    cmd.Parameters.AddWithValue("@n", displayNum);
    object? result = cmd.ExecuteScalar();
    return result is null ? 0 : Convert.ToInt32(result);
}

// ═══════════ 删除订阅源 + 它的所有文章 ═══════════
void DeleteFeed(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine("没找到这个编号"); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 1. 查标题和文章数，用于确认提示
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Title, (SELECT COUNT(*) FROM Items WHERE FeedId = @id)
        FROM Feeds WHERE Id = @id
    ";
    cmd.Parameters.AddWithValue("@id", realId);
    using var reader = cmd.ExecuteReader();
    reader.Read();
    string title = reader.GetString(0);
    int itemCount = reader.GetInt32(1);
    reader.Close();

    Console.Write($"确定删除 {title} 及其 {itemCount} 篇文章？(y/n)：");
    if (Console.ReadLine()!.ToLower() != "y")
    {
        Console.WriteLine("已取消");
        return;
    }

    // 2. 先删文章
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();

    // 3. 再删订阅源
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();

    Console.WriteLine($"{title}已删除");
}

// ═══════════ 加时间戳：标题 + _20260712_143000 ═══════════
// 加完后标题变了，下次下载同名源时 GetOldRawXml 找不到，
// 就会被当作新订阅源处理，不会触发去重
void AddTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine("没找到这个编号"); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 1. 查当前标题
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string oldTitle = cmd.ExecuteScalar()!.ToString()!;

    // 2. 已经归档的不能再归档
    if (IsArchived(oldTitle))
    {
        Console.WriteLine($" {oldTitle} 已被归档，无需重复操作");
        return;
    }

    // 3. 追加时间戳
    string newTitle = oldTitle + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // 4. 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", newTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine($"标题已变更：{oldTitle} → {newTitle} ");
}

// ═══════════ 去时间戳：去掉 _yyyymmdd_hhmmss 后缀 ═══════════
// 去掉之前检查原始标题是否已存在，防止冲突
void RemoveTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine("没找到这个编号"); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 1. 查当前标题
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string title = cmd.ExecuteScalar()!.ToString()!;

    // 2. 用正则去掉末尾 _8位数字_6位数字 的时间戳
    string plainTitle = Regex.Replace(title, @"_\d{8}_\d{6}$", "");

    if (plainTitle == title)
    {
        Console.WriteLine($" {title} 未归档化欸");
        return;
    }

    // 3. 检查 plainTitle 是否已被其他源占用（排除自己）
    cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Title = @title AND Id != @id";
    cmd.Parameters.AddWithValue("@title", plainTitle);
    long conflict = (long)cmd.ExecuteScalar()!;
    if (conflict > 0)
    {
        Console.WriteLine($"冲突！已存在另一个名为 {plainTitle} 的我，无法去除时间戳");
        return;
    }

    // 4. 安全 → 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", plainTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine($"时间戳已去除： {title} → {plainTitle} ");
}

// ════════════════════════════════════════════════════════
// 下面是 ShowDiff 的两个版本
// ════════════════════════════════════════════════════════

// ═══════════ 辅助方法：插入一篇新文章到 Items 表 ═══════════
// 统一管理 INSERT SQL，避免三处重复写同样的代码
void InsertNewItem(SqliteConnection conn, long feedId, FeedItem item, string guid, int version)
{
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Items (FeedId, Title, Link, Description, Author, PublishDate, Content, Guid, Status, Version)
        VALUES (@fid, @title, @link, @desc, @author, @pub, @content, @guid, 'active', @ver)
    ";
    cmd.Parameters.AddWithValue("@fid", feedId);
    cmd.Parameters.AddWithValue("@title", item.Title ?? "");
    cmd.Parameters.AddWithValue("@link", item.Link ?? "");
    cmd.Parameters.AddWithValue("@desc", item.Description ?? "");
    cmd.Parameters.AddWithValue("@author", item.Author ?? "");
    cmd.Parameters.AddWithValue("@pub", item.PublishingDate?.ToString("O") ?? "");
    cmd.Parameters.AddWithValue("@content", item.Content ?? "");
    cmd.Parameters.AddWithValue("@guid", guid);
    cmd.Parameters.AddWithValue("@ver", version);
    cmd.ExecuteNonQuery();
}

// ═══════════ ShowDiff（文章级别）：检测新增/修改/删除 + 输出 + 执行 ═══════════
// isNewFeed=true  → 新订阅源，全量插入 + 跳过删除检测
// isNewFeed=false → 已有源，逐篇比对：新增/修改/删除
void ShowDiff(Feed newFeed, long feedId, SqliteConnection conn, bool isNewFeed = false)
{
    int newCount = 0;
    int modifyCount = 0;
    var newGuids = new List<string>();

    foreach (var item in newFeed.Items)
    {
        string guid = item.Id ?? item.Link ?? "";
        newGuids.Add(guid);

        if (isNewFeed)
        {
            // 新源模式：不查重，直接插入
            InsertNewItem(conn, feedId, item, guid, version: 1);
            newCount++;
            continue;
        }

        // --- 更新模式：查是否已有 active 状态的同 Guid 文章 ---
        var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT Id, Version, Title, Content
            FROM Items WHERE Guid = @guid AND Status = 'active'
        ";
        checkCmd.Parameters.AddWithValue("@guid", guid);

        using var reader = checkCmd.ExecuteReader();

        if (reader.Read())
        {
            // --- 已有 → 检查内容是否变化 ---
            long existingId = reader.GetInt64(0);
            int oldVersion = reader.GetInt32(1);
            string oldContent = reader.IsDBNull(3) ? "" : reader.GetString(3);
            reader.Close();

            if (oldContent == (item.Content ?? ""))
                continue;  // 内容相同 → 跳过

            // 内容不同 → 强制归档该 Guid 下所有 active 的旧版（防止残留多版本）
            var archiveCmd = conn.CreateCommand();
            archiveCmd.CommandText = @"
                UPDATE Items SET Status = 'archived', ArchivedAt = @now
                WHERE Guid = @guid AND Status = 'active'
            ";
            archiveCmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
            archiveCmd.Parameters.AddWithValue("@guid", guid);
            archiveCmd.ExecuteNonQuery();

            // 插入新版本
            InsertNewItem(conn, feedId, item, guid, version: oldVersion + 1);

            Console.WriteLine($"  [已归档] {item.Title} 作者修改了内容，旧版已保留");
            modifyCount++;
        }
        else
        {
            reader.Close();
            // 新文章 → 直接插入
            InsertNewItem(conn, feedId, item, guid, version: 1);
            newCount++;
        }
    }

    // 新源跳过删除检测（没有旧数据可比）
    if (isNewFeed)
    {
        Console.WriteLine($"  新增 {newCount} 篇");
        return;
    }

    // --- 检测被删文章：数据库里 Status='active' 但 Guid 不在新 RSS 列表里 → 作者删了 ---
    var delCmd = conn.CreateCommand();
    delCmd.CommandText = "SELECT Id, Guid, Title FROM Items WHERE FeedId = @fid AND Status = 'active'";
    delCmd.Parameters.AddWithValue("@fid", feedId);

    int deleteCount = 0;  // 被删计数
    using (var delReader = delCmd.ExecuteReader())
    {
        var deletedIds = new List<long>();  // 先记下要标记的 Id
        while (delReader.Read())
        {
            if (!newGuids.Contains(delReader.GetString(1)))  // Guid 不在新列表 → 被删
            {
                deletedIds.Add(delReader.GetInt64(0));       // 记下第0列：真实 Id
                Console.WriteLine($"  [已删除] {delReader.GetString(2)} 作者删除了此文");
            }
        }
        delReader.Close();  // 关掉 reader 才能做 UPDATE

        // 批量标记为 deleted
        foreach (long delId in deletedIds)
        {
            var markCmd = conn.CreateCommand();
            markCmd.CommandText = "UPDATE Items SET Status = 'deleted', ArchivedAt = @now WHERE Id = @id";
            markCmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
            markCmd.Parameters.AddWithValue("@id", delId);
            markCmd.ExecuteNonQuery();
        }

        deleteCount = deletedIds.Count;
    }

    // 汇总输出
    Console.WriteLine($"  新增 {newCount} 篇，修改 {modifyCount} 篇，删除 {deleteCount} 篇");
}

// ═══════════ ShowDiff（Feed 级别）：纯文本比对，看旧 XML 和新 XML 有无差异 ═══════════
// 只负责输出和返回 bool，不做任何数据库操作
bool ShowFeedXmlDiff(string oldRaw, string newRaw)
{
    try
    {
        var oldFeed = FeedReader.ReadFromString(oldRaw);  // 把旧 XML 解析成 Feed 对象
        var newFeed = FeedReader.ReadFromString(newRaw);  // 把新 XML 解析成 Feed 对象

        // 把每条文章压成一行摘要（方便做 diff），然后用换行拼成一个大字符串
        string oldSummary = string.Join(Environment.NewLine, oldFeed.Items.Select(GetItemSummary));
        string newSummary = string.Join(Environment.NewLine, newFeed.Items.Select(GetItemSummary));

        // DiffPlex 是做文本比较的库，比较两个字符串哪行多了、少了、改了
        var diffResult = new InlineDiffBuilder(new Differ()).BuildDiffModel(oldSummary, newSummary);

        bool hasChanges = false;
        foreach (var line in diffResult.Lines)  // 逐行看差异
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:   // 新增文章（新 RSS 有、旧 RSS 没有）
                    Console.WriteLine($"+ {line.Text}");
                    hasChanges = true;
                    break;
                case ChangeType.Deleted:    // 被删掉的文章（旧 RSS 有、新 RSS 没有）
                    Console.WriteLine($"- {line.Text}");
                    hasChanges = true;
                    break;
                case ChangeType.Modified:   // 内容被修改的文章
                    Console.WriteLine($"~ {line.Text}");
                    hasChanges = true;
                    break;
            }
        }

        if (!hasChanges)  // 一个变化都没有
            Console.WriteLine("新旧 RSS 完全相同，无新增、删除或修改。");

        return hasChanges;  // 把结果返回给调用方，让它决定是否更新
    }
    catch (Exception ex)
    {
        Console.WriteLine($"比较条目差异时出错：{ex.Message}");
        return false;  // 出错了保守处理：不用旧数据覆盖，当作没变化
    }
}

// ═══════════ GetItemSummary：生成文章摘要行，供文本 diff 显示用 ═══════════
string GetItemSummary(FeedItem item)
{
    string id = !string.IsNullOrEmpty(item.Id) ? item.Id : item.Link ?? item.Title ?? "未知";
    return $"[{id}] {item.Title}";
}