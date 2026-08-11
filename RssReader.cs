// ===== 引用外部包 =====
// using 相当于导入工具包，每个包提供不同的工具
// System.* 是 C# 自带的（网络、文件、文字处理）
// CodeHollow.FeedReader 是第三方包，专门解析 RSS/Atom
// Microsoft.Data.Sqlite 是微软提供的轻量数据库
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using CodeHollow.FeedReader;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Data.Sqlite;
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// 统一 UTF-8 输入/输出：避免中文在终端 / AI 调用（PowerShell 默认 GBK 代码页）时乱码，
// 也保证管道输入的同意短语等中文内容按 UTF-8 解码
try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 某些重定向场景可能不支持，忽略 */ }
try { Console.InputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 同上，忽略 */ }

// 数据目录 = exe 同级下的 readwithhotsoup 文件夹（首次启动自动创建）
// 数据库、AI 配置、语言文件等所有配置文件都放在这里，方便整体备份/迁移
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string dataDir = Path.Combine(baseDir, "readwithhotsoup");
Directory.CreateDirectory(dataDir);

// 首次启动把默认语言文件复制到数据目录（已存在则跳过，保留用户编辑）
EnsureDefaultLanguages(baseDir, dataDir);

string dbPath = Path.Combine(dataDir, "rss.db");
InitDatabase(dbPath);

// 全局选项解析（任意位置均可，解析后从参数中剔除）
// --ignoresafeannouncement：跳过安全横幅等多余输出（供脚本/Agent 使用）
// --lang <代码>：指定语言文件（如 zh-CN / en-US）
string? langCode = null;
if (args.Any(a => a.Equals("--ignoresafeannouncement", StringComparison.OrdinalIgnoreCase)))
{
    AiState.IgnoreAnnouncement = true;
    args = args.Where(a => !a.Equals("--ignoresafeannouncement", StringComparison.OrdinalIgnoreCase)).ToArray();
}
for (int gi = 0; gi < args.Length - 1; gi++)
{
    if (args[gi].Equals("--lang", StringComparison.OrdinalIgnoreCase))
    {
        langCode = args[gi + 1];
        args = args.Where((a, i) => i != gi && i != gi + 1).ToArray();
        break;
    }
}
Lang.Init(dataDir, langCode);
TelemetryService.Init(dataDir);   // 遥测：默认关闭，仅本地，独立 telemetry.db

// ══════════ CLI 模式 ══════════
if (args.Length > 0)
{
    await RunCli(args, dbPath);
    RemindDueFeeds(args, dbPath);
    TelemetryService.Shutdown();   // 冲刷缓冲 + 检查点
    return AiState.ExitCode;
}

// ══════════ TUI 模式（无参数时进入）══════════
var tuiExit = await RunTui(dbPath);
TelemetryService.Shutdown();   // 冲刷缓冲 + 检查点
return tuiExit;

// ═══════════════════════════════════════════════════════════
// 以下是所有方法，按调用顺序排列
// ═══════════════════════════════════════════════════════════

// 把默认语言文件复制到 readwithhotsoup/languages/，确保 zh-CN / en-US 等官方翻译始终可用。
// 优先级：① exe 旁 languages/ 文件夹（发布外置，可编辑）> ② 内嵌程序集资源（单文件自带）。
// 只写入「缺失」或「旧格式（键为中文）」的文件，用户已编辑过的语言文件不会被覆盖。
// 返回本次是否恢复过文件（用于提示用户优先使用 languages/ 文件夹）。
bool EnsureDefaultLanguages(string baseDir, string dataDir)
{
    bool restored = false;
    try
    {
        string dst = Path.Combine(dataDir, "languages");
        Directory.CreateDirectory(dst);

        // ① exe 旁外置语言文件夹（发布/开发时复制出来的那份，用户可直接编辑）
        string src = Path.Combine(baseDir, "languages");
        if (Directory.Exists(src))
        {
            foreach (var f in Directory.GetFiles(src, "*.json"))
            {
                string target = Path.Combine(dst, Path.GetFileName(f));
                if (!File.Exists(target)) { File.Copy(f, target); restored = true; }
                else if (IsLegacyLangFile(target))
                {
                    // 旧格式（键为中文原文）→ 用新版英文键格式覆盖，避免界面回退英文
                    try { File.Copy(f, target, overwrite: true); restored = true; } catch { }
                }
                else
                {
                    // 新版文件：合并「内置有、本地缺」的 key（补上新翻译，不覆盖用户已改的 key）
                    MergeLangMissingKeys(f, target);
                }
            }
        }

        // ② 内嵌资源兜底：单文件包里自带官方翻译，外置文件夹缺失时也能恢复
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            const string prefix = "sip-lang.";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(dst, name.Substring(prefix.Length));
            if (!File.Exists(target))
            {
                using var rs = asm.GetManifestResourceStream(name);
                if (rs != null)
                {
                    using var ws = new FileStream(target, FileMode.Create, FileAccess.Write);
                    rs.CopyTo(ws);
                    restored = true;
                }
            }
        }
    }
    catch { /* 恢复失败不影响主流程 */ }

    // 仅在实际恢复过语言文件时提示（避免每次启动刷屏、污染 --json 输出）。
    // 提示走 stderr，告诉用户自定义翻译优先用 languages/ 文件夹。
    if (restored)
    {
        Console.Error.WriteLine("已恢复默认语言文件 → " + Path.Combine(dataDir, "languages"));
        Console.Error.WriteLine("自定义翻译请优先编辑该 languages/ 文件夹里的文件（内置副本仅作兜底，不会覆盖你的修改）");
    }
    return restored;
}

// 判断语言文件是否为旧格式：键**全是中文**（旧格式键=中文原文；新版键以英文为主，
// 允许个别中文源文本 key，如 slogan/同意短语，不算旧格式）
bool IsLegacyLangFile(string path)
{
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        bool hasAscii = false, hasCjk = false;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Any(c => c < 128)) hasAscii = true;
            if (prop.Name.Any(c => c >= 0x4E00 && c <= 0x9FFF)) hasCjk = true;
        }
        return hasCjk && !hasAscii;
    }
    catch { return false; }
}

// 把内置语言文件里「本地缺失」的 key 补进本地文件（不覆盖用户已改的 key）
void MergeLangMissingKeys(string builtinPath, string targetPath)
{
    try
    {
        var builtin = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(builtinPath)) as System.Text.Json.Nodes.JsonObject;
        var local = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(targetPath)) as System.Text.Json.Nodes.JsonObject;
        if (builtin == null || local == null) return;
        var localKeys = new HashSet<string>();
        CollectLangKeys(local, localKeys);
        var missing = new List<KeyValuePair<string, string>>();
        CollectLangLeaves(builtin, missing, localKeys);
        if (missing.Count == 0) return;
        foreach (var kv in missing) local[kv.Key] = kv.Value;
        // 用不转义非 ASCII 的编码器，保留中文可读性（用户后续可直接编辑）
        File.WriteAllText(targetPath, local.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }
    catch { /* 合并失败不影响启动 */ }
}

void CollectLangKeys(System.Text.Json.Nodes.JsonObject obj, HashSet<string> keys)
{
    foreach (var p in obj)
    {
        if (p.Value is System.Text.Json.Nodes.JsonObject nested) CollectLangKeys(nested, keys);
        else if (p.Value is System.Text.Json.Nodes.JsonValue) keys.Add(p.Key);
    }
}

void CollectLangLeaves(System.Text.Json.Nodes.JsonObject obj, List<KeyValuePair<string, string>> missing, HashSet<string> localKeys)
{
    foreach (var p in obj)
    {
        if (p.Value is System.Text.Json.Nodes.JsonObject nested) CollectLangLeaves(nested, missing, localKeys);
        else if (p.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && !localKeys.Contains(p.Key))
            missing.Add(new KeyValuePair<string, string>(p.Key, s));
    }
}

// CLI 命令结束后提示「有到期的订阅源」（不自动更新，只提醒用户手动 sip --sync）
// 纯读库判断，零网络、不阻塞、不改变退出码；--json 模式自动抑制（避免污染结构化输出）
void RemindDueFeeds(string[] args, string dbPath)
{
    try
    {
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase)) return;  // 不能污染 | jq 的 JSON
        string cmd = args[0].ToLowerInvariant();
        if (cmd is "-h" or "--help") return;                          // 帮助页不带噪声
        if (cmd is "-u" or "--update" or "-d" or "--download" or "--sync" or "--update-all"
            or "--schedule" or "--sched") return;                     // 本身就是更新类命令
        var due = GetDueFeeds(dbPath);
        if (due.Count == 0) return;                                   // 没有到期的就不打扰
        Console.WriteLine(Lang.T("{0} feeds are due, run sip --sync to update", due.Count));
    }
    catch { /* 提示失败不影响主命令与退出码 */ }
}

// ══════════ 全文抓取（fetch）：文件缓存，零改表 ══════════
// 全文文本存 dataDir/fulltext/<itemId>.md；sidecar 向量存 fulltext/vecs.json；
// 同意标记存 fulltext_consent.txt。fetch 不改 Items.Content、不产生新版本、
// 不参与 diff/更新，仅是一次性"补充阅读"副作用。

string FulltextDir() { string d = Path.Combine(dataDir, "fulltext"); Directory.CreateDirectory(d); return d; }
string FulltextPath(long itemId) => Path.Combine(FulltextDir(), itemId + ".md");
string FulltextVecsPath() => Path.Combine(FulltextDir(), "vecs.json");
string FulltextConsentPath() => Path.Combine(dataDir, "fulltext_consent.txt");

bool HasFulltextConsent() => File.Exists(FulltextConsentPath());
void WriteFulltextConsent() => File.WriteAllText(FulltextConsentPath(), DateTime.Now.ToString("O"));

// 内容是否过短（Content 或 Description 字符数 < 100 → 触发全文抓取）
bool ContentTooShort(string content, string desc)
{
    string c = string.IsNullOrWhiteSpace(content) ? desc : content;
    return c.Trim().Length < 100;
}

// 某文章内容是否过短（TUI 判断是否需二次确认用）
bool ArticleContentShort(string dbPath, int itemId)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Content, Description FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return true;
        return ContentTooShort(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
    }
    catch { return true; }
}

// 读取某文全文缓存；未缓存返回 null
string? ReadFulltextCache(long itemId)
{
    string p = FulltextPath(itemId);
    return File.Exists(p) ? File.ReadAllText(p) : null;
}

// —— SSRF 防护：地址分类 0=允许 1=硬拦截（回环/链路本地） 2=私网段（默认拦截，AllowPrivateNet=true 放行）——
int AddressCategory(System.Net.IPAddress ip)
{
    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        byte[] b = ip.GetAddressBytes();
        bool loopback = b[0] == 127;
        bool linkLocal = b[0] == 169 && b[1] == 254;            // 含云元数据 169.254.169.254
        bool privateRange = b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);      // CGNAT 100.64/10
        if (loopback || linkLocal || b[0] == 0) return 1;
        if (privateRange) return 2;
        return 0;
    }
    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.Equals(System.Net.IPAddress.IPv6None)) return 1;
        if (ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return 2;
        return 0;
    }
    return 2;  // 未知地址族保守拦截
}

// 抓取 URL 安全校验（SSRF 防护）：仅 http/https；回环/链路本地一律拒绝；
// 私网段按配置决定。返回错误信息；null = 允许
string? ValidateFetchUrl(string url, bool allowPrivateNet)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
        return Lang.T("Invalid URL: {0}", url);
    if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
        return Lang.T("Only http/https URLs can be fetched: {0}", url);
    if (u.IsLoopback)
        return Lang.T("Loopback URLs are not allowed: {0}", url);

    var hosts = new List<System.Net.IPAddress>();
    if (System.Net.IPAddress.TryParse(u.Host.Trim('[', ']'), out var ipLiteral))
    {
        hosts.Add(ipLiteral);
    }
    else
    {
        try { hosts.AddRange(System.Net.Dns.GetHostAddresses(u.Host)); }
        catch { return Lang.T("Cannot resolve host: {0}", u.Host); }
    }
    foreach (var ip in hosts)
    {
        int cat = AddressCategory(ip);
        if (cat == 1)
            return Lang.T("Loopback/link-local addresses are not allowed: {0}", u.Host);
        if (cat == 2 && !allowPrivateNet)
            return Lang.T("Private network address is blocked (set allowPrivateNet=true in ai_config.json to allow): {0}", u.Host);
    }
    return null;
}

// 下载链接页并抽取"可读正文"（简单 readability：去 script/style/nav/footer 等，取可见文本）
string? FetchAndExtract(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        var html = client.GetStringAsync(url).GetAwaiter().GetResult();
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.SelectNodes("//script | //style | //nav | //footer | //header | //aside | //form | //noscript") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
            node.Remove();
        var text = doc.DocumentNode.InnerText;
        text = Regex.Replace(text, @"[ \t\r]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
    catch { return null; }
}

// 抓取核心（调用方已完成同意与二次确认）。
// 返回 (全文, 退出码, 错误信息)；0=成功；错误时不再打 Console，由调用方展示
(string? Text, int ExitCode, string? Error) DoFetchCore(string dbPath, int itemId)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT i.Link, i.Content, i.Description, i.FeedId FROM Items i WHERE i.Id = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return (null, 3, Lang.T("Article {0} not found", itemId));
    string link = r.IsDBNull(0) ? "" : r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    int feedId = r.GetInt32(3);

    string? cached = ReadFulltextCache(itemId);
    if (cached != null)
    {
        // 缓存命中但 sidecar 缺失（先抓全文后索引）时补齐，避免永远追不上
        EnsureFulltextSidecar(dbPath, itemId, feedId, cached);
        return (cached, 0, null);
    }
    if (string.IsNullOrWhiteSpace(link)) return (null, 1, Lang.T("No link, cannot fetch"));
    string? urlErr = ValidateFetchUrl(link, LoadConfig(dbPath).AllowPrivateNet);
    if (urlErr != null) return (null, 2, urlErr);
    string text = FetchAndExtract(link) ?? "";
    if (string.IsNullOrWhiteSpace(text)) return (null, 2, Lang.T("Fetch failed"));
    File.WriteAllText(FulltextPath(itemId), text);
    TrimFulltextCache();
    // 该源若已索引 → 用全文做 sidecar 向量（存 fulltext/vecs.json，不污染主 Vectors 表）
    EmbedFulltextSidecar(dbPath, itemId, feedId, text);
    return (text, 0, null);
}

// 抓取全文主入口（CLI）。返回 (全文, 退出码, 错误信息)。
// yes=true（--yes）：跳过同意与二次确认（AI/脚本）。已缓存则直接返回缓存。
(string? Text, int ExitCode, string? Error) FetchFulltext(string dbPath, int itemId, bool yes, bool force = false)
{
    // 先读元信息判断是否过短（用于二次确认）；不存在直接返回错误
    string? content = null, desc = null, title = null;
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Content, Description, Title FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, 3, Lang.T("Article {0} not found", itemId));
        content = r.IsDBNull(0) ? "" : r.GetString(0);
        desc = r.IsDBNull(1) ? "" : r.GetString(1);
        title = r.GetString(2);
    }

    // 同意流程（一次性）：短语按当前语言（en-US 用英文短语）
    string agreePhrase = Lang.T("是的，我愿意与作者达成合理使用约定");
    if (!HasFulltextConsent())
    {
        if (yes) WriteFulltextConsent();
        else
        {
            Console.WriteLine(Lang.T("sip is a reading aid; article fetching is for personal reading/study only. You agree to respect the source's intellectual property and copyright. You alone bear any loss from malicious use."));
            Console.Write(Lang.T("Type exactly to agree: {0}: ", agreePhrase));
            string input = Console.ReadLine()?.Trim() ?? "";
            if (input != agreePhrase)
            {
                Console.WriteLine(Lang.T("Not agreed, cancelled"));
                return (null, 1, null);
            }
            WriteFulltextConsent();
        }
    }

    // 二次确认：仅对"非过短"文章（原文已够长，多半是误触，明确告知）
    if (!force && !ContentTooShort(content!, desc!) && !yes)
    {
        Console.Write(Lang.T("The original text is already long. Did you mean to fetch? Fetch anyway? (y/n) "));
        if (!"y".Equals(Console.ReadLine()?.Trim().ToLower()))
        {
            Console.WriteLine(Lang.T("Cancelled"));
            return (null, 1, null);
        }
    }

    return DoFetchCore(dbPath, itemId);
}

// CLI：sip --fulltext <id> [--yes] [--json]
void FulltextCli(string[] args, string dbPath)
{
    if (!int.TryParse(args[0], out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var (text, code, err) = FetchFulltext(dbPath, itemId, yes);
    if (text == null)
    {
        SetExit(code);
        if (json && err != null)
            JsonOut(new { success = false, error = new { code = code == 2 ? "FETCH_FAILED" : (code == 3 ? "ITEM_NOT_FOUND" : "CANCELLED"), message = err } });
        else if (err != null) Console.WriteLine(err);
        return;
    }
    if (json) JsonOut(new { success = true, itemId, cached = true, content = text });
    else Console.WriteLine(StripControlChars(text));
}

// —— sidecar 向量（方案甲）——

List<(int ItemId, int FeedId, int ModelId, float[] Vector)> LoadFulltextVecs()
{
    string p = FulltextVecsPath();
    if (!File.Exists(p)) return new();
    try
    {
        var arr = JsonSerializer.Deserialize<List<FulltextVecEntry>>(File.ReadAllText(p));
        return arr?.Select(e => (e.ItemId, e.FeedId, e.ModelId, e.Vector)).ToList() ?? new();
    }
    catch { return new(); }
}

void SaveFulltextVecs(List<(int ItemId, int FeedId, int ModelId, float[] Vector)> list)
    => File.WriteAllText(FulltextVecsPath(), JsonSerializer.Serialize(list.Select(e => new FulltextVecEntry { ItemId = e.ItemId, FeedId = e.FeedId, ModelId = e.ModelId, Vector = e.Vector }).ToList()));

// 该源是否已索引（Vectors 表里该 FeedId 是否有向量）
bool FeedHasVectors(string dbPath, int feedId)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Vectors WHERE FeedId = @f";
        cmd.Parameters.AddWithValue("@f", feedId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    catch { return false; }
}

// 当前 embedding 模型 Id；无则返回 0
int CurrentEmbeddingModelId(string dbPath)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
        var o = cmd.ExecuteScalar();
        return o == null ? 0 : Convert.ToInt32(o);
    }
    catch { return 0; }
}

// fetch 后：若该源已索引，用全文算向量存 sidecar；失败静默
void EmbedFulltextSidecar(string dbPath, int itemId, int feedId, string text)
{
    try
    {
        if (!FeedHasVectors(dbPath, feedId)) return;
        var cfg = LoadConfig(dbPath);
        var vec = SafeEmbed(text, cfg, json: false).GetAwaiter().GetResult();
        if (vec == null) return;
        int modelId = CurrentEmbeddingModelId(dbPath);
        if (modelId <= 0) return;
        var list = LoadFulltextVecs();
        list.RemoveAll(e => e.ItemId == itemId);
        list.Add((itemId, feedId, modelId, vec));
        SaveFulltextVecs(list);
    }
    catch { /* 嵌入失败不影响抓取 */ }
}

// 补齐单篇 sidecar：全文缓存命中但尚无对应向量时生成（修复「先抓全文后索引」时序缺陷）。
// 幂等：已有当前模型向量的不重复嵌入
void EnsureFulltextSidecar(string dbPath, int itemId, int feedId, string text)
{
    try
    {
        if (!FeedHasVectors(dbPath, feedId)) return;
        int modelId = CurrentEmbeddingModelId(dbPath);
        if (modelId <= 0) return;
        var list = LoadFulltextVecs();
        if (list.Any(e => e.ItemId == itemId && e.ModelId == modelId)) return;
        var cfg = LoadConfig(dbPath);
        var vec = SafeEmbed(text, cfg, json: false).GetAwaiter().GetResult();
        if (vec == null) return;
        list.RemoveAll(e => e.ItemId == itemId);
        list.Add((itemId, feedId, modelId, vec));
        SaveFulltextVecs(list);
    }
    catch { /* 嵌入失败不影响抓取 */ }
}

// 批量回补：给已有全文缓存的文章补 sidecar 向量（--index / --reindex 后调用）；返回成功数
int BackfillFulltextSidecars(string dbPath, List<(int Id, int FeedId)> items)
{
    int modelId = CurrentEmbeddingModelId(dbPath);
    if (modelId <= 0) return 0;
    var cfg = LoadConfig(dbPath);
    var list = LoadFulltextVecs();
    var toAdd = new List<(int ItemId, int FeedId, int ModelId, float[] Vector)>();
    foreach (var (id, feedId) in items)
    {
        if (list.Any(e => e.ItemId == id && e.ModelId == modelId) || toAdd.Any(e => e.ItemId == id)) continue;
        string? ft = ReadFulltextCache(id);
        if (ft == null) continue;
        var vec = SafeEmbed(ft, cfg, json: false).GetAwaiter().GetResult();
        if (vec == null) continue;
        toAdd.Add((id, feedId, modelId, vec));
    }
    if (toAdd.Count == 0) return 0;
    list.RemoveAll(e => toAdd.Any(a => a.ItemId == e.ItemId));
    list.AddRange(toAdd);
    SaveFulltextVecs(list);
    return toAdd.Count;
}

// CLI：sip --fulltext <id> [--yes] [--json]
// CLI：sip --purge-fulltext [id]（删全部或单篇缓存）
void PurgeFulltextCli(string arg, string dbPath)
{
    if (string.IsNullOrWhiteSpace(arg))
    {
        Directory.Delete(FulltextDir(), recursive: true);
        Directory.CreateDirectory(FulltextDir());
        Console.WriteLine(Lang.T("All fulltext cache cleared"));
        return;
    }
    if (!int.TryParse(arg, out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    string p = FulltextPath(itemId);
    if (File.Exists(p)) File.Delete(p);
    var list = LoadFulltextVecs();
    if (list.RemoveAll(e => e.ItemId == itemId) > 0) SaveFulltextVecs(list);
    Console.WriteLine(Lang.T("Cleared cache for article {0}", itemId));
}

// ══════════ 阅读进度记忆（按文章记录滚动位置，文件存储，零改表）══════════
string ReadingProgressPath() => Path.Combine(dataDir, "reading_progress.json");

Dictionary<long, int> LoadReadingProgress()
{
    try
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ReadingProgressPath()));
        return d?.ToDictionary(kv => long.Parse(kv.Key), kv => kv.Value) ?? new Dictionary<long, int>();
    }
    catch { return new Dictionary<long, int>(); }
}

void SaveReadingProgress(Dictionary<long, int> map)
{
    try
    {
        File.WriteAllText(ReadingProgressPath(), JsonSerializer.Serialize(
            map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true }));
    }
    catch { /* 保存失败不影响使用 */ }
}

// ══════════ 来源健康状态（sidecar 文件，零改表）══════════
string FeedHealthPath() => Path.Combine(dataDir, "feed_health.json");

Dictionary<int, (int FailCount, string LastError, string LastOkAt)> LoadFeedHealth()
{
    try
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, FeedHealthEntry>>(File.ReadAllText(FeedHealthPath()));
        return d?.ToDictionary(kv => int.Parse(kv.Key), kv => (kv.Value.FailCount, kv.Value.LastError, kv.Value.LastOkAt)) ?? new();
    }
    catch { return new(); }
}

void SaveFeedHealth(Dictionary<int, (int FailCount, string LastError, string LastOkAt)> map)
{
    try
    {
        File.WriteAllText(FeedHealthPath(), JsonSerializer.Serialize(
            map.ToDictionary(kv => kv.Key.ToString(), kv => new FeedHealthEntry { FailCount = kv.Value.FailCount, LastError = kv.Value.LastError, LastOkAt = kv.Value.LastOkAt }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

void RecordFeedFailure(int feedId, string error)
{
    var map = LoadFeedHealth();
    map.TryGetValue(feedId, out var e);
    map[feedId] = (e.FailCount + 1, error, e.LastOkAt);
    SaveFeedHealth(map);
}

void RecordFeedSuccess(int feedId)
{
    var map = LoadFeedHealth();
    map[feedId] = (0, "", DateTime.Now.ToString("O"));
    SaveFeedHealth(map);
}

// 长期未更新判定：距上次拉取超过「计划间隔 × 3」；无计划/手动按 30 天
bool IsFeedStale(string schedule, DateTime lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual) return (now - lastChecked).TotalDays > 30;
    if (s.Interval is TimeSpan iv) return now - lastChecked > iv * 3;
    if (s.IsDaily) return (now - lastChecked).TotalHours > 72;
    if (s.IsWeekly) return (now - lastChecked).TotalDays > 21;
    return (now - lastChecked).TotalDays > 30;
}

// 来源健康状态：正常 / ⚠ 长期未更新 / ✗ 失败 N 次
string FeedHealthText(int feedId, string schedule, DateTime? lastChecked, DateTime now)
{
    var map = LoadFeedHealth();
    map.TryGetValue(feedId, out var e);
    if (e.FailCount > 0) return Lang.T("✗ 失败 {0} 次", e.FailCount);
    if (lastChecked is DateTime lc && IsFeedStale(schedule, lc, now)) return Lang.T("⚠ 长期未更新");
    return Lang.T("正常");
}

// 从 Feeds.RawXml 解析来源类型与作者（Atom author / RSS managingEditor / dc:creator）
(string Type, string Author) ParseFeedMeta(string rawXml)
{
    string type = "RSS", author = "";
    try
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(rawXml);
        var nsm = new System.Xml.XmlNamespaceManager(doc.NameTable);
        nsm.AddNamespace("atom", "http://www.w3.org/2005/Atom");
        nsm.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");
        var root = doc.DocumentElement;
        if (root != null && root.LocalName == "feed")
        {
            type = "Atom";
            author = root.SelectSingleNode("atom:author/atom:name", nsm)?.InnerText?.Trim() ?? "";
        }
        else
        {
            author = root?.SelectSingleNode("channel/managingEditor")?.InnerText?.Trim() ?? "";
            if (string.IsNullOrEmpty(author))
                author = root?.SelectSingleNode("channel/dc:creator", nsm)?.InnerText?.Trim() ?? "";
        }
    }
    catch { }
    return (type, author);
}

// CLI：sip --feed-info <编号> [--json] —— 来源身份与健康状态
void FeedInfoCli(string[] args, string dbPath)
{
    if (args.Length < 1 || !int.TryParse(args[0], out int dn))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --feed-info <feed-number> [--json]")); return;
    }
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    int realId = GetRealId(dn, dbPath);
    if (realId == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number {0} not found", dn), json: json); return; }

    string title = "", link = "", url = "", rawXml = "", schedule = "", lastChecked = "", lastArticle = "";
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, Link, FeedUrl, RawXml, Schedule, LastCheckedAt FROM Feeds WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", realId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number {0} not found", dn), json: json); return; }
        title = r.GetString(0);
        link = r.IsDBNull(1) ? "" : r.GetString(1);
        url = r.IsDBNull(2) ? "" : r.GetString(2);
        rawXml = r.IsDBNull(3) ? "" : r.GetString(3);
        schedule = r.IsDBNull(4) ? "" : r.GetString(4);
        lastChecked = r.IsDBNull(5) ? "" : r.GetString(5);
    }
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(PublishDate) FROM Items WHERE FeedId = @id AND Status = 'active'";
        cmd.Parameters.AddWithValue("@id", realId);
        var o = cmd.ExecuteScalar();
        lastArticle = o == null || o == DBNull.Value ? "" : o.ToString()!;
    }

    DateTime? lc = lastChecked.Length > 0 ? TryParseIso(lastChecked) : null;
    var (type, author) = ParseFeedMeta(rawXml);
    string status = FeedHealthText(realId, schedule, lc, DateTime.Now);
    var health = LoadFeedHealth();
    health.TryGetValue(realId, out var h);

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                id = realId,
                title,
                type,
                author,
                link,
                feedUrl = url,
                schedule,
                lastChecked = lc,
                lastArticle,
                status,
                failCount = h.FailCount,
                lastError = h.LastError
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("来源 [编号 {0}] {1}", dn, CjkSpace(StripControlChars(title))));
    Console.WriteLine("─────────────────────");
    Console.WriteLine(Lang.T("  名称 name       : {0}", CjkSpace(StripControlChars(title))));
    Console.WriteLine(Lang.T("  类型 type       : {0}", StripControlChars(type)));
    if (author.Length > 0) Console.WriteLine(Lang.T("  作者 author     : {0}", CjkSpace(StripControlChars(author))));
    if (link.Length > 0) Console.WriteLine(Lang.T("  官网 site       : {0}", StripControlChars(link)));
    Console.WriteLine(Lang.T("  Feed url        : {0}", StripControlChars(url)));
    Console.WriteLine(Lang.T("  上次更新 updated : {0}", lc is DateTime d ? d.ToString("yyyy-MM-dd HH:mm") : Lang.T("从未")));
    if (lastArticle.Length > 0) Console.WriteLine(Lang.T("  最近文章 latest : {0}", StripControlChars(lastArticle)));
    Console.WriteLine(Lang.T("  状态 status     : {0}", status));
}

// ══════════ OPML 导入导出（RSS 标准，零改表）══════════
string XmlEscape(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

// CLI：sip --export-opml [feeds.opml]
void ExportOpmlCli(string arg, string dbPath)
{
    string file = string.IsNullOrWhiteSpace(arg) ? "feeds.opml" : arg;
    var feeds = new List<(string Title, string Url)>();
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, FeedUrl FROM Feeds ORDER BY Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            feeds.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
    }

    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<opml version=\"2.0\">");
    sb.AppendLine("  <head><title>sip feeds</title></head>");
    sb.AppendLine("  <body>");
    foreach (var (t, u) in feeds)
        if (!string.IsNullOrWhiteSpace(u))
            sb.AppendLine($"    <outline type=\"rss\" text=\"{XmlEscape(t)}\" title=\"{XmlEscape(t)}\" xmlUrl=\"{XmlEscape(u)}\"/>");
    sb.AppendLine("  </body>");
    sb.AppendLine("</opml>");
    try
    {
        File.WriteAllText(file, sb.ToString());
    }
    catch (Exception ex)
    {
        SetExit();
        Console.WriteLine(Lang.T("Export OPML failed: {0}", ex.Message));
        return;
    }
    Console.WriteLine(Lang.T("Exported {0} feeds to {1}", feeds.Count(f => f.Url.Length > 0), file));
}

bool FeedUrlExists(string dbPath, string url)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE FeedUrl = @u";
        cmd.Parameters.AddWithValue("@u", url);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    catch { return false; }
}

// CLI：sip --import-opml <file.opml>（逐条下载添加，已存在的跳过）
void ImportOpmlCli(string file, string dbPath)
{
    if (!File.Exists(file)) { SetExit(); Console.WriteLine(Lang.T("File not found: {0}", file)); return; }
    var urls = new List<string>();
    try
    {
        var doc = new System.Xml.XmlDocument();
        doc.Load(file);
        var nodes = doc.SelectNodes("//outline[@xmlUrl]");
        if (nodes != null)
            foreach (System.Xml.XmlNode n in nodes)
            {
                string u = n.Attributes?["xmlUrl"]?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(u)) urls.Add(u.Trim());
            }
    }
    catch (Exception ex) { SetExit(); Console.WriteLine(Lang.T("Parse OPML failed: {0}", ex.Message)); return; }

    if (urls.Count == 0) { Console.WriteLine(Lang.T("No feeds found in the OPML file")); return; }
    int ok = 0, skip = 0, fail = 0;
    foreach (var u in urls)
    {
        if (FeedUrlExists(dbPath, u)) { skip++; continue; }
        try { DownloadAndSaveToDb(u, dbPath, interactive: false).Wait(); ok++; }
        catch { fail++; }
    }
    Console.WriteLine(Lang.T("Import done: {0} added, {1} skipped (already exist), {2} failed", ok, skip, fail));
}

// ══════════ 文章标记信号（article_signals.json，零改表）══════════
// 与 telemetry 分离：signals = 结论/标记层，telemetry 只记 article_like 事实
string SignalsPath() => Path.Combine(dataDir, "article_signals.json");

Dictionary<string, SignalEntry> LoadSignals()
{
    try
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, SignalEntry>>(File.ReadAllText(SignalsPath()));
        return d ?? new Dictionary<string, SignalEntry>();
    }
    catch { return new Dictionary<string, SignalEntry>(); }
}

