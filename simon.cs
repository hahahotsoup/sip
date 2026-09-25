// ===== 孟思琳(simon)——安全守护(从 sipcore.cs 拆出)=====
// 与 sipcore.cs 同属 partial class Program(入口文件顶层语句生成的类),
// 可自由调用 sipcore.cs 的顶层函数与基础设施;入口的 RunCli 经
// SimonCheckBlock/SimonCli 与此文件交互。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public partial class Program
{

// ══════════ 孟思琳(simon)——安全守护(默认开启,无法关闭,只能调节挡位)══════════
// 挡位:1=基础(完整性自愈+基础防护,现状能力) 2=严格(非交互禁破坏性写)
//       3=极致(非交互禁全部写)
// 原则:挡位只能 1/2/3(无 0=不可关闭);降挡必须真实交互终端(防 AI/脚本把守护调弱)

class SimonEvent
{
    public string Ts { get; set; } = "";
    public string Type { get; set; } = "";     // repair_db / blocked_cmd / level_change
    public int? Level { get; set; }
    public string Detail { get; set; } = "";
}

static string SimonEventsPath() => Path.Combine(dataDir, "simon_events.json");

static void SimonRecord(string type, string detail, int? level = null)
{
    try
    {
        var evs = new List<SimonEvent>();
        if (File.Exists(SimonEventsPath()))
            evs = JsonSerializer.Deserialize<List<SimonEvent>>(File.ReadAllText(SimonEventsPath())) ?? new();
        evs.Add(new SimonEvent { Ts = DateTime.Now.ToString("O"), Type = type, Level = level, Detail = detail });
        if (evs.Count > 200) evs.RemoveRange(0, evs.Count - 200);   // 只留最近 200 条
        File.WriteAllText(SimonEventsPath(), JsonSerializer.Serialize(evs,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

static List<SimonEvent> SimonLoadEvents()
{
    try
    {
        if (File.Exists(SimonEventsPath()))
            return JsonSerializer.Deserialize<List<SimonEvent>>(File.ReadAllText(SimonEventsPath())) ?? new();
    }
    catch { }
    return new();
}

// 凭据作用域:按数据目录哈希隔离——同一用户的多个 sip 副本(不同目录)互不影响;
// 同目录的不同 exe 版本(升级)共享同一作用域。环境变量可覆盖(自动化测试用)。
static string SimonScopeHash()
{
    try
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir)))[..12];
    }
    catch { return "default"; }
}

// 挡位存储:权威值在系统凭据库(文件编辑无法降挡——孟思琳不能被改 JSON 绕过);
// sip_settings.json 仅作缓存/兼容(凭据库缺失时回退)。升降挡只经程序接口。
static string SimonLevelKey()
{
    string? t = Environment.GetEnvironmentVariable("SIP_SIMON_KEY_NAME");
    return string.IsNullOrEmpty(t) ? "simon_level_" + SimonScopeHash() : t;
}

static int SimonLevelGet()
{
    // 环境变量换掉的只是**命名空间**（测试靠它隔离），但「换个名字」绝不能成为降挡通道：
    // 只读 namespaced 的值、把真实作用域丢掉，等于设一个 SIP_SIMON_KEY_NAME 就让凭据库里的
    // 挡位失效、退回 settings 里那个可能过期的低挡位。所以两个作用域都读，取**更高**的一档
    // —— 环境变量只能收紧，不能放宽（与 Agent 门同一套规矩）。
    int cred = Math.Max(CredLevelFrom(SimonLevelKey() + "_level"),
                        CredLevelFrom("simon_level_" + SimonScopeHash() + "_level"));
    return cred > 0 ? cred : Math.Clamp(LoadSettings().SimonLevel, 1, 3);   // 兼容旧配置/凭据库缺失
}

// 读不到或不是数字 → 0，表示「这个作用域没有权威值」，不参与取最大
static int CredLevelFrom(string key)
{
    string? k = CredGet(key);
    return !string.IsNullOrEmpty(k) && int.TryParse(k, out int lvl) ? Math.Clamp(lvl, 1, 3) : 0;
}

// 返回「权威值（系统凭据库）是否写入成功」。
// 凭据库才是权威:settings 只是缓存/兜底显示,它写失败不影响挡位是否生效。
// 凭据库写失败**不能**回退去信 settings —— 那样「改 JSON 无法降挡」这条不变量就破了。
// 也必须吞掉异常:之前 CredSet 一抛(如 Windows 凭据库返回 ERROR_NOT_ENOUGH_MEMORY)
// 整个进程就未捕获崩溃(2026-09-12 实测)。
static bool SimonLevelSet(int lvl)
{
    int v = Math.Clamp(lvl, 1, 3);
    bool authoritative = true;
    try { CredSet(SimonLevelKey() + "_level", v.ToString()); }
    catch { authoritative = false; }
    try
    {
        var s = LoadSettings();
        s.SimonLevel = v;   // 文件缓存同步(供凭据库缺失场景兜底显示)
        SaveSettings(s);
    }
    catch { /* 缓存写失败不报错:权威值已生效,下次启动仍以凭据库为准 */ }
    return authoritative;
}

static int CurrentSimonLevel() => SimonLevelGet();

// 挡位 2 的写命令 = 一切非只读命令(用户语义:2 级起不允许写入数据库)
// 挡位 3 = 所有 CLI 调用(唯一例外 simon status,见 SimonCheckBlock)

// 挡位 3 的读命令白名单(挡位 2 用:非只读即写,一律拦截)
static bool SimonIsReadOnly(string cmd, string sub)
    => cmd is "-l" or "--list" or "--show" or "--content" or "--versions" or "--history"
          or "--diff" or "--grep" or "--search" or "--today" or "--feed-info" or "--export-opml"
          or "--help" or "-h" or "--version" or "--insights" or "--insights-interval" or "simon"
          // 界面入口不是"操作"：`sip tui` / `sip pic` 打开的是**真人终端界面**本身，
          // 而挡位 3 的提示语就是"只允许通过 TUI 使用" —— 把自己的入口拦掉就等于死胡同。
          // （pic 在分发里排在挡位检查之前，这里列出来是为了语义完整，不是必需。）
          or "tui" or "--tui" or "pic"
       || (cmd == "telemetry" && sub is "status" or "show")
       || (cmd == "db" && sub is "" or "status" or "show")   // 主库查询是只读；set/merge 是写（挡位 2 起拦）
       || (cmd == "--dedup" && sub is "list" or "scan")
       || (cmd == "--policy" && sub == "list");

// 统一拦截入口:返回被拦截的原因;null=放行。
// 用户语义:挡位 2 = CLI 写操作一律拒绝;挡位 3 = CLI 所有调用一律拒绝。
// CLI 本身(含交互终端)是不可信通道;TUI 命令栏不经此检查,永远是真人通道。
// 唯一例外:simon status(守护状态查询)在任意挡位放行,否则挡位 3 下无法查看守护状态。
static string? SimonCheckBlock(string cmd, string[] args)
{
    int level = CurrentSimonLevel();
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    // --start / webpass / aikey：本机 Web 与凭据管理，不依赖 TUI
    // --agentok/off/status：Agent 门自身的管理命令，各自由更合适的门把关
    bool isCredAdmin = cmd is "webpass" or "aikey" or "--start"
                       or "--agentok" or "--agentoff" or "--agentstatus";
    // simon level 必须放行：否则挡位 3 就成了死胡同 —— 唯一降挡通道是已弃用的 TUI。
    // 放宽保护的方向改由 SimonCli 内的 SensitiveActionAllowed(真 TTY + Web 口令)把关，
    // 那比「入口一刀切」更强，也不再依赖任何界面。
    bool isSimonStatus = cmd == "simon" && (sub is "" or "status" or "show" or "list" or "--json" or "level");
    if (isCredAdmin || isSimonStatus)
        return null;
    if (level >= 3)
        return Lang.T("挡位 3(极致):CLI 调用已全部拒绝({0});只允许通过 TUI 使用。", cmd);
    if (level >= 2 && !SimonIsReadOnly(cmd, sub))
        return Lang.T("挡位 {0}(严格):CLI 写操作已拒绝({1});只读命令可用,或到 TUI 操作。", level, cmd);
    return null;
}

// CLI:sip simon status [--json] | level <1|2|3>
// 升档（收紧保护）任意通道放行 —— Agent 发现异常时要能立刻收紧，收紧不需要授权。
// 降档（放宽保护）走人工通道：真 TTY + Web 口令，见 SensitiveActionAllowed。
static void SimonCli(string[] args, string dbPath, string? passwordFromUi = null)
{
    bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
    if (sub == "level")
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int lvl) || lvl is < 1 or > 3)
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "USAGE", message = Lang.T("Usage: sip simon level <1|2|3>  (1=基础 2=严格 3=极致;无法关闭)") } });
            else Console.WriteLine(Lang.T("Usage: sip simon level <1|2|3>  (1=基础 2=严格 3=极致;无法关闭)"));
            return;
        }
        int cur = SimonLevelGet();
        if (lvl < cur)
        {
            // 降挡 = 放宽保护 → 人工通道。
            // --json 通常意味着非交互脚本，直接给结构化拒绝，不把提示文字混进 JSON 流
            if (json)
            {
                SetExit();
                JsonOut(new { success = false, error = new { code = "SIMON_LOCKED", level = cur, message = Lang.T("Lowering the Simon level needs a real terminal and the Web password (drop --json to be prompted).") } });
                return;
            }
            if (!SensitiveActionAllowed(Lang.T("lower the Simon level"), Lang.T("lower level"), passwordFromUi))
                return;
        }
        if (!SimonLevelSet(lvl))
        {
            // 凭据库拒绝写入 → 挡位并未改变。必须报错，不能让它看起来成功了。
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "LEVEL_SAVE_FAILED", level = cur, message = Lang.T("The OS credential store rejected the write; the level was NOT changed.") } });
            else Console.WriteLine(Lang.T("系统凭据库写入失败，挡位未改变。"));
            return;
        }
        SimonRecord("level_change", $"{cur} → {lvl}", lvl);
        if (json) JsonOut(new { success = true, data = new { level = lvl } });
        else Console.WriteLine(Lang.T("孟思琳(simon) 守护挡位: {0} → {1}", cur, lvl));
        return;
    }
    if (sub is "status" or "show" or "list")
    {
        int level = CurrentSimonLevel();
        var evs = SimonLoadEvents();
        var repairs = evs.Where(e => e.Type == "repair_db").ToList();
        var blocks = evs.Where(e => e.Type == "blocked_cmd").ToList();
        var bk = BackupSummary();
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    name = "孟思琳(simon)",
                    level,
                    canDisable = false,
                    repairs = repairs.Count,
                    blocked = blocks.Count,
                    backups = bk.Count,
                    backupLatest = bk.Latest,
                    recent = evs.TakeLast(10).Select(e => new { ts = e.Ts, type = e.Type, level = e.Level, detail = e.Detail })
                }
            });
            return;
        }
        string levelName = level switch { 2 => Lang.T("严格"), 3 => Lang.T("极致"), _ => Lang.T("基础") };
        Console.WriteLine(Lang.T("孟思琳(simon) 安全守护"));
        Console.WriteLine(Lang.T("挡位: {0}({1})——默认开启,无法关闭,只能调节", level, levelName));
        Console.WriteLine(Lang.T("永远作为此软件的最后一道安全防线。"));
        Console.WriteLine(Lang.T("数据库修复: {0} 次", repairs.Count));
        foreach (var e in repairs.TakeLast(3))
            Console.WriteLine(Lang.T("  · {0} {1}", TryParseIso(e.Ts) is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : e.Ts, e.Detail));
        Console.WriteLine(Lang.T("已拦截非交互调用: {0} 次", blocks.Count));
        foreach (var e in blocks.TakeLast(3))
            Console.WriteLine(Lang.T("  · {0} {1}", TryParseIso(e.Ts) is DateTime dt2 ? dt2.ToString("yyyy-MM-dd HH:mm") : e.Ts, e.Detail));
        Console.WriteLine(bk.Count == 0
            ? Lang.T("本地备份: 还没有(每天首次启动自动留一份)")
            : Lang.T("本地备份: {0} 份 · {1} MB · 最新 {2}", bk.Count, bk.Bytes / (1024 * 1024), bk.Latest));
        Console.WriteLine(AgentModeOn()
            ? Lang.T("Agent 外部调用: 已开启(程序可调用 sip)")
            : Lang.T("Agent 外部调用: 已关闭(默认;只有真实终端可调用)"));
        return;
    }
    SetExit(); Console.WriteLine(Lang.T("Usage: sip simon status [--json] | level <1|2|3>"));
}

