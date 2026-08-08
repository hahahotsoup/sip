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

// 工作目录 = exe 所在文件夹（Mac/Linux/Windows 都适用）
string workDir = AppDomain.CurrentDomain.BaseDirectory;
string dbPath = Path.Combine(workDir, "rss.db");
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
Lang.Init(workDir, langCode);

// ══════════ CLI 模式 ══════════
if (args.Length > 0)
{
    await RunCli(args, dbPath);
    return 0;
}

// ══════════ TUI 模式（无参数时进入）══════════
return await RunTui(dbPath);

// ═══════════════════════════════════════════════════════════
// 以下是所有方法，按调用顺序排列
// ═══════════════════════════════════════════════════════════

// ══════════ CLI 参数处理 ══════════
async Task RunCli(string[] args, string dbPath)
{
    var cmd = args[0].ToLower();

    if (cmd is "-h" or "--help")
    {
        PrintHelp();
        return;
    }

    if (cmd is "-l" or "--list")
    {
        if (args.Length >= 2)
        {
            // -l 后面带编号 → 列出该源的文章
            if (!int.TryParse(args[1], out int lNum)) { Console.WriteLine(Lang.T("编号必须是数字")); return; }
            int feedRealId = GetRealId(lNum, dbPath);
            if (feedRealId == 0) { Console.WriteLine(Lang.T("没找到这个编号")); return; }
            ListArticlesFromDb(feedRealId, lNum, dbPath);
        }
        else
        {
            ListFeedsFromDb(dbPath);
        }
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
    }

    // 已知但需要参数的命令；不在此列的一律当作"已知命令"但少参数，否则是未知命令
    bool needsArg = cmd is "-u" or "--update" or "-d" or "--download" or "-a" or "--archive"
                    or "-una" or "--unarchive" or "-r" or "--remove" or "--search" or "--summary";
    if (args.Length < 2)
    {
        if (!needsArg) { Console.WriteLine(Lang.T("未知命令: {0}", cmd)); PrintHelp(); return; }
        Console.WriteLine(Lang.T("缺少参数。用法: rssreader {0} <参数>", cmd));
        return;
    }

    switch (cmd)
    {
        case "-u" or "--update":
            if (!int.TryParse(args[1], out int aNum)) { Console.WriteLine(Lang.T("编号必须是数字")); return; }
            UpdateFeed(aNum, dbPath).Wait();
            break;
        case "-d" or "--download":
            DownloadCli(args[1], dbPath);
            break;
        case "-a" or "--archive":
            if (!int.TryParse(args[1], out int tNum)) { Console.WriteLine(Lang.T("编号必须是数字")); return; }
            AddTimestamp(tNum, dbPath);
            break;
        case "-una" or "--unarchive":
            if (!int.TryParse(args[1], out int uNum)) { Console.WriteLine(Lang.T("编号必须是数字")); return; }
            RemoveTimestamp(uNum, dbPath);
            break;
        case "-r" or "--remove":
            if (!int.TryParse(args[1], out int dNum)) { Console.WriteLine(Lang.T("编号必须是数字")); return; }
            DeleteFeed(dNum, dbPath);
            break;
        case "--search":
            if (args.Length < 2) { Console.WriteLine(Lang.T("用法: rssreader --search <查询> [--feed 编号] [--threshold 0.7] [--json]")); return; }
            SearchCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--summary":
            SummaryCli(args[1], dbPath).Wait();
            break;
        default:
            Console.WriteLine(Lang.T("未知命令: {0}", cmd));
            PrintHelp();
            break;
    }
}

