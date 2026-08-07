// ===== 引用外部包 =====
// using 相当于"导入工具箱"，每个包提供不同的工具
// System.* 是 C# 自带的（网络、文件、文字处理）
// CodeHollow.FeedReader 是第三方包，专门解析 RSS/Atom
// Microsoft.Data.Sqlite 是微软提供的轻量数据库
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CodeHollow.FeedReader;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Data.Sqlite;
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;

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

    // ═══════════ AI 无参数/自定义参数命令（不要求 args.Length >= 2）═══════════
    switch (cmd)
    {
        case "--init":
            InitAiConfigInteractive(dbPath);
            return;
        case "--config":
            ShowConfig(dbPath);
            return;
        case "--index":
            IndexArticlesCli(new string[] { }, dbPath);
            return;
        case "--reindex":
            ReindexCli(dbPath);
            return;
        case "--summary-all":
            SummaryAllCli(dbPath);
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
        case "--search":
            if (args.Length < 2) { Console.WriteLine("用法: rssreader --search <查询> [--feed 编号] [--threshold 0.7] [--json]"); return; }
            SearchCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--summary":
            if (!int.TryParse(args[1], out int sumId)) { Console.WriteLine("用法: rssreader --summary <文章编号 或 feed:编号>"); return; }
            SummaryCli(sumId, dbPath).Wait();
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

AI 命令:
  --init           首次配置 AI（模型 + API Key）
  --config         查看/修改 AI 配置
  --index          对文章做 Embedding 向量化（交互式选择）
  --reindex        更换 Embedding 模型后重新向量化
  --search <查询>   [--feed 编号] [--threshold 0.7] [--json] 语义搜索
  --summary <编号>  为文章生成摘要（保存到数据库）；可传 feed:<编号> 为该源全部文章生成
  --summary-all    为所有未生成摘要的文章生成摘要

示例:
  rssreader -l
  rssreader -d https://example.com/rss
  rssreader -u 1
  rssreader -a 1
  rssreader --search ""LLM Agent"" --feed 1 --json
  rssreader --summary 12
  rssreader --summary feed:3
");
    Console.WriteLine(@"
安全提示:
  API Key 存储在操作系统原生凭据库（Windows 凭据管理器 / macOS 钥匙串 / Linux Secret Service），
  不写入任何文件。请勿泄露 API Key。首次调用 AI 功能时会提示。
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
    // 先开外键约束 + WAL 模式（允许多进程并发读，写只阻塞写），再建表
    cmd.CommandText = "PRAGMA foreign_keys = ON;";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "PRAGMA journal_mode = WAL;";
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

        CREATE TABLE IF NOT EXISTS Models ( --记录每个 Embedding 模型的元数据
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ModelType   TEXT    NOT NULL,   -- 'embedding' / 'llm'
            Provider    TEXT    NOT NULL,   -- 'ollama' / 'openai' / 'deepseek'
            ModelName   TEXT    NOT NULL,   -- 模型名
            Dimensions  INTEGER,            -- 向量维度（仅 embedding）
            IsCurrent   INTEGER DEFAULT 0,  -- 是否为当前使用的 embedding 模型
            CreatedAt   TEXT
        );

        CREATE TABLE IF NOT EXISTS Vectors ( --文章向量索引
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ItemId      INTEGER NOT NULL,   -- 关联文章 Id
            ModelId     INTEGER NOT NULL,   -- 关联模型 Id
            Vector      BLOB    NOT NULL,   -- 向量二进制（float[]）
            CreatedAt   TEXT,
            FOREIGN KEY (ItemId) REFERENCES Items(Id),
            FOREIGN KEY (ModelId) REFERENCES Models(Id)
        );
    ";
    cmd.ExecuteNonQuery();

    // 旧库迁移：给已存在的 Items 表补 Summary / SummaryAt 字段（若缺就加）
    try
    {
        cmd.CommandText = "ALTER TABLE Items ADD COLUMN Summary TEXT";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException) { /* 字段已存在则忽略 */ }
    try
    {
        cmd.CommandText = "ALTER TABLE Items ADD COLUMN SummaryAt TEXT";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException) { /* 字段已存在则忽略 */ }
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

// ═══════════ 删文章 ═══════════
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

// ═══════════════════════════════════════════════════════════
// AI 相关功能：配置、凭据、Embedding、向量、搜索、摘要
// ═══════════════════════════════════════════════════════════
// （配置类 AiConfig / EmbeddingCfg / LlmCfg / SearchHit / AiException 见文件末尾类型区）

string ConfigPath(string dbPath) => Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "ai_config.json");

AiConfig LoadConfig(string dbPath)
{
    string path = ConfigPath(dbPath);
    if (File.Exists(path))
    {
        try { return JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(path)) ?? new AiConfig(); }
        catch { /* 配置损坏时用默认值 */ }
    }
    return new AiConfig();
}

