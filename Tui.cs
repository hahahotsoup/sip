// ===== TUI 视图与组件（自绘侧栏/管理列表/报告卡片/启动页等）=====
// 与 RssReader.cs 同为全局命名空间（无 namespace）。视图类只依赖 Lang 与 Terminal.Gui，
// 不调用主程序顶层函数，因此可独立于此文件。
using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

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

// 今日变化摘要：按源新增计数 + 高频源 + 被作者改过 + 可能同文（纯事实，零 LLM）
class TodayDigest
{
    public int NewTotal { get; set; }
    public int SourceCount { get; set; }
    public List<SourceCount> NewBySource { get; set; } = new();   // 每个源新增数（含高频标记）
    public List<TodayModified> Modified { get; set; } = new();    // 被作者改过（改动概览）
    public List<DedupCandidate> Dedups { get; set; } = new();     // 可能同文（跨源重复）
}

class SourceCount
{
    public string Source { get; set; } = "";
    public int Count { get; set; }
    public bool Flood { get; set; }          // 腹泻式/高频源
}

class TodayModified
{
    public int ItemId { get; set; }          // 最新版本 Id → sip --diff <ItemId>
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public bool TitleChanged { get; set; }   // 标题是否改过
    public int AddedLines { get; set; }      // 正文新增行数
    public int RemovedLines { get; set; }    // 正文删除行数
    public int WordDelta { get; set; }       // 约 ±字数
}

// 跨源去重规则（dedup.json）：键 = "feedId:url"（被隐藏那篇）；值为 canonical 信息
class DedupRule
{
    public int HiddenFeedId { get; set; }
    public string HiddenUrl { get; set; } = "";
    public int CanonicalFeedId { get; set; }
    public string CanonicalUrl { get; set; } = "";
    public string At { get; set; } = "";
}

// 一个「可能同文」候选对（检测结果，未处理）
class DedupCandidate
{
    public int ItemIdA { get; set; }
    public string TitleA { get; set; } = "";
    public string SourceA { get; set; } = "";
    public int ItemIdB { get; set; }
    public string TitleB { get; set; } = "";
    public string SourceB { get; set; } = "";
    public double Overlap { get; set; }      // 段落重合度
    public string DiffCmd { get; set; } = ""; // sip --diff A B
}

class InsightsView : View
{
    public List<InsightsFeed> Feeds { get; private set; } = new();
    public int Selected { get; private set; }
    public event EventHandler? SelectionChanged;

    public int SelectedFeedId => Selected < Feeds.Count ? Feeds[Selected].FeedId : 0;

    public void SetFeeds(List<InsightsFeed> feeds)
    {
        Feeds = feeds;
        Selected = Math.Clamp(Selected, 0, Math.Max(0, Feeds.Count - 1));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    public void MoveTo(int delta)
    {
        if (Feeds.Count == 0) return;
        int before = Selected;
        Selected = Math.Clamp(Selected + delta, 0, Feeds.Count - 1);
        if (Selected != before) { SelectionChanged?.Invoke(this, EventArgs.Empty); SetNeedsDraw(); }
    }

    // 卡片固定 6 行：标题 / 订阅积压 / 打开读完 / 点赞+AI调用 / 观察 / 分隔线
    int CardHeight => 6;

    List<string> CardLines(InsightsFeed x)
    {
        string rate = x.Opened > 0 ? Math.Round(100.0 * x.Completed / x.Opened, 0) + "%" : "—";
        string status = x.Status == Lang.T("正常") ? "" : "  " + x.Status;
        string ai = (x.LlmCalls > 0 || x.EmbeddingCalls > 0) ? $" · AI 摘要 {x.LlmCalls} 次" : "";
        string reasons = x.Reasons.Count > 0 ? "   " + string.Join(" · ", x.Reasons) : "";
        return new List<string>
        {
            $"[{x.FeedId}] {x.Title}{status}",
            $"    订阅 {x.Active} 篇 · 未读积压 {x.Backlog}",
            $"    打开 {x.Opened} · 读完 {x.Completed} · 完成率 {rate} · 跳过 {x.Skipped}",
            $"    ♥ 你点赞 {x.UserLikes} · 🤖 AI 点赞 {x.AiLikes}{ai}",
            reasons,
            "──────────────────────"
        };
    }

    protected override bool OnKeyDown(Key key)
    {
        if (Feeds.Count == 0) return false;
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
            case KeyCode.J: MoveTo(1); return true;
            case KeyCode.CursorUp:
            case KeyCode.K: MoveTo(-1); return true;
            case KeyCode.PageDown: MoveTo(Math.Max(1, Viewport.Height / CardHeight)); return true;
            case KeyCode.PageUp: MoveTo(-Math.Max(1, Viewport.Height / CardHeight)); return true;
            case KeyCode.Home: MoveTo(-Feeds.Count); return true;
            case KeyCode.End: MoveTo(Feeds.Count); return true;
            default: return false;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width, h = Viewport.Height;
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        if (Feeds.Count == 0) return true;

        int cardTop = Math.Max(0, Selected * CardHeight - (h - CardHeight) / 2);
        for (int i = 0; i < Feeds.Count; i++)
        {
            int top = i * CardHeight - cardTop;
            if (top + CardHeight < 0 || top >= h) continue;
            var lines = CardLines(Feeds[i]);
            bool sel = i == Selected;
            for (int k = 0; k < CardHeight; k++)
            {
                int sy = top + k;
                if (sy < 0 || sy >= h) continue;
                SetAttribute(sel && k == 0
                    ? GetAttributeForRole(HasFocus ? VisualRole.Focus : VisualRole.Active)
                    : GetAttributeForRole(sel ? VisualRole.Active : VisualRole.Normal));
                string line = (sel && k == 0 ? "▶ " : "  ") + (k < lines.Count ? lines[k] : "");
                int cols = line.GetColumns();
                if (cols > w) line = line[..Math.Max(0, w - 1)] + "…";
                AddStr(0, sy, line);
            }
        }
        return true;
    }
}