void PrintHelp()
{
    Console.WriteLine(Lang.T("用法: rssreader <命令> [参数]"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("命令:"));
    Console.WriteLine(Lang.T("  -l, --list       列出所有订阅源"));
    Console.WriteLine(Lang.T("  -u, --update     更新指定订阅源（编号）"));
    Console.WriteLine(Lang.T("  -d, --download   下载新的 RSS 源（URL）"));
    Console.WriteLine(Lang.T("  -a, --archive    归档（加时间戳）"));
    Console.WriteLine(Lang.T("  -una, --unarchive 去归档"));
    Console.WriteLine(Lang.T("  -r, --remove     删除订阅源"));
    Console.WriteLine(Lang.T("  -h, --help       显示此帮助"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("AI 命令:"));
    Console.WriteLine(Lang.T("  --init           首次配置 AI（模型 + API Key）"));
    Console.WriteLine(Lang.T("  --config         查看/修改 AI 配置"));
    Console.WriteLine(Lang.T("  --index          对文章做 Embedding 向量化（交互式选择）"));
    Console.WriteLine(Lang.T("  --reindex        更换 Embedding 模型后重新向量化"));
    Console.WriteLine(Lang.T("  --search <查询>   [--feed 编号] [--threshold 0.7] [--json] 语义搜索（不带 --feed 时搜索全部源）"));
    Console.WriteLine(Lang.T("  --summary <编号>  为文章生成摘要（保存到数据库）；可传 feed:<编号> 为该源全部文章生成"));
    Console.WriteLine(Lang.T("  --summary-all    为所有未生成摘要的文章生成摘要"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("示例:"));
    Console.WriteLine(Lang.T("  rssreader -l"));
    Console.WriteLine(Lang.T("  rssreader -d https://example.com/rss"));
    Console.WriteLine(Lang.T("  rssreader -u 1"));
    Console.WriteLine(Lang.T("  rssreader -a 1"));
    Console.WriteLine(Lang.T("  rssreader --search \"LLM Agent\" --feed 1 --json"));
    Console.WriteLine(Lang.T("  rssreader --summary 12"));
    Console.WriteLine(Lang.T("  rssreader --summary feed:3"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("全局选项:"));
    Console.WriteLine(Lang.T("  --ignoresafeannouncement   跳过安全横幅等提示，仅输出数据（供脚本 / AI Agent 调用）"));
    Console.WriteLine(Lang.T("  --lang <代码>              指定语言文件（如 zh-CN / en-US，默认 zh-CN）"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("安全提示:"));
    Console.WriteLine(Lang.T("  API Key 存储在操作系统原生凭据库（Windows 凭据管理器 / macOS 钥匙串 / Linux Secret Service），"));
    Console.WriteLine(Lang.T("  不写入任何文件。请勿泄露 API Key。首次调用 AI 功能时会提示。"));
}

// ══════════ TUI（Terminal.Gui 文件夹视图）═══════════
// 布局：左侧订阅源+文章树（源为父节点，展开即见文章）/ 右侧正文预览 / 底部状态栏
// 操作：↑↓ 选择，Enter 折叠/展开源或打开文章，←→ 切换树/正文，PageUp/PageDown 翻页，
//       U 更新当前源，F6 全部更新，A 归档，R 去归档，X 删除，D 加源，S 搜索，Y 摘要，H 帮助，Q 退出
#pragma warning disable CS0618  // 使用尚未迁移的静态 Application API
async Task<int> RunTui(string dbPath)
{
    Application.Init();
    try
    {
        // —— 左侧：订阅源 + 文章 树形视图 ——
        var tree = new TreeView<TuiNode>
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(30),
            Height = Dim.Fill() - 2,
            CanFocus = true,
            BorderStyle = LineStyle.Single,
            Title = " " + Lang.T("订阅源") + " "
        };
        tree.TreeBuilder = new DelegateTreeBuilder<TuiNode>(
            childGetter: n => n.IsFeed ? LoadArticleNodes(n.FeedId, dbPath) : Enumerable.Empty<TuiNode>(),
            canExpand: n => n.IsFeed
        );
        tree.AspectGetter = n => n.Title;

        // —— 右侧：正文预览（占满剩余空间）——
        var contentView = new TextView
        {
            X = Pos.Right(tree),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2,
            CanFocus = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = true,
            BorderStyle = LineStyle.Single,
            Title = " " + Lang.T("正文") + " "
        };

        // 底部命令行（按 : 聚焦，Enter 执行，Esc 取消）
        var cmdBar = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1),
            Height = 1,
            CanFocus = true,
            Text = "",
            Secret = false
        };
        var cmdLabel = new Label
        {
            Text = ":",
            X = 0,
            Y = Pos.AnchorEnd(2),
            CanFocus = false
        };

        // 主窗口
        var top = new Window
        {
            Title = " sip RSS Reader ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        // 状态栏快捷操作（全键盘，键位对齐外部 CLI）
        var statusBar = new StatusBar(new Shortcut[]
        {
            new Shortcut(Key.H, Lang.T("帮助"), () => ShowHelpDialog(), Lang.T("查看全部快捷键")),
            new Shortcut(Key.U, Lang.T("更新"), () => RefreshSelectedFeed(), Lang.T("下载更新当前源 (同 CLI -u)")),
            new Shortcut(Key.F6, Lang.T("全部更新"), () => RefreshAllFeeds(), Lang.T("下载更新所有源")),
            new Shortcut(Key.A, Lang.T("归档"), () => ArchiveSelectedFeed(), Lang.T("给当前源加时间戳 (同 CLI -a)")),
            new Shortcut(Key.R, Lang.T("去归档"), () => UnarchiveSelectedFeed(), Lang.T("去掉时间戳 (同 CLI -una)")),
            new Shortcut(Key.X, Lang.T("删除"), () => DeleteSelected(), Lang.T("删除选中源/文章 (同 CLI -r)")),
            new Shortcut(Key.D, Lang.T("加源"), () => AddFeedDialog(), Lang.T("添加新订阅源 (同 CLI -d)")),
            new Shortcut(Key.S, Lang.T("搜索"), () => SearchDialog(), Lang.T("语义搜索 (同 CLI --search)")),
            new Shortcut(Key.Y, Lang.T("摘要"), () => SummarizeSelected(), Lang.T("给当前文章生成摘要 (同 CLI --summary)")),
            new Shortcut(Key.Q, Lang.T("退出"), () => top.RequestStop(), Lang.T("退出程序"))
        });

        top.Add(tree, contentView, cmdLabel, cmdBar, statusBar);

        void RebuildTree()
        {
            tree.ClearObjects();
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
                if (active > 0) parts.Add(Lang.T("现存{0}篇", active + deleted));
                if (archive > 0) parts.Add(Lang.T("其中有{0} 篇发生了更改", archive));
                if (deleted > 0) parts.Add(Lang.T("{0} 篇被作者删掉了，但是我们已经帮你存档了", deleted));
                string stats = string.Join(", ", parts);
                tree.AddObject(new TuiNode { IsFeed = true, FeedId = id, Title = $"{title} {stats}" });
            }
            tree.RebuildTree();
            tree.ExpandAll();
        }

        void ShowSelectedContent()
        {
            var n = tree.SelectedObject;
            if (n == null || n.IsFeed) { contentView.Text = ""; return; }
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Title, Content, Description, Link, PublishDate FROM Items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", n.ItemId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                string title = r.GetString(0);
                string content = r.IsDBNull(1) ? "" : r.GetString(1);
                string desc = r.IsDBNull(2) ? "" : r.GetString(2);
                string link = r.IsDBNull(3) ? "" : r.GetString(3);
                string pub = r.IsDBNull(4) ? "" : r.GetString(4);
                string body = string.IsNullOrWhiteSpace(content) ? desc : content;
                body = StripHtml(body);
                contentView.Text = $"{title}\n\n{link}\n{pub}\n\n{body}";
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
                int ans = Ask(Lang.T("确定删除 {0}？此操作不可恢复！(y/n)", n.Title),
                    Lang.T("确定"), Lang.T("取消"));
                if (ans != 0) return;
                DeleteFeedByRealId(n.FeedId, dbPath);
                RebuildTree();
                contentView.Text = "";
            }
            else
            {
                // 删除单篇文章
                int ans = Ask(Lang.T("确定删除这篇文章？此操作不可恢复！"), Lang.T("确定"), Lang.T("取消"));
                if (ans != 0) return;
                DeleteArticleByRealId(n.ItemId, dbPath);
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

        // 网络操作阻塞 TUI 时显示提示，结束后重建树
        void RunNetworkOp(Action op)
        {
            contentView.Text = Lang.T("处理中，请稍候...");
            op();
            contentView.Text = "";
            RebuildTree();
        }

        void AddFeedDialog()
        {
            // 输入 URL 添加新源（同 CLI -d <url>）
            var dlg = new Dialog { Title = " " + Lang.T("添加订阅源") + " " };
            var lbl = new Label { Text = Lang.T("RSS 链接："), X = 0, Y = 0 };
            var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("确定"), IsDefault = true, X = 0, Y = 3 };
            var cancel = new Button { Text = Lang.T("取消"), X = Pos.Right(ok) + 1, Y = 3 };
            dlg.Add(lbl, input, ok, cancel);
            dlg.Width = 60;
            dlg.Height = 7;

            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };

            Application.Run(dlg);
            string url = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;
            RunNetworkOp(() =>
            {
                try { DownloadAndSaveToDb(url, dbPath).Wait(); }
                catch (Exception ex) { contentView.Text = Lang.T("出错: {0}", ex.Message); }
            });
        }

        void SearchDialog()
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("尚未配置 AI，请先用命令行执行 sip --init 配置"), Lang.T("确定"));
                return;
            }
            var dlg = new Dialog { Title = " " + Lang.T("语义搜索") + " " };
            var lbl = new Label { Text = Lang.T("搜索内容："), X = 0, Y = 0 };
            var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("搜索"), IsDefault = true, X = 0, Y = 3 };
            var cancel = new Button { Text = Lang.T("取消"), X = Pos.Right(ok) + 1, Y = 3 };
            dlg.Add(lbl, input, ok, cancel);
            dlg.Width = 60;
            dlg.Height = 7;

            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };

            Application.Run(dlg);
            string q = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(q)) return;

            // 复用 CLI 的搜索逻辑，把结果展示到正文区
            var results = DoSearch(q, dbPath);
            if (results == null) { contentView.Text = Lang.T("搜索失败"); return; }
            var sb = new StringBuilder();
            sb.AppendLine(Lang.T("搜索结果（查询：{0}，共 {1} 条）", q, results.Count));
            foreach (var h in results)
                sb.AppendLine($"  [{h.ItemId}] {h.Title}\n      来源：{h.FeedTitle} | 相似度：{h.Score:P1}");
            contentView.Text = sb.ToString();
        }

        void SummarizeSelected()
        {
            var n = GetSelected();
            if (n == null || n.IsFeed)
            {
                Ask(Lang.T("请先选中一篇文章再生成摘要"), Lang.T("确定"));
                return;
            }
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("尚未配置 AI，请先用命令行执行 sip --init 配置"), Lang.T("确定"));
                return;
            }
            contentView.Text = Lang.T("正在生成摘要，请稍候...");
            SummarizeItem(dbPath, (int)n.ItemId).Wait();
            ShowSelectedContent();
        }

        void ShowHelpDialog()
        {
            var dlg = new Dialog { Title = " " + Lang.T("快捷键帮助") + " " };
            var txt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = false
            };
            txt.Text = string.Join("\n",
                Lang.T("U          更新当前源"),
                Lang.T("F6         更新所有源"),
                Lang.T("A          归档当前源"),
                Lang.T("R          去归档"),
                Lang.T("X          删除选中源/文章"),
                Lang.T("D          添加新订阅源"),
                Lang.T("S          语义搜索"),
                Lang.T("Y          生成文章摘要"),
                Lang.T("H          显示本帮助"),
                Lang.T("Q          退出"),
                Lang.T("Enter      源:折叠/展开; 文章:打开正文"),
                Lang.T("← / →      切换树/正文"),
                Lang.T("PageUp/Dn  上下翻页"));
            var ok = new Button { Text = Lang.T("确定"), IsDefault = true, X = 0, Y = Pos.Bottom(txt) };
            dlg.Add(txt, ok);
            ok.Accepted += (s, e) => dlg.RequestStop();
            Application.Run(dlg);
        }

        // 通用确认/提示对话框，返回按钮索引（0 = 第一个按钮）
        int Ask(string message, params string[] buttons)
        {
            var btns = buttons.Length > 0 ? buttons : new[] { Lang.T("确定") };
            return MessageBox.Query(Application.Instance, Lang.T("提示"), message, btns) ?? 0;
        }

        // —— 事件绑定 ——
        tree.SelectionChanged += (s, e) => ShowSelectedContent();

        // 树：Enter 折叠/展开源或确认文章；←/→ 切换栏；PageUp/PageDown 翻页；: 打开命令行
        tree.KeyDown += (s, e) =>
        {
            var n = tree.SelectedObject;
            if (e.KeyCode == KeyCode.Enter)
            {
                if (n != null && n.IsFeed) tree.Toggle(n);
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorRight)
            {
                if (n is { IsFeed: false }) contentView.SetFocus();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.PageUp)
            {
                tree.MovePageUp(false);
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.PageDown)
            {
                tree.MovePageDown(false);
                e.Handled = true;
            }
            else if (e.AsRune.Value == ':')
            {
                cmdBar.SetFocus();
                e.Handled = true;
            }
        };

        // 正文栏：← 返回树；↑↓ 平滑滚动；PageUp/PageDown 小幅翻页；: 打开命令行
        contentView.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.CursorLeft:
                    tree.SetFocus();
                    e.Handled = true;
                    break;
                case KeyCode.CursorUp:
                    contentView.ScrollVertical(-1);
                    e.Handled = true;
                    break;
                case KeyCode.CursorDown:
                    contentView.ScrollVertical(1);
                    e.Handled = true;
                    break;
                case KeyCode.PageUp:
                    contentView.ScrollVertical(-6);
                    e.Handled = true;
                    break;
                case KeyCode.PageDown:
                    contentView.ScrollVertical(6);
                    e.Handled = true;
                    break;
                default:
                    if (e.AsRune.Value == ':')
                    {
                        cmdBar.SetFocus();
                        e.Handled = true;
                    }
                    break;
            }
        };

        // 命令行：Enter 执行，Esc 返回树
        cmdBar.KeyDown += (s, e) =>
        {
            if (e.KeyCode == KeyCode.Enter)
            {
                string input = cmdBar.Text.Trim();
                cmdBar.Text = "";
                tree.SetFocus();
                if (input.Length > 0) RunCommand(input);
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Esc)
            {
                cmdBar.Text = "";
                tree.SetFocus();
                e.Handled = true;
            }
        };

        // 执行命令行输入（复用 CLI 命令语法）
        void RunCommand(string input)
        {
            var parts = input.Split(' ', 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "q" or "quit" or "exit":
                    top.RequestStop();
                    return;
                case "h" or "help":
                    ShowHelpDialog();
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
                case "y" or "--summary":
                    SummarizeSelected();
                    return;
                default:
                    Ask(Lang.T("未知命令: {0}，按 H 查看帮助", cmd), Lang.T("确定"));
                    return;
            }
        }

        // TUI 内语义搜索并显示到正文区
        void DoTuiSearch(string query)
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("尚未配置 AI，请先用命令行执行 sip --init 配置"), Lang.T("确定"));
                return;
            }
            contentView.Text = Lang.T("正在搜索，请稍候...");
            var results = DoSearch(query, dbPath);
            if (results == null) { contentView.Text = Lang.T("搜索失败"); return; }
            var sb = new StringBuilder();
            sb.AppendLine(Lang.T("搜索结果（查询：{0}，共 {1} 条）", query, results.Count));
            foreach (var h in results)
                sb.AppendLine($"  [{h.ItemId}] {h.Title}\n      来源：{h.FeedTitle} | 相似度：{h.Score:P1}");
            contentView.Text = sb.ToString();
        }

        RebuildTree();
        tree.ExpandAll();
        Application.Run(top);
        return 0;
    }
    finally
    {
        Application.Shutdown();
    }
}
#pragma warning restore CS0618