void SaveConfig(string dbPath, AiConfig cfg)
{
    var opts = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(ConfigPath(dbPath), JsonSerializer.Serialize(cfg, opts));
}

// ═══════════ 凭据存储（系统原生凭据管理器）═══════════
// 服务标识：固定字符串，用于在系统凭据库中区分本应用的条目
void CredSet(string key, string value)
{
    var store = CredentialStoreFactory.CreateDefault("hahaRSSReader");
    var cache = new ktsu.CredentialCache.CredentialCache(store);
    cache.AddOrReplace(new PersonaGUID { WeakString = key }, new CredentialWithToken { Token = new CredentialToken { WeakString = value } });
}

string? CredGet(string key)
{
    try
    {
        var store = CredentialStoreFactory.CreateDefault("hahaRSSReader");
        var cache = new ktsu.CredentialCache.CredentialCache(store);
        if (cache.TryGet(new PersonaGUID { WeakString = key }, out var cred) && cred is CredentialWithToken ct)
            return ct.Token.WeakString;
    }
    catch { /* 凭据库不可用时返回 null */ }
    return null;
}

bool CredHas(string key) => CredGet(key) != null;

// ═══════════ 安全提醒（首次调用 AI 功能时输出）═══════════
void EnsureAiPrompted()
{
    if (AiState.Warned) return;
    AiState.Warned = true;
    Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  🔐 安全提醒                                                  ║");
    Console.WriteLine("║  你的 API Key 存储在操作系统原生凭据库                        ║");
    Console.WriteLine("║  （Windows 凭据管理器 / macOS 钥匙串 / Linux Secret Service） ║");
    Console.WriteLine("║  不会写入任何项目文件。请注意：                               ║");
    Console.WriteLine("║  1. 不要将 API Key 分享/发给他人                             ║");
    Console.WriteLine("║  2. 不要截图或上传含密钥的界面                               ║");
    Console.WriteLine("║  3. 如怀疑泄露，请立即更换密钥                               ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
}

// ═══════════ JSON 输出辅助 ═══════════
void JsonOut(object obj) => Console.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));

// 自然语言报错 + JSON 双格式
void ReportError(string code, string message, string? suggestion = null, string? details = null, bool json = false)
{
    if (json)
    {
        JsonOut(new { success = false, error = new { code, message, suggestion, details } });
    }
    else
    {
        Console.WriteLine($"错误 [{code}] {message}");
        if (suggestion != null) Console.WriteLine($"建议：{suggestion}");
        if (details != null) Console.WriteLine($"详情：{details}");
    }
}