void SaveSignals(Dictionary<string, SignalEntry> map)
{
    try
    {
        File.WriteAllText(SignalsPath(), JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

SignalEntry? GetSignal(int itemId)
{
    var map = LoadSignals();
    return map.TryGetValue(itemId.ToString(), out var e) ? e : null;
}

// 切换用户/AI 点赞（再执行 = 取消）；返回切换后是否已标记
bool ToggleSignal(int itemId, bool ai, string? reason, string dbPath)
{
    var map = LoadSignals();
    var key = itemId.ToString();
    map.TryGetValue(key, out var e);
    e ??= new SignalEntry();
    bool liked;
    if (ai)
    {
        e.AiLike = !e.AiLike;
        if (e.AiLike) e.AiReason = string.IsNullOrWhiteSpace(reason) ? "" : reason;
        liked = e.AiLike;
    }
    else
    {
        e.UserLike = !e.UserLike;
        liked = e.UserLike;
    }
    e.UpdatedAt = DateTime.Now.ToString("O");
    if (!e.UserLike && !e.AiLike) map.Remove(key); else map[key] = e;
    SaveSignals(map);
    TelemetryService.Record("article_like", articleId: itemId, data: new { actor = ai ? "ai" : "user", liked });
    return liked;
}

// CLI：sip --like <id> [--ai [reason]]（切换）
void LikeCli(string[] args, string dbPath)
{
    if (args.Length < 1 || !int.TryParse(args[0], out int itemId))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --like <article-id> [--ai [reason]]")); return;
    }
    if (!ArticleExists(itemId, dbPath)) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId)); return; }
    bool ai = args.Any(a => a.Equals("--ai", StringComparison.OrdinalIgnoreCase));
    string? reason = null;
    int idx = Array.FindIndex(args, a => a.Equals("--ai", StringComparison.OrdinalIgnoreCase));
    if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
        reason = string.Join(" ", args.Skip(idx + 1));
    bool liked = ToggleSignal(itemId, ai, reason, dbPath);
    Console.WriteLine(ai
        ? (liked ? Lang.T("AI 标记了文章 {0}（用户可能喜欢）", itemId) : Lang.T("已取消 AI 标记 {0}", itemId))
        : (liked ? Lang.T("已收藏文章 {0} ♥", itemId) : Lang.T("已取消收藏 {0}", itemId)));
}

// CLI：sip --likes [--json] —— 列出所有标记文章
void LikesCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var map = LoadSignals();
    var ids = map.Keys.Where(k => int.TryParse(k, out _)).Select(int.Parse).ToList();
    if (ids.Count == 0)
    {
        if (json) JsonOut(new { success = true, data = new { signals = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("No liked articles yet"));
        return;
    }
    // 查标题
    var titles = new Dictionary<int, string>();
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title FROM Items WHERE Id IN (" + string.Join(",", ids) + ")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) titles[r.GetInt32(0)] = r.GetString(1);
    }
    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                signals = ids.OrderBy(id => id).Select(id =>
                {
                    map.TryGetValue(id.ToString(), out var e);
                    return new
                    {
                        itemId = id,
                        title = titles.TryGetValue(id, out var t) ? t : "",
                        userLike = e?.UserLike ?? false,
                        aiLike = e?.AiLike ?? false,
                        aiReason = e?.AiReason ?? "",
                        updatedAt = e?.UpdatedAt ?? ""
                    };
                })
            }
        });
        return;
    }
    foreach (var id in ids.OrderBy(id => id))
    {
        map.TryGetValue(id.ToString(), out var e);
        string marks = (e?.UserLike == true ? "♥" : "") + (e?.AiLike == true ? "🤖" : "");
        Console.WriteLine($"[{id}] {marks} {StripControlChars(titles.TryGetValue(id, out var t) ? t : "")}" + (string.IsNullOrEmpty(e?.AiReason) ? "" : $"  ({StripControlChars(e.AiReason)})"));
    }
}

// CLI：sip telemetry status|show|enable|disable|clear|export
void TelemetryCli(string[] args, string dbPath)
{
    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
    switch (sub)
    {
        case "status":
        {
            var (count, first, last) = TelemetryService.Stats();
            Console.WriteLine(Lang.T("Sumenia（苏暖泉）: {0}", TelemetryService.Consent == "enabled" ? Lang.T("开启") : TelemetryService.Consent == "disabled" ? Lang.T("关闭(用户拒绝)") : Lang.T("关闭(未选择)")));
            Console.WriteLine(Lang.T("  事件数 events   : {0}", count));
            Console.WriteLine(Lang.T("  首次记录 first   : {0}", first ?? Lang.T("—")));
            Console.WriteLine(Lang.T("  最后记录 last    : {0}", last ?? Lang.T("—")));
            if (TelemetryService.Consent == "unset")
                Console.WriteLine(Lang.T("  提示：Sumenia 默认关闭；如需开启运行 sip telemetry enable"));
            return;
        }
        case "show":
        {
            int limit = 20;
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i] == "--limit" && int.TryParse(args[i + 1], out int n)) limit = Math.Clamp(n, 1, 1000);
            var events = TelemetryService.AllEvents(limit);
            foreach (var e in events)
            {
                string ts = e.Timestamp.Length >= 19 ? e.Timestamp[..19].Replace("T", " ") : e.Timestamp;
                string extra = e.ArticleId.HasValue ? " article=" + e.ArticleId : "";
                if (e.Surface != null) extra += " surface=" + e.Surface;
                Console.WriteLine($"{ts} {e.Type}{extra}" + (string.IsNullOrEmpty(e.DataJson) ? "" : $"  {e.DataJson}"));
            }
            if (events.Count == 0) Console.WriteLine(Lang.T("(Sumenia 暂无事件记录)"));
            return;
        }
        case "enable":
            TelemetryService.SetConsent("enabled");
            Console.WriteLine(Lang.T("Sumenia（苏暖泉）已开启（仅本地记录，不会上传）"));
            return;
        case "disable":
            TelemetryService.SetConsent("disabled");
            Console.WriteLine(Lang.T("Sumenia（苏暖泉）已关闭（历史数据保留，不再记录新事件）"));
            return;
        case "clear":
            TelemetryService.Clear();
            Console.WriteLine(Lang.T("Sumenia 事件已清空（保留你的开关选择）"));
            return;
        case "export":
        {
            string file = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "telemetry.json";
            var events = TelemetryService.AllEvents();
            var arr = events.Select(e => new
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
                data = string.IsNullOrEmpty(e.DataJson) ? null : System.Text.Json.Nodes.JsonNode.Parse(e.DataJson)
            });
            File.WriteAllText(file, JsonSerializer.Serialize(new { exportedAt = DateTime.Now.ToString("O"), events = arr },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine(Lang.T("Exported {0} events to {1}", events.Count, file));
            Console.WriteLine(Lang.T("这些数据仅由你决定是否分享给开发者，sip 不会自动上传"));
            return;
        }
        default:
            SetExit();
            Console.WriteLine(Lang.T("Usage: sip telemetry status | show [--limit N] | enable | disable | clear | export [file]"));
            return;
    }
}

// ══════════ Sip Today v1（规则式每日清单，先引导习惯，不做个性化）══════════
// 选文规则（可解释、无黑盒）：近 48h 新增 / 近期被作者更新 / 全文质量 / ♥🤖 标记加权。
// 等 telemetry 积累足够行为数据后，再演进为个性化排序。
//
// 「一天一碗」：当日清单缓存到 sip_today_cache.json，一天只生成一次（仪式感、防反复刷）；
// --refresh 可显式重新生成（新订阅/新标记当天可见）；进度(done/target)保持实时不进缓存。
string TodayCachePath() => Path.Combine(dataDir, "sip_today_cache.json");

// 返回(缓存日期, 生成时间, 条目)；缓存缺失/损坏返回空
(string Date, string GeneratedAt, List<TodayItem> Items) LoadTodayCache()
{
    try
    {
        if (!File.Exists(TodayCachePath())) return ("", "", new List<TodayItem>());
        var doc = JsonDocument.Parse(File.ReadAllText(TodayCachePath()));
        var root = doc.RootElement;
        string date = root.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        string genAt = root.TryGetProperty("generatedAt", out var g) ? g.GetString() ?? "" : "";
        var items = new List<TodayItem>();
        if (root.TryGetProperty("items", out var arr))
            foreach (var it in arr.EnumerateArray())
            {
                items.Add(new TodayItem
                {
                    ItemId = it.TryGetProperty("itemId", out var ii) ? ii.GetInt32() : 0,
                    Title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Source = it.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                    Reason = it.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    Minutes = it.TryGetProperty("minutes", out var m) ? m.GetDouble() : 0,
                    Score = it.TryGetProperty("score", out var sc) ? sc.GetInt32() : 0
                });
            }
        return (date, genAt, items);
    }
    catch { return ("", "", new List<TodayItem>()); }   // 缓存损坏 → 当无缓存
}

void SaveTodayCache(string date, List<TodayItem> items)
{
    try
    {
        File.WriteAllText(TodayCachePath(), JsonSerializer.Serialize(new
        {
            date,
            generatedAt = DateTime.Now.ToString("O"),
            items = items.Select(i => new
            {
                itemId = i.ItemId, title = i.Title, source = i.Source,
                reason = i.Reason, minutes = i.Minutes, score = i.Score
            })
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

// 今日清单：refresh=false 时当天固定（读缓存）；跨天/无缓存/损坏/refresh=true 时重算并落盘
List<TodayItem> GetTodayList(string dbPath, int limit, bool refresh, out string generatedAt)
{
    string today = DateTime.Now.ToString("yyyy-MM-dd");
    var (cacheDate, cacheAt, cacheItems) = LoadTodayCache();
    if (!refresh && cacheDate == today && cacheItems.Count > 0)
    {
        // 缓存里的生成时间是 ISO，格式化到 HH:mm 便于展示
        generatedAt = TryParseIso(cacheAt) is DateTime g ? g.ToString("HH:mm") : cacheAt;
        return cacheItems.Take(limit).ToList();
    }
    var items = BuildTodayList(dbPath, limit);
    SaveTodayCache(today, items);
    generatedAt = DateTime.Now.ToString("HH:mm");
    return items;
}

List<TodayItem> BuildTodayList(string dbPath, int limit = 10)
{
    var items = new List<TodayItem>();
    var signals = LoadSignals();
    var now = DateTime.Now;
    var freshCutoff = now.AddHours(-48);

    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, PublishDate, Version, Content, Description, FeedTitle
            FROM (
                SELECT i.Id, i.Title, i.PublishDate, i.Version, i.Content, i.Description,
                       f.Title AS FeedTitle,
                       ROW_NUMBER() OVER (PARTITION BY i.Guid ORDER BY i.Version DESC) AS rn
                FROM Items i JOIN Feeds f ON i.FeedId = f.Id
                WHERE i.Status = 'active' AND i.Guid IS NOT NULL
            )
            WHERE Guid = '' OR rn = 1
            ORDER BY Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int itemId = r.GetInt32(0);
            string title = r.GetString(1);
            DateTime? pub = r.IsDBNull(2) ? null : TryParseIso(r.GetString(2));
            int version = r.GetInt32(3);
            string content = r.IsDBNull(4) ? "" : r.GetString(4);
            string desc = r.IsDBNull(5) ? "" : r.GetString(5);
            string source = r.GetString(6);

            int score = 0;
            var reasons = new List<string>();
            if (pub is DateTime p && p >= freshCutoff) { score += 3; reasons.Add(Lang.T("新增")); }
            else if (pub is DateTime p2 && p2 >= now.AddDays(-7)) { score += 1; }
            else score -= 1;
            if (version > 1) { score += 2; reasons.Add(Lang.T("有更新")); }
            string q = ContentQuality(content, desc);
            if (q == "full") score += 1;
            else if (q == "empty") score -= 1;
            if (q == "short") reasons.Add(Lang.T("仅摘要"));

            if (signals.TryGetValue(itemId.ToString(), out var sig))
            {
                if (sig.AiLike) { score += 3; reasons.Add(Lang.T("AI 关注")); }
                if (sig.UserLike) { score += 2; reasons.Add(Lang.T("你收藏过")); }
            }

            int chars = Math.Max(content.Length, desc.Length);
            double minutes = Math.Max(0.5, Math.Round(chars / (5.0 * 60.0), 1));   // ≈300 字/分
            // 没有任何具体理由时给个兜底（近期可读全文）
            if (reasons.Count == 0) reasons.Add(q == "short" ? Lang.T("仅摘要") : Lang.T("近期"));
            items.Add(new TodayItem
            {
                ItemId = itemId, Title = title, Source = source,
                Reason = string.Join(" · ", reasons), Minutes = minutes, Score = score
            });
        }
    }

    return items.Where(i => i.Score >= 2)          // 有明确正面信号才进今日
                .OrderByDescending(i => i.Score)
                .ThenByDescending(i => i.ItemId)
                .Take(limit)
                .ToList();
}

// 今日阅读进度：目标固定 5 篇（v1；数据积累后做成配置/自适应）；
// 完成数来自 telemetry 的 article_complete（当天）；遥测关闭时 tracking=false
(int Done, int Target, bool Tracking) TodayProgress(string dbPath)
{
    const int target = 5;
    if (!TelemetryService.IsEnabled || TelemetryService.Consent != "enabled")
        return (0, target, false);
    int done = 0;
    try
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(dataDir, "telemetry.db")}");
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM telemetry_events WHERE type = 'article_complete' AND timestamp LIKE @d";
        c.Parameters.AddWithValue("@d", DateTime.Now.ToString("yyyy-MM-dd") + "%");
        done = Convert.ToInt32(c.ExecuteScalar());
    }
    catch { }
    return (done, target, true);
}

// CLI：sip --today [--json] [--refresh] [--quick N]
void TodayCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    bool refresh = args.Any(a => a.Equals("--refresh", StringComparison.OrdinalIgnoreCase));
    int quick = 5;
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals("--quick", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int q))
            quick = Math.Clamp(q, 1, 5);   // 时间不够就只喝一小口：--quick N
    var (done, target, tracking) = TodayProgress(dbPath);
    var list = GetTodayList(dbPath, quick, refresh, out string generatedAt);   // 一天一碗；--refresh 重生成

    if (json)
    {
        JsonOut(new
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
        return;
    }

    Console.WriteLine(Lang.T("今日哈汤 · {0}", DateTime.Now.ToString("yyyy-MM-dd")));
    if (refresh)
        Console.WriteLine(Lang.T("（已重新生成今日哈汤）"));
    Console.WriteLine("─────────────────────");
    if (list.Count == 0)
    {
        Console.WriteLine(Lang.T("（今天还没有值得读的——去添加或更新一些订阅源吧）"));
    }
    for (int i = 0; i < list.Count; i++)
    {
        var it = list[i];
        Console.WriteLine($" {i + 1}. {CjkSpace(StripControlChars(it.Title))}");
        Console.WriteLine(Lang.T("    [{0} · ~{1} 分钟 · {2}]", it.Source, it.Minutes, it.Reason));
    }
    Console.WriteLine("─────────────────────");
    double total = list.Sum(i => i.Minutes);
    if (tracking)
        Console.WriteLine(Lang.T("共约 {0} 分钟 · 今日目标 {1} 篇 · 已完成 {2} 篇{3}", total, target, done, done >= target ? Lang.T(" 🎉 今天结束") : ""));
    else
        Console.WriteLine(Lang.T("共约 {0} 分钟 · 今日目标 {1} 篇（开启 Sumenia 可跟踪完成进度）", total, target));
    if (!refresh)
        Console.WriteLine(Lang.T("（今日哈汤已生成于 {0} · --refresh 可重新来一碗 · 新文章随时可从侧栏/--search/--grep 看）", generatedAt));
}

// ══════════ CLI 参数处理 ══════════
async Task RunCli(string[] args, string dbPath)
{
    var cmd = args[0].ToLower();

    // 原文阅读：sip --show <文章编号>
    //   默认 → 全屏阅读界面（无侧栏，W 进入完整 TUI，Esc/Q 退出），给人读文章
    //   --json → 原文 JSON 打到标准输出（未渲染），供 AI / 脚本读取
    if (cmd is "--show" or "--content")
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int sNum))
        {
            SetExit(); Console.WriteLine(Lang.T("Usage: sip --show <article-id>"));
            return;
        }
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (json) ShowArticleJson(sNum, dbPath);
        else
        {
            if (!ArticleExists(sNum, dbPath)) { SetExit(); Console.WriteLine(Lang.T("Article {0} not found", sNum)); return; }
            await RunFullscreenReader(sNum, dbPath);
        }
        return;
    }

    if (cmd is "-h" or "--help")
    {
        PrintHelp();
        return;
    }

    if (cmd is "-l" or "--list")
    {
        bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        // 找第一个非 flag 的参数作为编号（-l --json 或 -l 1 --json 都能用）
        var numArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (numArg != null)
        {
            // -l 后面带编号 → 列出该源的文章
            if (!int.TryParse(numArg, out int lNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            int feedRealId = GetRealId(lNum, dbPath);
            if (feedRealId == 0)
            {
                if (json) ReportError("FEED_NOT_FOUND", Lang.T("Feed number not found"), json: true);
                else { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); }
                return;
            }
            ListArticlesFromDb(feedRealId, lNum, dbPath, json);
        }
        else
        {
            ListFeedsFromDb(dbPath, json);
        }
        return;
    }

    // ══════════ 更新调度命令 ═══════════
    if (cmd is "--schedule" or "--sched")
    {
        if (args.Length < 3)
        {
            SetExit(); Console.WriteLine(Lang.T("Usage: sip --schedule <id> <expr>; expr like 30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual"));
            return;
        }
        SetFeedSchedule(args[1], args[2], dbPath);
        return;
    }
    if (cmd is "--sync")
    {
        await SyncCli(args.Skip(1).ToArray(), dbPath);
        return;
    }
    if (cmd is "--update-all")
    {
        await UpdateAllCli(dbPath);
        return;
    }

    // ══════════ AI 无参数命令（注意不能用 args.Length >= 2 判断）═══════════
    switch (cmd)
    {
        case "--init":
            InitAiConfigInteractive(dbPath);
            return;
        case "--config":
            ShowConfig(dbPath);
            return;
        case "--index":
            await IndexArticlesCli(new string[] { }, dbPath);
            return;
        case "--reindex":
            await ReindexCli(dbPath);
            return;
        case "--summary-all":
            await SummaryAllCli(dbPath);
            return;
        case "--purge-fulltext":
            PurgeFulltextCli(args.Length > 1 ? args[1] : "", dbPath);
            return;
        case "--export-opml":
            ExportOpmlCli(args.Length > 1 ? args[1] : "", dbPath);
            return;
        case "--likes":
            LikesCli(args.Skip(1).ToArray(), dbPath);
            return;
        case "--today":
            TodayCli(args.Skip(1).ToArray(), dbPath);
            return;
    }

    // 已知但需要参数的命令；不在此列的一律当作"已知命令"但少参数，否则是未知命令
    bool needsArg = cmd is "-u" or "--update" or "-d" or "--download" or "-a" or "--archive"
                    or "-una" or "--unarchive" or "-r" or "--remove" or "--search" or "--summary" or "--grep"
                    or "--versions" or "--history" or "--fulltext" or "--diff" or "--export"
                    or "--feed-info" or "--import-opml" or "--like" or "telemetry";
    if (args.Length < 2)
    {
        if (!needsArg) { SetExit(); Console.WriteLine(Lang.T("Unknown command: {0}", cmd)); PrintHelp(); return; }
        SetExit(); Console.WriteLine(Lang.T("Missing argument. Usage: sip {0} <arg>", cmd));
        return;
    }

    switch (cmd)
    {
        case "-u" or "--update":
            if (!int.TryParse(args[1], out int aNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            UpdateFeed(aNum, dbPath).Wait();
            break;
        case "-d" or "--download":
            DownloadCli(args[1], dbPath);
            break;
        case "-a" or "--archive":
            if (!int.TryParse(args[1], out int tNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            AddTimestamp(tNum, dbPath);
            break;
        case "-una" or "--unarchive":
            if (!int.TryParse(args[1], out int uNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            RemoveTimestamp(uNum, dbPath);
            break;
        case "-r" or "--remove":
            if (!int.TryParse(args[1], out int dNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            DeleteFeed(dNum, dbPath, args.Contains("--yes", StringComparer.OrdinalIgnoreCase) || args.Contains("-y", StringComparer.OrdinalIgnoreCase));
            break;
        case "--search":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --search <query> [--feed number] [--threshold 0.7] [--json]")); return; }
            SearchCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--grep":
            GrepCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--fulltext":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --fulltext <article-id> [--yes] [--json]")); return; }
            FulltextCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--versions" or "--history":
            ListVersionsCli(args[1], dbPath, args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)));
            break;
        case "--diff":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --diff <article-id> [vA vB] [--json]")); return; }
            DiffCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--export":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --export <article-id | feed:number | all> [out.md | dir] [--yes]")); return; }
            ExportCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--feed-info":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --feed-info <feed-number> [--json]")); return; }
            FeedInfoCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--import-opml":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --import-opml <file.opml>")); return; }
            ImportOpmlCli(args[1], dbPath);
            break;
        case "--like":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --like <article-id> [--ai [reason]]")); return; }
            LikeCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "telemetry":
            TelemetryCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--summary":
            SummaryCli(args[1], dbPath, args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase))).Wait();
            break;
        default:
            SetExit(); Console.WriteLine(Lang.T("Unknown command: {0}", cmd));
            PrintHelp();
            break;
    }
}