// 从数据库加载某源的文章节点（TUI 树的叶子）
IEnumerable<TuiNode> LoadArticleNodes(int feedId, string dbPath)
{
    var nodes = new List<TuiNode>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title, Status, Version
        FROM Items WHERE FeedId = @fid
        ORDER BY Id
    ";
    cmd.Parameters.AddWithValue("@fid", feedId);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        long id = r.GetInt64(0);
        string title = r.GetString(1);
        string status = r.GetString(2);
        int version = r.GetInt32(3);
        string tag = status switch
        {
            "active" => Lang.T("[现]"),
            "archived" => Lang.T("[旧]"),
            "deleted" => Lang.T("[删]"),
            _ => "[?]"
        };
        nodes.Add(new TuiNode { IsFeed = false, FeedId = feedId, ItemId = id, Title = $"{tag} v{version} | {title}" });
    }
    return nodes;
}


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
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Vectors WHERE FeedId = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();
}

// ══════════ 更新指定订阅源（A 菜单和 CLI 共用）═══════════
async Task UpdateFeed(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine(Lang.T("没找到这个编号")); return; }

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

    if (IsArchived(title)) { Console.WriteLine(Lang.T("{0} 已归档，不能更新", title)); return; }

    try { await DownloadAndSaveToDb(url, dbPath); Console.WriteLine(Lang.T("更新完成")); }
    catch (TaskCanceledException) { Console.WriteLine(Lang.T("下载超时，请检查网络或链接是否有效")); }
    catch (HttpRequestException) { Console.WriteLine(Lang.T("网络请求失败，链接可能已失效")); }
    catch (SqliteException ex) { Console.WriteLine(Lang.T("数据库出错：{0}", ex.Message)); }
    catch (Exception ex) { Console.WriteLine(Lang.T("未知错误：{0}", ex.Message)); }
}