// ── 统一打开数据库 ────────────────────────────────
// 挡位 3 的 SQLCipher 加密已移除:它付了密钥管理与丢库风险的全部成本,
// 却只防「副本被拷走」一种场景,挡不住以本机用户身份运行的其他程序。
// 旧版加密过的库由 CheckMainDbIntegrity 的启动护栏明确报错。
static SqliteConnection OpenDb(string dbPath)
{
    var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    return conn;
}

// ══════════ Agent / 外部调用门（默认关闭）══════════
// 目的：默认不让「程序」调用 sip 读你的库 —— OpenClaw 那类事故的共同点是默认开放。
//
// 与孟思琳挡位是**两条独立的轴**，别混：
//   挡位    = 命令级策略（读 / 写 / 全部），连你自己的终端一起拦
//   Agent 门 = 调用者级策略（人 / 程序），只拦非交互调用，你自己的终端不受影响
//
// 判据是「有没有真交互终端」，不是「命令是什么」：
//   真终端（stdin/stdout 都没被重定向）→ 人 → 放行
//   管道 / 重定向 / 无控制台          → 程序 → 默认拒绝
// 注意副作用：`sip -l | grep x` 这种自己也管道了输出的用法，会被算作程序调用。
//
// 状态存**系统凭据库**：与挡位同理，改 sip_settings.json 绕不过去。
// 值用显式的 "on"/"off" 而不是空串 —— CredGet 对空串返回 ""，不能靠它判空。
//
// **没有兜底文件**（2026-09-12 删除）。曾有一个 agent_mode.json 兜底，理由是
// 「凭据库写不进去时开关永远打不开」。它错在两处：
//   ① 触发条件写错了：代码实际是「凭据库没有值就信文件」，而「没有值」正是
//      **全新安装的默认状态**。于是默认状态下任何程序写一个 JSON 文件就能把门打开，
//      与「只有 --agentok 能开」直接矛盾。一道能被它拦的对象自己打开的门只是装饰。
//   ② 「无法自救」的代价被高估了：这道门拦的只是**非交互调用**，人坐在真实终端前
//      从不受它影响（见 AgentModeBlock）。凭据库坏掉时人照样能用 CLI/TUI，
//      丢的只是「程序可以调用 sip」这一项授权 —— 属于收紧方向的失败，
//      应当**响亮地失败**，而不是悄悄换一条更弱的路放行。
// 结论：凭据库是唯一权威，读不到值 = 关（fail closed）。
// 测试要开门时，由测试宿主往**自己那个隔离数据目录**的凭据作用域写 on 并负责删除 ——
// 走的是与 --agentok 完全相同的存储层，产品本身不留任何旁路。