void PrintHelp()
{
    Console.WriteLine(Lang.T("Usage: sip <command> [args]"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Commands:"));
    Console.WriteLine(Lang.T("  -l, --list       list all feeds"));
    Console.WriteLine(Lang.T("  -u, --update     update a feed (number)"));
    Console.WriteLine(Lang.T("  -d, --download   download a new RSS feed (URL)"));
    Console.WriteLine(Lang.T("  -a, --archive    archive a feed (add timestamp)"));
    Console.WriteLine(Lang.T("  -una, --unarchive unarchive a feed"));
    Console.WriteLine(Lang.T("  -r, --remove     delete a feed (add --yes to skip confirmation)"));
    Console.WriteLine(Lang.T("  --show <id>      fullscreen reading (no sidebar; W = full TUI, Esc = exit); add --json to output raw content for AI/scripts"));
    Console.WriteLine(Lang.T("  --versions <id>  list all versions of an article (use --show <id> to view one)"));
    Console.WriteLine(Lang.T("  --diff <id> [vA vB]  diff two versions of an article (default: last two); --json for structured output"));
    Console.WriteLine(Lang.T("  --export <id | feed:N | all> [out.md|dir]  export article(s) as Markdown (--yes to skip confirm)"));
    Console.WriteLine(Lang.T("  --fulltext <id>  fetch the article's full text to a local cache (--yes skip consent/confirm; --json)"));
    Console.WriteLine(Lang.T("  --purge-fulltext [id]  clear the full-text cache"));
    Console.WriteLine(Lang.T("  --feed-info <n>  source identity & health (type/author/site/updated/status; --json)"));
    Console.WriteLine(Lang.T("  --export-opml [file]  export feeds as OPML; --import-opml <file>  import feeds"));
    Console.WriteLine(Lang.T("  --like <id> [--ai [reason]]  mark an article (♥ user / 🤖 AI); --likes lists marks"));
    Console.WriteLine(Lang.T("  --today [--json]  today's curated reading list (rule-based; guides daily reading habit)"));
    Console.WriteLine(Lang.T("  telemetry status|show|enable|disable|clear|export  local reading telemetry · Sumenia (default OFF)"));
    Console.WriteLine(Lang.T("  -h, --help       show this help"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Update scheduling:"));
    Console.WriteLine(Lang.T("  --sync [--feed N] [--json]  update only 'due' feeds (lists last/next times)"));
    Console.WriteLine(Lang.T("  --update-all            force update all feeds (same as TUI F6)"));
    Console.WriteLine(Lang.T("  --schedule <id> <expr>  set a feed's update schedule, e.g. 30m / 1h / 7d / daily@10:00 / weekly@Mon 08:00 / manual"));
    Console.WriteLine(Lang.T("  -l shows each feed's 'schedule · last · next'"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("AI commands:"));
    Console.WriteLine(Lang.T("  --init           configure AI for the first time (model + API key)"));
    Console.WriteLine(Lang.T("  --config         view/edit AI config"));
    Console.WriteLine(Lang.T("  --index          embed articles (interactive selection)"));
    Console.WriteLine(Lang.T("  --reindex        re-embed after changing the embedding model"));
    Console.WriteLine(Lang.T("  --search <query> [--feed number] [--threshold 0.7] [--json] semantic search (all feeds without --feed)"));
    Console.WriteLine(Lang.T("  --grep <keyword>   full-text search (title/content/summary, no AI needed); outputs id+title+count and ±50-char snippets, bounded (--limit N / --max-snippets N / --json / --full)"));
    Console.WriteLine(Lang.T("  --summary <id>   summarize one article; use feed:<number> for all articles of a feed (--json)"));
    Console.WriteLine(Lang.T("  --summary-all    summarize all articles without a summary"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Examples:"));
    Console.WriteLine(Lang.T("  sip -l"));
    Console.WriteLine(Lang.T("  sip -d https://example.com/rss"));
    Console.WriteLine(Lang.T("  sip -u 1"));
    Console.WriteLine(Lang.T("  sip -a 1"));
    Console.WriteLine(Lang.T("  sip --search \"LLM Agent\" --feed 1 --json"));
    Console.WriteLine(Lang.T("  sip --summary 12"));
    Console.WriteLine(Lang.T("  sip --summary feed:3"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Global options:"));
    Console.WriteLine(Lang.T("  --ignoresafeannouncement   skip safety banner, data only (for scripts / AI agents)"));
    Console.WriteLine(Lang.T("  --lang <code>              language file (e.g. zh-CN / en-US, default zh-CN)"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Security notes:"));
    Console.WriteLine(Lang.T("  API keys are stored in the OS-native credential store (Windows Credential Manager / macOS Keychain / Linux Secret Service),"));
    Console.WriteLine(Lang.T("  never written to any file. Do not leak it. You will be prompted on first AI use."));
}

// ══════════ TUI（Terminal.Gui 文件夹视图）═══════════
// 布局：左侧订阅源+文章树（源为父节点，展开即见文章）/ 右侧正文预览 / 底部状态栏
// 操作：↑↓ 选择，Enter 折叠/展开源或打开文章，←→ 切换树/正文，PageUp/PageDown 翻页，
//       U 更新当前源，F6 全部更新，A 归档，R 去归档，X 删除，D 加源，S 搜索，Y 摘要，H 帮助，Q 退出
#pragma warning disable CS0618  // 使用尚未迁移的静态 Application API
async Task<int> RunTui(string dbPath, bool appReady = false, bool showStartScreen = true, long preselectItemId = 0)
{
    if (!appReady) Application.Init();
    try
    {
        // 开始界面：回车进入 / Q 退出
        if (showStartScreen && !ShowStartScreen(dbPath)) return 0;
        EnsureTelemetryConsentTui();   // 首次询问遥测（默认保持关闭）

        // —— 左侧：订阅源 + 文章 侧栏（文章标题自动换行显示）——
        // 侧栏为自绘 View：来源可展开/折叠，标题过长时自动换行（CJK 宽度感知）
        var tree = new SidebarView(feedId => LoadArticleNodes(feedId, dbPath))
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(24),
            Height = Dim.Fill() - 3,
            CanFocus = true,
            BorderStyle = LineStyle.Single,
            Title = " " + Lang.T("Feeds") + " (C " + Lang.T("collapse") + ") "
        };
        tree.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
            HotNormal = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black),
            // 聚焦时选中行用清晰的亮青反色；正文区聚焦（阅读中）时选中行柔和变暗，不抢注意力
            Active = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkCyan),
            HotActive = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkCyan),
            Focus = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
            HotFocus = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
            Highlight = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
            ReadOnly = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black)
        });

        // —— 中间垂直分隔线 ——
        var vDivider = new Line
        {
            Orientation = Orientation.Vertical,
            Style = LineStyle.Single,
            X = Pos.Right(tree) + 1,
            Y = 0,
            Height = Dim.Fill() - 3
        };

        // —— 右侧：正文预览（Markdown 渲染：标题/粗体/斜体/删除线/分隔线/列表/图片）——
        var contentView = CreateMarkdownView();
        contentView.X = Pos.Right(tree) + 2;
        contentView.Y = 0;
        contentView.Width = Dim.Fill();
        contentView.Height = Dim.Fill() - 3;
        contentView.CanFocus = true;
        contentView.BorderStyle = LineStyle.Single;
        contentView.Title = " " + Lang.T("Content") + " ";

        // 侧栏折叠状态：按 C 折叠左侧栏，正文区扩张（再按 C 恢复）
        bool sidebarCollapsed = false;
        void ToggleSidebar()
        {
            sidebarCollapsed = !sidebarCollapsed;
            tree.Visible = !sidebarCollapsed;
            vDivider.Visible = !sidebarCollapsed;
            if (sidebarCollapsed) contentView.X = 0;
            else contentView.X = Pos.Right(tree) + 2;
            UpdateLinkNavTitle();
            contentView.SetFocus();
        }

        // 沉浸阅读状态（ToggleImmersive 定义在 statusBar 之后，因为要用到它）
        bool immersive = false;

        // 底部命令行：平时隐藏，按 Esc 唤出，Enter 执行后隐藏，再按 Esc 隐藏
        var cmdBar = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1),
            Height = 1,
            CanFocus = true,
            Text = "",
            Secret = false,
            Visible = false
        };
        var cmdLabel = new Label
        {
            Text = ":",
            X = 0,
            Y = Pos.AnchorEnd(2),
            CanFocus = false,
            Visible = false
        };

        // 主窗口（先于 UpdateStats 声明，后者会更新窗口标题）
        var top = new Window
        {
            Title = " sip RSS Reader ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        // 状态行（命令行隐藏时显示）：源数 · 文章位置/总数
        var statsLabel = new Label
        {
            Text = "",
            X = 1,
            Y = Pos.AnchorEnd(2),
            CanFocus = false,
            Visible = true
        };
        // —— 阅读进度记忆（按文章记住滚动位置；文件存储，零改表）——
        // 变量声明必须在 UpdateStats 之前（局部变量不能前向引用）
        var progressMap = LoadReadingProgress();
        long _currentArticleId = 0;
        int _savedScrollY = -1;   // 打开文章时若检测到历史进度，存这里；-1 = 无

        // —— Telemetry 阅读状态（仅内存，会话内）——
        double _maxProgress = 0;      // 当前文章最大进度 0-1
        DateTime _lastActivity = default;
        double _activeSeconds = 0;    // 当前文章活跃阅读秒数（空档不计）
        int _estimatedSeconds = 0;    // 预估阅读时长 ERT
        int _lastMilestone = 0;       // 已上报里程碑 0/25/50/75/100
        // 活动事件时累计活跃时间：空档超过 ERT×25%（10~120s）不计入
        void TelemetryActivityTick()
        {
            if (_currentArticleId == 0) return;
            var now = DateTime.Now;
            if (_lastActivity == default) { _lastActivity = now; return; }
            double gap = (now - _lastActivity).TotalSeconds;
            double idleThreshold = Math.Clamp(_estimatedSeconds * 0.25, 10, 120);
            if (gap <= idleThreshold) _activeSeconds += gap;
            _lastActivity = now;
        }
        // 打开文章：记录 open + 按内容长度算 ERT + 初始化计时
        void TelemetryOpenArticle(long itemId, int feedId)
        {
            TelemetryService.Record("article_open", articleId: (int)itemId, sourceId: feedId);
            int chars = 0;
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                var c = conn.CreateCommand();
                c.CommandText = "SELECT LENGTH(COALESCE(Content,'')), LENGTH(COALESCE(Description,'')) FROM Items WHERE Id = @id";
                c.Parameters.AddWithValue("@id", itemId);
                using var r = c.ExecuteReader();
                if (r.Read()) chars = Math.Max(r.GetInt32(0), r.GetInt32(1));
            }
            catch { }
            _estimatedSeconds = Math.Max(10, chars / 5);
            _lastActivity = DateTime.Now;
            _activeSeconds = 0;
            _maxProgress = 0;
            _lastMilestone = 0;
        }
        // 进度更新：里程碑 25/50/75/100 + 滚到底 = complete（带 active/estimated/time_ratio）
        void TelemetryProgressTick(double ratio)
        {
            if (ratio > _maxProgress) _maxProgress = ratio;
            if (_maxProgress <= 0) return;
            if (ratio >= 1.0 && _lastMilestone < 100)
            {
                _lastMilestone = 100;
                TelemetryService.Record("article_complete", articleId: (int)_currentArticleId,
                    data: new { active_seconds = Math.Round(_activeSeconds, 1), estimated_seconds = _estimatedSeconds,
                               time_ratio = Math.Round(_estimatedSeconds > 0 ? _activeSeconds / _estimatedSeconds : 0, 3),
                               max_progress = Math.Round(_maxProgress, 3) });
                return;
            }
            int ms = (int)(Math.Min(ratio, 0.999) * 100 / 25) * 25;
            if (ms > _lastMilestone)
            {
                _lastMilestone = ms;
                TelemetryService.Record("article_progress", articleId: (int)_currentArticleId,
                    data: new { progress = ms / 100.0, max_progress = Math.Round(_maxProgress, 3) });
            }
        }
        // 离开当前文章：progress < 10% 记 skip（主动离开才触发）；否则补记最终进度
        void TelemetryCloseArticle()
        {
            if (_currentArticleId == 0) return;
            if (_maxProgress < 0.10)
            {
                TelemetryService.Record("article_skip", articleId: (int)_currentArticleId,
                    data: new { progress = Math.Round(_maxProgress, 3) });
            }
            else if (_maxProgress > 0 && _lastMilestone < 100)
            {
                TelemetryService.Record("article_progress", articleId: (int)_currentArticleId,
                    data: new { progress = Math.Round(_maxProgress, 3), max_progress = Math.Round(_maxProgress, 3) });
            }
        }

        void UpdateStats()
        {
            // 检测到阅读进度时，状态行优先显示跳转提示（标题栏会截断，这里更显眼）
            if (_savedScrollY > 0)
            {
                statsLabel.Text = Lang.T("▷ 按 Space 跳回上次位置");
                return;
            }
            int feeds = 0, articles = 0;
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                var c = conn.CreateCommand();
                c.CommandText = "SELECT (SELECT COUNT(*) FROM Feeds), (SELECT COUNT(*) FROM Items WHERE Status = 'active')";
                using var rr = c.ExecuteReader();
                if (rr.Read()) { feeds = rr.GetInt32(0); articles = rr.GetInt32(1); }
            }
            catch { }
            var (cur, tot) = tree.ArticlePosition();
            statsLabel.Text = Lang.T("feeds {0} · article {1}/{2}", feeds, cur, Math.Max(articles, tot));
            top.Title = $" sip RSS Reader · {Lang.T("feeds {0}", feeds)} ";
        }

        // 正文/概要模式 + 链接导航状态（供状态栏快捷键引用）
        bool contentMode = true;     // true=完整正文，false=文章概要
        bool linkNavMode = false;
        bool _syncing = false;       // 到期源自动同步进行中（防重入）
        int linkNavIndex = 0;

        // —— 阅读进度：保存 / 跳转 / 退出（函数必须在变量声明之后）——
        void SaveCurrentScroll()
        {
            if (_currentArticleId == 0) return;
            try { progressMap[_currentArticleId] = contentView.Viewport.Y; } catch { }
            // 遥测进度：按滚动位置算 ratio，驱动里程碑/complete
            try
            {
                int h = contentView.GetContentHeight();
                if (h > 0) TelemetryProgressTick(Math.Clamp(contentView.Viewport.Y / (double)h, 0, 1.0));
            }
            catch { }
        }
        // 跳到上次阅读位置（按 Space 触发）：对进度做边界校验，绝不跳到负数或超出正文范围
        void JumpToSaved()
        {
            if (_savedScrollY <= 0) return;
            try
            {
                int maxY = Math.Max(0, contentView.GetContentHeight() - contentView.Viewport.Height);
                int y = Math.Clamp(_savedScrollY, 0, maxY);
                contentView.ScrollVertical(y);
                _savedScrollY = -1;
                SaveCurrentScroll();
                UpdateStats();
                UpdateLinkNavTitle();
            }
            catch { _savedScrollY = -1; }
        }
        // 退出前保存并落盘（必须在 RequestStop 之前调，否则 Viewport 已归 0）
        void QuitApp()
        {
            SaveCurrentScroll();
            TelemetryCloseArticle();   // 主动退出，低进度记 skip
            SaveReadingProgress(progressMap);
            top.RequestStop();
        }

        // 状态栏快捷操作（全键盘，键位对齐外部 CLI）
        var statusBar = new StatusBar(new Shortcut[]
        {
            new Shortcut(Key.H, Lang.T("Help"), () => ShowHelpDialog(), Lang.T("Show all keybindings")),
            new Shortcut(Key.F2, Lang.T("About"), () => ShowAboutDialog(), Lang.T("About sip")),
            new Shortcut(Key.U, Lang.T("Update"), () => RefreshSelectedFeed(), Lang.T("Update selected feed (same as CLI -u)")),
            new Shortcut(Key.F6, Lang.T("Update all"), () => RefreshAllFeeds(), Lang.T("Update all feeds")),
            new Shortcut(Key.A, Lang.T("Archive"), () => ArchiveSelectedFeed(), Lang.T("Add timestamp to feed (same as CLI -a)")),
            new Shortcut(Key.R, Lang.T("Unarchive"), () => UnarchiveSelectedFeed(), Lang.T("Remove timestamp (same as CLI -una)")),
            new Shortcut(Key.X, Lang.T("Delete"), () => DeleteSelected(), Lang.T("Delete selected feed/article (same as CLI -r)")),
            new Shortcut(Key.D, Lang.T("Add"), () => AddFeedDialog(), Lang.T("Add new feed (same as CLI -d)")),
            new Shortcut(Key.S, Lang.T("Search"), () => SearchDialog(), Lang.T("Semantic search (same as CLI --search)")),
            new Shortcut(Key.Y, Lang.T("Summary"), () => SummarizeSelected(), Lang.T("Summarize current article (same as CLI --summary)")),
            new Shortcut(Key.G, Lang.T("Overview"), () => ToggleContentMode(), Lang.T("Toggle content/overview")),
                new Shortcut(Key.Q, Lang.T("Quit"), QuitApp, Lang.T("Exit program"))
        });

        top.Add(tree, vDivider, contentView, cmdLabel, cmdBar, statsLabel, statusBar);

        // 沉浸阅读：隐藏侧栏/分隔线/状态栏/状态行，正文占满全屏；再按 i 恢复
        void ToggleImmersive()
        {
            immersive = !immersive;
            tree.Visible = !immersive && !sidebarCollapsed;
            vDivider.Visible = !immersive;
            statusBar.Visible = !immersive;
            statsLabel.Visible = !immersive && !cmdBar.Visible;
            cmdBar.Visible = false;
            cmdLabel.Visible = false;
            if (immersive) contentView.X = 0;
            else contentView.X = sidebarCollapsed ? 0 : Pos.Right(tree) + 2;
            UpdateLinkNavTitle();
            contentView.SetFocus();
        }

        // —— 侧栏宽度自适应：宽屏固定列宽（正文更宽更好读），窄屏退回比例 ——
        const int WideSidebarWidth = 32;
        const int WideThreshold = 130;   // 终端宽度 ≥ 此列数时用固定列宽
        void ApplyResponsiveSidebar()
        {
            tree.Width = top.Frame.Width >= WideThreshold ? Dim.Absolute(WideSidebarWidth) : Dim.Percent(24);
        }
        top.FrameChanged += (s, e) => ApplyResponsiveSidebar();
        ApplyResponsiveSidebar();

        void RebuildTree()
        {
            var feeds = new List<TuiNode>();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Title,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'active')   AS ActiveCount,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'archived') AS ArchiveCount,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'deleted')  AS DeleteCount
                FROM Feeds
                ORDER BY Id
            ";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                string title = r.GetString(1);
                int active = r.GetInt32(2);
                int archive = r.GetInt32(3);
                int deleted = r.GetInt32(4);
                var parts = new List<string>();
                if (active > 0) parts.Add(Lang.T("{0} current", active + deleted));
                if (archive > 0) parts.Add(Lang.T("{0} changed", archive));
                if (deleted > 0) parts.Add(Lang.T("{0} deleted by author, but archived for you", deleted));
                string stats = string.Join("，", parts);
                feeds.Add(new TuiNode { IsFeed = true, FeedId = id, Title = $"{CjkSpace(title)} {stats}" });
            }
            tree.SetFeeds(feeds);   // 默认折叠；用户展开的源在 SetFeeds 里会保留
            UpdateStats();
        }

        void ShowSelectedContent()
        {
            SaveCurrentScroll();                       // 先记住上一篇的位置
            var n = tree.SelectedObject;
            if (n == null || n.IsFeed)
            {
                TelemetryCloseArticle();               // 从文章切到源/空 → 主动离开
                contentView.Text = ""; _currentArticleId = 0; _savedScrollY = -1; UpdateStats();
                return;
            }
            if (n.ItemId != _currentArticleId)
            {
                TelemetryCloseArticle();               // 主动切换 → 低进度记 skip
                contentView.Text = BuildArticleMarkdown(n.ItemId, contentMode, dbPath, contentView.GetContentWidth(), showFetchHint: true);
                _currentArticleId = n.ItemId;
                TelemetryOpenArticle(n.ItemId, n.FeedId);   // article_open + 计时初始化
            }
            else
            {
                contentView.Text = BuildArticleMarkdown(n.ItemId, contentMode, dbPath, contentView.GetContentWidth(), showFetchHint: true);
            }
            // 检测到历史进度 → 提示（不自动跳，等用户按 Space）；非法值直接忽略
            _savedScrollY = progressMap.TryGetValue(n.ItemId, out int y) && y > 0 ? y : -1;
            UpdateStats();                             // 有进度时状态行显示跳转提示
            UpdateLinkNavTitle();
        }

        // 在正文区显示某个历史版本的内容
        void ShowSelectedVersion(long itemId, int version)
        {
            contentMode = true;   // 历史版本固定用完整正文
            contentView.Text = BuildArticleMarkdown(itemId, true, dbPath, contentView.GetContentWidth());
            contentView.Title = " " + Lang.T("Content") + " · v" + version + " ";
            contentView.SetFocus();
        }

        // V：查看当前文章的版本历史 / 变更（列出所有版本，可输入编号查看旧版正文）
        void ShowVersionHistory(TuiNode n)
        {
            if (n == null || n.IsFeed || string.IsNullOrEmpty(n.Guid)) return;

            var versions = new List<(long Id, int Version, string Status, string At)>();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Version, Status, ArchivedAt FROM Items WHERE Guid = @g ORDER BY Version DESC";
                cmd.Parameters.AddWithValue("@g", n.Guid);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    versions.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
            }

            if (versions.Count <= 1)
            {
                Ask(Lang.T("This article has only one version, no change history"), Lang.T("OK"));
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < versions.Count; i++)
            {
                var (_, ver, status, at) = versions[i];
                string tag = status switch
                {
                    "active" => Lang.T("current"),
                    "archived" => Lang.T("archived"),
                    "deleted" => Lang.T("deleted"),
                    _ => ""
                };
                string when = at.Length > 0 && TryParseIso(at) is DateTime dt ? " · " + dt.ToString("yyyy-MM-dd HH:mm") : "";
                sb.AppendLine($"{i + 1}.  v{ver}  {tag}{when}");
            }
            sb.AppendLine();
            sb.AppendLine(Lang.T("Enter a number to view that version, 0 to cancel"));

            var dlg = new Dialog { Title = " " + Lang.T("Version History") + " ", Width = 60, Height = 14 };
            var txt = new TextView { X = 0, Y = 0, Width = Dim.Fill(2), Height = 9, ReadOnly = true, CanFocus = false };
            txt.Text = sb.ToString();
            var input = new TextField { X = 0, Y = Pos.Bottom(txt), Width = 5, Text = "" };
            var ok = new Button { Text = Lang.T("View"), IsDefault = true, X = 0, Y = Pos.Bottom(input) + 1 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = Pos.Bottom(input) + 1 };
            // input 第一个加入 + 列表只读不抢焦点 → 打开对话框时光标就在输入框上，直接敲数字即可
            dlg.Add(input, txt, ok, cancel);
            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
            input.Initialized += (s, e) => input.SetFocus();
            Application.Run(dlg);

            if (int.TryParse(input.Text.Trim(), out int idx) && idx >= 1 && idx <= versions.Count)
            {
                var (id2, ver2, _, _) = versions[idx - 1];
                ShowSelectedVersion(id2, ver2);
            }
        }

        int GetSelectedFeedId()
        {
            var n = tree.SelectedObject;
            return n?.FeedId ?? 0;
        }

        TuiNode? GetSelected() => tree.SelectedObject;

        void ArchiveSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) return;
            AddTimestampForRealId(realId, dbPath);
            RebuildTree();
        }

        void UnarchiveSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) return;
            RemoveTimestampForRealId(realId, dbPath);
            RebuildTree();
        }

        void DeleteSelected()
        {
            var n = GetSelected();
            if (n == null) return;
            if (n.IsFeed)
            {
                // 删除源（同 CLI -r）
                int ans = Ask(Lang.T("Delete {0}? This cannot be undone! (y/n)", n.Title),
                    Lang.T("OK"), Lang.T("Cancel"));
                if (ans != 0) return;
                DeleteFeedByRealId(n.FeedId, dbPath);
                RebuildTree();
                contentView.Text = "";
            }
            else
            {
                // 删除整篇文章（该 Guid 的全部版本，含向量）
                int ans = Ask(Lang.T("Delete this article (with all its versions)? This cannot be undone!"), Lang.T("OK"), Lang.T("Cancel"));
                if (ans != 0) return;
                DeleteArticleByGuid(n.Guid, dbPath);
                RebuildTree();
                contentView.Text = "";
            }
        }

        void RefreshSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) return;
            RunNetworkOp(() => RefreshOneFeed(realId, dbPath));
        }

        void RefreshAllFeeds()
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, FeedUrl FROM Feeds";
            using var r = cmd.ExecuteReader();
            var list = new List<(int Id, string Url)>();
            while (r.Read())
                list.Add((r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            RunNetworkOp(() =>
            {
                foreach (var f in list)
                    if (!string.IsNullOrWhiteSpace(f.Url))
                        try { DownloadAndSaveToDb(f.Url, dbPath).Wait(); } catch { }
            });
        }

        // 网络/耗时操作：弹出居中进度对话框，把 Console 输出重定向到对话框内实时显示，
        // 完成后自动关闭并重建树（不污染正文区）
        void RunNetworkOp(Action op)
        {
            var sb = new StringBuilder();
            var outTxt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = true, ScrollBars = true
            };
            var dlg = new Dialog { Title = " " + Lang.T("Working") + " ", Width = 64, Height = 18 };
            dlg.Add(outTxt);

            TextWriter oldOut = Console.Out;
            var writer = new StringWriter(sb);
            Console.SetOut(writer);
            object lockObj = new();
            bool done = false;

            // 后台线程执行操作，避免卡住 UI 刷新
            Task.Run(() =>
            {
                try { op(); }
                catch (Exception ex) { lock (lockObj) sb.AppendLine(Lang.T("Error: {0}", ex.Message)); }
                finally { lock (lockObj) { done = true; sb.AppendLine(); } }
            });

            // 定时把缓冲内容刷到对话框；完成后自动关闭
            Application.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
            {
                lock (lockObj) outTxt.Text = sb.ToString();
                if (done)
                {
                    Console.SetOut(oldOut);
                    dlg.RequestStop();
                    return false;  // 停止定时器
                }
                return true;
            });

            Application.Run(dlg);  // 等后台完成
            Console.SetOut(oldOut);
            RebuildTree();
        }

        void AddFeedDialog()
        {
            // 输入 URL 添加新源（同 CLI -d <url>）
            var dlg = new Dialog { Title = " " + Lang.T("Add feed") + " " };
            var lbl = new Label { Text = Lang.T("RSS URL: "), X = 0, Y = 0 };
            var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 3 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
            dlg.Add(lbl, input, ok, cancel);
            dlg.Width = 60;
            dlg.Height = 7;

            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };

            Application.Run(dlg);
            string url = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;
            RunNetworkOp(() => { DownloadAndSaveToDb(url, dbPath).Wait(); });
        }

        void SearchDialog()
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            var dlg = new Dialog { Title = " " + Lang.T("Semantic search") + " " };
            var lbl = new Label { Text = Lang.T("Search for: "), X = 0, Y = 0 };
            var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("Search"), IsDefault = true, X = 0, Y = 3 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
            dlg.Add(lbl, input, ok, cancel);
            dlg.Width = 60;
            dlg.Height = 7;

            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };

            Application.Run(dlg);
            string q = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(q)) return;

            // 复用语义搜索，渲染带链接的结果
            DoTuiSearch(q);
        }

        void SummarizeSelected()
        {
            var n = GetSelected();
            if (n == null || n.IsFeed)
            {
                Ask(Lang.T("Select an article first to summarize it"), Lang.T("OK"));
                return;
            }
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            long itemId = n.ItemId;
            RunNetworkOp(() => SummarizeItem(dbPath, (int)itemId).Wait());
            ShowSelectedContent();
        }

        void ShowHelpDialog()
        {
            var dlg = new Dialog { Title = " " + Lang.T("Keyboard help") + " ", Width = 56, Height = 22 };
            var txt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = false
            };
            txt.Text = string.Join("\n",
                Lang.T("j/k ↑↓    move up/down"),
                Lang.T("l/Enter   open article / toggle feed"),
                Lang.T("←         back (to sidebar)"),
                Lang.T("Space/b   page down/up  ·  Ctrl+D/U half page"),
                Lang.T("i         immersive reading (hide all UI)"),
                Lang.T("U          update current feed"),
                Lang.T("F6         update all feeds"),
                Lang.T("A          archive current feed"),
                Lang.T("R          unarchive"),
                Lang.T("X          delete selected feed/article"),
                Lang.T("D          add new feed"),
                Lang.T("S          semantic search"),
                Lang.T("Y          summarize article"),
                Lang.T("G          toggle content/overview"),
                Lang.T("V          view article versions/changes (marked ✎)"),
                Lang.T("C          collapse/expand sidebar"),
                Lang.T("Esc        open command line"),
                Lang.T("H          show this help"),
                Lang.T("Q          quit"),
                Lang.T("← / →      switch sidebar/content"),
                Lang.T("PageUp/Dn  page up/down"),
                "",
                Lang.T("Auto-sync: on open + every 15 min, only 'due' feeds (set frequency with schedule)"),
                Lang.T("Commands: init / index / reindex / u / d / a / r / s / g / y / q"),
                Lang.T("           schedule <id> <expr> (e.g. 30m / daily@10:00 / manual)"),
                Lang.T("           sync / all"),
                Lang.T("           lang <code> (switch UI language, e.g. zh-CN / en-US)"));
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = Pos.Bottom(txt) };
            var about = new Button { Text = Lang.T("About"), X = Pos.Right(ok) + 1, Y = Pos.Bottom(txt) };
            dlg.Add(txt, ok, about);
            ok.Accepted += (s, e) => dlg.RequestStop();
            about.Accepted += (s, e) => { dlg.RequestStop(); ShowAboutDialog(); };
            Application.Run(dlg);
        }

        void ShowAboutDialog()
        {
            var dlg = new Dialog { Title = " " + Lang.T("About") + " ", Width = 60, Height = 18 };
            var txt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = true
            };
            txt.Text = string.Join("\n",
                Lang.T("🍲 sip"),
                "",
                Lang.T("——「品，你细品。」"),
                "",
                Lang.T("一个让你站着把信息喝了的 RSS 阅读器核心。"),
                "",
                Lang.T("功能：文件夹视图 TUI + 全功能 CLI"),
                Lang.T("      · 全文搜索  --grep / 语义搜索 --search"),
                Lang.T("      · 版本追踪 / 快照归档 / AI 摘要"),
                Lang.T("      · 多语言（readwithhotsoup/languages/*.json）"),
                "",
                Lang.T("作者：hahahotsoup with ❤"),
                Lang.T("thanks to deepseek + opencode + chatgpt"),
                "",
                Lang.T("博客：https://blog.hotsouprealm.top/atom.xml"),
                Lang.T("关注热汤茶馆喵 关注热汤茶馆谢谢喵 🐾"));
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = Pos.Bottom(txt) };
            dlg.Add(txt, ok);
            ok.Accepted += (s, e) => dlg.RequestStop();
            Application.Run(dlg);
        }

        // 通用确认/提示对话框，返回按钮索引（0 = 第一个按钮）
        int Ask(string message, params string[] buttons)
        {
            var btns = buttons.Length > 0 ? buttons : new[] { Lang.T("OK") };
            return MessageBox.Query(Application.Instance, Lang.T("Notice"), message, btns) ?? 0;
        }

        // 在浏览器/默认程序中打开链接（仅放行 http/https，防 javascript: 等注入）
        void OpenUrl(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
                    || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                {
                    Ask(Lang.T("Unsupported link scheme, not opened: {0}", url), Lang.T("OK"));
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Ask(Lang.T("Failed to open link: {0}", ex.Message), Lang.T("OK"));
            }
        }

        // 进入/退出链接导航模式
        void ToggleLinkNav()
        {
            if (TuiMdState.Links.Count == 0)
            {
                Ask(Lang.T("This article has no openable links"), Lang.T("OK"));
                return;
            }
            linkNavMode = !linkNavMode;
            linkNavIndex = 0;
            UpdateLinkNavTitle();
            if (linkNavMode) contentView.SetFocus();
        }

        void UpdateLinkNavTitle()
        {
            string extra = linkNavMode && TuiMdState.Links.Count > 0
                ? $"  [ {linkNavIndex + 1}/{TuiMdState.Links.Count} ]  {TuiMdState.Links[linkNavIndex].Text}"
                : "";
            string modeTag = immersive ? Lang.T("Immersive") : (contentMode ? Lang.T("Content") : Lang.T("Overview"));
            if (sidebarCollapsed && !immersive) modeTag = "◀ " + modeTag;
            string focusTag = contentView.HasFocus ? " ◉" : "";
            contentView.Title = " " + modeTag + focusTag + (linkNavMode ? " (链接模式)" : "") + extra + " ";
        }

        void ToggleContentMode()
        {
            contentMode = !contentMode;
            UpdateLinkNavTitle();
            ShowSelectedContent();
            contentView.SetFocus();
        }

        void OpenCurrentLink()
        {
            if (!linkNavMode || TuiMdState.Links.Count == 0) return;
            var (text, url) = TuiMdState.Links[linkNavIndex];
            int ans = Ask(Lang.T("Open link?\n{0}\n{1}", text, url), Lang.T("Open"), Lang.T("Cancel"));
            if (ans == 0) OpenUrl(url);
        }

        // —— 事件绑定 ——
        tree.SelectionChanged += (s, e) => ShowSelectedContent();
        // 焦点变化时刷新正文标题栏的 ◉ 焦点标记（阅读区聚焦不再整块变色，靠它指示）
        contentView.HasFocusChanged += (s, e) => UpdateLinkNavTitle();

        // 鼠标点击正文中的链接直接打开
        contentView.LinkClicked += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Url)) OpenUrl(e.Url);
            e.Handled = true;
        };

        // 侧栏：j/k 上下移动，l/Enter 展开源或打开文章，Space/b 翻页，C 折叠侧栏，
        //       i 沉浸阅读，V 版本，Esc 命令行
        tree.KeyDown += (s, e) =>
        {
            var n = tree.SelectedObject;
            if (e.KeyCode == KeyCode.Enter || e.KeyCode == KeyCode.L || e.KeyCode == KeyCode.Space)
            {
                if (n != null && n.IsFeed) tree.Toggle(n);
                else contentView.SetFocus();   // Space：直接跳到正文页
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorRight)
            {
                if (n is { IsFeed: false }) contentView.SetFocus();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorDown || e.KeyCode == KeyCode.J)
            {
                tree.MoveDown();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorUp || e.KeyCode == KeyCode.K)
            {
                tree.MoveUp();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.PageUp || e.KeyCode == KeyCode.B)
            {
                tree.MovePageUp();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.PageDown)
            {
                tree.MovePageDown();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.C)
            {
                ToggleSidebar();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.I)
            {
                ToggleImmersive();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.M)
            {
                ShowFeedManager(dbPath);
                RebuildTree();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.V)
            {
                if (n is { IsFeed: false } && n.HasHistory) ShowVersionHistory(n);
                else Ask(Lang.T("This article has no change history (only ones marked ✎ have it)"), Lang.T("OK"));
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Esc)
            {
                ShowCmdBar();
                e.Handled = true;
            }
        };

        // 正文栏：← 返回树；j/k/↑↓ 平滑滚动；Space/b/PageUp/PageDown 翻页；Ctrl+D/Ctrl+U 半页；
        //       l/Enter 打开当前链接；i 沉浸阅读；C 折叠侧栏；V 版本；Esc 命令行
        // 链接导航：Ctrl+O 进入/退出，Tab/Shift+Tab 切换链接，Enter/l 打开当前链接
        contentView.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.CursorLeft:
                    if (linkNavMode) { /* 链接模式下 ← 不抢 */ }
                    else if (immersive) ToggleImmersive();
                    else if (!sidebarCollapsed) tree.SetFocus();
                    e.Handled = true;
                    break;
                case KeyCode.CursorUp:
                case KeyCode.K:
                    if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }   // 手动滚动 → 撤掉跳转提示
                    if (linkNavMode) { CycleLink(-1); }
                    else { TelemetryActivityTick(); contentView.ScrollVertical(-1); SaveCurrentScroll(); }
                    e.Handled = true;
                    break;
                case KeyCode.CursorDown:
                case KeyCode.J:
                    if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                    if (linkNavMode) { CycleLink(1); }
                    else { TelemetryActivityTick(); contentView.ScrollVertical(1); SaveCurrentScroll(); }
                    e.Handled = true;
                    break;
                case KeyCode.PageUp:
                case KeyCode.B:
                    if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                    TelemetryActivityTick();
                    contentView.ScrollVertical(-6);
                    SaveCurrentScroll();
                    e.Handled = true;
                    break;
                case KeyCode.PageDown:
                case KeyCode.Space:
                    if (_savedScrollY > 0) { JumpToSaved(); }   // 有历史进度 → Space 跳回
                    else { TelemetryActivityTick(); contentView.ScrollVertical(6); SaveCurrentScroll(); }
                    e.Handled = true;
                    break;
                case KeyCode.Enter:
                case KeyCode.L:
                    if (linkNavMode) OpenCurrentLink();
                    e.Handled = true;
                    break;
                case KeyCode.C:
                    ToggleSidebar();
                    e.Handled = true;
                    break;
                case KeyCode.I:
                    ToggleImmersive();
                    e.Handled = true;
                    break;
                case KeyCode.M:
                    ShowFeedManager(dbPath);
                    RebuildTree();
                    e.Handled = true;
                    break;
                case KeyCode.V:
                {
                    var nv = tree.SelectedObject;
                    if (nv is { IsFeed: false } && nv.HasHistory) ShowVersionHistory(nv);
                    else Ask(Lang.T("This article has no change history (only ones marked ✎ have it)"), Lang.T("OK"));
                    e.Handled = true;
                    break;
                }
                case KeyCode.Esc:
                    if (linkNavMode) { linkNavMode = false; UpdateLinkNavTitle(); }
                    else ShowCmdBar();
                    e.Handled = true;
                    break;
                default:
                    if (e.IsCtrl && e.KeyCode == (KeyCode.O | KeyCode.CtrlMask))
                    {
                        ToggleLinkNav();
                        e.Handled = true;
                    }
                    else if (e.IsCtrl && e.KeyCode == (KeyCode.Tab | KeyCode.CtrlMask))
                    {
                        if (linkNavMode) CycleLink(1);
                        e.Handled = true;
                    }
                    else if (e.IsCtrl && e.KeyCode == (KeyCode.D | KeyCode.CtrlMask))
                    {
                        // Ctrl+D：半页向下（vim 习惯）
                        if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                        TelemetryActivityTick();
                        contentView.ScrollVertical(3);
                        SaveCurrentScroll();
                        e.Handled = true;
                    }
                    else if (e.IsCtrl && e.KeyCode == (KeyCode.U | KeyCode.CtrlMask))
                    {
                        // Ctrl+U：半页向上（vim 习惯）
                        if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                        TelemetryActivityTick();
                        contentView.ScrollVertical(-3);
                        SaveCurrentScroll();
                        e.Handled = true;
                    }
                    else if (e.KeyCode == KeyCode.G && !e.IsCtrl)
                    {
                        // G：切换「完整正文 / 文章概要」
                        contentMode = !contentMode;
                        ShowSelectedContent();
                        e.Handled = true;
                    }
                    break;
            }
        };

        void CycleLink(int dir)
        {
            if (TuiMdState.Links.Count == 0) return;
            linkNavIndex = (linkNavIndex + dir + TuiMdState.Links.Count) % TuiMdState.Links.Count;
            UpdateLinkNavTitle();
        }

        void ShowCmdBar()
        {
            cmdBar.Visible = true;
            cmdLabel.Visible = true;
            statsLabel.Visible = false;
            cmdBar.Text = "";
            cmdBar.SetFocus();
        }

        void HideCmdBar()
        {
            cmdBar.Visible = false;
            cmdLabel.Visible = false;
            statsLabel.Visible = !immersive;
            cmdBar.Text = "";
            tree.SetFocus();
        }

        // 命令行：Enter 执行，Esc 隐藏
        cmdBar.KeyDown += (s, e) =>
        {
            if (e.KeyCode == KeyCode.Enter)
            {
                string input = cmdBar.Text.Trim();
                cmdBar.Text = "";
                HideCmdBar();
                if (input.Length > 0) RunCommand(input);
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Esc)
            {
                HideCmdBar();
                e.Handled = true;
            }
        };

        // Telemetry 同意对话框（TUI，仅 unset 时询问一次；默认保持关闭）
        void EnsureTelemetryConsentTui()
        {
            if (TelemetryService.Consent != "unset") return;
            var dlg = new Dialog { Title = " " + Lang.T("Sumenia · 苏暖泉") + " ", Width = 78, Height = 16 };
            var txt = new TextView { X = 0, Y = 0, Width = Dim.Fill(2), Height = 10, ReadOnly = true, CanFocus = false, WordWrap = true };
            txt.Text = Lang.T("Sumenia（苏暖泉）是一个会主动了解你阅读习惯的软萌妹纸：她会记录哪些文章被打开/读完/跳过、AI 调用情况，用于未来改进内容筛选。\n\nSumenia 默认关闭。开启后数据仅保存在本机 telemetry.db，sip 绝不会自动上传；你可以随时查看、关闭、删除或导出。");
            var enable = new Button { Text = Lang.T("开启 Sumenia"), IsDefault = false, X = 0, Y = Pos.Bottom(txt) + 1 };
            var keep = new Button { Text = Lang.T("保持关闭"), IsDefault = true, X = Pos.Right(enable) + 2, Y = Pos.Bottom(txt) + 1 };
            dlg.Add(txt, enable, keep);
            bool enabled = false;
            enable.Accepted += (s, e) => { enabled = true; dlg.RequestStop(); };
            keep.Accepted += (s, e) => dlg.RequestStop();
            enable.Initialized += (s, e) => keep.SetFocus();   // 默认焦点在「保持关闭」
            Application.Run(dlg);
            TelemetryService.SetConsent(enabled ? "enabled" : "disabled");
        }

        // 全文抓取同意对话框（TUI）：要求输入指定短语，同意后写标记文件
        bool FulltextConsentDialog()
        {
            if (HasFulltextConsent()) return true;
            string phrase = Lang.T("是的，我愿意与作者达成合理使用约定");
            var dlg = new Dialog { Title = " " + Lang.T("Consent") + " ", Width = 76, Height = 14 };
            var txt = new TextView { X = 0, Y = 0, Width = Dim.Fill(2), Height = 8, ReadOnly = true, CanFocus = false, WordWrap = true };
            txt.Text = Lang.T("sip is a reading aid; article fetching is for personal reading/study only. You agree to respect the source's intellectual property and copyright. You alone bear any loss from malicious use.") + "\n\n" +
                Lang.T("Type exactly to agree: {0}", phrase);
            var input = new TextField { X = 0, Y = Pos.Bottom(txt), Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = Pos.Bottom(input) + 1 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = Pos.Bottom(input) + 1 };
            dlg.Add(input, txt, ok, cancel);
            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
            input.Initialized += (s, e) => input.SetFocus();
            Application.Run(dlg);
            if (input.Text.Trim() == phrase)
            {
                WriteFulltextConsent();
                return true;
            }
            return false;
        }

        // TUI：抓取当前/指定文章的全文
        void FetchFulltextTui(int itemId)
        {
            if (!FulltextConsentDialog()) { Ask(Lang.T("Not agreed, cancelled"), Lang.T("OK")); return; }
            if (!ArticleContentShort(dbPath, itemId))
            {
                // 原文已够长 → 提示可能是误触
                int ans = Ask(Lang.T("The original text is already long. Did you mean to fetch? Fetch anyway?"), Lang.T("Fetch"), Lang.T("Cancel"));
                if (ans != 0) return;
            }
            var (text, _, err) = DoFetchCore(dbPath, itemId);
            ShowSelectedContent();   // 重新渲染（现在会显示原文 + 分界 + 全文）
            if (text == null) Ask(err ?? Lang.T("Fetch failed"), Lang.T("OK"));
        }

        // 执行命令行输入（复用 CLI 命令语法）
        void RunCommand(string input)
        {
            var parts = input.Split(' ', 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "q" or "quit" or "exit":
                    QuitApp();
                    return;
                case "h" or "help":
                    ShowHelpDialog();
                    return;
                case "manage":
                    ShowFeedManager(dbPath);
                    RebuildTree();
                    return;
                case "u" or "-u" or "--update":
                    if (int.TryParse(arg, out int unum))
                        RunNetworkOp(() => RefreshOneFeed(unum, dbPath));
                    else RefreshSelectedFeed();
                    return;
                case "a" or "-a" or "--archive":
                    if (int.TryParse(arg, out int anum)) { AddTimestampForRealId(anum, dbPath); RebuildTree(); }
                    else ArchiveSelectedFeed();
                    return;
                case "r" or "una" or "-r" or "-una" or "--remove" or "--unarchive":
                    if (int.TryParse(arg, out int rnum)) { RemoveTimestampForRealId(rnum, dbPath); RebuildTree(); }
                    else UnarchiveSelectedFeed();
                    return;
                case "x" or "--delete":
                    DeleteSelected();
                    return;
                case "d" or "-d" or "--download":
                    if (string.IsNullOrWhiteSpace(arg))
                        AddFeedDialog();
                    else
                        RunNetworkOp(() => { try { DownloadAndSaveToDb(arg, dbPath).Wait(); } catch { } });
                    return;
                case "s" or "--search":
                    if (string.IsNullOrWhiteSpace(arg)) { SearchDialog(); return; }
                    DoTuiSearch(arg);
                    return;
                case "g" or "--grep":
                    if (string.IsNullOrWhiteSpace(arg)) { Ask(Lang.T("Usage: grep <keyword>"), Lang.T("OK")); return; }
                    DoTuiGrep(arg);
                    return;
                case "y" or "--summary":
                    SummarizeSelected();
                    return;
                case "fetch" or "--fulltext":
                {
                    long fid = 0;
                    if (!string.IsNullOrWhiteSpace(arg) && int.TryParse(arg, out int fnum)) fid = fnum;
                    else
                    {
                        var sel = tree.SelectedObject;
                        if (sel is { IsFeed: false }) fid = sel.ItemId;
                        else { Ask(Lang.T("Select an article first to fetch"), Lang.T("OK")); return; }
                    }
                    FetchFulltextTui((int)fid);
                    return;
                }
                case "init" or "--init":
                    InitConfigDialog();
                    return;
                case "index" or "--index":
                    IndexSelectedFeed();
                    return;
                case "reindex" or "--reindex":
                    ReindexAll();
                    return;
                case "schedule" or "sched" or "--schedule":
                {
                    var sp = arg.Split(' ', 2);
                    if (sp.Length < 2 || !int.TryParse(sp[0], out int sn))
                    {
                        Ask(Lang.T("Usage: schedule <id> <expr>, e.g. schedule 1 30m / schedule 1 daily@10:00 / schedule 1 manual"), Lang.T("OK"));
                        return;
                    }
                    if (sn <= 0 || GetRealId(sn, dbPath) == 0) { Ask(Lang.T("Feed number not found"), Lang.T("OK")); return; }
                    SetFeedSchedule(sn.ToString(), sp[1], dbPath);
                    RebuildTree();
                    return;
                }
                case "sync" or "--sync":
                    SyncDueFeeds();
                    return;
                case "all" or "--update-all" or "update-all":
                    RefreshAllFeeds();
                    return;
                case "lang" or "--lang":
                    SwitchLanguage(arg);
                    return;
                default:
                    Ask(Lang.T("Unknown command: {0}. Press H for help", cmd), Lang.T("OK"));
                    return;
            }
        }

        // 运行时切换界面语言：lang <代码>（如 lang zh-CN / lang en-US）
        void SwitchLanguage(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                Ask(Lang.T("Usage: lang <code>, e.g. lang zh-CN / lang en-US"), Lang.T("OK"));
                return;
            }
            string dataDir = Path.GetDirectoryName(dbPath) ?? ".";
            string file = Path.Combine(dataDir, "languages", code + ".json");
            if (!File.Exists(file))
            {
                Ask(Lang.T("Language file not found: {0}", file), Lang.T("OK"));
                return;
            }
            Lang.Init(dataDir, code);
            // 重绘持久化的静态标签（Terminal.Gui 设置 Title 会自动触发重绘）
            tree.Title = " " + Lang.T("Feeds") + " (C " + Lang.T("collapse") + ") ";
            contentView.Title = " " + Lang.T("Content") + " ";
            RebuildStatusBar();
            Ask(Lang.T("Language switched to {0}", code), Lang.T("OK"));
        }

        // 用当前语言重建状态栏（语言切换后调用）
        void RebuildStatusBar()
        {
            var sb = new StatusBar(new Shortcut[]
            {
                new Shortcut(Key.H, Lang.T("Help"), () => ShowHelpDialog(), Lang.T("Show all keybindings")),
                new Shortcut(Key.F2, Lang.T("About"), () => ShowAboutDialog(), Lang.T("About sip")),
                new Shortcut(Key.U, Lang.T("Update"), () => RefreshSelectedFeed(), Lang.T("Update selected feed (same as CLI -u)")),
                new Shortcut(Key.F6, Lang.T("Update all"), () => RefreshAllFeeds(), Lang.T("Update all feeds")),
                new Shortcut(Key.A, Lang.T("Archive"), () => ArchiveSelectedFeed(), Lang.T("Add timestamp to feed (same as CLI -a)")),
                new Shortcut(Key.R, Lang.T("Unarchive"), () => UnarchiveSelectedFeed(), Lang.T("Remove timestamp (same as CLI -una)")),
                new Shortcut(Key.X, Lang.T("Delete"), () => DeleteSelected(), Lang.T("Delete selected feed/article (same as CLI -r)")),
                new Shortcut(Key.D, Lang.T("Add"), () => AddFeedDialog(), Lang.T("Add new feed (same as CLI -d)")),
                new Shortcut(Key.S, Lang.T("Search"), () => SearchDialog(), Lang.T("Semantic search (same as CLI --search)")),
                new Shortcut(Key.Y, Lang.T("Summary"), () => SummarizeSelected(), Lang.T("Summarize current article (same as CLI --summary)")),
                new Shortcut(Key.G, Lang.T("Overview"), () => ToggleContentMode(), Lang.T("Toggle content/overview")),
            new Shortcut(Key.Q, Lang.T("Quit"), QuitApp, Lang.T("Exit program"))
            });
            top.Remove(statusBar);
            statusBar = sb;
            top.Add(statusBar);
        }

        // TUI 内语义搜索并显示到正文区
        void DoTuiSearch(string query)
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            contentView.Text = Lang.T("Searching, please wait...");
            var results = DoSearch(query, dbPath);
            if (results == null) { contentView.Text = Lang.T("Search failed"); return; }
            // 让 Ctrl+O 链接导航也能遍历搜索结果
            TuiMdState.Links.Clear();
            foreach (var h in results)
                if (!string.IsNullOrWhiteSpace(h.Link))
                    TuiMdState.Links.Add((h.Title, h.Link));
            var sb = new StringBuilder();
            sb.AppendLine(Lang.T("Search results (query: {0}, total {1})", query, results.Count));
            sb.AppendLine(Lang.T("Hint: Enter/Tab or Ctrl+O to open link"));
            sb.AppendLine();
            foreach (var h in results)
            {
                string titleLink = !string.IsNullOrWhiteSpace(h.Link)
                    ? $"[{EscapeMd(h.Title)}]({EscapeMdUrl(h.Link)})"
                    : EscapeMd(h.Title);
                string feedLink = !string.IsNullOrWhiteSpace(h.Link)
                    ? $"[{EscapeMd(h.FeedTitle)}]({EscapeMdUrl(h.Link)})"
                    : EscapeMd(h.FeedTitle);
                sb.AppendLine($"- {titleLink}  （{Lang.T("similarity")} {h.Score:P1}）");
                sb.AppendLine($"  来源：{feedLink}");
                if (!string.IsNullOrWhiteSpace(h.Description))
                    sb.AppendLine($"  摘要：{EscapeMd(h.Description)}");
                sb.AppendLine();
            }
            contentView.Text = sb.ToString();
        }

        // TUI 内全文搜索（等价 CLI --grep，不依赖 AI）
        void DoTuiGrep(string keyword)
        {
            contentView.Text = Lang.T("Searching, please wait...");
            var hits = DoGrep(keyword, dbPath);
            if (hits == null) { contentView.Text = Lang.T("Search failed"); return; }
            // 让 Ctrl+O 链接导航也能遍历搜索结果
            TuiMdState.Links.Clear();
            foreach (var h in hits)
                if (!string.IsNullOrWhiteSpace(h.Link))
                    TuiMdState.Links.Add((h.Title, h.Link));
            var sb = new StringBuilder();
            sb.AppendLine(Lang.T("Full-text search \"{0}\": {1} hits", keyword, hits.Count));
            sb.AppendLine(Lang.T("Hint: Enter/Tab or Ctrl+O to open link"));
            sb.AppendLine();
            foreach (var h in hits)
            {
                string titleLink = !string.IsNullOrWhiteSpace(h.Link)
                    ? $"[{EscapeMd(h.Title)}]({EscapeMdUrl(h.Link)})"
                    : EscapeMd(h.Title);
                sb.AppendLine($"- {titleLink}");
                if (!string.IsNullOrWhiteSpace(h.Description))
                    sb.AppendLine($"  {EscapeMd(h.Description)}");
                sb.AppendLine();
            }
            contentView.Text = sb.ToString();
        }

        // TUI 内 AI 配置向导（对话框版，等价 CLI --init）
        void InitConfigDialog()
        {
            var cfg = LoadConfig(dbPath);
            int y = 0;
            var embEp = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.ApiEndpoint };
            var embEpL = new Label { Text = Lang.T("Embedding endpoint: "), X = 1, Y = y };
            y++;
            var embM = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.Model };
            var embML = new Label { Text = Lang.T("Embedding model: "), X = 1, Y = y };
            y++;
            var embD = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.Dimensions.ToString() };
            var embDL = new Label { Text = Lang.T("Vector dims: "), X = 1, Y = y };
            y++;
            var llmEp = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Llm.ApiEndpoint };
            var llmEpL = new Label { Text = Lang.T("LLM endpoint: "), X = 1, Y = y };
            y++;
            var llmM = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Llm.Model };
            var llmML = new Label { Text = Lang.T("LLM model: "), X = 1, Y = y };
            y++;
            var embKey = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = "", Secret = true };
            var embKeyL = new Label { Text = Lang.T("Embedding Key: "), X = 1, Y = y };
            y++;
            var llmKey = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = "", Secret = true };
            var llmKeyL = new Label { Text = Lang.T("LLM Key: "), X = 1, Y = y };
            y++;
            var thr = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.SearchThreshold.ToString() };
            var thrL = new Label { Text = Lang.T("Search threshold: "), X = 1, Y = y };
            y++;
            var ok = new Button { Text = Lang.T("Save"), IsDefault = true, X = 1, Y = y };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = y };
            var dlg = new Dialog { Title = " " + Lang.T("AI config") + " ", Width = 64, Height = y + 3 };
            dlg.Add(embEpL, embEp, embML, embM, embDL, embD, llmEpL, llmEp, llmML, llmM,
                    embKeyL, embKey, llmKeyL, llmKey, thrL, thr, ok, cancel);
            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { cfg = null!; dlg.RequestStop(); };

            Application.Run(dlg);
            if (cfg == null) return;  // 用户取消

            // 保存非敏感配置
            if (embEp.Text.Trim().Length > 0) cfg.Embedding.ApiEndpoint = EnsureV1Endpoint(embEp.Text.Trim());
            if (embM.Text.Trim().Length > 0) cfg.Embedding.Model = embM.Text.Trim();
            if (int.TryParse(embD.Text.Trim(), out int dim) && dim > 0) cfg.Embedding.Dimensions = dim;
            if (llmEp.Text.Trim().Length > 0) cfg.Llm.ApiEndpoint = EnsureV1Endpoint(llmEp.Text.Trim());
            if (llmM.Text.Trim().Length > 0) cfg.Llm.Model = llmM.Text.Trim();
            if (float.TryParse(thr.Text.Trim(), out float t)) cfg.Embedding.SearchThreshold = t;
            SaveConfig(dbPath, cfg);

            // Key 存系统凭据库
            if (!string.IsNullOrEmpty(embKey.Text)) CredSet("embedding_api_key", embKey.Text);
            if (!string.IsNullOrEmpty(llmKey.Text)) CredSet("llm_api_key", llmKey.Text);

            Ask(Lang.T("AI config saved. Run reindex after changing the Embedding model."), Lang.T("OK"));
        }

        // TUI 内对当前选中源做向量化（等价 CLI --index，作用于当前源）
        void IndexSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) { Ask(Lang.T("Select a feed first"), Lang.T("OK")); return; }
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            var cfg = LoadConfig(dbPath);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT i.Id, i.Title FROM Items i
                WHERE i.FeedId = @fid AND i.Status = 'active'
                AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
            ";
            cmd.Parameters.AddWithValue("@fid", realId);
            using var r = cmd.ExecuteReader();
            var articles = new List<(int Id, string Title)>();
            while (r.Read()) articles.Add((r.GetInt32(0), r.GetString(1)));

            if (articles.Count == 0) { Ask(Lang.T("All articles of this feed are already embedded"), Lang.T("OK")); return; }

            Console.WriteLine(Lang.T("Embedding {0} articles...", articles.Count));
            RunNetworkOp(() =>
            {
                int modelId = EnsureModel(dbPath, cfg.Embedding);
                int ok = 0, fail = 0;
                foreach (var a in articles)
                {
                    var vec = SafeEmbed(a.Title, cfg).GetAwaiter().GetResult();
                    if (vec == null) { fail++; Console.WriteLine(Lang.T("  failed: {0}", a.Title)); continue; }
                    if (vec.Length != cfg.Embedding.Dimensions)
                    {
                        cfg.Embedding.Dimensions = vec.Length;
                        SaveConfig(dbPath, cfg);
                    }
                    SaveVector(dbPath, realId, a.Id, modelId, vec);
                    ok++;
                    if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, articles.Count));
                }
                Console.WriteLine(Lang.T("Embedding done: {0} OK, {1} failed", ok, fail));
            });
        }

        // TUI 内重新向量化全部（等价 CLI --reindex）：清空所有向量后重建
        void ReindexAll()
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            int ans = Ask(Lang.T("Delete all vectors and re-embed all active articles?"), Lang.T("OK"), Lang.T("Cancel"));
            if (ans != 0) return;

            var cfg = LoadConfig(dbPath);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Vectors";
            cmd.ExecuteNonQuery();
            // 换模型后旧 sidecar 向量（抓取全文的）同样失效，一并清空
            if (File.Exists(FulltextVecsPath())) { try { File.Delete(FulltextVecsPath()); } catch { } }
            cmd.CommandText = "SELECT Id, FeedId, Title FROM Items WHERE Status = 'active'";
            using var r = cmd.ExecuteReader();
            var items = new List<(int Id, int FeedId, string Title)>();
            while (r.Read()) items.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));

            if (items.Count == 0) { Ask(Lang.T("No articles to embed"), Lang.T("OK")); return; }

            Console.WriteLine(Lang.T("Re-embedding {0} articles...", items.Count));
            RunNetworkOp(() =>
            {
                int modelId = EnsureModel(dbPath, cfg.Embedding);
                int ok = 0, fail = 0;
                foreach (var it in items)
                {
                    var vec = SafeEmbed(it.Title, cfg).GetAwaiter().GetResult();
                    if (vec == null) { fail++; continue; }
                    if (vec.Length != cfg.Embedding.Dimensions)
                    {
                        cfg.Embedding.Dimensions = vec.Length;
                        SaveConfig(dbPath, cfg);
                    }
                    SaveVector(dbPath, it.FeedId, it.Id, modelId, vec);
                    ok++;
                    if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, items.Count));
                }
                Console.WriteLine(Lang.T("Re-indexing done: {0} OK, {1} failed", ok, fail));
            });
        }

        RebuildTree();
        // 默认折叠；从 --show 按 W 进入时才展开并定位到原文章
        if (preselectItemId != 0) { tree.ExpandAll(); tree.SelectItem(preselectItemId); }
        tree.SetFocus();

        // —— 到期源自动同步 ——
        // 启动后稍等片刻，主界面先显示，再非阻塞地同步到期的源；开着期间每 15 分钟后台检查一次
        void SyncDueFeeds()
        {
            if (_syncing) return;
            try
            {
                var due = GetDueFeeds(dbPath);
                if (due.Count == 0) return;
                _syncing = true;
                RunNetworkOp(() =>
                {
                    Console.WriteLine(Lang.T("Syncing {0} due feeds:", due.Count));
                    var now = DateTime.Now;
                    foreach (var f in due)
                    {
                        Console.WriteLine(Lang.T("  · {0} (last {1})", f.Title,
                            f.LastChecked is DateTime lc ? AgoText(lc, now) : Lang.T("never")));
                        try
                        {
                            DownloadAndSaveToDb(f.Url, dbPath, interactive: false).Wait();
                            Console.WriteLine(Lang.T("    ✓ updated"));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(Lang.T("    ✗ {0}", ex.Message));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Ask(Lang.T("Error syncing due feeds: {0}", ex.Message), Lang.T("OK"));
            }
            finally
            {
                _syncing = false;
            }
        }

        // 启动同步：一次性的，主界面显示后约 0.4 秒开始
        Application.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            SyncDueFeeds();
            return false;
        });
        // 后台检查：程序开着期间每 15 分钟查一次到期源（没到期不请求，几乎零开销）
        Application.AddTimeout(TimeSpan.FromMinutes(15), () =>
        {
            SyncDueFeeds();
            return true;
        });

        Application.Run(top);
        // 退出时 progressMap 已由滚动时/QuitApp 实时更新；这里只落盘，不再重读 Viewport（已归 0）
        SaveReadingProgress(progressMap);
        return 0;
    }
    finally
    {
        if (!appReady) Application.Shutdown();
    }
}
#pragma warning restore CS0618

