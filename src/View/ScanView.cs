using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using MaxScanning.Core;
using MaxScanning.Filter;
using MaxScanning.Scan;
using MaxScanning.Settings;

namespace MaxScanning.View;

/// <summary>
/// One Max Scanning view: treemap of a FileTree with navigation, filter, tags, live scan preview, watch and editing.
/// Several views can share the same tree ("new window on this view").
/// </summary>
public sealed class ScanView : UserControl
{
    // ---- model
    public FileTree Tree { get; private set; }
    public int ViewRoot { get; private set; } = -1;
    public string ScanPath { get; private set; }
    public bool Scanning => _scanTask != null && !_scanTask.IsCompleted;
    public FilterSet Filter { get; private set; } = new("");
    public int Detail { get; private set; } = Config.Current.InitialDetail;
    public bool Watching => _watcher != null;
    public ScanProgress Progress { get; private set; }
    public ScanPauseToken Pause { get; } = new();

    public event Action<ScanView> StateChanged;      // title / nav / scan state
    public event Action<ScanView, int> HoverChanged; // node
    public event Action<string, bool> Log;

    readonly Stack<int> _history = new();
    readonly TreemapCanvas _canvas;
    readonly FlowLayoutPanel _crumbs;
    readonly InfoTip _tip = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    CancellationTokenSource _cts;
    Task _scanTask;
    FileSystemWatcher _watcher;
    readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.OrdinalIgnoreCase);
    long _lastLive, _lastWatch;
    FileTree _liveTree;

    // filter cache
    long[] _fsize; int _fsizeVersion = -1; string _fsizeFilter;
    int[] _bfsOrder; int _bfsVersion = -1;

    // layout/render
    List<Tile> _tiles = new();
    readonly RenderState _rs = new();
    int _hoverTile = -1, _selected = -1;
    int _layoutVersion = -1;
    bool _layoutDirty = true;

    // animation
    long _animStart; int _animMs; RectangleF _animFrom; bool _animating;
    float _hoverAmount; long _hoverChange;

    public ScanView()
    {
        Dock = DockStyle.Fill;
        _crumbs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(4, 3, 4, 0), WrapContents = false, AutoScroll = false, BackColor = SystemColors.Control };
        _canvas = new TreemapCanvas(this) { Dock = DockStyle.Fill };
        Controls.Add(_canvas);
        Controls.Add(_crumbs);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Config.Changed += OnConfigChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Config.Changed -= OnConfigChanged;
            _cts?.Cancel();
            StopWatch();
            _timer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    void OnConfigChanged() { _layoutDirty = true; _fsizeVersion = -1; _canvas.Invalidate(); }

    // ======================================================== scanning
    public void StartScan(string path)
    {
        _cts?.Cancel();
        StopWatch();
        ScanPath = ScanEngine.NormalizePath(path);
        Config.Current.AddRecent(ScanPath);
        Tree = null; _liveTree = null; ViewRoot = -1; _history.Clear(); _tiles.Clear(); _selected = -1;
        var cfg = Config.Current;
        var opt = new ScanOptions { AlternateStreams = cfg.AlternateStreams, PreferMft = cfg.PreferMft, Threads = cfg.ScanThreads, TreeCreated = t => _liveTree = t };
        var prog = new ScanProgress();
        prog.Log += (m, warn) => { if (!warn || cfg.LogScanWarnings) BeginInvokeSafe(() => Log?.Invoke(m, warn)); };
        Progress = prog;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Pause.Resume();
        Log?.Invoke($"{L.T("Scan")}: {ScanPath}", false);
        if (!Native.IsAdmin() && cfg.PreferMft) Log?.Invoke(L.T("Not elevated: using parallel scan. Restart as administrator for MFT turbo scan."), false);
        _scanTask = Task.Run(() =>
        {
            try
            {
                var t = ScanEngine.Scan(ScanPath, opt, prog, Pause, ct);
                BeginInvokeSafe(() => OnScanDone(t, null));
            }
            catch (OperationCanceledException) { BeginInvokeSafe(() => OnScanDone(null, null)); }
            catch (Exception ex) { BeginInvokeSafe(() => OnScanDone(null, ex)); }
        }, ct);
        StateChanged?.Invoke(this);
        _canvas.Invalidate();
    }

    void OnScanDone(FileTree t, Exception ex)
    {
        _liveTree = null;
        if (ex != null) { Log?.Invoke(L.T("Error") + ": " + ex.Message, true); MessageBox.Show(this, ex.Message, "Max Scanning", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        if (t == null)
        {
            if (ex == null) Log?.Invoke(L.T("Scan cancelled"), false);
            StateChanged?.Invoke(this);
            _canvas.Invalidate();
            return;
        }
        SetTree(t, t.Root);
        Log?.Invoke($"{L.T("Scan finished")}: {t.Files[t.Root]:N0} {L.T("files")}, {t.Dirs[t.Root]:N0} {L.T("folders")}, {Util.FormatSize(t.Size[t.Root])} — {t.ScanMethod}, {t.ScanTime.TotalSeconds:F2} s", false);
        if (Config.Current.FlashWhenDone && FindForm() is Form f && (Form.ActiveForm != f || f.WindowState == FormWindowState.Minimized)) Native.Flash(f.Handle);
    }

    public void SetTree(FileTree t, int root)
    {
        Tree = t;
        ViewRoot = root;
        ScanPath ??= t.RootPath;
        _history.Clear();
        _fsizeVersion = -1; _bfsVersion = -1;
        _layoutDirty = true;
        RebuildCrumbs();
        StateChanged?.Invoke(this);
        _canvas.Invalidate();
    }

    public void CancelScan() => _cts?.Cancel();
    public void TogglePause() { if (Pause.Paused) Pause.Resume(); else Pause.Pause(); StateChanged?.Invoke(this); }

    public void Rescan() { if (ScanPath != null) StartScan(ScanPath); }

    void BeginInvokeSafe(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

    // ======================================================== navigation
    public void NavigateTo(int node, bool record = true, bool animate = true)
    {
        if (Tree == null || node < 0 || node == ViewRoot) return;
        if (!Tree.IsContainer(node)) node = Tree.Parent[node];
        if (node < 0 || node == ViewRoot) return;
        // animation source: zoom in -> tile rect of node in current layout; zoom out -> current root rect in new layout
        RectangleF full = _canvas.ClientRectangle;
        RectangleF from = full;
        bool zoomIn = Tree.IsAncestor(ViewRoot, node);
        if (zoomIn)
        {
            int ti = _tiles.FindIndex(x => x.Node == node);
            if (ti < 0)
            {
                // nearest ancestor that is visible
                for (int p = node; p >= 0 && ti < 0; p = Tree.Parent[p]) ti = _tiles.FindIndex(x => x.Node == p);
            }
            if (ti >= 0) from = _tiles[ti].R;
        }
        if (record) _history.Push(ViewRoot);
        int oldRoot = ViewRoot;
        ViewRoot = node;
        _selected = -1;
        Relayout();
        if (!zoomIn)
        {
            int ti = _tiles.FindIndex(x => x.Node == oldRoot);
            if (ti >= 0 && _tiles[ti].R.Width > 1)
            {
                var d = _tiles[ti].R;
                float sx = full.Width / d.Width, sy = full.Height / d.Height;
                from = new RectangleF(-d.X * sx, -d.Y * sy, full.Width * sx, full.Height * sy);
            }
        }
        if (animate && Config.Current.ZoomTimeMs > 0 && from != full) StartAnim(from);
        RebuildCrumbs();
        StateChanged?.Invoke(this);
        _canvas.Invalidate();
    }

    public bool CanBack => _history.Count > 0;
    public bool CanUp => Tree != null && ViewRoot >= 0 && Tree.Parent[ViewRoot] >= 0;
    public void Back() { if (_history.Count > 0) NavigateTo(_history.Pop(), false); }
    public void Up() { if (CanUp) NavigateTo(Tree.Parent[ViewRoot]); }
    public void Home() { if (Tree != null && ViewRoot != Tree.Root) NavigateTo(Tree.Root); }
    public void MoreDetail() { Detail = Math.Min(Detail + 1, 12); _layoutDirty = true; StateChanged?.Invoke(this); _canvas.Invalidate(); }
    public void LessDetail() { Detail = Math.Max(Detail - 1, 1); _layoutDirty = true; StateChanged?.Invoke(this); _canvas.Invalidate(); }

    public void SetFilter(string text)
    {
        Filter = new FilterSet(text);
        if (Filter.Error != null) Log?.Invoke(L.T("Filter error") + ": " + Filter.Error, true);
        _fsizeVersion = -1;
        _layoutDirty = true;
        StateChanged?.Invoke(this);
        _canvas.Invalidate();
    }

    void RebuildCrumbs()
    {
        _crumbs.SuspendLayout();
        _crumbs.Controls.Clear();
        if (Tree != null && ViewRoot >= 0)
        {
            var chain = new List<int>();
            for (int p = ViewRoot; p >= 0; p = Tree.Parent[p]) chain.Add(p);
            chain.Reverse();
            foreach (int n in chain)
            {
                int target = n;
                var link = new LinkLabel
                {
                    Text = n == Tree.Root ? Tree.RootPath : Tree.Name(n),
                    AutoSize = true, Margin = new Padding(0, 2, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline,
                    LinkColor = n == ViewRoot ? Color.Black : Color.FromArgb(0, 90, 200)
                };
                link.Click += (_, _) => NavigateTo(target);
                _crumbs.Controls.Add(link);
                if (n != ViewRoot) _crumbs.Controls.Add(new Label { Text = "›", AutoSize = true, Margin = new Padding(2, 2, 2, 0), ForeColor = Color.Gray });
            }
            _crumbs.Controls.Add(new Label
            {
                Text = $"   {Util.FormatSize(SizeOf(ViewRoot))}  ·  {Tree.Files[ViewRoot]:N0} {L.T("files")}  ·  {Tree.Dirs[ViewRoot]:N0} {L.T("folders")}",
                AutoSize = true, Margin = new Padding(6, 2, 0, 0), ForeColor = Color.DimGray
            });
        }
        _crumbs.ResumeLayout();
    }

    // ======================================================== sizes & filter
    public long SizeOf(int i)
    {
        var t = Tree ?? _liveTree;
        if (t == null || i < 0) return 0;
        if (!Filter.IsEmpty && _fsize != null && i < _fsize.Length) return _fsize[i];
        return Config.Current.ShowSizeOnDisk ? t.Alloc[i] : t.Size[i];
    }

    bool Visible(int i)
    {
        var t = Tree ?? _liveTree;
        if ((t.Flags[i] & NodeFlags.Dead) != 0) return false;
        var k = t.Kind[i];
        if (k == NodeKind.Free) return Config.Current.ShowFreeSpace && Filter.IsEmpty;
        if (k == NodeKind.Unknown) return Config.Current.ShowUnknownSpace && Filter.IsEmpty;
        return true;
    }

    void EnsureFilterSizes()
    {
        var t = Tree;
        if (t == null || Filter.IsEmpty) return;
        if (_fsizeVersion == t.Version && _fsizeFilter == Filter.Text && _fsize != null) return;
        var sw = Stopwatch.StartNew();
        int n = t.Count;
        var fs = new long[n];
        bool alloc = Config.Current.ShowSizeOnDisk;
        var f = Filter;
        // 1) evaluate files in parallel
        Parallel.For(0, n, i =>
        {
            if ((t.Flags[i] & NodeFlags.Dead) != 0) return;
            if (t.Kind[i] == NodeKind.File && f.Match(t, i)) fs[i] = alloc ? t.Alloc[i] : t.Size[i];
        });
        // 2) accumulate bottom-up along BFS order
        var order = BfsOrder();
        for (int k = order.Length - 1; k > 0; k--)
        {
            int v = order[k];
            if (t.Kind[v] == NodeKind.Stream || t.IsVirtual(v)) continue;
            int p = t.Parent[v];
            if (p >= 0 && t.Kind[p] != NodeKind.File) fs[p] += fs[v];
        }
        _fsize = fs; _fsizeVersion = t.Version; _fsizeFilter = Filter.Text;
        if (Config.Current.DebugLog) Log?.Invoke($"filter '{Filter.Text}' evaluated in {sw.ElapsedMilliseconds} ms", false);
    }

    int[] BfsOrder()
    {
        var t = Tree;
        if (_bfsVersion == t.Version && _bfsOrder != null) return _bfsOrder;
        var order = new int[t.Count];
        int head = 0, tail = 0;
        order[tail++] = t.Root;
        while (head < tail)
        {
            int v = order[head++];
            foreach (int c in t.ChildrenOf(v)) if (tail < order.Length) order[tail++] = c;
        }
        Array.Resize(ref order, tail);
        _bfsOrder = order; _bfsVersion = t.Version;
        return order;
    }

    // ======================================================== layout / render
    void Relayout()
    {
        var t = Tree ?? _liveTree;
        if (t == null || _canvas.Width < 4) { _tiles = new(); return; }
        EnsureFilterSizes();
        var cfg = Config.Current;
        var s = new MaxScanning.View.LayoutSettings
        {
            MaxDepth = Detail, MinCell = Math.Max(2, cfg.MinCellSize * _canvas.DeviceDpi / 96f), Aspect = cfg.AspectRatio,
            HeaderHeight = _canvas.Renderer?.LineHeight + 3 ?? 18, Padding = Math.Max(2, 3 * _canvas.DeviceDpi / 96f),
            SortBySize = cfg.SortBySize
        };
        int root = ViewRoot >= 0 ? ViewRoot : t.Root;
        _tiles = TreemapLayout.Build(t, root, _canvas.ClientRectangle, s, SizeOf, Visible);
        _layoutVersion = t.Version;
        _layoutDirty = false;
        _hoverTile = -1;
    }

    void StartAnim(RectangleF from)
    {
        _animFrom = from;
        _animMs = Config.Current.ZoomTimeMs;
        _animStart = Stopwatch.GetTimestamp();
        _animating = true;
    }

    float AnimT()
    {
        double ms = Stopwatch.GetElapsedTime(_animStart).TotalMilliseconds;
        float p = (float)Math.Clamp(ms / Math.Max(1, _animMs), 0, 1);
        return 1 - (1 - p) * (1 - p) * (1 - p); // ease-out cubic
    }

    void Tick()
    {
        bool repaint = false;
        if (_animating)
        {
            repaint = true;
            if (AnimT() >= 1) { _animating = false; if (Config.Current.DynamicZoom) _layoutDirty = true; }
        }
        // hover fade
        float target = _hoverTile >= 0 ? 1f : 0f;
        if (Math.Abs(_hoverAmount - target) > 0.01f)
        {
            _hoverAmount += (target - _hoverAmount) * 0.35f;
            repaint = true;
        }
        long now = Environment.TickCount64;
        var cfg = Config.Current;
        // live preview while enumerating
        if (Scanning)
        {
            if (now - _lastLive > Math.Max(100, cfg.RefreshDelayMs))
            {
                _lastLive = now;
                var lt = _liveTree;
                if (lt != null && lt.Count > 1)
                {
                    lock (lt.Sync) lt.Finish();
                    _layoutDirty = true;
                }
                repaint = true;
                StateChanged?.Invoke(this);
            }
        }
        // watch changes
        if (_watcher != null && !_changed.IsEmpty && now - _lastWatch > Math.Max(300, cfg.RefreshDelayMs * 2))
        {
            _lastWatch = now;
            ProcessChanges();
        }
        if (repaint) _canvas.Invalidate();
    }

    internal void Paint(D2DRenderer r)
    {
        var t = Tree ?? _liveTree;
        var cfg = Config.Current;
        r.SetFont(cfg.FontSize, _canvas.DeviceDpi);
        _rs.Message = null; _rs.Progress = -1;
        if (t != null)
        {
            if (_layoutDirty || _layoutVersion != t.Version) Relayout();
            _rs.Transform = Matrix3x2.Identity;
            if (_animating)
            {
                float p = AnimT();
                RectangleF full = _canvas.ClientRectangle;
                var R = new RectangleF(
                    Lerp(_animFrom.X, full.X, p), Lerp(_animFrom.Y, full.Y, p),
                    Lerp(_animFrom.Width, full.Width, p), Lerp(_animFrom.Height, full.Height, p));
                if (cfg.DynamicZoom)
                {
                    var save = _tiles;
                    var s = new MaxScanning.View.LayoutSettings { MaxDepth = Detail, MinCell = Math.Max(2, cfg.MinCellSize), Aspect = cfg.AspectRatio, HeaderHeight = r.LineHeight + 3, SortBySize = cfg.SortBySize };
                    _tiles = TreemapLayout.Build(t, ViewRoot, R, s, SizeOf, Visible);
                    _ = save;
                }
                else
                    _rs.Transform = Matrix3x2.CreateScale(R.Width / Math.Max(1, full.Width), R.Height / Math.Max(1, full.Height)) * Matrix3x2.CreateTranslation(R.X, R.Y);
            }
        }
        _rs.Tree = t;
        _rs.Tiles = _tiles;
        _rs.HoverTile = _animating ? -1 : _hoverTile;
        _rs.HoverAmount = _hoverAmount;
        _rs.SelectedNode = _selected;
        if (Scanning && Progress != null)
        {
            var p = Progress;
            string phase = p.Phase switch { "MFT" => L.T("Reading MFT"), "MERGE" => L.T("Building tree"), "DIR" => L.T("Enumerating"), _ => L.T("Scanning") };
            _rs.Message = p.Phase == "MFT"
                ? $"{phase}{(Pause.Paused ? " — " + L.T("Pause") : "")}:  {p.Done:N0} / {p.Total:N0} MFT records   {p.Clock.Elapsed.TotalSeconds:F1} s"
                : $"{phase}{(Pause.Paused ? " — " + L.T("Pause") : "")}:  {p.Files:N0} {L.T("files")}, {p.Dirs:N0} {L.T("folders")}   {p.Clock.Elapsed.TotalSeconds:F1} s";
            _rs.Progress = (float)p.Percent;
        }
        else if (t != null && _tiles.Count <= 1 && !Filter.IsEmpty) _rs.Message = L.T("Nothing to show with the current filter");
        else if (t == null) _rs.Message = L.T("Select a drive or folder to scan");
        r.Render(_rs, cfg, SizeOf);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ======================================================== mouse / hover / tooltip
    internal void OnCanvasMouseMove(Point p)
    {
        if (_animating || (Tree ?? _liveTree) == null) return;
        int ti = TreemapLayout.HitTest(_tiles, p);
        if (ti != _hoverTile)
        {
            _hoverTile = ti;
            _hoverChange = Environment.TickCount64;
            HoverChanged?.Invoke(this, ti >= 0 ? _tiles[ti].Node : -1);
            _canvas.Invalidate();
            if (!Config.Current.KeepTooltip) _tip.Hide();
        }
        if (ti >= 0) ShowTip(ti, _canvas.PointToScreen(p));
        else _tip.Hide();
    }

    internal void OnCanvasMouseLeave()
    {
        _hoverTile = -1;
        _tip.Hide();
        HoverChanged?.Invoke(this, -1);
        _canvas.Invalidate();
    }

    void ShowTip(int ti, Point screen)
    {
        var t = Tree ?? _liveTree;
        var tile = _tiles[ti];
        int n = tile.Node;
        if (n < 0)
        {
            _tip.ShowAt(screen, $"{tile.RestCount:N0} {L.T("small items")}", $"{L.T("Size")}: {Util.FormatSize(tile.Size)}");
            return;
        }
        long sz = SizeOf(n);
        int parent = t.Parent[n];
        long psz = parent >= 0 ? SizeOf(parent) : 0;
        var sb = new System.Text.StringBuilder();
        if (t.IsVirtual(n))
            sb.Append($"{L.T("Size")}: {Util.FormatSize(sz)}  ({sz:N0} B)");
        else
        {
            sb.AppendLine(t.FullPath(n));
            sb.AppendLine($"{L.T("Size")}: {Util.FormatSize(t.Size[n])}  ({t.Size[n]:N0} B)");
            sb.AppendLine($"{L.T("Size on disk")}: {Util.FormatSize(t.Alloc[n])}");
            if (!Filter.IsEmpty) sb.AppendLine($"{L.T("Filter")}: {Util.FormatSize(sz)}");
            if (psz > 0) sb.AppendLine($"{sz * 100.0 / psz:F1}% {L.T("of parent")}");
            if (t.IsContainer(n)) sb.AppendLine($"{t.Files[n]:N0} {L.T("files")}, {t.Dirs[n]:N0} {L.T("folders")}");
            sb.AppendLine($"{L.T("Modified")}: {Util.FormatTime(t.MTime[n])}");
            sb.AppendLine($"{L.T("Created")}: {Util.FormatTime(t.CTime[n])}");
            sb.Append($"{L.T("Accessed")}: {Util.FormatTime(t.ATime[n])}");
            string attr = Util.AttrString(t.Attr[n]);
            if (attr.Length > 0) sb.Append($"\n{L.T("Attributes")}: {attr}");
            if ((t.Flags[n] & NodeFlags.AccessDenied) != 0) sb.Append($"\n⚠ {L.T("Access denied")}");
            if ((t.Flags[n] & NodeFlags.Hardlink) != 0) sb.Append($"\n{L.T("Hard links")}");
            if (t.Tag[n] != 0) sb.Append($"\n{L.T("Tag")}: {L.T(char.ToUpper(Util.TagName(t.Tag[n])[0]) + Util.TagName(t.Tag[n])[1..])}");
        }
        string title = t.IsVirtual(n) ? L.T(t.Kind[n] == NodeKind.Free ? "Free space" : "Unknown space") : t.Name(n);
        _tip.ShowAt(screen, title, sb.ToString());
    }

    public int HoverNode => _hoverTile >= 0 && _hoverTile < _tiles.Count ? _tiles[_hoverTile].Node : -1;
    public int SelectedNode => _selected;
    public int TargetNode => _selected >= 0 ? _selected : HoverNode >= 0 ? HoverNode : ViewRoot;

    internal void OnCanvasClick(MouseEventArgs e)
    {
        if (Tree == null) return;
        int ti = TreemapLayout.HitTest(_tiles, e.Location);
        int node = ti >= 0 ? _tiles[ti].Node : -1;
        if (e.Button == MouseButtons.Left) { _selected = node; HoverChanged?.Invoke(this, node); _canvas.Invalidate(); }
        else if (e.Button == MouseButtons.Right) { _selected = node; _canvas.Invalidate(); ShowContextMenu(node, e.Location); }
        else if (e.Button == MouseButtons.XButton1) Back();
        else if (e.Button == MouseButtons.Middle) Up();
    }

    internal void OnCanvasDoubleClick(MouseEventArgs e)
    {
        if (Tree == null || e.Button != MouseButtons.Left) return;
        int ti = TreemapLayout.HitTest(_tiles, e.Location);
        if (ti < 0) return;
        int node = _tiles[ti].Node;
        if (node < 0) { node = _tiles[_tiles[ti].Parent].Node; }
        if (node < 0 || Tree.IsVirtual(node)) return;
        if (node == ViewRoot) { Up(); return; }
        if (Tree.IsContainer(node)) NavigateTo(node);
        else if (Tree.Kind[node] == NodeKind.File) NavigateTo(Tree.Parent[node]);
    }

    internal void OnCanvasWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0) { if (e.Delta > 0) MoreDetail(); else LessDetail(); }
        else if (e.Delta > 0) { int n = HoverNode; if (n >= 0 && n != ViewRoot) { int z = n; while (z >= 0 && Tree.Parent[z] != ViewRoot) z = Tree.Parent[z]; if (z >= 0 && Tree.IsContainer(z)) NavigateTo(z); } }
        else Up();
    }

    // ======================================================== actions
    public static readonly string[] TagKeys = { "", "Red", "Green", "Blue", "Yellow" };

    void ShowContextMenu(int node, Point at)
    {
        var t = Tree;
        var m = new ContextMenuStrip();
        bool real = node >= 0 && !t.IsVirtual(node);
        string path = real ? t.FullPath(node) : null;
        if (real && t.IsContainer(node) && node != ViewRoot) m.Items.Add(L.T("Zoom in"), null, (_, _) => NavigateTo(node));
        if (real) m.Items.Add(L.T("Open"), null, (_, _) => OpenPath(path));
        if (real) m.Items.Add(L.T("Open in Explorer"), null, (_, _) => Explore(path));
        if (real && t.IsDirLike(node)) m.Items.Add(L.T("Open command prompt here"), null, (_, _) => { try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = path, UseShellExecute = true }); } catch { } });
        if (real) m.Items.Add(L.T("Copy path"), null, (_, _) => Clipboard.SetText(path));
        if (real) m.Items.Add(L.T("Copy name"), null, (_, _) => Clipboard.SetText(t.Name(node)));
        m.Items.Add(new ToolStripSeparator());
        if (real)
        {
            var tag = new ToolStripMenuItem(L.T("Tag"));
            for (int k = 1; k <= 4; k++)
            {
                int kk = k;
                var it = new ToolStripMenuItem(L.T(TagKeys[k]), null, (_, _) => SetTag(node, kk)) { Checked = t.Tag[node] == k, ShortcutKeyDisplayString = "Ctrl+" + k };
                tag.DropDownItems.Add(it);
            }
            tag.DropDownItems.Add(L.T("Clear tag"), null, (_, _) => SetTag(node, 0));
            m.Items.Add(tag);
        }
        if (real && t.IsDirLike(node)) m.Items.Add(L.T("Rescan this folder"), null, (_, _) => RescanFolder(node));
        if (real && node != t.Root) m.Items.Add(L.T("Delete (Recycle Bin)"), null, (_, _) => DeleteNode(node, false));
        if (real && node != t.Root) m.Items.Add(L.T("Delete permanently"), null, (_, _) => DeleteNode(node, true));
        if (real) { m.Items.Add(new ToolStripSeparator()); m.Items.Add(L.T("Properties"), null, (_, _) => Native.ShowProperties(path)); }
        m.Show(_canvas, at);
    }

    public static void OpenPath(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    public static void Explore(string p) { try { Process.Start("explorer.exe", "/select,\"" + p + "\""); } catch { } }

    public void SetTag(int node, int tag)
    {
        if (Tree == null || node < 0 || Tree.IsVirtual(node)) return;
        Tree.Tag[node] = (byte)(Tree.Tag[node] == tag ? 0 : tag);
        if (!Filter.IsEmpty) _fsizeVersion = -1;
        _layoutDirty = true;
        _canvas.Invalidate();
        StateChanged?.Invoke(this);
    }

    public void ClearTags()
    {
        if (Tree == null) return;
        Array.Clear(Tree.Tag, 0, Tree.Count);
        _fsizeVersion = -1; _layoutDirty = true; _canvas.Invalidate();
    }

    public void DeleteNode(int node, bool permanent)
    {
        var t = Tree;
        string path = t.FullPath(node);
        if (Config.Current.ConfirmDelete &&
            MessageBox.Show(this, $"{L.T("Are you sure you want to delete")}\n{path}\n({Util.FormatSize(t.Size[node])}, {t.Files[node]:N0} {L.T("files")})?",
                L.T("Delete"), MessageBoxButtons.YesNo, permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var opt = permanent ? Microsoft.VisualBasic.FileIO.RecycleOption.DeletePermanently : Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
            if (t.IsDirLike(node)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, opt);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, opt);
            Log?.Invoke($"{L.T("Delete")}: {path}", false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log?.Invoke(ex.Message, true); MessageBox.Show(this, ex.Message); }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            t.KillSubtree(node);
            AfterStructureChange();
        }
    }

    void AfterStructureChange()
    {
        var t = Tree;
        if (ViewRoot >= 0 && (t.Flags[ViewRoot] & NodeFlags.Dead) != 0) ViewRoot = t.Root;
        t.Finish();
        _fsizeVersion = -1; _bfsVersion = -1; _layoutDirty = true; _selected = -1;
        RebuildCrumbs();
        StateChanged?.Invoke(this);
        _canvas.Invalidate();
    }

    public void RescanFolder(int node) => RescanFolders(new[] { node });

    void RescanFolders(IEnumerable<int> nodes)
    {
        var t = Tree;
        var list = nodes.Where(n => n >= 0 && t.IsDirLike(n)).Distinct().ToList();
        if (list.Count == 0) return;
        var cfg = Config.Current;
        var jobs = list.Select(n => (node: n, path: t.FullPath(n))).ToList();
        Task.Run(() =>
        {
            var results = new List<(int node, FileTree sub)>();
            foreach (var j in jobs)
            {
                try
                {
                    if (!Directory.Exists(j.path)) { results.Add((j.node, null)); continue; }
                    var sub = DirScanner.Scan(j.path, new ScanOptions { AlternateStreams = cfg.AlternateStreams, Threads = cfg.ScanThreads, PreferMft = false }, new ScanProgress(), new ScanPauseToken(), CancellationToken.None);
                    results.Add((j.node, sub));
                }
                catch (Exception ex) { BeginInvokeSafe(() => Log?.Invoke(ex.Message, true)); }
            }
            BeginInvokeSafe(() =>
            {
                if (Tree != t) return;
                foreach (var (node, sub) in results) Splice(node, sub);
                AfterStructureChange();
            });
        });
    }

    /// <summary>Replace the children of node by the content of a freshly scanned subtree (tags of surviving paths are kept).</summary>
    void Splice(int node, FileTree sub)
    {
        var t = Tree;
        if ((t.Flags[node] & NodeFlags.Dead) != 0) return;
        // remember tags by relative path
        var tags = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        string basePath = t.FullPath(node);
        foreach (int c in t.ChildrenOf(node)) CollectTags(t, c, basePath, tags);
        foreach (int c in t.ChildrenOf(node).ToArray()) t.KillSubtree(c);
        if (sub == null) { t.KillSubtree(node); return; }
        var map = new int[sub.Count];
        map[sub.Root] = node;
        for (int i = 0; i < sub.Count; i++)
        {
            if (i == sub.Root) continue;
            int p = sub.Parent[i];
            if (p < 0) continue;
            int ni = t.Add(map[p], sub.NameSpan(i), sub.Kind[i], sub.OwnSize[i], sub.OwnAlloc[i], sub.Attr[i], sub.CTime[i], sub.MTime[i], sub.ATime[i]);
            t.Flags[ni] = sub.Flags[i];
            map[i] = ni;
        }
        if (tags.Count > 0)
            for (int i = 0; i < sub.Count; i++)
                if (i != sub.Root && tags.TryGetValue(sub.FullPath(i), out var tg)) t.Tag[map[i]] = tg;
    }

    static void CollectTags(FileTree t, int n, string basePath, Dictionary<string, byte> tags)
    {
        var st = new Stack<int>(); st.Push(n);
        while (st.Count > 0)
        {
            int v = st.Pop();
            if (t.Tag[v] != 0) tags[t.FullPath(v)] = t.Tag[v];
            foreach (int c in t.ChildrenOf(v)) st.Push(c);
        }
    }

    // ======================================================== watch
    public void StartWatch()
    {
        if (Tree == null || _watcher != null || Tree.RootPath.Length == 0) return;
        try
        {
            _watcher = new FileSystemWatcher(Tree.RootPath)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            FileSystemEventHandler h = (_, e) => _changed[Path.GetDirectoryName(e.FullPath) ?? e.FullPath] = 0;
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, e) => { _changed[Path.GetDirectoryName(e.FullPath) ?? e.FullPath] = 0; _changed[Path.GetDirectoryName(e.OldFullPath) ?? e.OldFullPath] = 0; };
            _watcher.Error += (_, e) => BeginInvokeSafe(() => { Log?.Invoke("watch: " + e.GetException().Message + " — " + L.T("Rescan needed"), true); _changed[Tree.RootPath] = 0; });
            _watcher.EnableRaisingEvents = true;
            Log?.Invoke(L.T("Watching for changes") + ": " + Tree.RootPath, false);
        }
        catch (Exception ex) { Log?.Invoke(ex.Message, true); _watcher = null; }
        StateChanged?.Invoke(this);
    }

    public void StopWatch()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
        _changed.Clear();
        StateChanged?.Invoke(this);
    }

    void ProcessChanges()
    {
        var t = Tree;
        if (t == null) return;
        var dirs = _changed.Keys.ToList();
        _changed.Clear();
        var nodes = new HashSet<int>();
        foreach (var d in dirs)
        {
            string p = d;
            int n = -1;
            while (p != null && (n = t.FindPath(p)) < 0) p = Path.GetDirectoryName(p);
            if (n >= 0) nodes.Add(t.IsDirLike(n) ? n : t.Parent[n]);
        }
        // drop nodes whose ancestor is also being rescanned
        var final = nodes.Where(n => !nodes.Any(o => o != n && t.IsAncestor(o, n))).ToList();
        if (Config.Current.DebugLog) Log?.Invoke($"watch: {dirs.Count} change(s) → rescan {final.Count} folder(s)", false);
        RescanFolders(final);
    }

    // ======================================================== canvas
    sealed class TreemapCanvas : Control
    {
        readonly ScanView _v;
        public D2DRenderer Renderer;
        public TreemapCanvas(ScanView v)
        {
            _v = v;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Renderer = new D2DRenderer(Handle, Width, Height); }
        protected override void OnHandleDestroyed(EventArgs e) { Renderer?.Dispose(); Renderer = null; base.OnHandleDestroyed(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Renderer?.Resize(Width, Height); _v._layoutDirty = true; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) { if (Renderer != null) _v.Paint(Renderer); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _v.OnCanvasMouseMove(e.Location); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _v.OnCanvasMouseLeave(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); _v.OnCanvasClick(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); _v.OnCanvasDoubleClick(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _v.OnCanvasWheel(e); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); _v._layoutDirty = true; Invalidate(); }
    }
}