// CLI 模式下载（同步等待异步方法）
void DownloadCli(string url, string dbPath)
{
    try { DownloadAndSaveToDb(url, dbPath).Wait(); Console.WriteLine(Lang.T("下载完成")); }
    catch (Exception ex) { Console.WriteLine(Lang.T("出错: {0}", ex.Message)); }
}

// ══════════ 建表方法 ══════════
// 只在程序启动时调用一次。IF NOT EXISTS 保证不会覆盖已有数据库
// 两张表的关系：Feeds 是"班级"，Items 是"学生"，FeedId 就是学生属于哪个班级
void InitDatabase(string dbPath)
{
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
}

// ══════════ 列出指定源的所有文章（用 ROW_NUMBER 显示编号）═══════════
void ListArticlesFromDb(int feedRealId, int feedDisplayNum, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    // 查 Feed 标题
    var titleCmd = conn.CreateCommand();
    titleCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    titleCmd.Parameters.AddWithValue("@id", feedRealId);
    string feedTitle = titleCmd.ExecuteScalar()!.ToString()!;
    Console.WriteLine(Lang.T("── [{0}] {1} 的文章列表──", feedDisplayNum, feedTitle));

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
        Console.WriteLine(Lang.T("  这个源还没有文章"));
        return;
    }
    while (reader.Read())
    {
        int displayNum = reader.GetInt32(5);    // 第 5 列 DisplayNum
        string status  = reader.GetString(3);   // 第 3 列 Status
        string title   = reader.GetString(2);   // 第 2 列 Title
        int version    = reader.GetInt32(4);    // 第 4 列 Version

        string tag = status switch
        {
            "active"   => Lang.T("[现]"),
            "archived" => Lang.T("[旧]"),
            "deleted"  => Lang.T("[删]"),
            _          => "[?]"
        };
        Console.WriteLine($"  [{displayNum}] {tag} v{version} | {title}");
    }
}

// ══════════ 列表方法：显示数据库中所有订阅源 ══════════
// ROW_NUMBER() 保证显示出来永远是 1, 2, 3 连续编号（不管中间有没有删过源）
// 但操作（更新/时间戳/删除）仍然用真实 Id，因为 Items 表靠它关联
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
    // 六列：[真实Id, 标题, 活跃数, 旧版本数, 已删除数, 显示编号]

    using var reader = cmd.ExecuteReader();
    if (!reader.HasRows)
    {
        Console.WriteLine(Lang.T("数据库里还没有订阅源"));
        return;
    }

    while (reader.Read())
    {
        int active = reader.GetInt32(2);
        int archive = reader.GetInt32(3);
        int deleted = reader.GetInt32(4);

        // 拼出显示文本：只显示非零的状态
        var parts = new List<string>();
        if (active > 0)  parts.Add(Lang.T("现存{0}篇", active + deleted));
        if (archive > 0) parts.Add(Lang.T("其中有{0} 篇发生了更改", archive));
        if (deleted > 0) parts.Add(Lang.T("{0} 篇被作者删掉了，但是我们已经帮你存档了", deleted));
        string stats = string.Join(", ", parts);

        Console.WriteLine($"[{reader.GetInt32(5)}] {reader.GetString(1)} {stats}");
    }
}