#pragma warning disable CS0618
// ══════════ 外部 CLI 全屏阅读（sip --show <文章编号>）═══════════
// 全屏阅读界面：无侧栏，正文 Markdown 渲染，底部提示「W 进入完整阅读器 · Esc 退出」；
// W → 进入完整 TUI 并定位到当前文章，Esc/Q → 退出
async Task RunFullscreenReader(int itemId, string dbPath)
{
    Application.Init();
    try
    {
        if (ShowFullscreenReader(itemId, dbPath))
            await RunTui(dbPath, appReady: true, showStartScreen: false, preselectItemId: itemId);
    }
    finally
    {
        Application.Shutdown();
    }
}

bool ShowFullscreenReader(int itemId, string dbPath)
{
    var md = CreateMarkdownView();
    md.X = 0;
    md.Y = 0;
    md.Width = Dim.Fill();
    md.Height = Dim.Fill() - 1;
    md.CanFocus = true;
    md.Title = " " + Lang.T("Article") + " ";
    md.Text = BuildArticleMarkdown(itemId, contentMode: true, dbPath, 90);

    var hint = new Label
    {
        Text = Lang.T("  Press W to enter the full reader  ·  Esc to exit  "),
        X = 0,
        Y = Pos.AnchorEnd(1),
        Width = Dim.Fill(),
        Height = 1,
        TextAlignment = Alignment.Center
    };

    var top = new Window
    {
        Title = " sip · " + Lang.T("Article") + " ",
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
    };
    top.Add(md, hint);

    bool enterTui = false;
    void OnKey(object? s, Key e)
    {
        if (e.KeyCode == KeyCode.W)
        {
            enterTui = true;
            top.RequestStop();
            e.Handled = true;
        }
        else if (e.KeyCode is KeyCode.Q or KeyCode.Esc)
        {
            top.RequestStop();
            e.Handled = true;
        }
    }
    top.KeyDown += OnKey;
    md.KeyDown += OnKey;

    md.SetFocus();
    Application.Run(top);
    return enterTui;
}

// 开始界面：全屏居中展示 slogan 与功能简介，回车进入 / Q 退出
// Dashboard 统计面板行（初始页数据）
List<string> DashboardStats(string dbPath)
{
    var lines = new List<string>();
    int feeds = 0, articles = 0, versions = 0, archived = 0, aiIndex = 0;
    long dbSize = 0; string lastSync = "";
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM Feeds), (SELECT COUNT(*) FROM Items WHERE Status='active'), (SELECT COUNT(*) FROM Items), (SELECT COUNT(*) FROM Items WHERE Status='archived'), (SELECT COUNT(*) FROM Vectors), (SELECT MAX(LastCheckedAt) FROM Feeds)";
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            feeds = r.GetInt32(0); articles = r.GetInt32(1); versions = r.GetInt32(2);
            archived = r.GetInt32(3); aiIndex = r.GetInt32(4);
            lastSync = r.IsDBNull(5) ? "" : r.GetString(5);
        }
        dbSize = new FileInfo(dbPath).Length;
    }
    catch { }
    lines.Add(Lang.T("──  Dashboard  ──"));
    lines.Add(Lang.T("  订阅源 feeds      : {0}", feeds));
    lines.Add(Lang.T("  文章 articles     : {0}", articles));
    lines.Add(Lang.T("  版本 versions     : {0}", versions));
    lines.Add(Lang.T("  归档 archived     : {0}", archived));
    lines.Add(Lang.T("  AI 索引 index     : {0}", aiIndex));
    lines.Add(Lang.T("  数据库 database   : {0:N1} MB", dbSize / 1048576.0));
    lines.Add(Lang.T("  最近同步 last sync: {0}", lastSync.Length > 0 ? lastSync : Lang.T("never")));
    return lines;
}

// 订阅源管理页（TUI：m 键 / manage 命令）
void ShowFeedManager(string dbPath)
{
    // Dialog 全屏；列表用自绘 FeedManagerList，方向键/翻页由它自己处理，不会被吞
    var top = new Dialog
    {
        Title = " " + Lang.T("Manage feeds") + " ",
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()
    };
    var list = new FeedManagerList
    {
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2),
        CanFocus = true
    };
    var hint = new Label
    {
        Text = Lang.T("  j/k 移动 · u 更新 · a 归档 · r 去归档 · x 删除 · s 计划 · d 加源 · Esc 返回  "),
        X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, TextAlignment = Alignment.Center
    };
    top.Add(list, hint);

    void Rebuild()
    {
        var rows = new List<(int Id, string Line)>();
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT f.Id, f.Title, f.Schedule, f.LastCheckedAt,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = f.Id AND Status='active'),
                       (SELECT COUNT(*) FROM Items WHERE FeedId = f.Id AND Status='archived')
                FROM Feeds f ORDER BY f.Id";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                string title = r.GetString(1);
                string sched = r.IsDBNull(2) ? "" : r.GetString(2);
                string last = r.IsDBNull(3) ? "" : r.GetString(3);
                int active = r.GetInt32(4); int arch = r.GetInt32(5);
                string s = (string.IsNullOrWhiteSpace(sched) || sched.Equals("manual", StringComparison.OrdinalIgnoreCase)) ? Lang.T("manual") : sched;
                string healthText = FeedHealthText(id, sched, last.Length > 0 ? TryParseIso(last) : null, DateTime.Now);
                string healthMark = healthText == Lang.T("正常") ? "" : " " + healthText;
                string line = $"[{id}] {CjkSpace(title)}  · {s} · {Lang.T("last")} {last} · {active}/{arch}{healthMark}";
                rows.Add((id, line));
            }
        }
        list.SetRows(rows);
    }

    try
    {
        Rebuild();
        top.Initialized += (s, e) => list.SetFocus();
        top.KeyDown += (s, e) =>
        {
            int id = list.SelectedId;
            // 方向键/翻页已由 list 自行处理，这里只处理动作键
            if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
            else if (e.KeyCode == KeyCode.U) { if (id != 0) RefreshOneFeed(id, dbPath); Rebuild(); e.Handled = true; }
            else if (e.KeyCode == KeyCode.A) { if (id != 0) { AddTimestampForRealId(id, dbPath); Rebuild(); } e.Handled = true; }
            else if (e.KeyCode == KeyCode.R) { if (id != 0) { RemoveTimestampForRealId(id, dbPath); Rebuild(); } e.Handled = true; }
            else if (e.KeyCode == KeyCode.X)
            {
                if (id != 0)
                {
                    if (MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("Delete feed {0}? This cannot be undone!", id), Lang.T("OK"), Lang.T("Cancel")) == 0)
                    { DeleteFeedByRealId(id, dbPath); Rebuild(); }
                }
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.S)
            {
                if (id != 0) { ScheduleManagerDialog(id, dbPath); Rebuild(); }
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.D)
            {
                AddFeedManagerDialog(dbPath);
                Rebuild();
                e.Handled = true;
            }
        };
        Application.Run(top);
    }
    catch (Exception ex)
    {
        // 管理页出错不崩溃整个 TUI
        MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("Manage page error: {0}", ex.Message), Lang.T("OK"));
    }
}

// 管理页：设置某源更新计划（对话框）
void ScheduleManagerDialog(int realId, string dbPath)
{
    var dlg = new Dialog { Title = " " + Lang.T("Update schedule") + " ", Width = 64, Height = 9 };
    var lbl = new Label { Text = Lang.T("Schedule (30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual): "), X = 0, Y = 0 };
    var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
    var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 3 };
    var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
    dlg.Add(lbl, input, ok, cancel);
    ok.Accepted += (s, e) => dlg.RequestStop();
    cancel.Accepted += (s, e) => dlg.RequestStop();
    Application.Run(dlg);
    string expr = input.Text.Trim();
    if (string.IsNullOrEmpty(expr)) return;
    // SetFeedSchedule 收的是列表显示编号（ROW_NUMBER），先把真实 Id 换算回去
    SetFeedSchedule(GetDisplayNum(realId, dbPath).ToString(), expr, dbPath);
}

// 真实源 Id → 列表显示编号（1,2,3...；找不到原样返回）
int GetDisplayNum(int realId, string dbPath)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS dn FROM Feeds";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (r.GetInt32(0) == realId) return r.GetInt32(1);
    }
    catch { }
    return realId;
}

// 管理页：加源对话框（下载放后台，不阻塞 TUI）
void AddFeedManagerDialog(string dbPath)
{
    var dlg = new Dialog { Title = " " + Lang.T("Add feed") + " " };
    var lbl = new Label { Text = Lang.T("RSS URL: "), X = 0, Y = 0 };
    var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
    var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 3 };
    var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
    dlg.Add(lbl, input, ok, cancel);
    dlg.Width = 60; dlg.Height = 7;
    ok.Accepted += (s, e) => dlg.RequestStop();
    cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
    Application.Run(dlg);
    string url = input.Text.Trim();
    if (string.IsNullOrWhiteSpace(url)) return;
    // 后台下载，避免冻结管理页；完成后由用户按任意键刷新列表
    _ = Task.Run(() =>
    {
        try { DownloadAndSaveToDb(url, dbPath).Wait(); } catch { }
    });
}

// 全文缓存自动清理：超过阈值时按最旧先删（保留 --purge-fulltext 手动清）
void TrimFulltextCache(int maxFiles = 200, long maxBytes = 200L * 1024 * 1024)
{
    try
    {
        var dir = FulltextDir();
        var files = new DirectoryInfo(dir).GetFiles("*.md").ToList();
        long total = files.Sum(f => f.Length);
        if (files.Count <= maxFiles && total <= maxBytes) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTime).Take(files.Count - maxFiles))
        {
            try { f.Delete(); } catch { }
        }
    }
    catch { /* 清理失败不影响主流程 */ }
}

// 起始页「今日哈汤」区块：规则清单 + 目标进度（引导习惯，不堆量）
List<string> TodayStartScreenLines(string dbPath)
{
    var lines = new List<string>();
    try
    {
        lines.Add("");
        lines.Add(Lang.T("──  今日哈汤  ──"));

        // 首次启动（还没有订阅源）：不显示空清单，给引导文案
        int feedCount = 0;
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM Feeds";
            feedCount = Convert.ToInt32(c.ExecuteScalar());
        }
        if (feedCount == 0)
        {
            lines.Add(Lang.T("  🍵 还没有订阅源——回车先去添加几个，明天起每天给你一小碗"));
            return lines;
        }

        var list = GetTodayList(dbPath, 5, refresh: false, out _);   // 一天一碗,当天固定
        var (done, target, tracking) = TodayProgress(dbPath);
        if (list.Count == 0)
            lines.Add(Lang.T("  今天还没有值得读的，回车后去更新订阅源"));
        for (int i = 0; i < list.Count; i++)
        {
            var it = list[i];
            lines.Add(Lang.T("  {0}. {1}", i + 1, CjkSpace(it.Title)));
            lines.Add(Lang.T("     [{0} · ~{1} 分钟{2}]", it.Source, it.Minutes, it.Reason.Length > 0 ? " · " + it.Reason : ""));
        }
        // 总时长：让时间不够的用户一眼判断「这碗汤要喝多久」
        double total = list.Sum(i => i.Minutes);
        if (tracking)
            lines.Add(done >= target
                ? Lang.T("  共约 {0} 分钟 · 已完成 🎉 今天结束", total)
                : Lang.T("  共约 {0} 分钟 · 目标 {1} 篇 · 已完成 {2} 篇", total, target, done));
        else
            lines.Add(Lang.T("  共约 {0} 分钟 · 目标 {1} 篇（开启 Sumenia 可跟踪进度）", total, target));
    }
    catch { /* 起始页不因异常崩溃 */ }
    return lines;
}

bool ShowStartScreen(string dbPath)
{
    var top = new Window
    {
        Title = " 🍲 sip RSS Reader ",
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
    };
    // slogan / 功能简介 + Dashboard 数据面板（同屏）
    var lines = new List<string>
    {
        Lang.T("🍲 sip"),
        "",
        Lang.T("——「品，你细品。」"),
        Lang.T("一个让你站着把信息喝了的 RSS 阅读器核心"),
        "",
        Lang.T("  订阅管理 · 全文搜索 · 语义搜索 · AI 摘要"),
        Lang.T("  版本追踪 · 快照归档 · 多语言"),
        ""
    };
    lines.AddRange(DashboardStats(dbPath));
    lines.AddRange(TodayStartScreenLines(dbPath));   // 「今日 Sip」：引导每日少量阅读
    lines.Add("");
    lines.Add(Lang.T("  Enter 进入  ·  Q 退出  "));

    var sv = new StartScreenView
    {
        Lines = lines.ToArray()
    };
    sv.X = 0;
    sv.Y = 0;
    sv.Width = Dim.Fill();
    sv.Height = Dim.Fill();
    top.Add(sv);

    bool cont = false;
    top.KeyDown += (s, e) =>
    {
        if (e.KeyCode is KeyCode.Enter or KeyCode.Space)
        {
            cont = true;
            top.RequestStop();
            e.Handled = true;
        }
        else if (e.KeyCode is KeyCode.Q or KeyCode.Esc)
        {
            top.RequestStop();
            e.Handled = true;
        }
    };
    Application.Run(top);
    return cont;
}
#pragma warning restore CS0618

// 统一配置的 Markdown 阅读视图（配色 + 软换行当硬换行 + 删除线）
Markdown CreateMarkdownView()
{
    var v = new Markdown
    {
        ShowHeadingPrefix = false,
        UseThemeBackground = true,
        EnableSixelImages = false,   // 图片已转链接，关闭 Sixel 管线避免重绘卡顿
        ImageLoader = MarkdownImageLoader
    };
    // 阅读配色：正文亮白、代码绿色、强调亮黄、链接亮青
    // 聚焦时正文保持白字黑底（不再整块变深蓝），靠标题栏 ◀/▶ 指示焦点，阅读更干净
    v.SetScheme(new Scheme
    {
        Normal = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
        HotNormal = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightCyan, StandardColor.Black),
        Active = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black, TextStyle.Bold),
        HotActive = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black, TextStyle.Bold),
        Focus = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
        HotFocus = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightCyan, StandardColor.Black),
        Highlight = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
        ReadOnly = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
        Code = new Terminal.Gui.Drawing.Attribute(StandardColor.Green, StandardColor.Black),
        CodeString = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black),
        CodeComment = new Terminal.Gui.Drawing.Attribute(StandardColor.Gray, StandardColor.Black)
    });
    // 软换行当硬换行 + 启用删除线（~~ 需要 UseEmphasisExtras）
    var pipeBuilder = new Markdig.MarkdownPipelineBuilder();
    Markdig.MarkdownExtensions.UseSoftlineBreakAsHardlineBreak(pipeBuilder);
    Markdig.MarkdownExtensions.UseEmphasisExtras(pipeBuilder, Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough);
    v.MarkdownPipeline = pipeBuilder.Build();
    return v;
}