static string AgentModeKey()
{
    // 与 SimonLevelKey 同一套作用域规则（环境变量仅测试用，见其注释）。
    // 注意：这里不会因为「作用域被换掉」而放宽 —— 新作用域里没有值就是**关**。
    string? t = Environment.GetEnvironmentVariable("SIP_SIMON_KEY_NAME");
    return string.IsNullOrEmpty(t) ? "agent_ok_" + SimonScopeHash() : "agent_ok_" + t;
}

// 读：唯一权威是系统凭据库。没有值 = 关。
static bool AgentModeOn()
{
    try { return CredGet(AgentModeKey()) == "on"; } catch { return false; }
}

// 写：只写凭据库。返回是否成功 —— 失败必须让调用方看见，绝不静默降级到文件。
static bool SetAgentMode(bool on)
{
    try { CredSet(AgentModeKey(), on ? "on" : "off"); return true; } catch { return false; }
}

static void AgentModeSaveFailed()
{
    SetExit();
    Console.WriteLine(Lang.T("Could not persist the switch (the OS credential store rejected the write); agent access was NOT changed."));
}

// 纯信息与自管理命令不受这道门影响：否则一旦打开就再也关不掉，也没必要拦。
// 另外两条**收紧方向**的也放行 —— 收紧不降低安全性，不该先要求「程序可调用」的授权：
//   · simon status：程序可以查守护是否还在（监控用，不含库数据）
//   · simon level N 且 N >= 当前挡位：Agent 发现异常时要能立刻升档保护
static bool AgentGateExempt(string cmd, string[] args)
{
    if (cmd is "--agentok" or "--agentoff" or "--agentstatus" or "--help" or "-h" or "--version"
        or "--start" or "webpass" or "aikey")   // 后三者有自己的门
        return true;
    if (cmd != "simon") return false;
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    if (sub is "" or "status" or "show" or "list") return true;
    if (sub == "level" && args.Length > 2 && int.TryParse(args[2], out int lvl))
        return lvl >= CurrentSimonLevel();      // 升档放行；降档仍要走人工通道
    return false;
}