// ═══════════ Embedding 服务（支持 ollama / openai，可扩展）═══════════
async Task<float[]?> GetEmbeddingAsync(string text, AiConfig cfg)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    switch (cfg.Embedding.Provider.ToLower())
    {
        case "ollama":
        {
            var req = new { model = cfg.Embedding.Model, input = text };
            var resp = await client.PostAsync($"{cfg.Embedding.ApiEndpoint}/api/embed",
                new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
                throw new AiException("MODEL_UNAVAILABLE", $"Ollama 服务不可用（HTTP {(int)resp.StatusCode}）",
                    "请确认 Ollama 已启动，或检查端点和模型名", await resp.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var emb = doc.RootElement.GetProperty("embeddings")[0];
            return emb.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        }
        case "openai":
        {
            string? key = CredGet("embedding_api_key");
            if (string.IsNullOrEmpty(key))
                throw new AiException("API_KEY_MISSING", "缺少 OpenAI Embedding API Key",
                    "请执行 rssreader --init 配置 OpenAI API Key");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            var body = new { model = cfg.Embedding.Model, input = text };
            var resp = await client.PostAsync($"{cfg.Embedding.ApiEndpoint}/embeddings",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
                throw new AiException("API_KEY_INVALID", $"OpenAI Embedding 请求失败（HTTP {(int)resp.StatusCode}）",
                    "请检查 API Key 是否正确，或检查模型名", await resp.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            return data.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        }
        default:
            throw new AiException("UNSUPPORTED_PROVIDER", $"不支持的 Embedding 提供商：{cfg.Embedding.Provider}",
                "支持 ollama / openai");
    }
}

// 模型调用失败时：捕获并自然语言报错，停止使用该模型
async Task<float[]?> SafeEmbed(string text, AiConfig cfg, bool json = false)
{
    try
    {
        EnsureAiPrompted();
        return await GetEmbeddingAsync(text, cfg);
    }
    catch (HttpRequestException ex)
    {
        ReportError("NETWORK_ERROR", "网络错误，无法连接到 Embedding 服务",
            "请检查网络连接，或检查 API 端点地址", ex.Message, json);
        return null;
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return null;
    }
}

// ═══════════ 向量存储与相似度 ═══════════
byte[] VectorToBytes(float[] v)
{
    var bytes = new byte[v.Length * sizeof(float)];
    Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
    return bytes;
}

float[] BytesToVector(byte[] bytes)
{
    var v = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
    return v;
}

float CosineSimilarity(float[] a, float[] b)
{
    if (a.Length != b.Length) return 0f;
    float dot = 0, na = 0, nb = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        na += a[i] * a[i];
        nb += b[i] * b[i];
    }
    return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-12f);
}

// 注册/获取当前 embedding 模型，返回 Models.Id；维度变化时更新 IsCurrent
int EnsureModel(string dbPath, EmbeddingCfg emb)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id FROM Models WHERE Provider = @p AND ModelName = @m AND ModelType = 'embedding'";
    cmd.Parameters.AddWithValue("@p", emb.Provider);
    cmd.Parameters.AddWithValue("@m", emb.Model);
    var id = cmd.ExecuteScalar();
    if (id != null)
    {
        int modelId = Convert.ToInt32(id);
        // 确保是当前模型
        cmd.CommandText = "UPDATE Models SET IsCurrent = CASE WHEN Id = @id THEN 1 ELSE 0 END WHERE ModelType = 'embedding'";
        cmd.Parameters.AddWithValue("@id", modelId);
        cmd.ExecuteNonQuery();
        return modelId;
    }
    // 新模型：把旧模型取消 IsCurrent
    cmd.CommandText = "UPDATE Models SET IsCurrent = 0 WHERE ModelType = 'embedding'";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO Models (ModelType, Provider, ModelName, Dimensions, IsCurrent, CreatedAt) VALUES ('embedding', @p, @m, @d, 1, @now)";
    cmd.Parameters.AddWithValue("@d", emb.Dimensions);
    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT last_insert_rowid()";
    return Convert.ToInt32(cmd.ExecuteScalar());
}

// 检查是否需要重新索引（模型维度变化时提醒）
string? CheckDimensionMismatch(string dbPath, EmbeddingCfg emb)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ModelName, Dimensions FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
    using var r = cmd.ExecuteReader();
    if (r.Read())
    {
        string oldName = r.GetString(0);
        int oldDim = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        if (oldName != emb.Model && oldDim != emb.Dimensions)
            return $"检测到 Embedding 模型维度变化（旧模型 {oldName} {oldDim} 维 → 新模型 {emb.Model} {emb.Dimensions} 维），旧向量已无法使用，请执行 rssreader --reindex 重新向量化。";
    }
    return null;
}

// 保存向量（幂等：同文章+同模型只留一条）
void SaveVector(string dbPath, int itemId, int modelId, float[] vector)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Vectors (ItemId, ModelId, Vector, CreatedAt)
        VALUES (@i, @m, @v, @now)
        ON CONFLICT(ItemId, ModelId) DO UPDATE SET Vector = excluded.Vector, CreatedAt = excluded.CreatedAt
    ";
    cmd.Parameters.AddWithValue("@i", itemId);
    cmd.Parameters.AddWithValue("@m", modelId);
    cmd.Parameters.AddWithValue("@v", VectorToBytes(vector));
    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
    cmd.ExecuteNonQuery();
}