// 把一篇文章渲染成 Markdown 字符串（TUI 正文区与 CLI 预览共用）
// showFetchHint=true（仅 TUI）：正文过短且未抓取全文时，提示输入 fetch
string BuildArticleMarkdown(long itemId, bool contentMode, string dbPath, int wrapWidth, bool showFetchHint = false)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT i.Title, i.Content, i.Description, i.Link, i.PublishDate, i.Summary, i.Author, f.Title
        FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
        WHERE i.Id = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Lang.T("(Article not found)");
    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string link = r.IsDBNull(3) ? "" : r.GetString(3);
    string pub = r.IsDBNull(4) ? "" : r.GetString(4);
    string aiSummary = r.IsDBNull(5) ? "" : r.GetString(5);
    string author = r.IsDBNull(6) ? "" : r.GetString(6);
    string feedTitle = r.IsDBNull(7) ? "" : r.GetString(7);

    var md = new StringBuilder();
    // 标题独立成段，像文章标题（不再加粗，H1 已足够醒目）
    md.AppendLine($"# {EscapeMd(CjkSpace(title))}");
    md.AppendLine();
    // 元信息独立成块：作者 / 日期 / 来源，与正文分离，避免混在一起
    var meta = new List<string>();
    if (!string.IsNullOrWhiteSpace(author)) meta.Add($"**{Lang.T("Author")}**: {EscapeMd(CjkSpace(author))}");
    if (!string.IsNullOrWhiteSpace(pub)) meta.Add($"**{Lang.T("Date")}**: {EscapeMd(CjkSpace(pub))}");
    if (meta.Count > 0) md.AppendLine(string.Join("  ·  ", meta));
    if (!string.IsNullOrWhiteSpace(link))
        md.AppendLine($"**{Lang.T("Source")}**: [{(string.IsNullOrWhiteSpace(feedTitle) ? link : EscapeMd(CjkSpace(feedTitle)))}]({EscapeMdUrl(link)})");
    else if (!string.IsNullOrWhiteSpace(feedTitle))
        md.AppendLine($"**{Lang.T("Source")}**: {EscapeMd(CjkSpace(feedTitle))}");
    md.AppendLine();
    md.AppendLine("---");
    md.AppendLine();

    if (contentMode)
    {
        // 完整正文模式：Content（原文）在上，抓取全文在下（若有缓存），中间分界
        string body = string.IsNullOrWhiteSpace(content) ? desc : content;
        md.Append(HtmlToMarkdown(body, wrapWidth));
        string? fulltext = ReadFulltextCache(itemId);
        if (!string.IsNullOrWhiteSpace(fulltext))
        {
            md.AppendLine();
            md.AppendLine("---");
            md.AppendLine();
            md.AppendLine("## " + Lang.T("Fetched full text"));
            md.AppendLine();
            md.AppendLine(EscapeMd(fulltext.Trim()));
        }
        else if (showFetchHint && ContentTooShort(content, desc))
        {
            // 摘要过短且未抓取 → 提示（仅 TUI，CLI 全屏没有命令行）
            md.AppendLine();
            md.AppendLine($"> {Lang.T("The summary is too short. Type fetch to get the full text.")}");
        }
    }
    else
    {
        // 概要模式：AI 摘要 + RSS 摘要
        if (!string.IsNullOrWhiteSpace(aiSummary))
        {
            md.AppendLine("## " + Lang.T("AI Summary"));
            md.AppendLine();
            md.AppendLine(EscapeMd(aiSummary));
            md.AppendLine();
            md.AppendLine("---");
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(desc))
            md.Append(HtmlToMarkdown(desc, wrapWidth));
        else
            md.Append(Lang.T("(No summary, press G for full content)"));
    }
    return md.ToString();
}

// 中文排版：在汉字与相邻的英文/数字之间插入空格，让混排更清爽
string CjkSpace(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    return Regex.Replace(s,
        @"(?<=[\u4E00-\u9FFF])(?=[A-Za-z0-9@#%])|(?<=[A-Za-z0-9@#%])(?=[\u4E00-\u9FFF])",
        " ");
}


// 从数据库加载某源的文章节点（TUI 树的叶子）
// 加载某源的文章节点（TUI 侧栏叶子）
// 每个 Guid（同一篇文章）只显示最新一版，不再堆「[现] v1」；若该文有被作者改过的旧版本，
// 标题右侧加 ✎ 标记，选中后按 V 可查看全部版本 / 变更历史
// 注意：Guid 为空串时（既无 Id 也无 Link 的文章）不做分组，避免把无关文章挤成一行
IEnumerable<TuiNode> LoadArticleNodes(int feedId, string dbPath)
{
    var nodes = new List<TuiNode>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title, Version, Status, Guid, VersionCount, ArchivedCount
        FROM (
            SELECT i.Id, i.Title, i.Version, i.Status, i.Guid,
                   CASE WHEN i.Guid = '' THEN 1
                        ELSE COUNT(*) OVER (PARTITION BY i.Guid) END AS VersionCount,
                   CASE WHEN i.Guid = '' THEN 0
                        ELSE COUNT(*) FILTER (WHERE i.Status = 'archived') OVER (PARTITION BY i.Guid) END AS ArchivedCount,
                   ROW_NUMBER() OVER (PARTITION BY i.Guid ORDER BY i.Version DESC) AS rn
            FROM Items i
            WHERE i.FeedId = @fid AND i.Guid IS NOT NULL
        )
        WHERE Guid = '' OR rn = 1
        ORDER BY Id
    ";
    cmd.Parameters.AddWithValue("@fid", feedId);
    var signals = LoadSignals();
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        long id = r.GetInt64(0);
        string title = r.GetString(1);
        string status = r.GetString(3);
        string guid = r.IsDBNull(4) ? "" : r.GetString(4);
        int versionCount = r.GetInt32(5);
        int archivedCount = r.GetInt32(6);
        bool hasHistory = archivedCount > 0;
        signals.TryGetValue(id.ToString(), out var sig);
        string marks = (sig?.UserLike == true ? "♥" : "") + (sig?.AiLike == true ? "🤖" : "");
        string display = CjkSpace(title) + (marks.Length > 0 ? " " + marks : "") + (hasHistory ? " ✎" : "");
        nodes.Add(new TuiNode
        {
            IsFeed = false,
            FeedId = feedId,
            ItemId = id,
            Status = status,
            Guid = guid,
            HasHistory = hasHistory,
            VersionCount = versionCount,
            Title = display
        });
    }
    return nodes;
}


// 从 URL 加载图片字节供 Markdown 渲染（带简单内存缓存，失败返回 null）
byte[]? MarkdownImageLoader(string url)
{
    try
    {
        if (TuiImageCache.Map.TryGetValue(url, out var cached)) return cached;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var bytes = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (bytes.Length == 0) return null;
        TuiImageCache.Map[url] = bytes;
        return bytes;
    }
    catch
    {
        return null;
    }
}

// HTML 正文转 Markdown（保留标题/粗体/斜体/删除线/分隔线/列表/代码/图片，供 TUI Markdown 渲染）
string HtmlToMarkdown(string html, int imageWidth = 80)
{
    TuiMdState.Links.Clear();
    TuiMdState.ImageWidth = imageWidth;
    if (string.IsNullOrWhiteSpace(html)) return "";
    try
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        var sb = new StringBuilder();
        WalkHtml(doc.DocumentNode, sb, 0);
        var text = sb.ToString();
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        // 只去掉首尾换行（不 Trim 空白，保留原文空格）
        return System.Net.WebUtility.HtmlDecode(text).Trim('\n', '\r');
    }
    catch
    {
        return StripHtml(html);
    }
}

void WalkHtml(HtmlAgilityPack.HtmlNode node, StringBuilder sb, int listDepth)
{
    if (node.NodeType == HtmlAgilityPack.HtmlNodeType.Text)
    {
        sb.Append(node.InnerText);
        return;
    }
    string name = node.Name;
    switch (name)
    {
        case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
            int level = name[1] - '0';
            sb.Append('\n').Append(new string('#', level)).Append(' ');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append('\n');
            return;
        case "p":
            // 段落不做首行缩进（博客原文通常没有缩进，避免格式打架）
            sb.Append('\n');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append("\n\n");
            return;
        case "div": case "section": case "article":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append("\n\n");
            return;
        case "blockquote":
            // 引用块也不加缩进（与段落一致）
            sb.Append('\n');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append("\n\n");
            return;
        case "br":
            // 保持单换行：依赖 UseSoftlineBreakAsHardlineBreak 管道渲染为硬换行，
            // 避免 \n\n 切断跨 <br> 的删除线/加粗标记、丢失段内缩进
            sb.Append('\n');
            return;
        case "hr":
            sb.Append("\n---\n");
            return;
        case "strong": case "b":
            sb.Append("**");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append("**");
            return;
        case "em": case "i":
            sb.Append('*');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append('*');
            return;
        case "del": case "s": case "strike":
            sb.Append("~~");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append("~~");
            return;
        case "u":
            sb.Append("__");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append("__");
            return;
        case "code":
            sb.Append('`').Append(node.InnerText).Append('`');
            return;
        case "pre":
            sb.Append("\n```\n").Append(node.InnerText).Append("\n```\n");
            return;
        case "a":
            string href = node.GetAttributeValue("href", "");
            var linkText = new StringBuilder();
            foreach (var c in node.ChildNodes) WalkHtml(c, linkText, listDepth);
            string ltxt = System.Net.WebUtility.HtmlDecode(linkText.ToString().Trim());
            if (!string.IsNullOrWhiteSpace(ltxt) && !string.IsNullOrWhiteSpace(href))
                TuiMdState.Links.Add((ltxt, href));
            sb.Append('[').Append(linkText).Append(']').Append('(').Append(EscapeMdUrl(href)).Append(')');
            return;
        case "img":
            string alt2 = node.GetAttributeValue("alt", "");
            string src2 = node.GetAttributeValue("src", "");
            if (!string.IsNullOrWhiteSpace(src2))
            {
                // Windows Terminal 等终端不支持 Sixel/kitty 内嵌图片，
                // 统一转成可点击链接，用链接导航模式/Ctrl+O 或鼠标点击在浏览器打开
                string label = string.IsNullOrWhiteSpace(alt2) ? Lang.T("Image") : alt2;
                sb.Append('[').Append("🖼️ ").Append(label).Append(']').Append('(').Append(EscapeMdUrl(src2)).Append(')');
            }
            return;
        case "ul": case "ol":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth + 1);
            sb.Append('\n');
            return;
        case "li":
            sb.Append('\n').Append(new string(' ', listDepth * 2)).Append("- ");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            return;
        case "tr":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append('\n');
            return;
        case "td": case "th":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append(" | ");
            return;
        case "table":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            sb.Append('\n');
            return;
        case "script": case "style": case "head": case "nav": case "footer": case "aside":
            return;  // 丢弃
        default:
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth);
            return;
    }
}

// Markdown 转义标题/链接文本里的特殊字符
string EscapeMd(string s) => StripControlChars(s).Replace("\\", "\\\\").Replace("*", "\\*").Replace("#", "\\#").Replace("[", "\\[").Replace("]", "\\]").Replace("|", "\\|");

// 剥除终端控制字符（ESC 序列 / BEL / 其他 C0-C1 控制符），防止恶意内容注入终端。
// 保留 \n \t \r 等正常空白；JSON 路径不受影响（序列化器自行转义）
string StripControlChars(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    var sb = new StringBuilder(s.Length);
    foreach (char c in s)
    {
        if (c == '\n' || c == '\t' || c == '\r') { sb.Append(c); continue; }
        if (c < 0x20 || c == 0x7f || (c >= 0x80 && c <= 0x9f)) continue;  // C0（除空白）+ DEL + C1
        sb.Append(c);
    }
    return sb.ToString();
}

string EscapeMdUrl(string s) => s.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");

// HTML 正文转纯文本（去标签、解实体，保留段落/换行）
string StripHtml(string html)
{
    if (string.IsNullOrWhiteSpace(html)) return "";
    try
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        // 块级元素与换行标签后补一个换行，避免整篇被压成一坨
        foreach (var node in doc.DocumentNode.SelectNodes("//text()[normalize-space()]") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
        {
            var parent = node.ParentNode;
            if (parent == null) continue;
            string name = parent.Name;
            if (name is "p" or "div" or "br" or "li" or "tr" or "section" or "article" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "ul" or "ol")
                node.InnerHtml = node.InnerHtml.TrimEnd() + "\n";
        }
        var text = doc.DocumentNode.InnerText;
        // 把连续的多个空行压成一个空行
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
    catch
    {
        return html;
    }
}

// 按真实 Id 归档（不查显示编号）
void AddTimestampForRealId(int realId, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string oldTitle = cmd.ExecuteScalar()!.ToString()!;
    if (IsArchived(oldTitle)) return;
    string newTitle = oldTitle + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", newTitle);
    cmd.ExecuteNonQuery();
}

// 按真实 Id 去归档
void RemoveTimestampForRealId(int realId, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string title = cmd.ExecuteScalar()!.ToString()!;
    string plainTitle = Regex.Replace(title, @"_\d{8}_\d{6}$", "");
    if (plainTitle == title) return;
    cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Title = @title AND Id != @id";
    cmd.Parameters.AddWithValue("@title", plainTitle);
    long conflict = (long)cmd.ExecuteScalar()!;
    if (conflict > 0) return;
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", plainTitle);
    cmd.ExecuteNonQuery();
}

// 按真实 Id 删除源（含文章与向量）
void DeleteFeedByRealId(int realId, string dbPath)
{
    // 先取该源全部文章 Id（用于清理全文缓存与 sidecar 向量）
    var ids = new List<int>();
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Id FROM Items WHERE FeedId = @id";
        c.Parameters.AddWithValue("@id", realId);
        using var r = c.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt32(0));
    }
    foreach (var id in ids)
    {
        string p = FulltextPath(id);
        if (File.Exists(p)) { try { File.Delete(p); } catch { } }
    }
    RemoveFulltextVecs(ids);

    using var db = new SqliteConnection($"Data Source={dbPath}");
    db.Open();
    var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM Vectors WHERE FeedId = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();
}

// 从 sidecar vecs.json 移除指定 itemId（删文章/删源时清理孤儿向量）
void RemoveFulltextVecs(List<int> itemIds)
{
    if (itemIds.Count == 0) return;
    var list = LoadFulltextVecs();
    int before = list.Count;
    list.RemoveAll(e => itemIds.Contains(e.ItemId));
    if (list.Count != before) SaveFulltextVecs(list);
}

// ══════════ 更新指定订阅源（A 菜单和 CLI 共用）═══════════
async Task UpdateFeed(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

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

    if (IsArchived(title)) { SetExit(); Console.WriteLine(Lang.T("{0} is archived and cannot be updated", title)); return; }

    try
    {
        await DownloadAndSaveToDb(url, dbPath);
        RecordFeedSuccess(realId);
        Console.WriteLine(Lang.T("Update complete"));
    }
    catch (TaskCanceledException) { RecordFeedFailure(realId, "timeout"); ReportError("NETWORK_ERROR", Lang.T("Download timed out; check your network or the URL"), Lang.T("Check your network connection or the URL")); }
    catch (HttpRequestException ex) { RecordFeedFailure(realId, ex.Message); ReportError("NETWORK_ERROR", Lang.T("Network request failed, the URL may be dead"), Lang.T("Check the URL or your network connection"), ex.Message); }
    catch (SqliteException ex) { SetExit(); Console.WriteLine(Lang.T("Database error: {0}", ex.Message)); }
    catch (Exception ex) { SetExit(); Console.WriteLine(Lang.T("Unknown error: {0}", ex.Message)); }
}

// CLI 模式下载（同步等待异步方法）
void DownloadCli(string url, string dbPath)
{
    Exception? err = null;
    try { DownloadAndSaveToDb(url, dbPath).Wait(); Console.WriteLine(Lang.T("Download complete")); return; }
    catch (AggregateException ae) { err = ae.GetBaseException(); }   // .Wait() 会把异常包成 AggregateException
    catch (Exception ex) { err = ex; }

    switch (err)
    {
        case TaskCanceledException:
            ReportError("NETWORK_ERROR", Lang.T("Download timed out; check your network or the URL"), Lang.T("Check your network connection or the URL"));
            break;
        case HttpRequestException http:
            ReportError("NETWORK_ERROR", Lang.T("Network request failed, the URL may be dead"), Lang.T("Check the URL or your network connection"), http.Message);
            break;
        default:
            SetExit();
            Console.WriteLine(Lang.T("Error: {0}", err?.Message ?? ""));
            break;
    }
}

// ══════════ 更新计划（CLI）═══════════
// 设置某源的更新计划表达式；无效表达式会给出提示
void SetFeedSchedule(string displayNum, string expr, string dbPath)
{
    if (!int.TryParse(displayNum, out int dn)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    int realId = GetRealId(dn, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

    string raw = (expr ?? "").Trim();
    if (raw.Length == 0 || raw.Equals("manual", StringComparison.OrdinalIgnoreCase))
    {
        // 清空计划 = 手动，不自动更新
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Feeds SET Schedule = '' WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", realId);
        cmd.ExecuteNonQuery();
        Console.WriteLine(Lang.T("Feed {0} set to manual updates (not automatic)", dn));
        return;
    }

    var s = TryParseSchedule(raw);
    if (s == null)
    {
        SetExit();
        Console.WriteLine(Lang.T("Invalid schedule expression: {0}; e.g. 30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual", raw));
        return;
    }

    using var conn2 = new SqliteConnection($"Data Source={dbPath}");
    conn2.Open();
    var cmd2 = conn2.CreateCommand();
    cmd2.CommandText = "UPDATE Feeds SET Schedule = @s WHERE Id = @id";
    cmd2.Parameters.AddWithValue("@s", raw.ToLowerInvariant());
    cmd2.Parameters.AddWithValue("@id", realId);
    cmd2.ExecuteNonQuery();

    string hint = TryParseSchedule(raw.ToLowerInvariant()) is FeedSchedule ps && !ps.IsManual && ps.Raw.Length > 0
        ? HumanSchedule(ps) : raw;
    Console.WriteLine(Lang.T("Feed {0} update schedule set: {1}", dn, hint));
}

// --sync：只更新到期的订阅源（可 --feed N 限定单个源）；输出每个源的 上次/下次；--json 结构化
async Task SyncCli(string[] extra, string dbPath)
{
    bool json = extra.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    int? feedReal = null;
    for (int i = 0; i < extra.Length; i++)
        if (extra[i] == "--feed" && i + 1 < extra.Length && int.TryParse(extra[++i], out int f))
            feedReal = GetRealId(f, dbPath);

    if (feedReal.HasValue && feedReal.Value == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number not found"), json: json); return; }

    var now = DateTime.Now;
    var due = feedReal.HasValue
        ? GetDueFeeds(dbPath).Where(d => d.Id == feedReal.Value).ToList()
        : GetDueFeeds(dbPath);

    if (feedReal.HasValue && due.Count == 0)
    {
        // 限定单个源但未到期 → 提示还差多久
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, LastCheckedAt, Schedule FROM Feeds WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", feedReal.Value);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            string title = r.GetString(0);
            DateTime? lc = r.IsDBNull(1) ? null : TryParseIso(r.GetString(1));
            string schedule = r.IsDBNull(2) ? "" : r.GetString(2);
            var next = FeedNextDue(schedule, lc, now);
            if (json)
                JsonOut(new { success = true, data = new { feedId = feedReal.Value, title, due = false, nextUpdate = next }});
            else if (next != null)
                Console.WriteLine(Lang.T("{0} is not due yet; next update in {1}", title, UntilText(next.Value, now)));
            else
                Console.WriteLine(Lang.T("{0} has no auto-update schedule (set one with --schedule)", title));
        }
        return;
    }

    if (due.Count == 0)
    {
        if (json) JsonOut(new { success = true, data = new { feeds = Array.Empty<object>(), ok = 0, fail = 0 } });
        else Console.WriteLine(Lang.T("No feeds are due"));
        return;
    }

    if (!json) Console.WriteLine(Lang.T("{0} feeds are due, syncing...", due.Count));
    int ok = 0, fail = 0;
    var results = new List<object>();
    foreach (var f in due)
    {
        if (!json) Console.WriteLine(Lang.T("  · {0} (last {1})", f.Title,
            f.LastChecked is DateTime lc ? AgoText(lc, now) : Lang.T("never")));
        try
        {
            await DownloadAndSaveToDb(f.Url, dbPath, interactive: false);
            RecordFeedSuccess(f.Id);
            ok++;
            if (json) results.Add(new { feedId = f.Id, title = f.Title, ok = true });
        }
        catch (Exception ex)
        {
            RecordFeedFailure(f.Id, ex.Message);
            fail++;
            if (json) results.Add(new { feedId = f.Id, title = f.Title, ok = false, error = ex.Message });
            else Console.WriteLine(Lang.T("    ✗ {0}", ex.Message));
        }
    }
    if (json)
    {
        JsonOut(new { success = true, data = new { feeds = results, ok, fail } });
        if (fail > 0) SetExit();
    }
    else
    {
        Console.WriteLine(Lang.T("Sync done: {0} ok, {1} failed", ok, fail));
        if (fail > 0) SetExit();
    }
}

// --update-all：强制更新所有订阅源（等价 TUI F6）
async Task UpdateAllCli(string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Title, FeedUrl FROM Feeds ORDER BY Id";
    using var r = cmd.ExecuteReader();
    var feeds = new List<(int Id, string Title, string Url)>();
    while (r.Read())
        feeds.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
    r.Close();

    if (feeds.Count == 0) { Console.WriteLine(Lang.T("No feeds in the database yet")); return; }

    Console.WriteLine(Lang.T("Updating all feeds ({0} total)...", feeds.Count));
    int ok = 0, fail = 0;
    foreach (var f in feeds)
    {
        if (IsArchived(f.Title)) { Console.WriteLine(Lang.T("  · skipping archived feed: {0}", f.Title)); continue; }
        if (string.IsNullOrWhiteSpace(f.Url)) { fail++; continue; }
        try
        {
            await DownloadAndSaveToDb(f.Url, dbPath, interactive: false);
            RecordFeedSuccess(f.Id);
            ok++;
        }
        catch (Exception ex)
        {
            RecordFeedFailure(f.Id, ex.Message);
            fail++;
            Console.WriteLine(Lang.T("    ✗ {0}", ex.Message));
        }
    }
    Console.WriteLine(Lang.T("Update done: {0} ok, {1} failed", ok, fail));
    if (fail > 0) SetExit();
}

// ══════════ 建表方法 ══════════
// 只在程序启动时调用一次。IF NOT EXISTS 保证不会覆盖已有数据库
// 两张表的关系：Feeds 是"班级"，Items 是"学生"，FeedId 就是学生属于哪个班级
void InitDatabase(string dbPath)
{
    // 主库完整性检查：损坏时改名保留现场并重建，绝不崩溃（与 telemetry 自愈同策略）
    CheckMainDbIntegrity(dbPath);

    // $ 开头是"字符串插值"：把 {dbPath} 替换成实际路径
    // using 保证连接用完会自动关闭，不占资源
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();  // 打开连接

    var cmd = conn.CreateCommand();  // 创建一个命令对象
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
            Dimensions  INTEGER,            -- 向量维度（仅 embedding 用）
            IsCurrent   INTEGER DEFAULT 0,  -- 是否为当前使用的 embedding 模型
            CreatedAt   TEXT
        );

        CREATE TABLE IF NOT EXISTS Vectors ( --文章向量索引
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            FeedId      INTEGER NOT NULL,   -- 所属源 Id（删除源时整组清除）
            ItemId      INTEGER NOT NULL,   -- 关联文章 Id
            ModelId     INTEGER NOT NULL,   -- 关联模型 Id
            Vector      BLOB    NOT NULL,   -- 向量二进制（float[] 序列化）
            CreatedAt   TEXT,
            FOREIGN KEY (FeedId) REFERENCES Feeds(Id),
            FOREIGN KEY (ItemId) REFERENCES Items(Id),
            FOREIGN KEY (ModelId) REFERENCES Models(Id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UQ_Vectors_ItemModel ON Vectors (ItemId, ModelId);
        CREATE INDEX IF NOT EXISTS idx_items_guid ON Items (Guid);
        CREATE INDEX IF NOT EXISTS idx_items_feedid ON Items (FeedId);
        CREATE INDEX IF NOT EXISTS idx_items_status ON Items (Status);
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
    // 旧库迁移：给 Vectors 加 FeedId 列并回填（按 Items 的归属源补上）
    try
    {
        cmd.CommandText = "ALTER TABLE Vectors ADD COLUMN FeedId INTEGER";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @"
            UPDATE Vectors SET FeedId = (
                SELECT Items.FeedId FROM Items WHERE Items.Id = Vectors.ItemId
            )
        ";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException) { /* 列已存在则忽略 */ }
    // 旧库迁移：给 Feeds 补更新计划相关字段（Schedule=计划表达式，LastCheckedAt=上次拉取时间）
    try { cmd.CommandText = "ALTER TABLE Feeds ADD COLUMN Schedule TEXT"; cmd.ExecuteNonQuery(); }
    catch (SqliteException) { /* 列已存在则忽略 */ }
    try { cmd.CommandText = "ALTER TABLE Feeds ADD COLUMN LastCheckedAt TEXT"; cmd.ExecuteNonQuery(); }
    catch (SqliteException) { /* 列已存在则忽略 */ }
}

// 主库完整性检查：魔数不符/打开失败/quick_check 非 ok → 改名保留现场 → 重建新库；绝不崩溃
void CheckMainDbIntegrity(string dbPath)
{
    try
    {
        if (!File.Exists(dbPath)) return;  // 新建库走正常建表流程
        bool ok = TelemetryService.IsSqliteFile(dbPath);
        if (ok)
        {
            // 独立方法 + 显式 Dispose：确保句柄释放后文件才能改名
            ok = QuickCheckOk(dbPath);
        }
        if (ok) return;
        string corrupt = dbPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        try
        {
            SqliteConnection.ClearAllPools();
            File.Move(dbPath, corrupt);
        }
        catch (Exception ex)
        {
            // 文件被其他进程占用（并发启动）：不是真损坏现场，本次跳过自愈，下次启动再试
            try { File.Delete(dbPath); } catch { }
            Console.Error.WriteLine("rss.db 完整性检查异常（文件可能被占用，本次跳过自愈）：" + ex.Message);
            return;
        }
        Console.Error.WriteLine("rss.db 完整性检查失败，已保留现场并重建：" + corrupt);
    }
    catch { /* 完整性检查失败不阻断启动 */ }
}

// 打开主库执行 quick_check；返回是否完好。连接在本方法内用完即关，避免文件句柄占用。
// 带 busy_timeout；busy/locked 与瞬时非 ok 结果重试，连续多次失败才算损坏（与 telemetry 同策略）
bool QuickCheckOk(string dbPath)
{
    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "PRAGMA busy_timeout = 2000;";
            c.ExecuteNonQuery();
            c.CommandText = "PRAGMA quick_check";
            if (c.ExecuteScalar()?.ToString() == "ok") return true;
        }
        catch (SqliteException ex) when (TelemetryService.IsBusyCode(ex.SqliteErrorCode)) { /* 锁冲突：重试 */ }
        catch { return false; }   // 真损坏/打开失败
    }
    return false;
}

// ══════════ 列出指定源的所有文章（用 ROW_NUMBER 显示编号）═══════════
void ListArticlesFromDb(int feedRealId, int feedDisplayNum, string dbPath, bool json = false)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 查 Feed 标题
    var titleCmd = conn.CreateCommand();
    titleCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    titleCmd.Parameters.AddWithValue("@id", feedRealId);
    string feedTitle = titleCmd.ExecuteScalar()!.ToString()!;

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title, Version,
               ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum,
               VersionCount, ArchivedCount, Content, Description
        FROM (
            SELECT i.Id, i.Title, i.Version, i.Guid, i.Content, i.Description,
                   CASE WHEN i.Guid = '' THEN 1
                        ELSE COUNT(*) OVER (PARTITION BY i.Guid) END AS VersionCount,
                   CASE WHEN i.Guid = '' THEN 0
                        ELSE COUNT(*) FILTER (WHERE i.Status = 'archived') OVER (PARTITION BY i.Guid) END AS ArchivedCount,
                   ROW_NUMBER() OVER (PARTITION BY i.Guid ORDER BY i.Version DESC) AS rn
            FROM Items i
            WHERE i.FeedId = @fid AND i.Guid IS NOT NULL
        )
        WHERE Guid = '' OR rn = 1
        ORDER BY Id
    ";
    cmd.Parameters.AddWithValue("@fid", feedRealId);
    using var reader = cmd.ExecuteReader();
    if (!reader.HasRows)
    {
        if (json) JsonOut(new { success = true, data = new { feedId = feedRealId, feedTitle, articles = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("  This feed has no articles yet"));
        return;
    }

    var items = new List<(int RealId, int DisplayNum, string Title, bool HasHistory, string Quality)>();
    while (reader.Read())
    {
        int realId = reader.GetInt32(0);
        string title = reader.GetString(1);
        int displayNum = reader.GetInt32(3);
        int archived = reader.GetInt32(5);
        string content = reader.IsDBNull(6) ? "" : reader.GetString(6);
        string desc = reader.IsDBNull(7) ? "" : reader.GetString(7);
        items.Add((realId, displayNum, title, archived > 0, ContentQuality(content, desc)));
    }

    // 信号（点赞/AI 标记）只读一次，避免每篇文章重复读磁盘文件
    var signals = LoadSignals();
    SignalEntry? SignalOf(int realId) => signals.TryGetValue(realId.ToString(), out var e) ? e : null;

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                feedId = feedRealId,
                feedTitle,
                articles = items.Select(a =>
                {
                    var sig = SignalOf(a.RealId);
                    return new
                    {
                        itemId = a.RealId,
                        displayNum = a.DisplayNum,
                        title = a.Title,
                        hasHistory = a.HasHistory,
                        quality = a.Quality,
                        liked = sig?.UserLike ?? false,
                        aiLiked = sig?.AiLike ?? false
                    };
                })
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("── [{0}] {1} article list──", feedDisplayNum, feedTitle));
    Console.WriteLine(Lang.T("  [seq/real] left = list sequence, right = global article ID (use the right one with --show/--versions/--summary)"));
    foreach (var a in items)
    {
        var sig = SignalOf(a.RealId);
        string marks = (sig?.UserLike == true ? "♥" : "") + (sig?.AiLike == true ? "🤖" : "");
        string marker = (marks.Length > 0 ? " " + marks : "") + (a.HasHistory ? " ✎" : "") + (a.Quality == "short" ? Lang.T(" [摘要]") : a.Quality == "empty" ? Lang.T(" [无正文]") : "");
        Console.WriteLine($"  [{a.DisplayNum}/{a.RealId}] {CjkSpace(StripControlChars(a.Title))}{marker}");
    }
}

// 内容质量三级：full（正文完整）/ short（过短，仅摘要）/ empty（无正文）
string ContentQuality(string content, string desc)
{
    string c = string.IsNullOrWhiteSpace(content) ? desc : content;
    if (string.IsNullOrWhiteSpace(c)) return "empty";
    return c.Trim().Length < 100 ? "short" : "full";
}

// 文章是否存在（--show 全屏模式启动前检查，避免进空界面）
bool ArticleExists(int itemId, string dbPath)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    catch { return false; }
}

// ══════════ 原文 JSON 直出（sip --show <文章编号> --json）：供 AI / 脚本读取 ═══════════
// 不做任何渲染，标题/来源/链接/作者等元信息 + 原始正文（Content 原文，空则 Description）原样输出
void ShowArticleJson(int itemId, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT i.Title, i.Content, i.Description, i.Link, i.PublishDate, i.Author, f.Title
        FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
        WHERE i.Id = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: true); return; }

    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string link = r.IsDBNull(3) ? "" : r.GetString(3);
    string pub = r.IsDBNull(4) ? "" : r.GetString(4);
    string author = r.IsDBNull(5) ? "" : r.GetString(5);
    string feed = r.IsDBNull(6) ? "" : r.GetString(6);

    var sig = GetSignal(itemId);
    // 有全文缓存时合并输出（AI 读全文主路径；无缓存则不出现该字段，避免误导）
    string? fulltext = ReadFulltextCache(itemId);
    JsonOut(new
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
            content = string.IsNullOrWhiteSpace(content) ? desc : content,
            fulltext = fulltext
        }
    });
}