// ══════════ 核心方法：下载 RSS → 解析 → 去重 → 写入数据库 ══════════
async Task DownloadAndSaveToDb(string url, string dbPath)
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
    Console.WriteLine(Lang.T("正在下载..."));

    string rawXml;
    try
    {
        rawXml = await client.GetStringAsync(url);
    }
    catch (HttpRequestException) when (wasAutoPrefixed && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        // https 失败 → 站点可能只支持 http，重试一次
        string httpUrl = "http://" + url["https://".Length..];
        Console.WriteLine(Lang.T("https:// 连接失败，改用 http://{0} 重试...", httpUrl["http://".Length..]));
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
        Console.WriteLine(Lang.T("订阅源{0}已存在，正在比对...", feed.Title));
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
            Console.WriteLine(Lang.T("内容有变化，已更新订阅源"));
        }
        else
        {
            Console.WriteLine(Lang.T("内容无变化，跳过更新"));
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

    Console.WriteLine(Lang.T("{0} 写入完成", feed.Title));

    // --- 第 6 步：若已初始化 AI，询问是否把该源未向量化的文章加入 embedding ---
    await MaybeIndexNewArticles(feedId, dbPath);
}

// ══════════ 辅助方法：下载/更新后询问是否对新文章做向量化 ══════════
// 仅当已执行过 --init（存在 ai_config.json）时才会询问，避免打扰未配置 AI 的用户
async Task MaybeIndexNewArticles(long feedId, string dbPath)
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

    Console.WriteLine(Lang.T("这个源有 {0} 篇新文章还未向量化，是否加入语义搜索（{1}）？(y/n)", pending, cfg.Embedding.Model));
    if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine(Lang.T("已跳过，需要时可用 rssreader --index 补上")); return; }

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
    Console.WriteLine(Lang.T("向量化完成：成功 {0}，失败 {1}", ok, fail));
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
    Console.WriteLine(Lang.T("检测到链接缺少协议前缀，已自动补全为 https://{0}", trimmed));
    return "https://" + trimmed;
}

// ══════════ 辅助方法：规范化 OpenAI 兼容端点 ══════════
// 用户常只填 "http://host:11434"，这里补上 "/v1"（OpenAI 兼容路径）
string EnsureV1Endpoint(string ep)
{
    string e = ep.Trim().TrimEnd('/');
    if (e.Length == 0 || e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
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
void DeleteFeed(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine(Lang.T("没找到这个编号")); return; }

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

    Console.Write(Lang.T("确定删除 {0} 及其 {1} 篇文章？(y/n)", title, itemCount));
    if (!"y".Equals(Console.ReadLine()?.Trim().ToLower()))
    {
        Console.WriteLine(Lang.T("已取消"));
        return;
    }

    // 2. 先删该源的向量和文章
    cmd.CommandText = "DELETE FROM Vectors WHERE FeedId = @id";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();

    // 3. 再删订阅源
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("{0}已删除", title));
}

// ══════════ 加时间戳：标题 + _20260712_143000 ══════════
// 加完后标题变了，下次下载同名源时 GetOldRawXml 找不到，
// 就会被当作新订阅源处理，不会触发去重
void AddTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine(Lang.T("没找到这个编号")); return; }

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
        Console.WriteLine(Lang.T("{0} 已被归档，无需重复操作", oldTitle));
        return;
    }

    // 3. 追加时间戳
    string newTitle = oldTitle + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // 4. 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", newTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("标题已变更：{0} → {1} ", oldTitle, newTitle));
}

// ══════════ 去时间戳：去掉 _yyyymmdd_hhmmss 后缀 ══════════
// 去掉之前检查原始标题是否已存在，防止冲突
void RemoveTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { Console.WriteLine(Lang.T("没找到这个编号")); return; }

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
        Console.WriteLine(Lang.T("{0} 未归档", title));
        return;
    }

    // 3. 检查 plainTitle 是否已被其他源占用（排除自己）
    cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Title = @title AND Id != @id";
    cmd.Parameters.AddWithValue("@title", plainTitle);
    long conflict = (long)cmd.ExecuteScalar()!;
    if (conflict > 0)
    {
        Console.WriteLine(Lang.T("冲突！已存在另一个名称为 {0} 的源，无法去除时间戳", plainTitle));
        return;
    }

    // 4. 安全 → 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", plainTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("时间戳已去除：{0} → {1} ", title, plainTitle));
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

            Console.WriteLine(Lang.T("  [已归档] {0} 作者修改了内容，旧版已保留", item.Title));
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
        Console.WriteLine(Lang.T("  新增 {0} 篇", newCount));
        return;
    }

    // 不检测删除：很多站点 RSS 只推最近 N 篇，老文章不在列表里不代表被删，
    // 因此只跟踪新增与修改，避免把正常下架的文章误标为 deleted
    Console.WriteLine(Lang.T("  新增 {0} 篇，修改 {1} 篇", newCount, modifyCount));
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
            Console.WriteLine(Lang.T("新旧 RSS 完全相同，无新增、删除或修改"));

        return hasChanges;  // 把结果返回给调用方，让它决定是否更新
    }
    catch (Exception ex)
    {
        Console.WriteLine(Lang.T("比较条目差异时出错：{0}", ex.Message));
        return false;  // 出错了保守处理：不用旧数据覆盖，当作没变化
    }
}