// ═══════════ 交互式选择文章进行向量化 ═══════════
async Task IndexArticlesCli(string[] extraArgs, string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);

    // 默认全选模式；也可支持 --all
    ListFeedsFromDb(dbPath);
    Console.WriteLine();
    Console.Write("请输入要向量化的订阅源编号（逗号分隔多个，输入 all 表示全部）：");
    string input = Console.ReadLine()?.Trim() ?? "";

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var feedIds = new List<int>();
    if (input.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Feeds";
        using var r = cmd.ExecuteReader();
        while (r.Read()) feedIds.Add(r.GetInt32(0));
    }
    else
    {
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int disp))
            {
                int real = GetRealId(disp, dbPath);
                if (real != 0) feedIds.Add(real);
            }
        }
    }

    if (feedIds.Count == 0) { Console.WriteLine("未选择任何订阅源，已取消"); return; }

    // 收集未向量化的 active 文章
    var articles = new List<(int Id, string Title)>();
    var cmd2 = conn.CreateCommand();
    cmd2.CommandText = @"
        SELECT i.Id, i.Title FROM Items i
        WHERE i.Status = 'active' AND i.FeedId IN (" + string.Join(",", feedIds) + @")
        AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
    ";
    using var r2 = cmd2.ExecuteReader();
    while (r2.Read()) articles.Add((r2.GetInt32(0), r2.GetString(1)));

    if (articles.Count == 0) { Console.WriteLine("所选订阅源的文章都已向量化，无需处理"); return; }

    Console.WriteLine($"将向量化 {articles.Count} 篇文章，确认？(y/n)：");
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine("已取消"); return; }

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    int ok = 0, fail = 0;
    for (int i = 0; i < articles.Count; i++)
    {
        var a = articles[i];
        var vec = await SafeEmbed(a.Title, cfg);
        if (vec == null) { fail++; Console.WriteLine($"  [{i + 1}/{articles.Count}] 失败：{a.Title}"); continue; }
        if (vec.Length != cfg.Embedding.Dimensions)
        {
            // 自动校正维度（以实际为准）
            cfg.Embedding.Dimensions = vec.Length;
            SaveConfig(dbPath, cfg);
        }
        SaveVector(dbPath, a.Id, modelId, vec);
        ok++;
        if (ok % 10 == 0) Console.WriteLine($"  已处理 {ok + fail}/{articles.Count}");
    }
    Console.WriteLine($"完成：成功 {ok}，失败 {fail}");
}

// 重新向量化（更换模型后）：清空旧向量并重建
async Task ReindexCli(string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Status = 'active'";
    long total = (long)cmd.ExecuteScalar()!;

    Console.Write($"将删除现有向量并重新向量化全部 {total} 篇 active 文章，确认？(y/n)：");
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine("已取消"); return; }

    cmd.CommandText = "DELETE FROM Vectors";
    cmd.ExecuteNonQuery();

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    cmd.CommandText = "SELECT Id, Title FROM Items WHERE Status = 'active'";
    using var r = cmd.ExecuteReader();
    var items = new List<(int Id, string Title)>();
    while (r.Read()) items.Add((r.GetInt32(0), r.GetString(1)));
    r.Close();

    int ok = 0, fail = 0;
    foreach (var item in items)
    {
        var vec = await SafeEmbed(item.Title, cfg);
        if (vec == null) { fail++; continue; }
        SaveVector(dbPath, item.Id, modelId, vec);
        ok++;
        if ((ok + fail) % 10 == 0) Console.WriteLine($"  已处理 {ok + fail}/{items.Count}");
    }
    Console.WriteLine($"重新索引完成：成功 {ok}，失败 {fail}");
}