// 查看文章版本历史 CLI：--versions <文章Id> [--json]
// 列出同一 Guid 的所有版本；想看某版原文，用 sip --show <该版本的 Id>
void ListVersionsCli(string arg, string dbPath, bool json = false)
{
    if (!int.TryParse(arg, out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var gCmd = conn.CreateCommand();
    gCmd.CommandText = "SELECT Guid, Title FROM Items WHERE Id = @id";
    gCmd.Parameters.AddWithValue("@id", itemId);
    using var gr = gCmd.ExecuteReader();
    if (!gr.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: json); return; }
    string guid = gr.IsDBNull(0) ? "" : gr.GetString(0);
    string title = gr.GetString(1);
    gr.Close();

    if (string.IsNullOrEmpty(guid))
    {
        if (json) JsonOut(new { success = true, data = new { itemId, title, versions = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("This article has no version history (no Guid)"));
        return;
    }

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Version, Status, ArchivedAt, Title FROM Items WHERE Guid = @g ORDER BY Version DESC";
    cmd.Parameters.AddWithValue("@g", guid);
    using var r = cmd.ExecuteReader();
    var list = new List<(long Id, int Version, string Status, string At, string T)>();
    while (r.Read())
        list.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4)));

    if (list.Count <= 1)
    {
        if (json) JsonOut(new { success = true, data = new { itemId, title, versions = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("This article has only one version, no change history"));
        return;
    }

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                itemId,
                title,
                versions = list.Select(v => new
                {
                    id = v.Id,
                    version = v.Version,
                    status = v.Status,
                    archivedAt = v.At,
                    title = v.T,
                    current = v.Id == itemId
                })
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("Version history of: {0}", title));
    foreach (var (id, ver, status, at, t) in list)
    {
        string tag = status switch
        {
            "active" => Lang.T("current"),
            "archived" => Lang.T("archived"),
            "deleted" => Lang.T("deleted"),
            _ => ""
        };
        string when = at.Length > 0 && TryParseIso(at) is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : "";
        string sep = when.Length > 0 ? " · " : "";
        string mark = id == itemId ? " ←" : "";
        Console.WriteLine($"  [{id}] v{ver}  {tag}{sep}{when}  {StripControlChars(t)}{mark}");
    }
    Console.WriteLine();
    Console.WriteLine(Lang.T("View a version's full text with sip --show <article-id>"));
}

// ══════════ Diff（sip --diff <id> [vA vB] [--json]）═══════════
// 对比同一文章两个版本的正文（Content/Description）；不涉及抓取全文
void DiffCli(string[] args, string dbPath)
{
    if (args.Length < 1 || !int.TryParse(args[0], out int itemId))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --diff <article-id> [vA vB] [--json]")); return;
    }
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var vers = args.Where(a => a.StartsWith("v", StringComparison.OrdinalIgnoreCase) && a.Length > 1 && int.TryParse(a[1..], out _))
                   .Select(a => int.Parse(a[1..])).ToList();

    // 查文章 + 所有版本正文
    string guid;
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var gCmd = conn.CreateCommand();
        gCmd.CommandText = "SELECT Guid FROM Items WHERE Id = @id";
        gCmd.Parameters.AddWithValue("@id", itemId);
        var o = gCmd.ExecuteScalar();
        if (o == null) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: json); return; }
        guid = o.ToString() ?? "";
    }
    if (string.IsNullOrEmpty(guid))
    {
        if (json)
            JsonOut(new { success = true, article = itemId, from = 0, to = 0, changes = Array.Empty<object>() });
        else
            Console.WriteLine(Lang.T("This article has no version history (no Guid)"));
        return;
    }

    var rows = new List<(int Version, string Text)>();
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version, Content, Description FROM Items WHERE Guid = @g ORDER BY Version";
        cmd.Parameters.AddWithValue("@g", guid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetInt32(0), string.IsNullOrWhiteSpace(r.IsDBNull(1) ? "" : r.GetString(1))
                ? (r.IsDBNull(2) ? "" : r.GetString(2)) : r.GetString(1)));
    }
    if (rows.Count < 2)
    {
        if (json)
            JsonOut(new { success = true, article = itemId, from = 0, to = 0, changes = Array.Empty<object>() });
        else
            Console.WriteLine(Lang.T("This article has only one version, no change history"));
        return;
    }

    // 选定两个版本：指定 vA vB，或默认最后两个
    (int va, int vb) = SelectDiffVersions(rows, vers);
    var rowA = rows.FirstOrDefault(x => x.Version == va);
    var rowB = rows.FirstOrDefault(x => x.Version == vb);
    if (rowA.Text == null || rowB.Text == null)
    {
        ReportError("VERSION_NOT_FOUND", Lang.T("Version {0} or {1} not found", va, vb), json: json);
        return;
    }

    var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(rowA.Text, rowB.Text);

    if (json)
    {
        // 把 DiffPlex 的 Deleted/Inserted 相邻配对成 replace；其余为 insert/delete
        var lines = diff.Lines;
        var changes = new List<object>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Type == ChangeType.Unchanged) continue;
            if (lines[i].Type == ChangeType.Deleted && i + 1 < lines.Count && lines[i + 1].Type == ChangeType.Inserted)
            {
                changes.Add(new { type = "replace", before = lines[i].Text, after = lines[i + 1].Text });
                i++;
            }
            else if (lines[i].Type == ChangeType.Deleted)
                changes.Add(new { type = "delete", before = lines[i].Text, after = "" });
            else if (lines[i].Type == ChangeType.Inserted)
                changes.Add(new { type = "insert", before = "", after = lines[i].Text });
        }
        JsonOut(new { article = itemId, from = va, to = vb, changes });
        return;
    }

    Console.WriteLine(Lang.T("v{0} → v{1}", va, vb));
    Console.WriteLine();
    foreach (var line in diff.Lines)
    {
        switch (line.Type)
        {
            case ChangeType.Inserted: Console.WriteLine($"+ {StripControlChars(line.Text)}"); break;
            case ChangeType.Deleted: Console.WriteLine($"- {StripControlChars(line.Text)}"); break;
            case ChangeType.Modified: Console.WriteLine($"~ {StripControlChars(line.Text)}"); break;
        }
    }
}

// 从请求的 v 后缀选两个版本；不足则默认取最后两个（按版本号）
(int, int) SelectDiffVersions(List<(int Version, string Text)> rows, List<int> vers)
{
    if (vers.Count >= 2) return (vers[0], vers[1]);
    var byVer = rows.OrderBy(x => x.Version).ToList();
    if (byVer.Count >= 2) return (byVer[^2].Version, byVer[^1].Version);
    return (byVer[0].Version, byVer[0].Version);
}

// ══════════ Markdown 导出（sip --export <id | feed:N | all> [out.md|dir] [--yes]）═══════════
// 导出 = 屏幕所见（BuildArticleMarkdown：原文 + 分界 + 抓取全文，若有缓存）
void ExportCli(string[] args, string dbPath)
{
    bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
    var pos = args.Where(a => !a.StartsWith("--")).ToList();
    if (pos.Count == 0)
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --export <article-id | feed:number | all> [out.md | dir] [--yes]")); return;
    }
    string target = pos[0];
    string outPath = pos.Count > 1 ? pos[1] : "";

    if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        if (!yes)
        {
            Console.Write(Lang.T("You sure? This will produce a lot of files. Export all? (y/n) "));
            if (!"y".Equals(Console.ReadLine()?.Trim().ToLower())) { Console.WriteLine(Lang.T("Cancelled")); return; }
        }
        ExportArticlesToDir(GetActiveItemIds(dbPath, null), outPath, dbPath);
        return;
    }
    if (target.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
    {
        if (!int.TryParse(target["feed:".Length..].Trim(), out int fd)) { SetExit(); Console.WriteLine(Lang.T("Bad format. Correct: {0}", "--export feed:3")); return; }
        int feedReal = GetRealId(fd, dbPath);
        if (feedReal == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number {0} not found", fd)); return; }
        ExportArticlesToDir(GetActiveItemIds(dbPath, feedReal), outPath, dbPath);
        return;
    }
    if (!int.TryParse(target, out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    if (!ArticleExists(itemId, dbPath)) { SetExit(); Console.WriteLine(Lang.T("Article {0} not found", itemId)); return; }
    string md = BuildArticleMarkdown(itemId, true, dbPath, 90);
    if (string.IsNullOrWhiteSpace(outPath)) outPath = itemId + ".md";
    File.WriteAllText(outPath, md);
    Console.WriteLine(Lang.T("Exported to {0}", outPath));
}

// 取 active 文章 Id；feedReal=null 表示全部
List<int> GetActiveItemIds(string dbPath, int? feedReal)
{
    var list = new List<int>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id FROM Items WHERE Status = 'active'" + (feedReal.HasValue ? " AND FeedId = @fid" : "") + " ORDER BY Id";
    if (feedReal.HasValue) cmd.Parameters.AddWithValue("@fid", feedReal.Value);
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(r.GetInt32(0));
    return list;
}

void ExportArticlesToDir(List<int> itemIds, string dir, string dbPath)
{
    if (string.IsNullOrWhiteSpace(dir)) dir = "sip-export";
    Directory.CreateDirectory(dir);
    int ok = 0;
    foreach (var id in itemIds)
    {
        string md = BuildArticleMarkdown(id, true, dbPath, 90);
        File.WriteAllText(Path.Combine(dir, id + ".md"), md);
        ok++;
    }
    Console.WriteLine(Lang.T("Exported {0} articles to {1}", ok, dir));
}

// ══════════ 列表方法：显示数据库中所有订阅源 ══════════
// ROW_NUMBER() 保证显示出来永远是 1, 2, 3 连续编号（不管中间有没有删过源）
// 但操作（更新/时间戳/删除）仍然用真实 Id，因为 Items 表靠它关联
void ListFeedsFromDb(string dbPath, bool json = false)
{
    var rows = new List<(int RealId, int DisplayNum, string Title, int Active, int Archived, int Deleted, DateTime? LastChecked, string Schedule)>();
    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'active')   AS ActiveCount,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'archived') AS ArchiveCount,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'deleted')  AS DeleteCount,
                   ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum,
                   LastCheckedAt, Schedule
            FROM Feeds";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(5), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(6) ? null : TryParseIso(reader.GetString(6)),
                reader.IsDBNull(7) ? "" : reader.GetString(7)));
    }

    if (rows.Count == 0)
    {
        if (json) JsonOut(new { success = true, data = new { feeds = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("No feeds in the database yet"));
        return;
    }

    if (json)
    {
        var health = LoadFeedHealth();
        JsonOut(new
        {
            success = true,
            data = new
            {
                feeds = rows.Select(r =>
                {
                    health.TryGetValue(r.RealId, out var h);
                    return new
                    {
                        id = r.RealId,
                        displayNum = r.DisplayNum,
                        title = r.Title,
                        active = r.Active,
                        archived = r.Archived,
                        deleted = r.Deleted,
                        schedule = r.Schedule,
                        lastChecked = r.LastChecked,
                        health = h.FailCount > 0 ? "failed" : (r.LastChecked is DateTime lc && IsFeedStale(r.Schedule, lc, DateTime.Now) ? "stale" : "ok"),
                        failCount = h.FailCount
                    };
                })
            }
        });
        return;
    }

    var now = DateTime.Now;
    foreach (var r in rows)
    {
        var parts = new List<string>();
        if (r.Active > 0) parts.Add(Lang.T("{0} current", r.Active + r.Deleted));
        if (r.Archived > 0) parts.Add(Lang.T("{0} changed", r.Archived));
        if (r.Deleted > 0) parts.Add(Lang.T("{0} deleted by author, but archived for you", r.Deleted));
        string stats = string.Join(", ", parts);
        string status = FormatFeedStatus(r.Schedule, r.LastChecked, now);
        string healthText = FeedHealthText(r.RealId, r.Schedule, r.LastChecked, now);
        // 健康标记只显示异常（正常不显示）
        string marker = healthText == Lang.T("正常") ? "" : "  " + healthText;
        Console.WriteLine($"[{r.DisplayNum}] {StripControlChars(r.Title)} {stats}{status}{marker}");
    }
}

// ══════════ 更新计划（调度）═══════════
// 每个订阅源可设一条「更新计划」，到期才自动拉取，避免浪费资源：
//   间隔：     5m / 30m / 1h / 6h / 1d / 7d / 30d
//   固定时刻： daily@HH:mm  /  weekly@Ddd HH:mm（Ddd = Mon..Sun）
//   manual 或空：不自动更新
// 到期判断：now >= 上次拉取时间 + 计划到期点；LastCheckedAt 为空视为首次，到期更新一次。
// 每次成功拉取（手动 U / F6 / --sync / 启动同步）都会重写 LastCheckedAt，计时从最新拉取重算。

FeedSchedule? TryParseSchedule(string raw)
{
    string r = (raw ?? "").Trim();
    if (r.Length == 0 || r.Equals("manual", StringComparison.OrdinalIgnoreCase))
        return new FeedSchedule { Raw = r, IsManual = true };
    string lower = r.ToLowerInvariant();

    var mInterval = Regex.Match(lower, @"^(\d+)([mhd])$");
    if (mInterval.Success)
    {
        double n = int.Parse(mInterval.Groups[1].Value);
        double minutes = mInterval.Groups[2].Value switch { "m" => n, "h" => n * 60, _ => n * 1440 };
        return new FeedSchedule { Raw = r, Interval = TimeSpan.FromMinutes(minutes) };
    }

    if (lower.StartsWith("daily@"))
    {
        if (TryParseHhmm(lower["daily@".Length..], out int h, out int m))
            return new FeedSchedule { Raw = r, IsDaily = true, DailyHour = h, DailyMinute = m };
        return null;   // 无效 → 返回 null 表示表达式有误
    }

    if (lower.StartsWith("weekly@"))
    {
        var parts = lower["weekly@".Length..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryParseWeekday(parts[0], out int dow) && TryParseHhmm(parts[1], out int h, out int m))
            return new FeedSchedule { Raw = r, IsWeekly = true, WeeklyDay = dow, WeeklyHour = h, WeeklyMinute = m };
        return null;
    }

    return null;   // 无效表达式
}

bool TryParseHhmm(string s, out int hour, out int minute)
{
    hour = minute = 0;
    var p = s.Split(':');
    if (p.Length != 2) return false;
    if (!int.TryParse(p[0], out hour) || !int.TryParse(p[1], out minute)) return false;
    return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
}

bool TryParseWeekday(string s, out int dow)
{
    dow = s switch
    {
        "sun" or "sunday" => 0,
        "mon" or "monday" => 1,
        "tue" or "tuesday" => 2,
        "wed" or "wednesday" => 3,
        "thu" or "thursday" => 4,
        "fri" or "friday" => 5,
        "sat" or "saturday" => 6,
        _ => -1
    };
    return dow >= 0;
}

string WeekdayName(int dow) => new[] { Lang.T("Sun"), Lang.T("Mon"), Lang.T("Tue"), Lang.T("Wed"), Lang.T("Thu"), Lang.T("Fri"), Lang.T("Sat") }[dow];

// 计算某源的下一次到期时间；手动/无效计划返回 null
DateTime? ComputeNextDue(FeedSchedule s, DateTime lastChecked, DateTime now)
{
    if (s.IsManual) return null;
    if (s.Interval.HasValue)
        return lastChecked.Add(s.Interval.Value);
    if (s.IsDaily)
    {
        var cand = new DateTime(now.Year, now.Month, now.Day, s.DailyHour, s.DailyMinute, 0);
        if (cand <= lastChecked) cand = cand.AddDays(1);
        return cand;
    }
    if (s.IsWeekly)
    {
        var cand = new DateTime(now.Year, now.Month, now.Day, s.WeeklyHour, s.WeeklyMinute, 0);
        int diff = (s.WeeklyDay - (int)cand.DayOfWeek + 7) % 7;
        cand = cand.AddDays(diff);
        if (cand <= lastChecked) cand = cand.AddDays(7);
        return cand;
    }
    return null;
}

bool IsFeedDue(string schedule, DateTime? lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual) return false;
    if (lastChecked == null) return true;   // 首次：到期
    var due = ComputeNextDue(s, lastChecked.Value, now);
    return due != null && now >= due.Value;
}

// 列出当前到期的订阅源（归档源跳过）
List<DueFeed> GetDueFeeds(string dbPath)
{
    var now = DateTime.Now;
    var list = new List<DueFeed>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Title, FeedUrl, LastCheckedAt, Schedule FROM Feeds ORDER BY Id";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        int id = r.GetInt32(0);
        string title = r.GetString(1);
        string url = r.IsDBNull(2) ? "" : r.GetString(2);
        DateTime? lc = r.IsDBNull(3) ? null : TryParseIso(r.GetString(3));
        string schedule = r.IsDBNull(4) ? "" : r.GetString(4);
        if (IsArchived(title) || string.IsNullOrWhiteSpace(url)) continue;
        if (IsFeedDue(schedule, lc, now))
            list.Add(new DueFeed { Id = id, Title = title, Url = url, LastChecked = lc, Schedule = schedule });
    }
    return list;
}

DateTime? TryParseIso(string s)
{
    return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}

// 返回某源的下一次到期时间（用于「距离下次还需多久」提示）；手动/未设置/从未检查返回 null
DateTime? FeedNextDue(string schedule, DateTime? lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual || lastChecked == null) return null;
    return ComputeNextDue(s, lastChecked.Value, now);
}

string HumanSchedule(FeedSchedule s)
{
    if (s.IsManual) return Lang.T("manual");
    if (s.Interval is TimeSpan iv)
    {
        double minutes = iv.TotalMinutes;
        if (minutes < 60) return Lang.T("{0} min", (int)minutes);
        if (minutes < 1440) return Lang.T("{0} hr", (int)(minutes / 60));
        return Lang.T("{0} days", (int)(minutes / 1440));
    }
    if (s.IsDaily) return Lang.T("daily at {0:00}:{1:00}", s.DailyHour, s.DailyMinute);
    if (s.IsWeekly) return Lang.T("weekly {0} {1:00}:{2:00}", WeekdayName(s.WeeklyDay), s.WeeklyHour, s.WeeklyMinute);
    return "";
}

string AgoText(DateTime t, DateTime now)
{
    var span = now - t;
    if (span.TotalSeconds < 60) return Lang.T("just now");
    if (span.TotalMinutes < 60) return Lang.T("{0} min ago", (int)span.TotalMinutes);
    if (span.TotalHours < 24) return Lang.T("{0} hr ago", (int)span.TotalHours);
    return Lang.T("{0} days ago", (int)span.TotalDays);
}

string UntilText(DateTime t, DateTime now)
{
    var span = t - now;
    if (span.TotalSeconds < 60) return Lang.T("soon");
    if (span.TotalMinutes < 60) return Lang.T("in {0} min", (int)span.TotalMinutes);
    if (span.TotalHours < 24) return Lang.T("in {0} hr", (int)span.TotalHours);
    return Lang.T("in {0} days", (int)span.TotalDays);
}

// -l 里追加的「频率 / 上次 / 下次」状态；手动或未设置时返回空串
string FormatFeedStatus(string schedule, DateTime? lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual) return "";
    string sched = HumanSchedule(s);
    string last = lastChecked is DateTime lc ? AgoText(lc, now) : Lang.T("never");
    string next;
    if (lastChecked is DateTime lc2)
    {
        var due = ComputeNextDue(s, lc2, now);
        next = due is DateTime d ? Lang.T("in ") + UntilText(d, now) : "—";
    }
    else
    {
        next = Lang.T("first update pending");
    }
    return Lang.T("({0} · last {1} · next {2})", sched, last, next);
}


// ══════════ 核心方法：下载 RSS → 解析 → 去重 → 写入数据库 ══════════
async Task DownloadAndSaveToDb(string url, string dbPath, bool interactive = true)
{
    // 用户可能忘记 https:// 或 http:// 前缀，自动补全；
    // 若补全的 https 连不上（站点只提供 http），再回退 http 重试一次
    string raw = url.Trim();
    bool wasAutoPrefixed = !(raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                             raw.StartsWith("//", StringComparison.OrdinalIgnoreCase));
    url = EnsureUrlScheme(raw);

    // --- 第 1 步：下载 RSS 原始 XML ---
    // 不加 User-Agent 有些服务器会返回 403 拒绝
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
    Console.WriteLine(Lang.T("Downloading..."));

    string rawXml;
    try
    {
        rawXml = await client.GetStringAsync(url);
    }
    catch (HttpRequestException) when (wasAutoPrefixed && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        // https 失败 → 站点可能只支持 http，重试一次
        string httpUrl = "http://" + url["https://".Length..];
        Console.WriteLine(Lang.T("https:// failed, retrying with http://{0}...", httpUrl["http://".Length..]));
        rawXml = await client.GetStringAsync(httpUrl);
        url = httpUrl;  // 后续用有效的 http 地址写入 / 更新
    }

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
        Console.WriteLine(Lang.T("Feed {0} already exists, comparing...", feed.Title));
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
            Console.WriteLine(Lang.T("Content changed, feed updated"));
        }
        else
        {
            Console.WriteLine(Lang.T("No content change, skipped update"));
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
            INSERT INTO Feeds (Title, FeedUrl, Link, Description, LastFetched, RawXml, LastCheckedAt)
            VALUES (@title, @url, @link, @desc, @fetched, @rawXml, @checked)
        ";
        insertCmd.Parameters.AddWithValue("@title", feed.Title);
        insertCmd.Parameters.AddWithValue("@url", url);
        insertCmd.Parameters.AddWithValue("@link", feed.Link ?? "");
        insertCmd.Parameters.AddWithValue("@desc", feed.Description ?? "");
        insertCmd.Parameters.AddWithValue("@fetched", DateTime.Now.ToString("O"));
        insertCmd.Parameters.AddWithValue("@rawXml", rawXml);
        insertCmd.Parameters.AddWithValue("@checked", DateTime.Now.ToString("O"));
        insertCmd.ExecuteNonQuery();

        insertCmd.CommandText = "SELECT last_insert_rowid()";
        feedId = (long)insertCmd.ExecuteScalar()!;
    }

    // 无论内容是否有变化，都记录「上次拉取时间」——调度只关心上次何时真正查过
    var touchCmd = conn.CreateCommand();
    touchCmd.CommandText = "UPDATE Feeds SET LastCheckedAt = @checked WHERE Id = @id";
    touchCmd.Parameters.AddWithValue("@checked", DateTime.Now.ToString("O"));
    touchCmd.Parameters.AddWithValue("@id", feedId);
    touchCmd.ExecuteNonQuery();

    // --- 第 5 步：ShowDiff 负责检测文章变化 + 输出 + 执行归档/插入/标记删除 ---
    // 新源 → 全量插入不过滤；旧源 → 逐篇比对
    ShowDiff(feed, feedId, conn, isNewFeed);

    Console.WriteLine(Lang.T("{0} saved", feed.Title));

    // --- 第 6 步：若已初始化 AI，询问是否把该源未向量化的文章加入 embedding ---
    await MaybeIndexNewArticles(feedId, dbPath, interactive);
}

// ══════════ 辅助方法：下载/更新后询问是否对新文章做向量化 ══════════
// 仅当已执行过 --init（存在 ai_config.json）时才会询问，避免打扰未配置 AI 的用户
// ask=false（自动同步/后台检查）时跳过询问，默认不向量化，避免卡在读输入
async Task MaybeIndexNewArticles(long feedId, string dbPath, bool ask = true)
{
    if (!File.Exists(ConfigPath(dbPath))) return;

    var cfg = LoadConfig(dbPath);
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) FROM Items i
        WHERE i.FeedId = @fid AND i.Status = 'active'
        AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
    ";
    cmd.Parameters.AddWithValue("@fid", feedId);
    long pending = (long)cmd.ExecuteScalar()!;
    if (pending == 0) return;

    // 自动同步 / 后台检查时跳过 y/n 询问，不打扰、不卡输入
    if (!ask) { Console.WriteLine(Lang.T("{0} new articles not embedded (auto-sync, skipped; use sip --index when needed)", pending)); return; }

    Console.WriteLine(Lang.T("This feed has {0} new articles not yet embedded. Add to semantic search ({1})? (y/n)", pending, cfg.Embedding.Model));
    if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine(Lang.T("Skipped, run sip --index later if needed")); return; }

    cmd.CommandText = "SELECT Id, Title FROM Items WHERE FeedId = @fid AND Status = 'active' AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = Items.Id)";
    using var r = cmd.ExecuteReader();
    var articles = new List<(int Id, string Title)>();
    while (r.Read()) articles.Add((r.GetInt32(0), r.GetString(1)));
    r.Close();

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    int ok = 0, fail = 0;
    foreach (var a in articles)
    {
        var vec = await SafeEmbed(a.Title, cfg);
        if (vec == null) { fail++; continue; }
        if (vec.Length != cfg.Embedding.Dimensions)
        {
            cfg.Embedding.Dimensions = vec.Length;
            SaveConfig(dbPath, cfg);
        }
        SaveVector(dbPath, (int)feedId, a.Id, modelId, vec);
        ok++;
    }
    Console.WriteLine(Lang.T("Embedding done: {0} OK, {1} failed", ok, fail));
}

// ══════════ 辅助方法：补全 URL 协议前缀 ══════════
// 用户可能直接输入 "example.com/rss" 而忘记 https:// 或 http://
// 无协议时默认补 https://（GET 失败会由调用方捕获提示）
string EnsureUrlScheme(string url)
{
    string trimmed = url.Trim();
    if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return trimmed;
    if (trimmed.StartsWith("//", StringComparison.OrdinalIgnoreCase))
        return "https:" + trimmed;
    Console.WriteLine(Lang.T("URL missing a scheme, auto-prefixed to https://{0}", trimmed));
    return "https://" + trimmed;
}

// ══════════ 辅助方法：规范化 OpenAI 兼容端点 ══════════
// 用户常只填 "http://host:11434"，这里补上 "/v1"（OpenAI 兼容路径）；
// 缺协议头（如 "open.cherryin.net/v1"）时静默补 https://，避免请求崩溃
string EnsureV1Endpoint(string ep)
{
    string e = ep.Trim().TrimEnd('/');
    if (e.Length == 0) return e;
    if (!e.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !e.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        e = "https://" + e;
    if (e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        return e;
    return e + "/v1";
}

// ══════════ 辅助方法：按标题查未归档源的 RawXml ══════════
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

// ══════════ 判断标题是否有时间戳后缀（即是否已被归档）═══════════
bool IsArchived(string title)
{
    return Regex.IsMatch(title, @"_\d{8}_\d{6}$");
}


// ══════════ 显示编号 → 真实 Id ══════════
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

// ══════════ 删除订阅源 + 它的所有文章 ══════════
// 删除订阅源；yes=true（--yes/-y）跳过确认，供脚本/AI 非交互使用
void DeleteFeed(int displayNum, string dbPath, bool yes = false)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

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

    if (!yes)
    {
        Console.Write(Lang.T("Delete {0} and its {1} articles? (y/n)", title, itemCount));
        if (!"y".Equals(Console.ReadLine()?.Trim().ToLower()))
        {
            Console.WriteLine(Lang.T("Cancelled"));
            return;
        }
    }

    // 2. 先删该源的向量和文章
    cmd.CommandText = "DELETE FROM Vectors WHERE FeedId = @id";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();

    // 3. 再删订阅源
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("{0} deleted", title));
}

// ══════════ 加时间戳：标题 + _20260712_143000 ══════════
// 加完后标题变了，下次下载同名源时 GetOldRawXml 找不到，
// 就会被当作新订阅源处理，不会触发去重
void AddTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

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
        Console.WriteLine(Lang.T("{0} is already archived", oldTitle));
        return;
    }

    // 3. 追加时间戳
    string newTitle = oldTitle + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // 4. 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", newTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("Title changed: {0} → {1} ", oldTitle, newTitle));
}

// ══════════ 去时间戳：去掉 _yyyymmdd_hhmmss 后缀 ══════════
// 去掉之前检查原始标题是否已存在，防止冲突
void RemoveTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

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
        Console.WriteLine(Lang.T("{0} was not archived", title));
        return;
    }

    // 3. 检查 plainTitle 是否已被其他源占用（排除自己）
    cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Title = @title AND Id != @id";
    cmd.Parameters.AddWithValue("@title", plainTitle);
    long conflict = (long)cmd.ExecuteScalar()!;
    if (conflict > 0)
    {
        Console.WriteLine(Lang.T("Conflict! Another feed named {0} exists, cannot remove the timestamp", plainTitle));
        return;
    }

    // 4. 安全 → 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", plainTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("Timestamp removed: {0} → {1} ", title, plainTitle));
}

// ════════════════════════════════════════════════════════
// 下面是 ShowDiff 的两个版本
// ════════════════════════════════════════════════════════

// ══════════ 辅助方法：插入一篇新文章到 Items 表 ══════════
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

// ══════════ ShowDiff（文章级别）：检测新增/修改/删除 + 输出 + 执行 ══════════
// isNewFeed=true  → 新订阅源，全量插入 + 跳过删除检测
// isNewFeed=false → 已有源，逐篇比对：新增/修改/删除
void ShowDiff(Feed newFeed, long feedId, SqliteConnection conn, bool isNewFeed = false)
{
    int newCount = 0;
    int modifyCount = 0;

    foreach (var item in newFeed.Items)
    {
        string guid = item.Id ?? item.Link ?? "";

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

            Console.WriteLine(Lang.T("  [archived] {0} author changed content, old version kept", item.Title));
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

    // 新源跳过修改检测（没有旧数据可比）
    if (isNewFeed)
    {
        Console.WriteLine(Lang.T("  {0} new", newCount));
        return;
    }

    // 不检测删除：很多站点 RSS 只推最近 N 篇，老文章不在列表里不代表被删，
    // 因此只跟踪新增与修改，避免把正常下架的文章误标为 deleted
    Console.WriteLine(Lang.T("  {0} new, {1} modified", newCount, modifyCount));
}

// ══════════ ShowDiff（Feed 级别）：纯文本比对，看旧 XML 和新 XML 有无差异 ══════════
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
                    Console.WriteLine($"+ {StripControlChars(line.Text)}");
                    hasChanges = true;
                    break;
                case ChangeType.Deleted:    // 被删掉的文章（旧 RSS 有、新 RSS 没有）
                    Console.WriteLine($"- {StripControlChars(line.Text)}");
                    hasChanges = true;
                    break;
                case ChangeType.Modified:   // 内容被修改的文章
                    Console.WriteLine($"~ {StripControlChars(line.Text)}");
                    hasChanges = true;
                    break;
            }
        }

        if (!hasChanges)  // 一个变化都没有
            Console.WriteLine(Lang.T("New and old RSS are identical, no changes"));

        return hasChanges;  // 把结果返回给调用方，让它决定是否更新
    }
    catch (Exception ex)
    {
        Console.WriteLine(Lang.T("Error comparing item diffs: {0}", ex.Message));
        return false;  // 出错了保守处理：不用旧数据覆盖，当作没变化
    }
}

// ══════════ GetItemSummary：生成文章摘要行，供文本 diff 显示用 ══════════
string GetItemSummary(FeedItem item)
{
    string id = !string.IsNullOrEmpty(item.Id) ? item.Id : item.Link ?? item.Title ?? Lang.T("unknown");
    return $"[{id}] {item.Title}";
}

// ══════════════════════════════════════════════════════════
// AI 相关功能：配置、凭据、Embedding、向量、搜索、摘要
// ══════════════════════════════════════════════════════════
// （配置类 AiConfig / EmbeddingCfg / LlmCfg / SearchHit / AiException 见文件末尾类型区）

string ConfigPath(string dbPath) => Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "ai_config.json");

AiConfig LoadConfig(string dbPath)
{
    string path = ConfigPath(dbPath);
    AiConfig? cfg = null;
    if (File.Exists(path))
    {
        try
        {
            // 大小写不敏感绑定：兼容手写/旧版小驼峰配置（apiEndpoint/searchThreshold 等）
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            cfg = JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(path), opts);
        }
        catch { /* 配置损坏时用默认值 */ }
    }
    cfg ??= new AiConfig();
    // 容错：手写/旧配置缺协议头时补全（静默，不打扰用户）
    cfg.Embedding.ApiEndpoint = NormalizeEndpoint(cfg.Embedding.ApiEndpoint);
    cfg.Llm.ApiEndpoint = NormalizeEndpoint(cfg.Llm.ApiEndpoint);
    return cfg;
}