// ══════════ GetItemSummary：生成文章摘要行，供文本 diff 显示用 ══════════
string GetItemSummary(FeedItem item)
{
    string id = !string.IsNullOrEmpty(item.Id) ? item.Id : item.Link ?? item.Title ?? Lang.T("未知");
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
    Console.WriteLine(Lang.T("🔐 安全提醒"));
    Console.WriteLine(Lang.T("你的 API Key 存储在操作系统原生凭据库"));
    Console.WriteLine(Lang.T("（Windows 凭据管理器 / macOS 钥匙串 / Linux Secret Service）"));
    Console.WriteLine(Lang.T("不会写入任何项目文件。请注意保密："));
    Console.WriteLine(Lang.T("1. 不要把 API Key 分享/发给他人"));
    Console.WriteLine(Lang.T("2. 不要截图或上传含密钥的界面"));
    Console.WriteLine(Lang.T("3. 如怀疑泄露，请立即更换密钥"));
    Console.WriteLine(Lang.T("════════════════════════════════════════════════════"));
}

// ══════════ JSON 输出辅助 ══════════
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
        Console.WriteLine(Lang.T("错误 [{0}] {1}", code, message));
        if (suggestion != null) Console.WriteLine(Lang.T("建议：{0}", suggestion));
        if (details != null) Console.WriteLine(Lang.T("详情：{0}", details));
    }
}

// ══════════ Embedding 服务（OpenAI 兼容格式，端点可自定义）═══════════
// 统一走 POST {endpoint}/embeddings：Ollama(/v1)、DeepSeek、OpenAI 及任何
// 兼容服务均可；API Key 可选（本地 Ollama 不需要，填了才带 Bearer 头）
async Task<float[]?> GetEmbeddingAsync(string text, AiConfig cfg)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    string? key = CredGet("embedding_api_key");
    if (!string.IsNullOrEmpty(key))
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

    var body = new { model = cfg.Embedding.Model, input = text };
    var resp = await client.PostAsync($"{cfg.Embedding.ApiEndpoint}/embeddings",
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
    if (!resp.IsSuccessStatusCode)
        throw new AiException("MODEL_UNAVAILABLE", Lang.T("Embedding 请求失败（HTTP {0}）", (int)resp.StatusCode),
            Lang.T("请确认端点/端口/模型名正确；Ollama 可先执行 ollama list / ollama pull 拉取模型"), await resp.Content.ReadAsStringAsync());
    try
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        return data.EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }
    catch (JsonException)
    {
        // 返回的不是 JSON（比如端点缺少 /v1 返回的 HTML），给出友好提示而非崩溃
        throw new AiException("INVALID_RESPONSE", Lang.T("Embedding 服务返回的不是 JSON"),
            Lang.T("请检查端点是否缺少 /v1（正确形式 http://host:端口/v1）"));
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
        ReportError("NETWORK_ERROR", Lang.T("网络错误，无法连接到 Embedding 服务"),
            Lang.T("请检查网络连接，或检查 API 端点地址"), ex.Message, json);
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
            return Lang.T("检测到 Embedding 模型维度变化（旧模型 {0} {1} 维 → 新模型 {2} {3} 维），旧向量已无法使用，请执行 rssreader --reindex 重新向量化",
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
    Console.Write(Lang.T("请输入要向量化的订阅源编号（逗号分隔多个，输入 all 表示全部）："));
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

    if (feedIds.Count == 0) { Console.WriteLine(Lang.T("未选择任何订阅源，已取消")); return; }

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

    if (articles.Count == 0) { Console.WriteLine(Lang.T("所选订阅源的文章都已向量化，无需处理")); return; }

    Console.WriteLine(Lang.T("将向量化 {0} 篇文章，确认？(y/n)", articles.Count));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("已取消")); return; }

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    int ok = 0, fail = 0;
    for (int i = 0; i < articles.Count; i++)
    {
        var a = articles[i];
        var vec = await SafeEmbed(a.Title, cfg);
        if (vec == null) { fail++; Console.WriteLine(Lang.T("  [{0}/{1}] 失败：{2}", i + 1, articles.Count, a.Title)); continue; }
        if (vec.Length != cfg.Embedding.Dimensions)
        {
            // 自动校正维度（以实际为准）
            cfg.Embedding.Dimensions = vec.Length;
            SaveConfig(dbPath, cfg);
        }
        SaveVector(dbPath, a.FeedId, a.Id, modelId, vec);
        ok++;
        if (ok % 10 == 0) Console.WriteLine(Lang.T("  已处理 {0}/{1}", ok + fail, articles.Count));
    }
    Console.WriteLine(Lang.T("完成：成功 {0}，失败 {1}", ok, fail));
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

    Console.Write(Lang.T("将删除现有向量并重新向量化全部 {0} 篇 active 文章，确认？(y/n)", total));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("已取消")); return; }

    cmd.CommandText = "DELETE FROM Vectors";
    cmd.ExecuteNonQuery();

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
        if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  已处理 {0}/{1}", ok + fail, items.Count));
    }
    Console.WriteLine(Lang.T("重新索引完成：成功 {0}，失败 {1}", ok, fail));
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
                    if (feedReal == 0) { ReportError("FEED_NOT_FOUND", Lang.T("没有找到编号 {0} 的订阅源", f), json: json); return; }
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
    if (string.IsNullOrWhiteSpace(query)) { ReportError("EMPTY_QUERY", Lang.T("请输入搜索查询"), json: json); return; }

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
        Console.WriteLine(Lang.T("搜索结果（查询：{0}，阈值：{1}，共 {2} 条）", query, threshold, results.Count));
        foreach (var h in results)
        {
            Console.WriteLine($"  [{h.ItemId}] {h.Title}");
            Console.WriteLine(Lang.T("      来源：{0} | 相似度：{1:P1}", h.FeedTitle, h.Score));
            if (!string.IsNullOrEmpty(h.Description) && h.Description.Length > 80)
                Console.WriteLine(Lang.T("      摘要：{0}...", h.Description[..80]));
        }
    }
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
    if (modelObj == null) { ReportError("NO_INDEX", Lang.T("尚无向量索引，请先执行 rssreader --index"), json: json); return null; }
    int modelId = Convert.ToInt32(modelObj);

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Vectors WHERE ModelId = @m";
    cmd.Parameters.AddWithValue("@m", modelId);
    long count = (long)cmd.ExecuteScalar()!;
    if (count == 0) { ReportError("NO_INDEX", Lang.T("当前模型尚无向量索引，请先执行 rssreader --index"), json: json); return null; }

    cmd.Parameters.Clear();
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
            if (score < thr) continue;
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
    return results.OrderByDescending(h => h.Score).Take(20).ToList();
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