// 返回被拦截的原因；null = 放行
static string? AgentModeBlock(string cmd, string[] args)
{
    if (AgentModeOn()) return null;            // 已显式开启
    if (HasInteractiveConsole()) return null;  // 人坐在终端前
    if (AgentGateExempt(cmd, args)) return null;
    return Lang.T("Agent access is OFF by default, so programs cannot read your library. Your own terminal is unaffected. To allow programs: run `sip --agentok` in a real terminal. (Note: piping sip's own output also counts as a program call.)");
}

// CLI：sip --agentok | --agentoff | --agentstatus
static void AgentModeCli(string sub)
{
    if (sub == "--agentstatus")
    {
        Console.WriteLine(AgentModeOn()
            ? Lang.T("Agent access: ON — programs may call sip.")
            : Lang.T("Agent access: OFF (default) — only a real terminal may call sip."));
        Console.WriteLine(Lang.T("Enable: sip --agentok    Disable: sip --agentoff"));
        return;
    }

    if (sub == "--agentoff")
    {
        // 关闭 = 收紧保护 → 任意通道放行
        if (!SetAgentMode(false)) { AgentModeSaveFailed(); return; }
        SimonRecord("agent_mode", "off", CurrentSimonLevel());
        Console.WriteLine(Lang.T("Agent access: OFF. Only a real terminal may call sip again."));
        return;
    }

    // --agentok = 放宽保护 → 人工通道（真 TTY + Web 口令）
    if (!SensitiveActionAllowed(Lang.T("enable agent access"), Lang.T("enable agent access")))
        return;
    if (!SetAgentMode(true)) { AgentModeSaveFailed(); return; }
    SimonRecord("agent_mode", "on", CurrentSimonLevel());
    Console.WriteLine(Lang.T("Agent access: ON. Programs may now call sip and read your library."));
    Console.WriteLine(Lang.T("Turn it off with `sip --agentoff`; check with `sip --agentstatus`."));
}

    // ══════════ 人工通道：真 TTY + 口令 ══════════
    // 原先在 WebAuth.cs 里，但它不是 web 的一部分：它定义的是"谁有资格放宽保护"
    // （降挡、开 Agent 门都走这里），口令复用 Web 密码。放在孟思琳身边才找得到。

    static bool HasInteractiveConsole()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
            return Environment.UserInteractive;
        }
        catch { return false; }
    }


    static void RequireInteractiveTty(string what)
    {
        if (!HasInteractiveConsole())
        {
            SetExit();
            Console.WriteLine(Lang.T("{0} must be run in a real interactive terminal (no piped input)", what));
            Environment.Exit(AiState.ExitCode != 0 ? AiState.ExitCode : 1);
        }
    }

    // ══════════ 人工通道：真 TTY + 口令 ══════════
    // 「放宽保护」类操作（降挡、开启外部调用、签发令牌）的唯一入口。
    //
    // 为什么不是 TUI：TUI 同样是一条 pty，expect 一类工具照样能驱动；它的优势只是
    //   屏解析要多写几行 —— 那是摩擦，不是边界。
    // 为什么还要加口令：TTY 挡不住有 pty 的脚本，只有「脚本不知道的东西」挡得住。
    //
    // 强度（README 同步声明，不要夸大）：
    //   挡得住 —— 被脚本 / Agent 包装的调用、不知道口令的程序
    //   挡不住 —— 已经以你的身份运行、且知道口令的程序（那需要独立用户账户）
    //   这是应用层门，不是权限边界。

    class HumanAuthState
    {
        public int Fails { get; set; }
        public string LastFailAt { get; set; } = "";
        public string LockedUntil { get; set; } = "";
    }

    const int HumanAuthMaxFails = 5;
    const int HumanAuthLockMinutes = 15;

    static string HumanAuthPath() => Path.Combine(dataDir, "human_auth.json");

    static HumanAuthState LoadHumanAuth()
    {
        try
        {
            if (File.Exists(HumanAuthPath()))
                return JsonSerializer.Deserialize<HumanAuthState>(File.ReadAllText(HumanAuthPath())) ?? new HumanAuthState();
        }
        catch { }
        return new HumanAuthState();
    }

    static void SaveHumanAuth(HumanAuthState s)
    {
        try
        {
            File.WriteAllText(HumanAuthPath(), JsonSerializer.Serialize(s,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // 失败计数**必须落盘**：每次 sip 都是新进程，进程内计数器对脚本毫无意义
    static void NoteHumanAuthFail(string action)
    {
        var st = LoadHumanAuth();
        st.Fails++;
        st.LastFailAt = DateTime.Now.ToString("O");
        if (st.Fails >= HumanAuthMaxFails)
            st.LockedUntil = DateTime.Now.AddMinutes(HumanAuthLockMinutes).ToString("O");
        SaveHumanAuth(st);
        SimonRecord("auth_fail", $"{action} (fail #{st.Fails})", CurrentSimonLevel());
    }

    static bool HumanAuthLocked(out DateTime until)
    {
        until = default;
        var st = LoadHumanAuth();
        return DateTime.TryParse(st.LockedUntil, out until) && DateTime.Now < until;
    }

    // 不回显读取。复用 ReadSecret 的机制，并修掉它的一个坑：
    // 原实现无条件 Append(key.KeyChar)，而方向键 / F 键的 KeyChar 是 '\0'，
    // 手滑按一下就会往口令里塞一个 NUL —— 永远校验不过，且看不出原因。
    static string PromptSecret(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (k.Key == ConsoleKey.Escape) { sb.Clear(); break; }
            if (k.KeyChar == '\0') continue;
            sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    // passwordFromUi：TUI 已用掩码输入框收过口令时传入，避免在 TUI 里抢 Console
    static bool SensitiveActionAllowed(string action, string phrase, string? passwordFromUi = null)
    {
        if (HumanAuthLocked(out var until))
        {
            SetExit();
            Console.WriteLine(Lang.T("Too many failed attempts. Try again after {0}.", until.ToString("HH:mm")));
            return false;
        }

        if (passwordFromUi == null && !HasInteractiveConsole())
        {
            SetExit();
            Console.WriteLine(Lang.T("\"{0}\" needs a real interactive terminal (pipes and scripts are refused).", action));
            return false;
        }

        if (!WebPasswordIsSet())
        {
            // 退化路径：更弱，明确说出来，并指向如何变强
            Console.WriteLine(Lang.T("No Web password is set, so this only checks that you typed the phrase below."));
            Console.WriteLine(Lang.T("Run `sip webpass` to set one and make this gate real."));
            Console.Write(Lang.T("Type exactly \"{0}\": ", phrase));
            if (string.Equals(Console.ReadLine()?.Trim(), phrase, StringComparison.Ordinal))
            {
                SimonRecord("auth_weak", action, CurrentSimonLevel());
                return true;
            }
            NoteHumanAuthFail(action);
            SetExit();
            Console.WriteLine(Lang.T("Refused."));
            return false;
        }

        string pw = passwordFromUi ?? PromptSecret(Lang.T("Web password: "));
        if (VerifyWebPassword(pw))
        {
            var st = LoadHumanAuth();
            st.Fails = 0;
            st.LockedUntil = "";
            SaveHumanAuth(st);
            return true;
        }

        NoteHumanAuthFail(action);
        SetExit();
        Console.WriteLine(Lang.T("Wrong password — refused."));
        return false;
    }
}