// ═══════════ 语义搜索 ═══════════
void SearchCli(string[] args, string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);

    int? feedDisplay = null;
    int? feedReal = null;
    float threshold = cfg.Embedding.SearchThreshold;
    bool json = false;
    var queryParts = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--feed":
                if (i + 1 < args.Length && int.TryParse(args[++i], out int f))
                {
                    feedDisplay = f;
                    feedReal = GetRealId(f, dbPath);
                    if (feedReal == 0) { ReportError("FEED_NOT_FOUND", $"没有找到编号 {f} 的订阅源", json: json); return; }
                }
                break;
            case "--threshold":
                if (i + 1 < args.Length && float.TryParse(args[++i], out float t))
                    threshold = t;
                break;
            case "--json":
                json = true;
                break;
            default:
                queryParts.Add(args[i]);
                break;
        }
    }

    string query = string.Join(" ", queryParts);
    if (string.IsNullOrWhiteSpace(query)) { ReportError("EMPTY_QUERY", "请输入搜索查询", json: json); return; }

    var vec = SafeEmbed(query, cfg, json).GetAwaiter().GetResult();
    if (vec == null) return;

    // 校验维度与当前模型一致
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var modelCmd = conn.CreateCommand();
    modelCmd.CommandText = "SELECT Id FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
    var modelObj = modelCmd.ExecuteScalar();
    if (modelObj == null) { ReportError("NO_INDEX", "尚无向量索引，请先执行 rssreader --index", json: json); return; }
    int modelId = Convert.ToInt32(modelObj);

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Vectors WHERE ModelId = @m";
    cmd.Parameters.AddWithValue("@m", modelId);
    long count = (long)cmd.ExecuteScalar()!;
    if (count == 0) { ReportError("NO_INDEX", "当前模型尚无向量索引，请先执行 rssreader --index", json: json); return; }

    cmd.CommandText = @"
        SELECT v.ItemId, v.Vector, i.Title, i.Description, i.Link,
               f.Title AS FeedTitle, f.Id AS FeedId
        FROM Vectors v
        JOIN Items i ON v.ItemId = i.Id
        JOIN Feeds f ON i.FeedId = f.Id
        WHERE v.ModelId = @m AND i.Status = 'active'
        " + (feedReal.HasValue ? "AND i.FeedId = @fid" : "") + @"
        ORDER BY i.Id
    ";
    cmd.Parameters.AddWithValue("@m", modelId);
    if (feedReal.HasValue) cmd.Parameters.AddWithValue("@fid", feedReal.Value);

    var results = new List<SearchHit>();
    using (var r = cmd.ExecuteReader())
    {
        while (r.Read())
        {
            float[] stored = BytesToVector(r.GetFieldValue<byte[]>(1));
            float score = CosineSimilarity(vec, stored);
            if (score < threshold) continue;
            results.Add(new SearchHit
            {
                ItemId = r.GetInt32(0),
                Title = r.GetString(2),
                Description = r.IsDBNull(3) ? "" : r.GetString(3),
                Link = r.IsDBNull(4) ? "" : r.GetString(4),
                FeedTitle = r.GetString(5),
                FeedId = r.GetInt32(6),
                Score = score
            });
        }
    }

    results = results.OrderByDescending(h => h.Score).Take(20).ToList();

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                query,
                threshold,
                feedId = feedReal,
                results = results.Select(h => new
                {
                    itemId = h.ItemId,
                    title = h.Title,
                    description = h.Description,
                    link = h.Link,
                    feedId = h.FeedId,
                    feedTitle = h.FeedTitle,
                    score = Math.Round(h.Score, 4)
                }),
                total = results.Count
            }
        });
    }
    else
    {
        Console.WriteLine($"搜索结果（查询：{query}，阈值：{threshold}，共 {results.Count} 条）");
        foreach (var h in results)
        {
            Console.WriteLine($"  [{h.ItemId}] {h.Title}");
            Console.WriteLine($"      来源：{h.FeedTitle} | 相似度：{h.Score:P1}");
            if (!string.IsNullOrEmpty(h.Description) && h.Description.Length > 80)
                Console.WriteLine($"      摘要：{h.Description[..80]}...");
        }
    }
}