// 按真实 Id 删除单篇文章及其向量（TUI 用）
void DeleteArticleByRealId(long itemId, string dbPath)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Vectors WHERE ItemId = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE Id = @id";
    cmd.ExecuteNonQuery();
}

// （SearchHit 类见文件末尾类型区）
// ══════════ LLM 摘要服务（OpenAI 兼容，端点可自定义）═══════════
async Task<string?> CallLlmAsync(string prompt, AiConfig cfg)
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
        throw new AiException("API_KEY_INVALID", Lang.T("LLM 请求失败（HTTP {0}）", (int)resp.StatusCode),
            Lang.T("请检查 API Key / 模型名 / 端点配置"), await resp.Content.ReadAsStringAsync());
    try
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }
    catch (JsonException)
    {
        throw new AiException("INVALID_JSON", Lang.T("LLM 服务返回的不是 JSON"),
            Lang.T("请检查端点是否缺少 /v1（如 https://api.deepseek.com/v1）"));
    }
}

// 生成单篇文章摘要并保存到 rss.db（与文章同在库中）
async Task<bool> SummarizeItem(string dbPath, int itemId, bool json = false)
{
    var cfg = LoadConfig(dbPath);
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, Content, Description, Summary FROM Items WHERE Id = @id AND Status = 'active'";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("没有找到文章 {0}", itemId), json: json); return false; }
    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string existing = r.IsDBNull(3) ? "" : r.GetString(3);
    r.Close();

    if (!string.IsNullOrEmpty(existing))
    {
        Console.WriteLine(Lang.T("文章 [{0}] {1} 已有摘要，跳过（如想重新生成请先删除）", itemId, title));
        return true;
    }

    string text = string.IsNullOrEmpty(content) ? desc : content;
    if (text.Length > 6000) text = text[..6000];
    var prompt = $"请用 150 字以内概括以下文章的核心内容（用中文回答，直接输出摘要正文，不要额外解释）：\n\n标题：{title}\n\n正文：{text}";

    try
    {
        EnsureAiPrompted();
        var summary = await CallLlmAsync(prompt, cfg);
        if (summary == null) throw new AiException("EMPTY_RESPONSE", Lang.T("LLM 返回为空"), Lang.T("请重试或检查模型配置"));

        var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE Items SET Summary = @s, SummaryAt = @now WHERE Id = @id";
        upd.Parameters.AddWithValue("@s", summary.Trim());
        upd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        upd.Parameters.AddWithValue("@id", itemId);
        upd.ExecuteNonQuery();
        Console.WriteLine(Lang.T("已生成摘要：[{0}] {1}", itemId, title));
        if (json) JsonOut(new { success = true, itemId, title, summary = summary.Trim() });
        return true;
    }
    catch (HttpRequestException ex)
    {
        ReportError("NETWORK_ERROR", Lang.T("网络错误，无法连接 LLM 服务"), Lang.T("请检查网络连接"), ex.Message, json);
        return false;
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return false;
    }
}

// 单篇/整源摘要 CLI；支持 '12' 和 'feed:3'
async Task SummaryCli(string arg, string dbPath)
{
    EnsureAiPrompted();

    // feed:N → 为该订阅源全部未摘要的 active 文章逐个生成
    if (arg.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
    {
        if (!int.TryParse(arg["feed:".Length..].Trim(), out int feedDisplay))
        {
            Console.WriteLine(Lang.T("格式错误。正确：{0}", "--summary feed:3"));
            return;
        }
        int feedReal = GetRealId(feedDisplay, dbPath);
        if (feedReal == 0) { Console.WriteLine(Lang.T("没有找到编号 {0} 的订阅源", feedDisplay)); return; }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title FROM Items WHERE Status = 'active' AND FeedId = @fid AND (Summary IS NULL OR Summary = '')";
        cmd.Parameters.AddWithValue("@fid", feedReal);
        using var r = cmd.ExecuteReader();
        var items = new List<(int Id, string Title)>();
        while (r.Read()) items.Add((r.GetInt32(0), r.GetString(1)));
        r.Close();

        if (items.Count == 0) { Console.WriteLine(Lang.T("订阅源 {0} 的所有 active 文章都已有摘要", feedDisplay)); return; }
        Console.WriteLine(Lang.T("将为订阅源 {0} 的 {1} 篇文章生成摘要，确认？(y/n)", feedDisplay, items.Count));
        if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("已取消")); return; }

        int ok = 0, fail = 0;
        foreach (var it in items)
        {
            if (await SummarizeItem(dbPath, it.Id, json: false)) ok++; else fail++;
            Console.WriteLine(Lang.T("  进度：{0}/{1}", ok + fail, items.Count));
        }
        Console.WriteLine(Lang.T("完成：成功 {0}，失败 {1}", ok, fail));
        return;
    }

    // 单篇文章
    if (!int.TryParse(arg, out int sumId)) { Console.WriteLine(Lang.T("用法: rssreader --summary <文章编号 | feed:编号>")); return; }
    await SummarizeItem(dbPath, sumId);
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

    if (items.Count == 0) { Console.WriteLine(Lang.T("所有 active 文章都已有摘要")); return; }
    Console.WriteLine(Lang.T("将为 {0} 篇文章生成摘要，确认？(y/n)", items.Count));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("已取消")); return; }

    int ok = 0, fail = 0;
    foreach (var it in items)
    {
        if (await SummarizeItem(dbPath, it.Id)) ok++; else fail++;
        Console.WriteLine(Lang.T("  进度：{0}/{1}", ok + fail, items.Count));
    }
    Console.WriteLine(Lang.T("完成：成功 {0}，失败 {1}", ok, fail));
}