// 端点缺协议头时静默补 https://
string NormalizeEndpoint(string ep)
{
    string e = ep.Trim();
    if (e.Length == 0) return e;
    if (e.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || e.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return e;
    return "https://" + e;
}

void SaveConfig(string dbPath, AiConfig cfg)
{
    var opts = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(ConfigPath(dbPath), JsonSerializer.Serialize(cfg, opts));
}

// ══════════ 凭据存储（系统原生凭据管理器）═══════════
// 服务标识：固定字符串，用于在系统凭据库中区分本应用的条目
void CredSet(string key, string value)
{
    var store = CredentialStoreFactory.CreateDefault("hotsoupreader");
    var cache = new ktsu.CredentialCache.CredentialCache(store);
    cache.AddOrReplace(new PersonaGUID { WeakString = key }, new CredentialWithToken { Token = new CredentialToken { WeakString = value } });
}

string? CredGet(string key)
{
    try
    {
        var store = CredentialStoreFactory.CreateDefault("hotsoupreader");
        var cache = new ktsu.CredentialCache.CredentialCache(store);
        if (cache.TryGet(new PersonaGUID { WeakString = key }, out var cred) && cred is CredentialWithToken ct)
            return ct.Token.WeakString;
    }
    catch { /* 凭据库不可用时返回 null */ }
    return null;
}

bool CredHas(string key) => CredGet(key) != null;

// ══════════ 安全提醒（首次调用 AI 功能时输出）═══════════
// 传了 --ignoresafeannouncement 则不输出（供脚本/AI Agent 使用，避免多余内容）
void EnsureAiPrompted()
{
    if (AiState.Warned) return;
    AiState.Warned = true;
    if (AiState.IgnoreAnnouncement) return;
    Console.WriteLine(Lang.T("════════════════════════════════════════════════════"));
    Console.WriteLine(Lang.T("🔐 Security notice"));
    Console.WriteLine(Lang.T("Your API key is stored in the OS-native credential store"));
    Console.WriteLine(Lang.T("(Windows Credential Manager / macOS Keychain / Linux Secret Service)"));
    Console.WriteLine(Lang.T("It is never written to any project file. Please keep it secret:"));
    Console.WriteLine(Lang.T("1. Never share/send your API key to anyone"));
    Console.WriteLine(Lang.T("2. Never screenshot or upload screens with the key"));
    Console.WriteLine(Lang.T("3. Rotate your key immediately if you suspect a leak"));
    Console.WriteLine(Lang.T("════════════════════════════════════════════════════"));
}

// ══════════ JSON 输出辅助 ══════════
void JsonOut(object obj) => Console.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));

// 退出码分类（脚本/AI 用 exit code 判断成败）：
//   0=成功  1=通用错误（参数/用法/数据库）  2=网络/服务不可达  3=资源未就绪（AI 未配置/密钥缺失/无索引/找不到）
int ExitCodeFor(string code) => code switch
{
    "NETWORK_ERROR" or "MODEL_UNAVAILABLE" => 2,
    "API_KEY_MISSING" or "API_KEY_INVALID" or "NO_INDEX"
        or "FEED_NOT_FOUND" or "ITEM_NOT_FOUND" or "EMPTY_QUERY" => 3,
    _ => 1,
};

// 设置退出码（取最严重的：同一次调用里若有多次失败不会被较低严重度的覆盖）
void SetExit(int code = 1) => AiState.ExitCode = Math.Max(AiState.ExitCode, code);

// 自然语言报错 + JSON 双格式
void ReportError(string code, string message, string? suggestion = null, string? details = null, bool json = false)
{
    SetExit(ExitCodeFor(code));
    if (json)
    {
        JsonOut(new { success = false, error = new { code, message, suggestion, details } });
    }
    else
    {
        Console.WriteLine(Lang.T("Error [{0}] {1}", code, message));
        if (suggestion != null) Console.WriteLine(Lang.T("Suggestion: {0}", suggestion));
        if (details != null) Console.WriteLine(Lang.T("Details: {0}", details));
    }
}

// ══════════ Embedding 服务（OpenAI 兼容格式，端点可自定义）═══════════
// 统一走 POST {endpoint}/embeddings：Ollama(/v1)、DeepSeek、OpenAI 及任何
// 兼容服务均可；API Key 可选（本地 Ollama 不需要，填了才带 Bearer 头）
async Task<float[]?> GetEmbeddingAsync(string text, AiConfig cfg)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    bool ok = false;
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        string? key = CredGet("embedding_api_key");
        if (!string.IsNullOrEmpty(key))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var body = new { model = cfg.Embedding.Model, input = text };
        var resp = await client.PostAsync($"{cfg.Embedding.ApiEndpoint}/embeddings",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            throw new AiException("MODEL_UNAVAILABLE", Lang.T("Embedding request failed (HTTP {0})", (int)resp.StatusCode),
                Lang.T("Verify the endpoint/port/model name; with Ollama run ollama list / ollama pull first"), await resp.Content.ReadAsStringAsync());
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            ok = true;
            return data.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        }
        catch (JsonException)
        {
            // 返回的不是 JSON（比如端点缺少 /v1 返回的 HTML），给出友好提示而非崩溃
            throw new AiException("INVALID_RESPONSE", Lang.T("Embedding service did not return JSON"),
                Lang.T("Check whether the endpoint is missing /v1 (correct form http://host:port/v1)"));
        }
    }
    finally
    {
        // 遥测：记录 ai_call（不记 prompt/响应内容）
        TelemetryService.RecordAiCall("embedding", cfg.Embedding.Provider, cfg.Embedding.Model, ok, sw.ElapsedMilliseconds);
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
        ReportError("NETWORK_ERROR", Lang.T("Network error, cannot reach the Embedding service"),
            Lang.T("Check your network connection or the API endpoint"), ex.Message, json);
        return null;
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return null;
    }
}

// ══════════ 向量存储与相似度 ══════════
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
            return Lang.T("Embedding model dimensions changed (old {0} {1}D → new {2} {3}D), old vectors are unusable, run --reindex to re-embed",
                oldName, oldDim, emb.Model, emb.Dimensions);
    }
    return null;
}

// 保存向量（幂等：同文章 + 同模型只留一条）
void SaveVector(string dbPath, int feedId, int itemId, int modelId, float[] vector)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Vectors (FeedId, ItemId, ModelId, Vector, CreatedAt)
        VALUES (@f, @i, @m, @v, @now)
        ON CONFLICT(ItemId, ModelId) DO UPDATE SET FeedId = excluded.FeedId, Vector = excluded.Vector, CreatedAt = excluded.CreatedAt
    ";
    cmd.Parameters.AddWithValue("@f", feedId);
    cmd.Parameters.AddWithValue("@i", itemId);
    cmd.Parameters.AddWithValue("@m", modelId);
    cmd.Parameters.AddWithValue("@v", VectorToBytes(vector));
    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
    cmd.ExecuteNonQuery();
}

// ══════════ 交互式选择文章进行向量化 ══════════
async Task IndexArticlesCli(string[] extraArgs, string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);

    // 默认全选模式；也可支持 --all
    ListFeedsFromDb(dbPath);
    Console.WriteLine();
    Console.Write(Lang.T("Enter feed numbers to embed (comma-separated, \"all\" for all): "));
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

    if (feedIds.Count == 0) { Console.WriteLine(Lang.T("No feed selected, cancelled")); return; }

    // 收集未向量化的 active 文章
    var articles = new List<(int Id, int FeedId, string Title)>();
    var cmd2 = conn.CreateCommand();
    cmd2.CommandText = @"
        SELECT i.Id, i.FeedId, i.Title FROM Items i
        WHERE i.Status = 'active' AND i.FeedId IN (" + string.Join(",", feedIds) + @")
        AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
    ";
    using var r2 = cmd2.ExecuteReader();
    while (r2.Read()) articles.Add((r2.GetInt32(0), r2.GetInt32(1), r2.GetString(2)));

    if (articles.Count == 0) { Console.WriteLine(Lang.T("All articles of the selected feeds are already embedded")); return; }

    Console.WriteLine(Lang.T("Will embed {0} articles, confirm? (y/n)", articles.Count));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    int ok = 0, fail = 0;
    for (int i = 0; i < articles.Count; i++)
    {
        var a = articles[i];
        var vec = await SafeEmbed(a.Title, cfg);
        if (vec == null) { fail++; Console.WriteLine(Lang.T("  [{0}/{1}] failed: {2}", i + 1, articles.Count, a.Title)); continue; }
        if (vec.Length != cfg.Embedding.Dimensions)
        {
            // 自动校正维度（以实际为准）
            cfg.Embedding.Dimensions = vec.Length;
            SaveConfig(dbPath, cfg);
        }
        SaveVector(dbPath, a.FeedId, a.Id, modelId, vec);
        ok++;
        if (ok % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, articles.Count));
    }
    Console.WriteLine(Lang.T("Done: {0} OK, {1} failed", ok, fail));

    // 回补全文 sidecar：已抓全文但当时未索引的文章补向量（修复时序缺陷）
    int backfilled = BackfillFulltextSidecars(dbPath, articles.Select(a => (a.Id, a.FeedId)).ToList());
    if (backfilled > 0) Console.WriteLine(Lang.T("Fulltext sidecar vectors backfilled: {0}", backfilled));
}

// 重新向量化（更换模型后）：清空旧向量并重来
async Task ReindexCli(string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Status = 'active'";
    long total = (long)cmd.ExecuteScalar()!;

    Console.Write(Lang.T("Will delete existing vectors and re-embed all {0} active articles, confirm? (y/n)", total));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }

    cmd.CommandText = "DELETE FROM Vectors";
    cmd.ExecuteNonQuery();
    // 换模型后旧 sidecar 向量（抓取全文的）同样失效，一并清空
    if (File.Exists(FulltextVecsPath())) { try { File.Delete(FulltextVecsPath()); } catch { } }

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    cmd.CommandText = "SELECT Id, FeedId, Title FROM Items WHERE Status = 'active'";
    using var r = cmd.ExecuteReader();
    var items = new List<(int Id, int FeedId, string Title)>();
    while (r.Read()) items.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
    r.Close();

    int ok = 0, fail = 0;
    foreach (var item in items)
    {
        var vec = await SafeEmbed(item.Title, cfg);
        if (vec == null) { fail++; continue; }
        SaveVector(dbPath, item.FeedId, item.Id, modelId, vec);
        ok++;
        if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, items.Count));
    }
    Console.WriteLine(Lang.T("Re-indexing done: {0} OK, {1} failed", ok, fail));

    // 换模型后旧全文 sidecar 已清空，给有全文缓存的文章重算
    int backfilled = BackfillFulltextSidecars(dbPath, items.Select(a => (a.Id, a.FeedId)).ToList());
    if (backfilled > 0) Console.WriteLine(Lang.T("Fulltext sidecar vectors backfilled: {0}", backfilled));
}

// ══════════ 语义搜索 ══════════
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
                    if (feedReal == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number {0} not found", f), json: json); return; }
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
    if (string.IsNullOrWhiteSpace(query)) { ReportError("EMPTY_QUERY", Lang.T("Please enter a search query"), json: json); return; }

    var results = DoSearch(query, dbPath, feedReal, threshold, json);
    if (results == null) return;

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
                results = results.Select(h =>
                {
                    var sig = GetSignal(h.ItemId);
                    return new
                    {
                        itemId = h.ItemId,
                        title = h.Title,
                        description = h.Description,
                        link = h.Link,
                        feedId = h.FeedId,
                        feedTitle = h.FeedTitle,
                        score = Math.Round(h.Score, 4),
                        quality = ContentQuality(h.Content, h.Description),
                        liked = sig?.UserLike ?? false,
                        aiLiked = sig?.AiLike ?? false
                    };
                }),
                total = results.Count
            }
        });
    }
    else
    {
        Console.WriteLine(Lang.T("Search results (query: {0}, threshold: {1}, total {2})", query, threshold, results.Count));
        foreach (var h in results)
        {
            Console.WriteLine($"  [{h.ItemId}] {StripControlChars(h.Title)}");
            Console.WriteLine(Lang.T("      source: {0} | {1}", StripControlChars(h.FeedTitle), h.Link));
            if (!string.IsNullOrEmpty(h.Description) && h.Description.Length > 80)
                Console.WriteLine(Lang.T("      summary: {0}...", StripControlChars(h.Description[..80])));
        }
    }
}

// 全文搜索 CLI：在标题/正文/摘要里做关键字匹配（类似 VS Code 全文搜索，不依赖 AI）
// 默认「片段模式」：每篇只出 编号 + 标题 + 出现次数 + 上下 50 字符的片段，输出有上限、不会爆上下文；
// --full 恢复旧模式（整篇摘要），--json 结构化输出
void GrepCli(string[] args, string dbPath)
{
    var flags = args.Skip(1).ToArray();
    bool json = flags.Contains("--json", StringComparer.OrdinalIgnoreCase);
    bool full = flags.Contains("--full", StringComparer.OrdinalIgnoreCase);
    int limit = 20, maxSnippets = 10;
    for (int i = 0; i < flags.Length; i++)
    {
        if (flags[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Length && int.TryParse(flags[i + 1], out int l))
            limit = Math.Max(1, l);
        if (flags[i].Equals("--max-snippets", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Length && int.TryParse(flags[i + 1], out int ms))
            maxSnippets = Math.Max(1, ms);
    }
    string keyword = args[0];

    var hits = DoGrep(keyword, dbPath, limit);
    if (hits == null) return;

    // --full：整篇摘要直出（旧行为，显式 opt-in）
    if (full)
    {
        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    query = keyword,
                    results = hits.Select(h => new
                    {
                        itemId = h.ItemId,
                        title = h.Title,
                        description = h.Description,
                        link = h.Link,
                        feedTitle = h.FeedTitle
                    }),
                    total = hits.Count
                }
            });
        }
        else
        {
            Console.WriteLine(Lang.T("Full-text search \"{0}\": {1} hits", keyword, hits.Count));
            foreach (var h in hits)
            {
                Console.WriteLine($"  [{h.ItemId}] {StripControlChars(h.Title)}");
                Console.WriteLine(Lang.T("      source: {0} | {1}", StripControlChars(h.FeedTitle), h.Link));
                if (!string.IsNullOrEmpty(h.Description))
                    Console.WriteLine(Lang.T("      summary: {0}", StripControlChars(h.Description)));
            }
        }
        return;
    }

    // 片段模式：每篇统计出现次数 + 取前 maxSnippets 个 ±50 字符片段
    var items = new List<GrepSnippetResult>();
    foreach (var h in hits)
    {
        string haystack = h.Title + "\n" + StripHtml(string.IsNullOrWhiteSpace(h.Content) ? h.Description : h.Content)
                          + (string.IsNullOrWhiteSpace(h.Summary) ? "" : "\n" + h.Summary);
        var (snippets, total) = ExtractGrepSnippets(haystack, keyword, radius: 50, max: maxSnippets);
        items.Add(new GrepSnippetResult
        {
            ItemId = h.ItemId, Title = h.Title, Link = h.Link, FeedTitle = h.FeedTitle,
            Count = total, Snippets = snippets, TotalSnippets = total,
            Quality = ContentQuality(h.Content, h.Description)
        });
    }

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                query = keyword,
                results = items.Select(r => new
                {
                    itemId = r.ItemId,
                    title = r.Title,
                    count = r.Count,
                    totalSnippets = r.TotalSnippets,
                    snippets = r.Snippets,
                    link = r.Link,
                    feedTitle = r.FeedTitle,
                    quality = r.Quality
                }),
                total = items.Count
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("Full-text search \"{0}\": {1} hits", keyword, items.Count));
    foreach (var r in items)
    {
        Console.WriteLine($"  [{r.ItemId}] {StripControlChars(r.Title)} ({Lang.T("{0} occurrences", r.Count)})");
        for (int i = 0; i < r.Snippets.Count; i++)
            Console.WriteLine($"    {i + 1}. {StripControlChars(r.Snippets[i])}");
        if (r.TotalSnippets > r.Snippets.Count)
            Console.WriteLine(Lang.T("    …({0} more, view full text with sip --show {1})", r.TotalSnippets - r.Snippets.Count, r.ItemId));
    }
}

// 在纯文本 haystack 里大小写不敏感地找出 keyword 的所有出现位置，
// 每个位置取 [i-radius, i+radius+len] 的窗口；相邻窗口重叠时合并；
// 只保留前 max 段（超出返回 total 让调用方知道还有多少）
(List<string> Snippets, int Total) ExtractGrepSnippets(string haystack, string keyword, int radius, int max)
{
    if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(keyword)) return (new List<string>(), 0);

    int kwLen = keyword.Length;
    var ranges = new List<(int Start, int End)>();
    int from = 0, total = 0;
    while (true)
    {
        int idx = haystack.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) break;
        total++;

        int start = Math.Max(0, idx - radius);
        int end = Math.Min(haystack.Length, idx + kwLen + radius);
        // 与上一个窗口重叠 → 扩展合并，避免重复文本
        if (ranges.Count > 0 && start <= ranges[^1].End)
        {
            var last = ranges[^1];
            ranges[^1] = (last.Start, Math.Max(last.End, end));
        }
        else if (ranges.Count < max)
        {
            ranges.Add((start, end));
        }
        // 超过 max 后不再新增窗口，但继续统计总出现次数（total 是真实的全部次数）
        from = idx + kwLen;
    }
    var snippets = ranges.Select(r => haystack[r.Start..r.End]).ToList();
    return (snippets, total);
}

// 全文搜索核心逻辑（CLI 与 TUI 共用）：SQL LIKE 匹配标题/正文/摘要
// limit：命中数上限（TUI 传默认 200；CLI 默认 --limit 20）
List<GrepHit>? DoGrep(string keyword, string dbPath, int limit = 200)
{
    if (string.IsNullOrWhiteSpace(keyword)) { SetExit(); Console.WriteLine(Lang.T("Enter a search keyword")); return null; }
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT i.Id, i.Title, i.Description, i.Content, i.Summary, i.Link, f.Title AS FeedTitle
        FROM Items i
        JOIN Feeds f ON i.FeedId = f.Id
        WHERE i.Status = 'active'
          AND (i.Title LIKE @kw ESCAPE '\' OR i.Content LIKE @kw ESCAPE '\' OR i.Description LIKE @kw ESCAPE '\' OR i.Summary LIKE @kw ESCAPE '\')
        ORDER BY i.Id
        LIMIT @limit
    ";
    // 转义 LIKE 通配符（% _ \），让关键词按字面匹配而非被当作通配符
    string escaped = keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    cmd.Parameters.AddWithValue("@kw", "%" + escaped + "%");
    cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
    var hits = new List<GrepHit>();
    using (var r = cmd.ExecuteReader())
    {
        while (r.Read())
        {
            hits.Add(new GrepHit
            {
                ItemId = r.GetInt32(0),
                Title = r.GetString(1),
                Description = r.IsDBNull(2) ? "" : r.GetString(2),
                Content = r.IsDBNull(3) ? "" : r.GetString(3),
                Summary = r.IsDBNull(4) ? "" : r.GetString(4),
                Link = r.IsDBNull(5) ? "" : r.GetString(5),
                FeedTitle = r.GetString(6)
            });
        }
    }
    // Description 可能是 HTML，转纯文本便于阅读
    for (int i = 0; i < hits.Count; i++)
        hits[i] = new GrepHit { ItemId = hits[i].ItemId, Title = hits[i].Title, Description = StripHtml(hits[i].Description), Content = hits[i].Content, Summary = hits[i].Summary, Link = hits[i].Link, FeedTitle = hits[i].FeedTitle };
    return hits;
}

// 语义搜索核心逻辑（CLI 与 TUI 共用）；失败返回 null
List<SearchHit>? DoSearch(string query, string dbPath, int? feedReal = null, float? threshold = null, bool json = false)
{
    var cfg = LoadConfig(dbPath);
    float thr = threshold ?? cfg.Embedding.SearchThreshold;

    var vec = SafeEmbed(query, cfg, json).GetAwaiter().GetResult();
    if (vec == null) return null;

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var modelCmd = conn.CreateCommand();
    modelCmd.CommandText = "SELECT Id FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
    var modelObj = modelCmd.ExecuteScalar();
    if (modelObj == null) { ReportError("NO_INDEX", Lang.T("No vector index yet, run sip --index first"), json: json); return null; }
    int modelId = Convert.ToInt32(modelObj);

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Vectors WHERE ModelId = @m";
    cmd.Parameters.AddWithValue("@m", modelId);
    long count = (long)cmd.ExecuteScalar()!;
    if (count == 0) { ReportError("NO_INDEX", Lang.T("The current model has no vectors yet, run sip --index first"), json: json); return null; }

    cmd.Parameters.Clear();
    cmd.CommandText = @"
        SELECT v.ItemId, v.Vector, i.Title, i.Description, i.Content, i.Link,
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
            if (score < thr) continue;
            results.Add(new SearchHit
            {
                ItemId = r.GetInt32(0),
                Title = r.GetString(2),
                Description = r.IsDBNull(3) ? "" : r.GetString(3),
                Content = r.IsDBNull(4) ? "" : r.GetString(4),
                Link = r.IsDBNull(5) ? "" : r.GetString(5),
                FeedTitle = r.GetString(6),
                FeedId = r.GetInt32(7),
                Score = score
            });
        }
    }

    // 合并 sidecar（抓取全文向量）：只补主表没有的 itemId
    var seen = new HashSet<int>(results.Select(h => h.ItemId));
    foreach (var (sid, sfeed, smodel, svec) in LoadFulltextVecs())
    {
        if (smodel != modelId || seen.Contains(sid)) continue;
        if (feedReal.HasValue && sfeed != feedReal.Value) continue;
        float score = CosineSimilarity(vec, svec);
        if (score < thr) continue;
        var hit = GetSearchHitForItem(dbPath, sid, score);
        if (hit != null) { results.Add(hit); seen.Add(sid); }
    }

    return results.OrderByDescending(h => h.Score).Take(20).ToList();
}

// 按 itemId 取搜索结果条目（sidecar 向量命中用）
SearchHit? GetSearchHitForItem(string dbPath, int itemId, float score)
{
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT i.Title, i.Description, i.Content, i.Link, i.FeedId, f.Title FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id WHERE i.Id = @id AND i.Status = 'active'";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SearchHit
        {
            ItemId = itemId,
            Title = r.GetString(0),
            Description = r.IsDBNull(1) ? "" : r.GetString(1),
            Content = r.IsDBNull(2) ? "" : r.GetString(2),
            Link = r.IsDBNull(3) ? "" : r.GetString(3),
            FeedId = r.GetInt32(4),
            FeedTitle = r.IsDBNull(5) ? "" : r.GetString(5),
            Score = score
        };
    }
    catch { return null; }
}

// 按真实 Id 更新单个源（TUI 用）
void RefreshOneFeed(int realId, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT FeedUrl FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string? url = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrWhiteSpace(url)) return;
    try { DownloadAndSaveToDb(url, dbPath).Wait(); }
    catch { }
}

// 按 Guid 删除整篇文章（含全部历史版本与向量）
void DeleteArticleByGuid(string guid, string dbPath)
{
    // 先取该 Guid 全部 Id（清理全文缓存与 sidecar 向量）
    var ids = new List<int>();
    using (var q = new SqliteConnection($"Data Source={dbPath}"))
    {
        q.Open();
        var c = q.CreateCommand();
        c.CommandText = "SELECT Id FROM Items WHERE Guid = @g";
        c.Parameters.AddWithValue("@g", guid);
        using var r = c.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt32(0));
    }
    foreach (var id in ids)
    {
        string p = FulltextPath(id);
        if (File.Exists(p)) { try { File.Delete(p); } catch { } }
    }
    RemoveFulltextVecs(ids);

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Vectors WHERE ItemId IN (SELECT Id FROM Items WHERE Guid = @g)";
    cmd.Parameters.AddWithValue("@g", guid);
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE Guid = @g";
    cmd.Parameters.AddWithValue("@g", guid);
    cmd.ExecuteNonQuery();
}

// （SearchHit 类见文件末尾类型区）
// ══════════ LLM 摘要服务（OpenAI 兼容，端点可自定义）═══════════
async Task<string?> CallLlmAsync(string prompt, AiConfig cfg)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    bool ok = false;
    try
    {
        string? key = CredGet("llm_api_key");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        if (!string.IsNullOrEmpty(key))
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
            throw new AiException("API_KEY_INVALID", Lang.T("LLM request failed (HTTP {0})", (int)resp.StatusCode),
                Lang.T("Check the API key / model name / endpoint config"), await resp.Content.ReadAsStringAsync());
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            ok = true;
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (JsonException)
        {
            throw new AiException("INVALID_JSON", Lang.T("LLM service did not return JSON"),
                Lang.T("Check whether the endpoint is missing /v1 (e.g. https://api.deepseek.com/v1)"));
        }
    }
    finally
    {
        // 遥测：记录 ai_call（不记 prompt/响应内容）
        TelemetryService.RecordAiCall("llm", cfg.Llm.Provider, cfg.Llm.Model, ok, sw.ElapsedMilliseconds);
    }
}

// 生成单篇文章摘要并保存到 rss.db（与文章同在库中）
async Task<(bool Ok, string? Summary)> SummarizeItem(string dbPath, int itemId, bool json = false, bool quiet = false)
{
    var cfg = LoadConfig(dbPath);
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, Content, Description, Summary FROM Items WHERE Id = @id AND Status = 'active'";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: json); return (false, null); }
    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string existing = r.IsDBNull(3) ? "" : r.GetString(3);
    r.Close();

    if (!string.IsNullOrEmpty(existing))
    {
        if (!quiet) Console.WriteLine(Lang.T("Article [{0}] {1} already has a summary, skipped (delete it first to regenerate)", itemId, title));
        if (json) JsonOut(new { success = true, itemId, title, summary = existing, cached = true });
        return (true, existing);
    }

    string text = string.IsNullOrEmpty(content) ? desc : content;
    if (text.Length > 6000) text = text[..6000];
    var prompt = $"请用 150 字以内概括以下文章的核心内容（用中文回答，直接输出摘要正文，不要额外解释）：\n\n标题：{title}\n\n正文：{text}";

    try
    {
        EnsureAiPrompted();
        var summary = await CallLlmAsync(prompt, cfg);
        if (summary == null) throw new AiException("EMPTY_RESPONSE", Lang.T("LLM returned empty"), Lang.T("Retry or check the model config"));

        var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE Items SET Summary = @s, SummaryAt = @now WHERE Id = @id";
        upd.Parameters.AddWithValue("@s", summary.Trim());
        upd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        upd.Parameters.AddWithValue("@id", itemId);
        upd.ExecuteNonQuery();
        if (!quiet) Console.WriteLine(Lang.T("Summary generated: [{0}] {1}", itemId, title));
        if (json) JsonOut(new { success = true, itemId, title, summary = summary.Trim() });
        return (true, summary.Trim());
    }
    catch (HttpRequestException ex)
    {
        ReportError("NETWORK_ERROR", Lang.T("Network error, cannot reach the LLM service"), Lang.T("Check your network connection"), ex.Message, json);
        return (false, null);
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return (false, null);
    }
}

// 单篇/整源摘要 CLI；支持 '12' 和 'feed:3'；--json 结构化输出（feed: 模式跳过 y/n 确认）
async Task SummaryCli(string arg, string dbPath, bool json = false)
{
    EnsureAiPrompted();

    // feed:N → 为该订阅源全部未摘要的 active 文章逐个生成
    if (arg.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
    {
        if (!int.TryParse(arg["feed:".Length..].Trim(), out int feedDisplay))
        {
            SetExit();
            Console.WriteLine(Lang.T("Bad format. Correct: {0}", "--summary feed:3"));
            return;
        }
        int feedReal = GetRealId(feedDisplay, dbPath);
        if (feedReal == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number {0} not found", feedDisplay)); return; }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title FROM Items WHERE Status = 'active' AND FeedId = @fid AND (Summary IS NULL OR Summary = '')";
        cmd.Parameters.AddWithValue("@fid", feedReal);
        using var r = cmd.ExecuteReader();
        var items = new List<(int Id, string Title)>();
        while (r.Read()) items.Add((r.GetInt32(0), r.GetString(1)));
        r.Close();

        if (items.Count == 0)
        {
            if (json) JsonOut(new { success = true, data = new { feed = feedDisplay, results = Array.Empty<object>(), ok = 0, fail = 0 } });
            else Console.WriteLine(Lang.T("All active articles of feed {0} already have summaries", feedDisplay));
            return;
        }
        if (!json)
        {
            Console.WriteLine(Lang.T("Will summarize {1} articles of feed {0}, confirm? (y/n)", feedDisplay, items.Count));
            if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }
        }

        int ok = 0, fail = 0;
        var results = new List<object>();
        foreach (var it in items)
        {
            var (o, s) = await SummarizeItem(dbPath, it.Id, json: false, quiet: json);
            if (json) results.Add(new { itemId = it.Id, title = it.Title, ok = o, summary = s });
            if (o) ok++; else fail++;
            if (!json) Console.WriteLine(Lang.T("  progress: {0}/{1}", ok + fail, items.Count));
        }
        if (json)
        {
            JsonOut(new { success = true, data = new { feed = feedDisplay, results, ok, fail } });
            if (fail > 0) SetExit();
        }
        else Console.WriteLine(Lang.T("Done: {0} OK, {1} failed", ok, fail));
        return;
    }

    // 单篇文章
    if (!int.TryParse(arg, out int sumId)) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --summary <article-number | feed:number>")); return; }
    await SummarizeItem(dbPath, sumId, json: json, quiet: json);
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

    if (items.Count == 0) { Console.WriteLine(Lang.T("All active articles already have summaries")); return; }
    Console.WriteLine(Lang.T("Will summarize {0} articles, confirm? (y/n)", items.Count));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }

    int ok = 0, fail = 0;
    foreach (var it in items)
    {
        if ((await SummarizeItem(dbPath, it.Id)).Ok) ok++; else fail++;
        Console.WriteLine(Lang.T("  progress: {0}/{1}", ok + fail, items.Count));
    }
    Console.WriteLine(Lang.T("Done: {0} OK, {1} failed", ok, fail));
}

// ══════════ 交互式配置向导 ══════════
void InitAiConfigInteractive(string dbPath)
{
    EnsureAiPrompted();
    Console.WriteLine(Lang.T("===== RSS Reader AI Setup Wizard ====="));
    Console.WriteLine(Lang.T("All services use the OpenAI-compatible format (Ollama / DeepSeek / OpenAI / any compatible service), endpoints and ports can be freely specified."));
    var cfg = LoadConfig(dbPath);

// --- Embedding ---
    Console.WriteLine(Lang.T("\n[1/3] Embedding service (for semantic search, OpenAI-compatible format):"));
    Console.Write(Lang.T("  endpoint (just http://host:port or https://domain, /v1 auto-appended) [current: {0}]: ", cfg.Embedding.ApiEndpoint));
    string embEndpoint = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(embEndpoint))
    {
        cfg.Embedding.ApiEndpoint = EnsureV1Endpoint(embEndpoint);
        Console.WriteLine(Lang.T("  → final endpoint: {0}", cfg.Embedding.ApiEndpoint));
    }

    Console.Write(Lang.T("  model (e.g. nomic-embed-text / bge-m3 / text-embedding-3-small) [current: {0}]: ", cfg.Embedding.Model));
    string embModel = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(embModel)) cfg.Embedding.Model = embModel;

    Console.Write(Lang.T("  vector dimensions (e.g. 768/1024/1536, leave empty to auto-detect) [current: {0}]: ", cfg.Embedding.Dimensions));
    if (int.TryParse(Console.ReadLine()?.Trim(), out int embDim) && embDim > 0)
        cfg.Embedding.Dimensions = embDim;

    Console.Write(Lang.T("  Embedding API key (skip with Enter for local Ollama; hidden input, stored in OS credentials) [current: {0}]: ",
        CredHas("embedding_api_key") ? Lang.T("set") : Lang.T("not set")));
    var embKey = ReadSecret();
    if (!string.IsNullOrEmpty(embKey)) CredSet("embedding_api_key", embKey);

    // --- LLM ---
    Console.WriteLine(Lang.T("\n[2/3] LLM service (for summaries, OpenAI-compatible format):"));
    Console.Write(Lang.T("  endpoint (just https://host[:port] or http://host:port, /v1 auto-appended) [current: {0}]: ", cfg.Llm.ApiEndpoint));
    string llmEndpoint = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(llmEndpoint))
    {
        cfg.Llm.ApiEndpoint = EnsureV1Endpoint(llmEndpoint);
        Console.WriteLine(Lang.T("  → final endpoint: {0}", cfg.Llm.ApiEndpoint));
    }

    Console.Write(Lang.T("  model (e.g. deepseek-chat / gpt-4o-mini / qwen2.5) [current: {0}]: ", cfg.Llm.Model));
    string llmModel = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(llmModel)) cfg.Llm.Model = llmModel;

    Console.Write(Lang.T("  LLM API key (hidden input, stored in OS credentials, Enter to skip): "));
    var llmKey = ReadSecret();
    if (!string.IsNullOrEmpty(llmKey)) CredSet("llm_api_key", llmKey);

    // --- 通用 ---
    Console.Write(Lang.T("\n[3/3] Default search similarity threshold (0-1, suggest 0.7; 0.5 for local bge-m3) [current: {0}]: ", cfg.Embedding.SearchThreshold));
    if (float.TryParse(Console.ReadLine()?.Trim(), out float thr)) cfg.Embedding.SearchThreshold = thr;

    SaveConfig(dbPath, cfg);
    Console.WriteLine(Lang.T("\nConfig saved. You can tweak ai_config.json for the model; API keys live in the OS credential store."));
    Console.WriteLine(Lang.T("Note: after changing the Embedding model, run sip --reindex to re-embed."));
}