// （SearchHit 类见文件末尾类型区）
// ═══════════ LLM 摘要服务（DeepSeek，OpenAI 兼容）═══════════
async Task<string?> CallLlmAsync(string prompt, AiConfig cfg)
{
    string? key = CredGet("llm_api_key");
    if (string.IsNullOrEmpty(key))
        throw new AiException("API_KEY_MISSING", "缺少 LLM API Key", "请执行 rssreader --init 配置 LLM API Key");

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    var body = new
    {
        model = cfg.Llm.Model,
        messages = new[] { new { role = "user", content = prompt } },
        temperature = 0.3
    };
    var resp = await client.PostAsync($"{cfg.Llm.ApiEndpoint}/chat/completions",
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
    if (!resp.IsSuccessStatusCode)
        throw new AiException("API_KEY_INVALID", $"LLM 请求失败（HTTP {(int)resp.StatusCode}）",
            "请检查 API Key / 模型名 / 端点配置", await resp.Content.ReadAsStringAsync());
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
}

// 生成单篇文章摘要并保存到 rss.db（与文章同在）
async Task<bool> SummarizeItem(string dbPath, int itemId, bool json = false)
{
    var cfg = LoadConfig(dbPath);
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, Content, Description, Summary FROM Items WHERE Id = @id AND Status = 'active'";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { ReportError("ITEM_NOT_FOUND", $"没有找到文章 {itemId}", json: json); return false; }
    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string existing = r.IsDBNull(3) ? "" : r.GetString(3);
    r.Close();

    if (!string.IsNullOrEmpty(existing))
    {
        Console.WriteLine($"文章 [{itemId}] {title} 已有摘要，跳过（如想重新生成请先删除）。");
        return true;
    }

    string text = string.IsNullOrEmpty(content) ? desc : content;
    if (text.Length > 6000) text = text[..6000];
    var prompt = $"请用 150 字以内概括以下文章的核心内容（用中文回答，直接输出摘要正文，不要额外解释）：\n\n标题：{title}\n\n正文：{text}";

    try
    {
        EnsureAiPrompted();
        var summary = await CallLlmAsync(prompt, cfg);
        if (summary == null) throw new AiException("EMPTY_RESPONSE", "LLM 返回为空", "请重试或检查模型配置");

        var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE Items SET Summary = @s, SummaryAt = @now WHERE Id = @id";
        upd.Parameters.AddWithValue("@s", summary.Trim());
        upd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        upd.Parameters.AddWithValue("@id", itemId);
        upd.ExecuteNonQuery();
        Console.WriteLine($"已生成摘要：[{itemId}] {title}");
        if (json) JsonOut(new { success = true, itemId, title, summary = summary.Trim() });
        return true;
    }
    catch (HttpRequestException ex)
    {
        ReportError("NETWORK_ERROR", "网络错误，无法连接 LLM 服务", "请检查网络连接", ex.Message, json);
        return false;
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return false;
    }
}

// 单篇摘要 CLI；支持 feed:<编号>
async Task SummaryCli(int idOrFeed, string dbPath)
{
    // 通过特殊解析：命令行可能传 'feed:3'，但这里收到的是 int。改用字符串包装在调用处处理。
    await SummarizeItem(dbPath, idOrFeed);
}

// 全部摘要
async Task SummaryAllCli(string dbPath)
{
    EnsureAiPrompted();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Title FROM Items WHERE Status = 'active' AND (Summary IS NULL OR Summary = '')";
    using var r = cmd.ExecuteReader();
    var items = new List<(int Id, string Title)>();
    while (r.Read()) items.Add((r.GetInt32(0), r.GetString(1)));
    r.Close();

    if (items.Count == 0) { Console.WriteLine("所有 active 文章都已有摘要"); return; }
    Console.WriteLine($"将为 {items.Count} 篇文章生成摘要，确认？(y/n)：");
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine("已取消"); return; }

    int ok = 0, fail = 0;
    foreach (var it in items)
    {
        if (await SummarizeItem(dbPath, it.Id)) ok++; else fail++;
        Console.WriteLine($"  进度：{ok + fail}/{items.Count}");
    }
    Console.WriteLine($"完成：成功 {ok}，失败 {fail}");
}