// ══════════ 交互式配置向导 ══════════
void InitAiConfigInteractive(string dbPath)
{
    EnsureAiPrompted();
    Console.WriteLine(Lang.T("===== RSS Reader AI 配置向导 ====="));
    Console.WriteLine(Lang.T("所有服务均使用 OpenAI 兼容格式（Ollama / DeepSeek / OpenAI / 任意兼容服务均可），端点与端口可自由指定。"));
    var cfg = LoadConfig(dbPath);

// --- Embedding ---
    Console.WriteLine(Lang.T("\n[1/3] Embedding 服务（语义搜索用，OpenAI 兼容格式）："));
    Console.Write(Lang.T("  端点（只需 http://host:端口 或 https://域名，自动补全 /v1）〔当前：{0}〕：", cfg.Embedding.ApiEndpoint));
    string embEndpoint = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(embEndpoint))
    {
        cfg.Embedding.ApiEndpoint = EnsureV1Endpoint(embEndpoint);
        Console.WriteLine(Lang.T("  → 最终端点：{0}", cfg.Embedding.ApiEndpoint));
    }

    Console.Write(Lang.T("  模型名（如 nomic-embed-text / bge-m3 / text-embedding-3-small）〔当前：{0}〕：", cfg.Embedding.Model));
    string embModel = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(embModel)) cfg.Embedding.Model = embModel;

    Console.Write(Lang.T("  向量维度（如 768/1024/1536，不确定可回车后由程序自动探测）〔当前：{0}〕：", cfg.Embedding.Dimensions));
    if (int.TryParse(Console.ReadLine()?.Trim(), out int embDim) && embDim > 0)
        cfg.Embedding.Dimensions = embDim;

    Console.Write(Lang.T("  Embedding API Key（本地 Ollama 等无需 Key 可回车跳过；输入时不会显示，仅存系统凭据库）〔当前：{0}〕：",
        CredHas("embedding_api_key") ? Lang.T("已设置") : Lang.T("未设置")));
    var embKey = ReadSecret();
    if (!string.IsNullOrEmpty(embKey)) CredSet("embedding_api_key", embKey);

    // --- LLM ---
    Console.WriteLine(Lang.T("\n[2/3] LLM 服务（生成摘要用，OpenAI 兼容格式）："));
    Console.Write(Lang.T("  端点（只需 https://host[:端口] 或 http://host:端口，自动补全 /v1）〔当前：{0}〕：", cfg.Llm.ApiEndpoint));
    string llmEndpoint = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(llmEndpoint))
    {
        cfg.Llm.ApiEndpoint = EnsureV1Endpoint(llmEndpoint);
        Console.WriteLine(Lang.T("  → 最终端点：{0}", cfg.Llm.ApiEndpoint));
    }

    Console.Write(Lang.T("  模型名（如 deepseek-chat / gpt-4o-mini / qwen2.5）〔当前：{0}〕：", cfg.Llm.Model));
    string llmModel = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(llmModel)) cfg.Llm.Model = llmModel;

    Console.Write(Lang.T("  LLM API Key（输入时不会显示，仅存系统凭据库，回车跳过）："));
    var llmKey = ReadSecret();
    if (!string.IsNullOrEmpty(llmKey)) CredSet("llm_api_key", llmKey);

    // --- 通用 ---
    Console.Write(Lang.T("\n[3/3] 默认搜索相似度阈值（0-1，建议 0.7，本地 bge-m3 建议 0.5）〔当前：{0}〕：", cfg.Embedding.SearchThreshold));
    if (float.TryParse(Console.ReadLine()?.Trim(), out float thr)) cfg.Embedding.SearchThreshold = thr;

    SaveConfig(dbPath, cfg);
    Console.WriteLine(Lang.T("\n配置已保存。你可以修改 ai_config.json 调整模型，API Key 已在系统凭据库中。"));
    Console.WriteLine(Lang.T("注意：更换 Embedding 模型后需执行 rssreader --reindex 重新向量化。"));
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
    Console.WriteLine(Lang.T("===== AI 配置 ====="));
    Console.WriteLine(Lang.T("Embedding：{0} / {1} ({2} 维)", cfg.Embedding.Provider, cfg.Embedding.Model, cfg.Embedding.Dimensions));
    Console.WriteLine(Lang.T("  端点：{0}", cfg.Embedding.ApiEndpoint));
    Console.WriteLine(Lang.T("  默认搜索阈值：{0}", cfg.Embedding.SearchThreshold));
    Console.WriteLine(Lang.T("  API Key：{0}", CredHas("embedding_api_key") ? Lang.T("已设置") : Lang.T("未设置")));
    Console.WriteLine(Lang.T("LLM：{0} / {1}", cfg.Llm.Provider, cfg.Llm.Model));
    Console.WriteLine(Lang.T("  端点：{0}", cfg.Llm.ApiEndpoint));
    Console.WriteLine(Lang.T("  API Key：{0}", CredHas("llm_api_key") ? Lang.T("已设置") : Lang.T("未设置")));
    Console.WriteLine(Lang.T("配置文件：{0}", ConfigPath(dbPath)));

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
}

// ══════════ 语言 / 本地化支持 ══════════
// 用法：Lang.T("你好") / Lang.T("共有 {0} 篇", n)
// 查找顺序：languages/<代码>.json（可定制翻译）→ 内置中文默认值 → 原样返回
// 语言文件格式（JSON 字典，键为原文，值为译文）：
//   { "你好": "Hello", "共有 {0} 篇": "Total {0} articles" }
static class Lang
{
    public static string Code { get; private set; } = "zh-CN";

    private static readonly Dictionary<string, string> _custom = new();
    private static bool _loaded;

    public static void Init(string workDir, string? requested)
    {
        string code = requested ?? "";
        if (string.IsNullOrEmpty(code))
            code = Environment.GetEnvironmentVariable("LANG") ?? "zh-CN";
        Code = code;

        string path = Path.Combine(workDir, "languages", code + ".json");
        if (!File.Exists(path)) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded != null)
            {
                _custom.Clear();
                foreach (var kv in loaded) _custom[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"加载语言文件失败：{ex.Message}");
        }
        _loaded = true;
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
    public string Title { get; set; } = "";
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

// ══════════ 自定义异常 ══════════
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