// 读取密码（不回显）——跨平台简易实现。
// 非交互环境（stdin 被重定向，Console.ReadKey 会抛异常）降级为 ReadLine（回显，可接受）
string ReadSecret()
{
    var sb = new StringBuilder();
    try
    {
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
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        Console.WriteLine();  // 换行，避免与上一条提示挤在同一行
        return Console.ReadLine()?.Trim() ?? "";
    }
    Console.WriteLine();
    return sb.ToString();
}

// 查看配置
void ShowConfig(string dbPath)
{
    var cfg = LoadConfig(dbPath);
    Console.WriteLine(Lang.T("===== AI Config ====="));
    Console.WriteLine(Lang.T("Embedding: {0} / {1} ({2} dims)", cfg.Embedding.Provider, cfg.Embedding.Model, cfg.Embedding.Dimensions));
    Console.WriteLine(Lang.T("  endpoint: {0}", cfg.Embedding.ApiEndpoint));
    Console.WriteLine(Lang.T("  default search threshold: {0}", cfg.Embedding.SearchThreshold));
    Console.WriteLine(Lang.T("  API Key: {0}", CredHas("embedding_api_key") ? Lang.T("set") : Lang.T("not set")));
    Console.WriteLine(Lang.T("LLM: {0} / {1}", cfg.Llm.Provider, cfg.Llm.Model));
    Console.WriteLine(Lang.T("  endpoint: {0}", cfg.Llm.ApiEndpoint));
    Console.WriteLine(Lang.T("  API Key: {0}", CredHas("llm_api_key") ? Lang.T("set") : Lang.T("not set")));
    Console.WriteLine(Lang.T("Config file: {0}", ConfigPath(dbPath)));

    var warn = CheckDimensionMismatch(dbPath, cfg.Embedding);
    if (warn != null) Console.WriteLine($"\n{warn}");
}

// ══════════════════════════════════════════════════════════
// 以下为类型定义（必须位于所有顶级语句/局部函数之后）
// ══════════════════════════════════════════════════════════

// 进程内 AI 状态
static class AiState
{
    public static bool Warned = false;
    public static bool IgnoreAnnouncement = false;  // --ignoresafeannouncement：跳过安全横幅等多余输出
    public static int ExitCode = 0;  // CLI 退出码（脚本/AI 用 exit code 判断成败；0=成功，非零=失败）
}

// TUI 图片缓存（URL → 字节）
static class TuiImageCache
{
    public static readonly Dictionary<string, byte[]> Map = new();
}

// TUI Markdown 渲染过程状态（链接收集、图片宽度）
static class TuiMdState
{
    public static List<(string Text, string Url)> Links = new();
    public static int ImageWidth = 80;
}

// ══════════ 语言 / 本地化支持 ══════════
// 用法示例：T("Hello") / T("Total {0} articles", n)（源码原文 = 英文）
// 查找顺序：readwithhotsoup/languages/<代码>.json（首次启动已自动复制默认翻译，可直接编辑）
//        → 英文原文（原样返回）
// 语言文件格式（JSON 字典，键为英文原文，值为译文）：
//   例如 { "Hello": "你好", "Total {0} articles": "共有 {0} 篇" }
// 内置语言：zh-CN.json（英→中，默认界面）、en-US.json（英→英，即英文原文）
static class Lang
{
    public static string Code { get; private set; } = "zh-CN";

    private static readonly Dictionary<string, string> _custom = new();
    private static bool _loaded;

    public static void Init(string dataDir, string? requested)
    {
        string code = requested ?? "";
        if (string.IsNullOrEmpty(code))
            code = Environment.GetEnvironmentVariable("LANG") ?? "zh-CN";
        Code = code;

        // 语言文件只从数据目录 readwithhotsoup/languages/ 读取（首次启动已复制默认翻译）
        string path = Path.Combine(dataDir, "languages", code + ".json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _custom.Clear();
            Flatten(doc.RootElement, _custom);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"加载语言文件失败：{ex.Message}");
        }
        _loaded = true;
    }

    // 递归展平：兼容旧扁平文件（值全为字符串）与嵌套分组文件
    // 分组名（如 ui/start/help）只是组织分类，叶子用「英文源文本 → 译文」。
    static void Flatten(JsonElement el, Dictionary<string, string> target)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                break; // 由上层赋值，这里不处理
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        target[prop.Name] = prop.Value.GetString() ?? prop.Name;
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                        Flatten(prop.Value, target);
                }
                break;
        }
    }

    public static string T(string key)
    {
        if (_loaded && _custom.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            return v;
        return key;
    }

    public static string T(string key, params object[] args)
    {
        string s = T(key);
        try { return string.Format(s, args); }
        catch (FormatException) { return s; }
    }
}

// ══════════ AI 配置模型（ai_config.json，非敏感信息）═══════════
class AiConfig
{
    public EmbeddingCfg Embedding { get; set; } = new();
    public LlmCfg Llm { get; set; } = new();
    // 全文抓取安全策略：默认拦截私网地址（SSRF 防护），内网源可设 true 放行
    public bool AllowPrivateNet { get; set; } = false;
}

class EmbeddingCfg
{
    public string Provider { get; set; } = "openai-compatible";  // 备注字段（兼容服务名）
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimensions { get; set; } = 768;          // 向量维度
    public string ApiEndpoint { get; set; } = "http://localhost:11434/v1";  // 兼容服务端点
    public float SearchThreshold { get; set; } = 0.7f;  // 默认相似度阈值
}

class LlmCfg
{
    public string Provider { get; set; } = "openai-compatible";  // 备注字段（兼容服务名）
    public string Model { get; set; } = "deepseek-chat";
    public string ApiEndpoint { get; set; } = "https://api.deepseek.com/v1";
}

// TUI 树节点（订阅源或文章）
class TuiNode
{
    public bool IsFeed { get; set; }    // true=订阅源父节点，false=文章叶子
    public int FeedId { get; set; }     // 归属源 Id（文章节点也带，便于操作）
    public long ItemId { get; set; }    // 文章 Id（源节点为 0）
    public string Status { get; set; } = "active";  // 文章状态：active/archived/deleted
    public string Title { get; set; } = "";
    public string Guid { get; set; } = "";        // 文章 Guid（同一篇文章的多个版本共享）
    public bool HasHistory { get; set; }          // 是否有被改过的旧版本（有则标题右侧有 ✎ 标记）
    public int VersionCount { get; set; } = 1;    // 该文章共有几个版本
}

// 解析后的更新计划（见 TryParseSchedule）
class FeedSchedule
{
    public string Raw { get; set; } = "";       // 原始表达式
    public bool IsManual { get; set; }          // manual / 空：不自动更新
    public TimeSpan? Interval { get; set; }      // 间隔型：5m / 1h / 7d ...
    public bool IsDaily { get; set; }            // daily@HH:mm
    public int DailyHour { get; set; }
    public int DailyMinute { get; set; }
    public bool IsWeekly { get; set; }           // weekly@Ddd HH:mm
    public int WeeklyDay { get; set; }           // 0=周日 ... 6=周六
    public int WeeklyHour { get; set; }
    public int WeeklyMinute { get; set; }
}

// 一个到期的订阅源（GetDueFeeds 的结果）
class DueFeed
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? LastChecked { get; set; }
    public string Schedule { get; set; } = "";
}


// ══════════ 自绘侧栏（订阅源 + 文章，标题自动换行）═══════════
// Terminal.Gui 的 TreeView 每行只能画一行，长标题会被截断；
// 这里自绘一个轻量侧栏：来源可展开/折叠，标题按列宽换行（CJK 宽度感知），
// 提供与旧 TreeView 用法一致的接口（SelectedObject/SetFeeds/Toggle/...），
// 便于正文区、状态栏等其余代码无感替换。
class FeedManagerList : View
{
    public List<(int Id, string Line)> Rows { get; private set; } = new();
    public int Selected { get; private set; }
    public event EventHandler? SelectionChanged;

    public int SelectedId => Selected < Rows.Count ? Rows[Selected].Id : 0;

    public void SetRows(List<(int Id, string Line)> rows)
    {
        Rows = rows;
        Selected = Math.Clamp(Selected, 0, Math.Max(0, Rows.Count - 1));
        SetNeedsDraw();
    }

    public void MoveTo(int delta)
    {
        if (Rows.Count == 0) return;
        int before = Selected;
        Selected = Math.Clamp(Selected + delta, 0, Rows.Count - 1);
        if (Selected != before) { SelectionChanged?.Invoke(this, EventArgs.Empty); SetNeedsDraw(); }
    }

    // 方向键/PageUp/PageDown/Home/End 都由本视图单独处理，不被外层吞掉
    protected override bool OnKeyDown(Key key)
    {
        if (Rows.Count == 0) return false;
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
            case KeyCode.J: MoveTo(1); return true;
            case KeyCode.CursorUp:
            case KeyCode.K: MoveTo(-1); return true;
            case KeyCode.PageDown: MoveTo(Math.Max(1, Viewport.Height - 1)); return true;
            case KeyCode.PageUp: MoveTo(-Math.Max(1, Viewport.Height - 1)); return true;
            case KeyCode.Home: MoveTo(-Rows.Count); return true;
            case KeyCode.End: MoveTo(Rows.Count); return true;
            default: return false;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width, h = Viewport.Height;
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        int top = Math.Max(0, Selected - h / 2);   // 让选中行尽量居中
        for (int i = top; i < Math.Min(Rows.Count, top + h); i++)
        {
            int sy = i - top;
            bool sel = i == Selected;
            SetAttribute(sel
                ? GetAttributeForRole(HasFocus ? VisualRole.Focus : VisualRole.Active)
                : GetAttributeForRole(VisualRole.Normal));
            string line = (sel ? "> " : "  ") + Rows[i].Line;
            int cols = line.GetColumns();
            if (cols > w) line = line[..Math.Max(0, w - 1)] + "…";
            AddStr(0, sy, line);
        }
        return true;
    }
}

// ══════════ 自绘侧栏（订阅源 + 文章，标题自动换行）═══════════
// Terminal.Gui 的 TreeView 每行只能画一行，长标题会被截断；
// 这里自绘一个轻量侧栏：来源可展开/折叠，标题按列宽换行（CJK 宽度感知），
// 提供与旧 TreeView 用法一致的接口（SelectedObject/SetFeeds/Toggle/...），
// 便于正文区、状态栏等其余代码无感替换。
class SidebarRow
{
    public TuiNode Node { get; set; } = new();
    public bool IsFeed { get; set; }
    public bool IsLastChild { get; set; }   // 是否为父源下最后一篇文章（决定 └─ / ├─ 与续行竖线）
    public List<string> Lines { get; set; } = new();
}

class SidebarView : View
{
    private readonly Func<int, IEnumerable<TuiNode>> _childLoader;
    private readonly List<TuiNode> _roots = new();
    private readonly Dictionary<int, List<TuiNode>> _articles = new();
    private readonly HashSet<int> _expanded = new();
    private readonly List<SidebarRow> _rows = new();
    private int _sel;
    private int _scrollTop;      // 第一行可见的「换行后行号」
    private int _layoutWidth = -1;
    private bool _layoutDirty = true;

    public event EventHandler? SelectionChanged;

    public SidebarView(Func<int, IEnumerable<TuiNode>> childLoader)
    {
        _childLoader = childLoader;
        CanFocus = true;
    }

    public TuiNode? SelectedObject
    {
        get
        {
            if (_rows.Count == 0) return null;
            _sel = Math.Clamp(_sel, 0, _rows.Count - 1);
            return _rows[_sel].Node;
        }
    }

    public void SetFeeds(IEnumerable<TuiNode> feeds)
    {
        _roots.Clear();
        _roots.AddRange(feeds);
        _articles.Clear();
        foreach (var f in _roots)
            _articles[f.FeedId] = _childLoader(f.FeedId).ToList();
        // 保留用户已展开的源（默认折叠）；已被删除的源从展开集合里清掉
        var valid = new HashSet<int>(_roots.Select(f => f.FeedId));
        _expanded.RemoveWhere(id => !valid.Contains(id));
        _sel = 0;
        _scrollTop = 0;
        RebuildRows();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    public void ExpandAll()
    {
        foreach (var f in _roots) _expanded.Add(f.FeedId);
        RebuildRows();
        SetNeedsDraw();
    }

    public void Toggle(TuiNode n)
    {
        if (n == null || !n.IsFeed) return;
        if (!_expanded.Remove(n.FeedId)) _expanded.Add(n.FeedId);
        RebuildRows();
        int idx = _rows.FindIndex(r => ReferenceEquals(r.Node, n));
        if (idx >= 0) _sel = idx;
        EnsureSelectedVisible();
        SetNeedsDraw();
    }

    public void MovePageUp() => MoveSelection(-Math.Max(1, Viewport.Height));

    public void MovePageDown() => MoveSelection(Math.Max(1, Viewport.Height));

    public void MoveDown() => MoveSelection(1);

    public void MoveUp() => MoveSelection(-1);

    // 定位到指定文章（外部 CLI 全屏阅读按 W 进完整 TUI 时定位当前文章）；找不到返回 false
    public bool SelectItem(long itemId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].IsFeed && _rows[i].Node.ItemId == itemId)
            {
                _sel = i;
                OnSelectionChanged();
                EnsureSelectedVisible();
                return true;
            }
        }
        return false;
    }

    // 当前选中文章在全部文章中的位置（不含源行）；选中的是源时返回该源前最后一篇的位置
    public (int Current, int Total) ArticlePosition()
    {
        int cur = 0, total = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].IsFeed) continue;
            total++;
            if (i <= _sel) cur = total;
        }
        return (cur, total);
    }

    void RebuildRows()
    {
        _rows.Clear();
        foreach (var f in _roots)
        {
            _rows.Add(new SidebarRow { Node = f, IsFeed = true });
            if (_expanded.Contains(f.FeedId) && _articles.TryGetValue(f.FeedId, out var arts))
                for (int i = 0; i < arts.Count; i++)
                    _rows.Add(new SidebarRow { Node = arts[i], IsFeed = false, IsLastChild = i == arts.Count - 1 });
        }
        _sel = _rows.Count == 0 ? 0 : Math.Clamp(_sel, 0, _rows.Count - 1);
        _layoutDirty = true;
    }

    void EnsureLayout(int width)
    {
        if (!_layoutDirty && _layoutWidth == width) return;
        _layoutWidth = width;
        _layoutDirty = false;
        foreach (var row in _rows)
        {
            // 树状前缀：源用 ▼/▶ 折叠箭头，文章用 ├/└/│ 表示层级；
            // 前缀和续行缩进都按显示列宽算，保证换行的续行与首行文字对齐
            string prefix, continuation;
            if (row.IsFeed)
            {
                prefix = _expanded.Contains(row.Node.FeedId) ? "▼ " : "▶ ";
                continuation = "  ";
            }
            else
            {
                prefix = row.IsLastChild ? "  └─ " : "  ├─ ";
                continuation = "  │  ";
            }

            // 只对标题本体换行，再分别拼前缀（首行）与续行缩进（其余行）
            int prefixCols = prefix.GetColumns();
            var wrapped = Terminal.Gui.Text.TextFormatter.WordWrapText(row.Node.Title, Math.Max(1, width - prefixCols));
            if (wrapped.Count == 0) wrapped = new List<string> { "" };
            var lines = new List<string>(wrapped.Count);
            for (int i = 0; i < wrapped.Count; i++)
                lines.Add(i == 0 ? prefix + wrapped[i] : continuation + wrapped[i]);
            row.Lines = lines;
        }
        if (_scrollTop >= TotalLines() && TotalLines() > 0)
            _scrollTop = Math.Max(0, TotalLines() - 1);
    }

    int RowStartLine(int rowIndex)
    {
        int line = 0;
        for (int i = 0; i < rowIndex; i++) line += _rows[i].Lines.Count;
        return line;
    }

    int TotalLines()
    {
        int n = 0;
        foreach (var r in _rows) n += r.Lines.Count;
        return n;
    }

    int RowForLine(int line)
    {
        int l = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            l += _rows[i].Lines.Count;
            if (line < l) return i;
        }
        return _rows.Count - 1;
    }

    void OnSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;
        int target = Math.Clamp(_sel + delta, 0, _rows.Count - 1);
        if (target == _sel) return;
        _sel = target;
        OnSelectionChanged();
        EnsureSelectedVisible();
    }

    void EnsureSelectedVisible()
    {
        if (_rows.Count == 0) return;
        EnsureLayout(Viewport.Width);
        int h = Viewport.Height;
        int start = RowStartLine(_sel);
        int end = start + _rows[_sel].Lines.Count;
        if (start < _scrollTop) _scrollTop = start;
        else if (end > _scrollTop + h) _scrollTop = end - h;
        if (_scrollTop < 0) _scrollTop = 0;
    }

    protected override bool OnKeyDown(Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:
                MoveSelection(-1);
                return true;
            case KeyCode.CursorDown:
                MoveSelection(1);
                return true;
            case KeyCode.Home:
                MoveSelection(-_rows.Count);
                return true;
            case KeyCode.End:
                MoveSelection(_rows.Count);
                return true;
        }
        return false;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags is MouseFlags.LeftButtonPressed or MouseFlags.LeftButtonClicked)
        {
            if (mouse.Position.HasValue)
            {
                EnsureLayout(Viewport.Width);
                int row = RowForLine(mouse.Position.Value.Y + _scrollTop);
                if (row >= 0 && row < _rows.Count)
                {
                    _sel = row;
                    OnSelectionChanged();
                    EnsureSelectedVisible();
                }
            }
            SetFocus();
            return true;
        }
        return false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width;
        int h = Viewport.Height;
        EnsureLayout(w);
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        int line = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool selected = i == _sel;
            for (int li = 0; li < row.Lines.Count; li++)
            {
                int sy = line + li - _scrollTop;
                if (sy >= 0 && sy < h)
                {
                    Terminal.Gui.Drawing.Attribute attr = selected
                        ? (HasFocus ? GetAttributeForRole(VisualRole.Focus) : GetAttributeForRole(VisualRole.Active))
                        : (row.IsFeed ? GetAttributeForRole(VisualRole.HotNormal) : GetAttributeForRole(VisualRole.Normal));
                    SetAttribute(attr);
                    AddStr(0, sy, new string(' ', w));
                    AddStr(0, sy, row.Lines[li]);
                }
            }
            line += row.Lines.Count;
        }
        return true;
    }
}

// 开始界面的自绘视图：整块居中排版
class StartScreenView : View
{
    public string[] Lines { get; set; } = Array.Empty<string>();

    public StartScreenView()
    {
        SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
            HotNormal = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black),
            Focus = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkBlue),
            HotFocus = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.DarkBlue),
            Highlight = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan)
        });
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width;
        int h = Viewport.Height;
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        if (Lines.Length == 0) return true;

        int totalW = 0;
        foreach (var l in Lines)
        {
            int c = l.GetColumns();
            if (c > totalW) totalW = c;
        }
        int x0 = Math.Max(0, (w - totalW) / 2);
        int y0 = Math.Max(0, (h - Lines.Length) / 2);
        for (int i = 0; i < Lines.Length; i++)
        {
            int row = y0 + i;
            if (row < 0 || row >= h) continue;
            SetAttribute(GetAttributeForRole(i == 0 ? VisualRole.HotNormal : VisualRole.Normal));
            AddStr(x0, row, Lines[i]);
        }
        return true;
    }
}

// 搜索结果条目
class SearchHit
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
    public int FeedId { get; set; }
    public float Score { get; set; }
}

// 全文搜索结果条目
class FulltextVecEntry { public int ItemId { get; set; } public int FeedId { get; set; } public int ModelId { get; set; } public float[] Vector { get; set; } = Array.Empty<float>(); }

// 来源健康记录（feed_health.json）
class FeedHealthEntry { public int FailCount { get; set; } public string LastError { get; set; } = ""; public string LastOkAt { get; set; } = ""; }

// 文章标记信号（article_signals.json）
class SignalEntry
{
    public bool UserLike { get; set; }
    public bool AiLike { get; set; }
    public string AiReason { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

// Sip Today 条目
class TodayItem
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Reason { get; set; } = "";   // 为什么出现在今日（新增/更新/AI关注/你收藏过…）
    public double Minutes { get; set; }        // 预估阅读时长
    public int Score { get; set; }
}

// ══════════ Telemetry 服务（本地事实层）══════════
// 硬约束：默认关闭 / 首次征得同意 / 仅本地 / 绝不自动上传 / 可查看-关闭-删除-导出 /
//         记录事实不造画像 / 遥测损坏绝不影响 rss.db 与阅读（独立库 + 完整性检查 + 降级）
// 性能：事件白名单 + 内存缓冲批量写（50 条或 5 秒）+ 容量上限（10 万条留 8 万）
class TelemetryEvent
{
    public long Id { get; set; }
    public string Timestamp { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Type { get; set; } = "";
    public int? ArticleId { get; set; }
    public int? SourceId { get; set; }
    public int? VersionId { get; set; }
    public string? Surface { get; set; }
    public int? Position { get; set; }
    public string DataJson { get; set; } = "";
}

static class TelemetryService
{
    static string _dir = "";
    static string DbPath => Path.Combine(_dir, "telemetry.db");
    static string ConsentPath => Path.Combine(_dir, "telemetry_consent.json");
    static string CheckpointPath => Path.Combine(_dir, "telemetry_checkpoint.json");

    static string _consent = "unset";      // unset / enabled / disabled
    static bool _failed = false;           // 会话内连续写失败 → 降级停用
    static int _consecFails = 0;
    static string _sessionId = "";
    static readonly object _lock = new();
    static readonly List<TelemetryEvent> _buffer = new();
    static System.Threading.Timer? _flushTimer;

    public static bool IsEnabled => _consent == "enabled" && !_failed;
    public static string Consent => _consent;

    public static void Init(string dataDir)
    {
        _dir = dataDir;
        try { Directory.CreateDirectory(_dir); } catch { }
        LoadConsent();
        CheckIntegrity();
        _sessionId = Guid.NewGuid().ToString("N");
        try { _flushTimer = new System.Threading.Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)); } catch { }
    }

    public static void Shutdown()
    {
        try { _flushTimer?.Dispose(); } catch { }
        Flush();
        WriteCheckpoint("ok");
    }

    static void LoadConsent()
    {
        try
        {
            if (File.Exists(ConsentPath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConsentPath));
                if (d != null && d.TryGetValue("state", out var s) && (s == "enabled" || s == "disabled"))
                    _consent = s;
            }
        }
        catch { _consent = "unset"; }
    }

    public static void SetConsent(string state)
    {
        _consent = state == "enabled" ? "enabled" : "disabled";
        try
        {
            File.WriteAllText(ConsentPath, JsonSerializer.Serialize(new
            {
                state = _consent,
                updatedAt = DateTime.Now.ToString("O")
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { }
    }

    // 完整性检查：魔数不符/打开失败/quick_check 非 ok → 改名保留现场 → 重建新库；绝不崩溃、绝不动 rss.db。
    // 并发启动/写入时 quick_check 可能读到瞬时中间状态（误报损坏）：
    //   · busy/locked 类错误与瞬时非 ok 结果 → 重试，不算损坏
    //   · 连续重试仍失败 → 改名失败（文件被其他进程占用）时静默跳过，下次启动再试；不吓人、不动数据
    static void CheckIntegrity()
    {
        try
        {
            if (!File.Exists(DbPath)) { CreateSchema(); WriteCheckpoint("created"); return; }
            bool ok = IsSqliteFile(DbPath);
            if (ok) ok = TryQuickCheckOk();
            if (ok)
            {
                CreateSchema();   // 幂等：确保表结构与 WAL 模式（旧库首次启动即完成 WAL 迁移）
                WriteCheckpoint("ok");
                return;
            }
            string corrupt = DbPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            try
            {
                File.Move(DbPath, corrupt);
                CreateSchema();
                WriteCheckpoint("recreated");
                Console.Error.WriteLine("telemetry.db 完整性检查失败，已保留现场并重建：" + corrupt);
            }
            catch { /* 文件仍被占用（并发写入）：本次跳过，下次启动再试 */ }
        }
        catch { /* 完整性检查失败不阻断启动 */ }
    }

    // quick_check：带 busy_timeout；busy/locked 或瞬时非 ok 结果重试，连续多次失败才算损坏
    static bool TryQuickCheckOk()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                var c = conn.CreateCommand();
                c.CommandText = "PRAGMA busy_timeout = 2000;";
                c.ExecuteNonQuery();
                c.CommandText = "PRAGMA quick_check";
                if (c.ExecuteScalar()?.ToString() == "ok") return true;
            }
            catch (SqliteException ex) when (IsBusyCode(ex.SqliteErrorCode)) { /* 锁冲突：重试 */ }
            catch { return false; }   // 真损坏/打开失败
        }
        return false;
    }

    // SQLITE_BUSY / SQLITE_LOCKED 系列错误码（含共享缓存/恢复/快照/超时变体）
    internal static bool IsBusyCode(int rc) => rc is 5 or 6 or 261 or 262 or 283 or 284;

    // 校验 SQLite 文件头魔数（"SQLite format 3\0"）
    internal static bool IsSqliteFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            Span<byte> head = stackalloc byte[16];
            return fs.Read(head) >= 16 && head.SequenceEqual("SQLite format 3\0"u8);
        }
        catch { return false; }
    }

    static void CreateSchema()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var c = conn.CreateCommand();
        // WAL：并发启动/写入时读者永远看到一致快照，从根上避免瞬时损坏误报
        c.CommandText = "PRAGMA journal_mode = WAL;";
        c.ExecuteNonQuery();
        c.CommandText = @"
            CREATE TABLE IF NOT EXISTS telemetry_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                session_id TEXT NOT NULL,
                type TEXT NOT NULL,
                article_id INTEGER,
                source_id INTEGER,
                version_id INTEGER,
                surface TEXT,
                position INTEGER,
                data_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_telemetry_type ON telemetry_events(type);
            CREATE INDEX IF NOT EXISTS idx_telemetry_ts ON telemetry_events(timestamp);
        ";
        c.ExecuteNonQuery();
    }

    static void WriteCheckpoint(string status)
    {
        try
        {
            long size = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
            File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(new
            {
                status,
                dbFile = "telemetry.db",
                size,
                lastOkAt = DateTime.Now.ToString("O")
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // 事件入队（内存缓冲；只接受低频事件，滚动/按键不在此列）
    public static void Record(string type, int? articleId = null, int? sourceId = null, int? versionId = null,
                              string? surface = null, int? position = null, object? data = null)
    {
        if (!IsEnabled) return;
        lock (_lock)
        {
            if (_buffer.Count >= 500) _buffer.Clear();   // 极端积压兜底，防止内存膨胀
            _buffer.Add(new TelemetryEvent
            {
                Timestamp = DateTime.Now.ToString("O"),
                SessionId = _sessionId,
                Type = type,
                ArticleId = articleId,
                SourceId = sourceId,
                VersionId = versionId,
                Surface = surface,
                Position = position,
                DataJson = data == null ? "" : JsonSerializer.Serialize(data)
            });
        }
        if (_buffer.Count >= 50) Flush();
    }

    public static void RecordAiCall(string operation, string provider, string model, bool success, long durationMs)
        => Record("ai_call", data: new { operation, provider, model, success, durationMs });

    // 批量落库（best-effort；连续失败 → 本会话降级停用）
    static void Flush()
    {
        if (!IsEnabled) return;
        List<TelemetryEvent> batch;
        lock (_lock)
        {
            if (_buffer.Count == 0) return;
            batch = _buffer.ToList();
            _buffer.Clear();
        }
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var tx = conn.BeginTransaction();
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO telemetry_events
                (timestamp, session_id, type, article_id, source_id, version_id, surface, position, data_json)
                VALUES (@ts, @sid, @type, @aid, @fsid, @vid, @sf, @pos, @dj)";
            cmd.Parameters.Add("@ts", SqliteType.Text);
            cmd.Parameters.Add("@sid", SqliteType.Text);
            cmd.Parameters.Add("@type", SqliteType.Text);
            cmd.Parameters.Add("@aid", SqliteType.Integer);
            cmd.Parameters.Add("@fsid", SqliteType.Integer);
            cmd.Parameters.Add("@vid", SqliteType.Integer);
            cmd.Parameters.Add("@sf", SqliteType.Text);
            cmd.Parameters.Add("@pos", SqliteType.Integer);
            cmd.Parameters.Add("@dj", SqliteType.Text);
            foreach (var e in batch)
            {
                cmd.Parameters["@ts"].Value = e.Timestamp;
                cmd.Parameters["@sid"].Value = e.SessionId;
                cmd.Parameters["@type"].Value = e.Type;
                cmd.Parameters["@aid"].Value = (object?)e.ArticleId ?? DBNull.Value;
                cmd.Parameters["@fsid"].Value = (object?)e.SourceId ?? DBNull.Value;
                cmd.Parameters["@vid"].Value = (object?)e.VersionId ?? DBNull.Value;
                cmd.Parameters["@sf"].Value = (object?)e.Surface ?? DBNull.Value;
                cmd.Parameters["@pos"].Value = (object?)e.Position ?? DBNull.Value;
                cmd.Parameters["@dj"].Value = e.DataJson;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _consecFails = 0;
            CapIfNeeded(conn);
        }
        catch
        {
            _consecFails++;
            if (_consecFails >= 3) _failed = true;   // 连续 3 次失败 → 本会话停用，阅读不受影响
        }
    }

    // 容量上限：超过 10 万条 → 只保留最新 8 万
    static void CapIfNeeded(SqliteConnection conn)
    {
        try
        {
            var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM telemetry_events";
            long n = (long)c.ExecuteScalar()!;
            if (n > 100_000)
            {
                c.CommandText = "DELETE FROM telemetry_events WHERE id < (SELECT id FROM telemetry_events ORDER BY id DESC LIMIT 1 OFFSET 80000)";
                c.ExecuteNonQuery();
            }
        }
        catch { }
    }

    public static (long Count, string? First, string? Last) Stats()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*), MIN(timestamp), MAX(timestamp) FROM telemetry_events";
            using var r = c.ExecuteReader();
            if (r.Read()) return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        catch { }
        return (0, null, null);
    }

    public static void Clear()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "DELETE FROM telemetry_events";
            c.ExecuteNonQuery();
        }
        catch { }
    }

    public static List<TelemetryEvent> AllEvents(int limit = 100000)
    {
        var list = new List<TelemetryEvent>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT id, timestamp, session_id, type, article_id, source_id, version_id, surface, position, data_json FROM telemetry_events ORDER BY id DESC LIMIT @n";
            c.Parameters.AddWithValue("@n", limit);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new TelemetryEvent
                {
                    Id = r.GetInt64(0),
                    Timestamp = r.GetString(1),
                    SessionId = r.GetString(2),
                    Type = r.GetString(3),
                    ArticleId = r.IsDBNull(4) ? null : r.GetInt32(4),
                    SourceId = r.IsDBNull(5) ? null : r.GetInt32(5),
                    VersionId = r.IsDBNull(6) ? null : r.GetInt32(6),
                    Surface = r.IsDBNull(7) ? null : r.GetString(7),
                    Position = r.IsDBNull(8) ? null : r.GetInt32(8),
                    DataJson = r.IsDBNull(9) ? "" : r.GetString(9)
                });
        }
        catch { }
        return list;
    }
}

// 全文搜索结果条目
class GrepSnippetResult
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
    public int Count { get; set; }
    public List<string> Snippets { get; set; } = new();
    public int TotalSnippets { get; set; }
    public string Quality { get; set; } = "";
}

// 全文搜索结果条目
class GrepHit
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";   // 已转纯文本的摘要（TUI 渲染用）
    public string Content { get; set; } = "";        // 原始正文（HTML，CLI 片段模式用）
    public string Summary { get; set; } = "";        // AI 摘要（CLI 片段模式拼进 haystack）
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
}

// ══════════ 自定义异常 ══════════
#pragma warning disable SYSLIB0051
[Serializable]
public class AiException : Exception
{
    public string Code { get; private set; } = string.Empty;
    public string? Suggestion { get; private set; }
    public string? Details { get; private set; }

    public AiException() : base() { }

    public AiException(string message) : base(message) { }

    public AiException(string code, string message, string? suggestion = null, string? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Suggestion = suggestion;
        Details = details;
    }

    protected AiException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        Code = info.GetString(nameof(Code)) ?? string.Empty;
        Suggestion = info.GetString(nameof(Suggestion));
        Details = info.GetString(nameof(Details));
    }

    [Obsolete]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(Code), Code);
        info.AddValue(nameof(Suggestion), Suggestion);
        info.AddValue(nameof(Details), Details);
    }

    public override string ToString()
    {
        return $"错误码：{Code}，消息：{Message}，建议：{Suggestion}，详情：{Details}\n{base.ToString()}";
    }
}
#pragma warning restore SYSLIB0051