// ═══════════ 交互式配置向导 ═══════════
void InitAiConfigInteractive(string dbPath)
{
    EnsureAiPrompted();
    Console.WriteLine("===== RSS Reader AI 配置向导 =====");
    var cfg = LoadConfig(dbPath);

    // --- Embedding ---
    Console.WriteLine("\n[1/4] Embedding 提供商（用于语义搜索的文本向量化）：");
    Console.WriteLine("  1) Ollama（本地，免费，需安装 Ollama）");
    Console.WriteLine("  2) OpenAI（云端，需 API Key）");
    Console.Write($"当前：{cfg.Embedding.Provider}，选择 (1/2)：");
    string embChoice = Console.ReadLine()?.Trim() ?? "";
    if (embChoice == "2")
    {
        cfg.Embedding.Provider = "openai";
        cfg.Embedding.Model = "text-embedding-3-small";
        cfg.Embedding.Dimensions = 1536;
        cfg.Embedding.ApiEndpoint = "https://api.openai.com/v1";
    }
    else
    {
        cfg.Embedding.Provider = "ollama";
        cfg.Embedding.Model = "nomic-embed-text";
        cfg.Embedding.Dimensions = 768;
        cfg.Embedding.ApiEndpoint = "http://localhost:11434";
    }

    // --- Embedding API Key（openai 需要）---
    if (cfg.Embedding.Provider == "openai")
    {
        Console.Write("[2/4] 输入 OpenAI Embedding API Key（存储在系统凭据库）：");
        var key = ReadSecret();
        if (!string.IsNullOrEmpty(key)) CredSet("embedding_api_key", key);
    }

    // --- LLM ---
    Console.WriteLine("\n[3/4] LLM 提供商（用于生成文章摘要）：");
    Console.WriteLine("  1) DeepSeek（云端）");
    Console.WriteLine("  2) OpenAI（云端）");
    Console.Write($"当前：{cfg.Llm.Provider}，选择 (1/2)：");
    string llmChoice = Console.ReadLine()?.Trim() ?? "";
    if (llmChoice == "2")
    {
        cfg.Llm.Provider = "openai";
        cfg.Llm.Model = "gpt-4o-mini";
        cfg.Llm.ApiEndpoint = "https://api.openai.com/v1";
    }
    else
    {
        cfg.Llm.Provider = "deepseek";
        cfg.Llm.Model = "deepseek-chat";
        cfg.Llm.ApiEndpoint = "https://api.deepseek.com/v1";
    }

    Console.Write("[4/4] 输入 LLM API Key（存储在系统凭据库）：");
    var llmKey = ReadSecret();
    if (!string.IsNullOrEmpty(llmKey)) CredSet("llm_api_key", llmKey);

    Console.Write("默认搜索相似度阈值（0-1，建议 0.7）：");
    if (float.TryParse(Console.ReadLine(), out float thr)) cfg.Embedding.SearchThreshold = thr;

    SaveConfig(dbPath, cfg);
    Console.WriteLine("\n配置已保存。你可以修改 ai_config.json 调整模型，API Key 已在系统凭据库中。");
    Console.WriteLine("注意：更换 Embedding 模型后需执行 rssreader --reindex 重新向量化。");
}

// 读取密码（不回显）——跨平台简易实现
string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Length--;
            continue;
        }
        sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

// 查看配置
void ShowConfig(string dbPath)
{
    var cfg = LoadConfig(dbPath);
    Console.WriteLine("===== AI 配置 =====");
    Console.WriteLine($"Embedding：{cfg.Embedding.Provider} / {cfg.Embedding.Model} ({cfg.Embedding.Dimensions} 维)");
    Console.WriteLine($"  端点：{cfg.Embedding.ApiEndpoint}");
    Console.WriteLine($"  默认搜索阈值：{cfg.Embedding.SearchThreshold}");
    Console.WriteLine($"  API Key：{(CredHas("embedding_api_key") ? "已设置" : "未设置")}");
    Console.WriteLine($"LLM：{cfg.Llm.Provider} / {cfg.Llm.Model}");
    Console.WriteLine($"  端点：{cfg.Llm.ApiEndpoint}");
    Console.WriteLine($"  API Key：{(CredHas("llm_api_key") ? "已设置" : "未设置")}");
    Console.WriteLine($"配置文件：{ConfigPath(dbPath)}");

    var warn = CheckDimensionMismatch(dbPath, cfg.Embedding);
    if (warn != null) Console.WriteLine($"\n{warn}");
}

// ═══════════════════════════════════════════════════════════
// 以下为类型定义（必须位于所有顶级语句/局部函数之后）
// ═══════════════════════════════════════════════════════════

// 进程级 AI 状态
static class AiState
{
    public static bool Warned = false;
}

// ═══════════ AI 配置模型（ai_config.json，非敏感信息）═══════════
class AiConfig
{
    public EmbeddingCfg Embedding { get; set; } = new();
    public LlmCfg Llm { get; set; } = new();
}

class EmbeddingCfg
{
    public string Provider { get; set; } = "ollama";   // ollama / openai
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimensions { get; set; } = 768;          // 向量维度
    public string ApiEndpoint { get; set; } = "http://localhost:11434";
    public float SearchThreshold { get; set; } = 0.7f;  // 默认相似度阈值
}

class LlmCfg
{
    public string Provider { get; set; } = "deepseek"; // deepseek / openai
    public string Model { get; set; } = "deepseek-chat";
    public string ApiEndpoint { get; set; } = "https://api.deepseek.com/v1";
}

// 搜索结果条目
class SearchHit
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
    public int FeedId { get; set; }
    public float Score { get; set; }
}

// ═══════════ 自定义异常 ═══════════
class AiException : Exception
{
    public string Code { get; }
    public string? Suggestion { get; }
    public string? Details { get; }
    public AiException(string code, string message, string? suggestion = null, string? details = null)
        : base(message)
    {
        Code = code;
        Suggestion = suggestion;
        Details = details;
    }
}