using System.Diagnostics;
using System.Drawing;
using MaxScanning.Core;
using MaxScanning.IO;
using MaxScanning.Settings;
using MaxScanning.View;

namespace MaxScanning.UI;

public sealed class MainForm : Form
{
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(10, 4) };
    readonly MenuStrip _menu = new();
    readonly ToolStrip _tool = new() { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(16, 16) };
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _stHover = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _stInfo = new();
    readonly ToolStripStatusLabel _stMem = new();
    readonly ToolStripProgressBar _stProg = new() { Width = 140, Visible = false };
    readonly ToolStripComboBox _filterBox = new() { AutoSize = false, Width = 260, ToolTipText = "*.mp4;>100mb;<30d;:red;|*.tmp — F1" };
    readonly ConsoleForm _console = new();
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };
    ToolStripButton _btnPause, _btnWatch, _btnBack, _btnUp, _btnHome;

    internal ScanView CurrentView => Current;
    ScanView Current => _tabs.SelectedTab?.Controls.Count > 0 ? _tabs.SelectedTab.Controls[0] as ScanView : null;

    public MainForm(string[] args)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Max Scanning" + (Native.IsAdmin() ? "  [Admin · MFT]" : "");
        Icon = MakeIcon();
        var cfg = Config.Current;
        Size = new Size(cfg.WindowW, cfg.WindowH);
        if (cfg.WindowX >= 0) { StartPosition = FormStartPosition.Manual; Location = new Point(cfg.WindowX, cfg.WindowY); }
        else StartPosition = FormStartPosition.CenterScreen;
        if (cfg.WindowMax) WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        AllowDrop = true;
        Font = new Font("Segoe UI", 9f);

        BuildMenu();
        BuildToolbar();
        _status.Items.AddRange(new ToolStripItem[] { _stHover, _stProg, _stInfo, _stMem });
        Controls.Add(_tabs);
        Controls.Add(_tool);
        Controls.Add(_menu);
        Controls.Add(_status);
        MainMenuStrip = _menu;
        _tabs.SelectedIndexChanged += (_, _) => UpdateState();
        _tabs.MouseUp += TabsMouseUp;
        _uiTimer.Tick += (_, _) => UpdateStatus();
        _uiTimer.Start();
        Config.Changed += OnConfigChanged;
        FormClosed += (_, _) => Config.Changed -= OnConfigChanged;

        DragEnter += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data!.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0)
            {
                if (f[0].EndsWith(Snapshot.Extension, StringComparison.OrdinalIgnoreCase)) LoadSnapshot(f[0]);
                else NewScan(Directory.Exists(f[0]) ? f[0] : Path.GetDirectoryName(f[0]));
            }
        };

        Shown += (_, _) => HandleArgs(args);
        FormClosing += (_, _) => SaveWindow();
    }

    void OnConfigChanged() { if (IsDisposed) return; BuildMenu(); BuildToolbar(); UpdateState(); }

    void HandleArgs(string[] args)
    {
        int li = Array.IndexOf(args, "--load");
        if (li >= 0 && li + 1 < args.Length) { LoadSnapshot(args[li + 1]); return; }
        string fi = null; int fx = Array.IndexOf(args, "--filter"); if (fx >= 0 && fx + 1 < args.Length) fi = args[fx + 1];
        var path = args.FirstOrDefault(a => !a.StartsWith("--") && a != fi);
        if (path != null)
        {
            if (path.EndsWith(Snapshot.Extension, StringComparison.OrdinalIgnoreCase)) LoadSnapshot(path);
            else { NewScan(path); if (fi != null) { _filterBox.Text = fi; ApplyFilter(); } }
        }
        else ShowStartDialog();
    }

    void SaveWindow()
    {
        var c = Config.Current;
        c.WindowMax = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal) { c.WindowX = Left; c.WindowY = Top; c.WindowW = Width; c.WindowH = Height; }
        c.Save();
    }

    // ------------------------------------------------------------ menu / toolbar
    static ToolStripMenuItem Item(string text, Keys keys, Action a, string shortcut = null)
    {
        var it = new ToolStripMenuItem(L.T(text), null, (_, _) => a()) { ShortcutKeys = keys };
        if (shortcut != null) it.ShortcutKeyDisplayString = shortcut;
        return it;
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        var file = new ToolStripMenuItem(L.T("File"));
        file.DropDownItems.Add(Item("New scan...", Keys.Control | Keys.N, ShowStartDialog));
        var recent = new ToolStripMenuItem(L.T("Recent"));
        foreach (var p in Config.Current.RecentPaths) { string pp = p; recent.DropDownItems.Add(pp, null, (_, _) => NewScan(pp)); }
        recent.Enabled = recent.DropDownItems.Count > 0;
        file.DropDownItems.Add(recent);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Open snapshot...", Keys.Control | Keys.O, OpenSnapshot));
        file.DropDownItems.Add(Item("Save snapshot...", Keys.Control | Keys.S, SaveSnapshot));
        file.DropDownItems.Add(Item("Export...", Keys.Control | Keys.E, ShowExport));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Exit", Keys.Alt | Keys.F4, Close));

        var edit = new ToolStripMenuItem(L.T("Edit"));
        edit.DropDownItems.Add(Item("Copy path", Keys.None, CopyPath, "Ctrl+C"));
        var tag = new ToolStripMenuItem(L.T("Tag"));
        for (int k = 1; k <= 4; k++) { int kk = k; tag.DropDownItems.Add(Item(ScanView.TagKeys[k], Keys.Control | (Keys)((int)Keys.D0 + k), () => Current?.SetTag(Current.TargetNode, kk))); }
        tag.DropDownItems.Add(Item("Clear tag", Keys.Control | Keys.D0, () => Current?.SetTag(Current.TargetNode, 0)));
        tag.DropDownItems.Add(Item("Clear all tags", Keys.None, () => Current?.ClearTags()));
        edit.DropDownItems.Add(tag);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Delete (Recycle Bin)", Keys.None, () => DeleteTarget(false), "Del"));
        edit.DropDownItems.Add(Item("Delete permanently", Keys.None, () => DeleteTarget(true), "Shift+Del"));

        var view = new ToolStripMenuItem(L.T("View"));
        view.DropDownItems.Add(Item("Back", Keys.Alt | Keys.Left, () => Current?.Back(), "Alt+←"));
        view.DropDownItems.Add(Item("Up one level", Keys.None, () => Current?.Up(), "Backspace"));
        view.DropDownItems.Add(Item("Go to root", Keys.None, () => Current?.Home(), "Home"));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("Less detail", Keys.None, () => Current?.LessDetail(), "-"));
        view.DropDownItems.Add(Item("More detail", Keys.None, () => Current?.MoreDetail(), "+"));
        view.DropDownItems.Add(new ToolStripMenuItem(L.T("Show free space"), null, (_, _) => { var c = Config.Current.Clone(); c.ShowFreeSpace = !c.ShowFreeSpace; Config.Replace(c); }) { Checked = Config.Current.ShowFreeSpace });
        view.DropDownItems.Add(new ToolStripMenuItem(L.T("Show unknown space"), null, (_, _) => { var c = Config.Current.Clone(); c.ShowUnknownSpace = !c.ShowUnknownSpace; Config.Replace(c); }) { Checked = Config.Current.ShowUnknownSpace });
        view.DropDownItems.Add(new ToolStripMenuItem(L.T("Use size on disk instead of size"), null, (_, _) => { var c = Config.Current.Clone(); c.ShowSizeOnDisk = !c.ShowSizeOnDisk; Config.Replace(c); }) { Checked = Config.Current.ShowSizeOnDisk });
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("Rescan", Keys.F5, () => Current?.Rescan()));
        view.DropDownItems.Add(Item("Start watching", Keys.F6, ToggleWatch));
        view.DropDownItems.Add(Item("Event log console", Keys.F12, () => { _console.Show(this); _console.BringToFront(); }));

        var opts = new ToolStripMenuItem(L.T("Options"));
        opts.DropDownItems.Add(Item("Configuration...", Keys.Control | Keys.P, ShowConfig));
        var lang = new ToolStripMenuItem(L.T("Language"));
        lang.DropDownItems.Add(new ToolStripMenuItem("中文", null, (_, _) => SetLang("zh")) { Checked = L.Zh });
        lang.DropDownItems.Add(new ToolStripMenuItem("English", null, (_, _) => SetLang("en")) { Checked = !L.Zh });
        opts.DropDownItems.Add(lang);
        if (!Native.IsAdmin()) opts.DropDownItems.Add(Item("Restart as administrator (MFT turbo scan)", Keys.None, RestartAsAdmin));

        var win = new ToolStripMenuItem(L.T("Window"));
        win.DropDownItems.Add(Item("New window on this view", Keys.Control | Keys.T, CloneView));
        win.DropDownItems.Add(Item("Close window", Keys.Control | Keys.W, CloseCurrent));

        var help = new ToolStripMenuItem(L.T("Help"));
        help.DropDownItems.Add(Item("Quick help", Keys.F1, () => new HelpForm(false).Show(this)));
        help.DropDownItems.Add(Item("Filter help", Keys.None, () => new HelpForm(true).Show(this)));
        help.DropDownItems.Add(Item("About", Keys.None, () => MessageBox.Show(this,
            "Max Scanning 1.0\n\n" + (L.Zh ? "以 SpaceSniffer 为蓝本、功能对齐的高性能重写版。\nNTFS MFT 直读 + 多线程扫描 + Direct2D GPU 渲染。" : "A high-performance, feature-parity rewrite inspired by SpaceSniffer.\nRaw NTFS MFT reading + parallel scanning + Direct2D GPU rendering.") +
            $"\n\n.NET {Environment.Version} · {Environment.ProcessorCount} threads · {(Native.IsAdmin() ? "Administrator" : "Standard user")}", L.T("About"))));

        _menu.Items.AddRange(new ToolStripItem[] { file, edit, view, opts, win, help });
    }

    void BuildToolbar()
    {
        _tool.Items.Clear();
        ToolStripButton B(string text, string tip, Action a) { var b = new ToolStripButton(text, null, (_, _) => a()) { ToolTipText = L.T(tip), DisplayStyle = ToolStripItemDisplayStyle.Text }; _tool.Items.Add(b); return b; }
        B("📂 " + L.T("Scan"), "New scan...", ShowStartDialog);
        B("💾", "Save snapshot...", SaveSnapshot);
        B("📤", "Export...", ShowExport);
        _tool.Items.Add(new ToolStripSeparator());
        _btnBack = B("◀", "Back", () => Current?.Back());
        _btnUp = B("▲", "Up one level", () => Current?.Up());
        _btnHome = B("⌂", "Go to root", () => Current?.Home());
        _tool.Items.Add(new ToolStripSeparator());
        B("－", "Less detail", () => Current?.LessDetail());
        B("＋", "More detail", () => Current?.MoreDetail());
        _tool.Items.Add(new ToolStripSeparator());
        _btnPause = B("⏸", "Pause", () => Current?.TogglePause());
        B("⏹", "Stop", () => Current?.CancelScan());
        B("⟳", "Rescan", () => Current?.Rescan());
        _btnWatch = B("👁", "Start watching", ToggleWatch);
        _tool.Items.Add(new ToolStripSeparator());
        _tool.Items.Add(new ToolStripLabel(L.T("Filter") + ":"));
        _filterBox.KeyDown -= FilterKey; _filterBox.KeyDown += FilterKey;
        _tool.Items.Add(_filterBox);
        B("✔", "Apply", ApplyFilter);
        B("✖", "Clear", () => { _filterBox.Text = ""; ApplyFilter(); });
        _tool.Items.Add(new ToolStripSeparator());
        B("⚙", "Configuration...", ShowConfig);
        B("?", "Filter help", () => new HelpForm(true).Show(this));
    }

    void CopyPath() { var v = Current; if (v?.Tree != null && v.TargetNode >= 0) Clipboard.SetText(v.Tree.FullPath(v.TargetNode)); }

    void DeleteTarget(bool permanent)
    {
        var v = Current;
        if (v?.Tree != null && v.TargetNode >= 0 && v.TargetNode != v.Tree.Root && !v.Tree.IsVirtual(v.TargetNode)) v.DeleteNode(v.TargetNode, permanent);
    }

    void FilterKey(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyFilter(); } }

    void ApplyFilter()
    {
        var v = Current;
        if (v == null) return;
        string f = _filterBox.Text.Trim();
        if (f.Length > 0 && !_filterBox.Items.Contains(f)) _filterBox.Items.Insert(0, f);
        v.SetFilter(f);
    }

    // ------------------------------------------------------------ actions
    void ShowStartDialog()
    {
        using var d = new StartDialog();
        if (d.ShowDialog(this) == DialogResult.OK && d.SelectedPath != null) NewScan(d.SelectedPath);
    }

    ScanView AddView(string title)
    {
        var v = new ScanView();
        var page = new TabPage(title) { Padding = new Padding(0) };
        page.Controls.Add(v);
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
        v.StateChanged += _ => { if (v == Current) UpdateState(); UpdateTitle(v, page); };
        v.HoverChanged += (_, n) => ShowHover(v, n);
        v.Log += (m, w) => { _console.Add(m, w); if (w && Config.Current.PopupConsoleOnEvent && !_console.Visible) _console.Show(this); };
        return v;
    }

    public void NewScan(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var v = AddView(path);
        v.StartScan(path);
        UpdateState();
    }

    void CloneView()
    {
        var c = Current;
        if (c?.Tree == null) return;
        var v = AddView(c.Tree.Name(c.ViewRoot));
        v.SetTree(c.Tree, c.Tree.Root);
        if (c.ViewRoot != c.Tree.Root) v.NavigateTo(c.ViewRoot, true, false);
    }

    void CloseCurrent()
    {
        var page = _tabs.SelectedTab;
        if (page == null) return;
        (page.Controls[0] as ScanView)?.CancelScan();
        _tabs.TabPages.Remove(page);
        page.Dispose();
        UpdateState();
    }

    void TabsMouseUp(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle) return;
        for (int i = 0; i < _tabs.TabCount; i++)
            if (_tabs.GetTabRect(i).Contains(e.Location)) { _tabs.SelectedIndex = i; CloseCurrent(); break; }
    }

    void ToggleWatch()
    {
        var v = Current;
        if (v?.Tree == null) return;
        if (v.Watching) v.StopWatch(); else v.StartWatch();
    }

    void OpenSnapshot()
    {
        using var d = new OpenFileDialog { Filter = $"Max Scanning snapshot|*{Snapshot.Extension}|*.*|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) LoadSnapshot(d.FileName);
    }

    void LoadSnapshot(string file)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var t = Snapshot.Load(file);
            var v = AddView(Path.GetFileName(file));
            v.SetTree(t, t.Root);
            _console.Add($"{L.T("Snapshot")}: {file} — {t.Count:N0} nodes, {sw.ElapsedMilliseconds} ms", false);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void SaveSnapshot()
    {
        var v = Current;
        if (v?.Tree == null) return;
        string name = string.Concat(v.Tree.Name(v.Tree.Root).Split(Path.GetInvalidFileNameChars())).Trim();
        using var d = new SaveFileDialog { Filter = $"Max Scanning snapshot|*{Snapshot.Extension}", FileName = $"{(name.Length == 0 ? "scan" : name)}_{DateTime.Now:yyyyMMdd_HHmm}{Snapshot.Extension}" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var sw = Stopwatch.StartNew();
            Snapshot.Save(v.Tree, d.FileName);
            _console.Add($"{L.T("Snapshot")}: {d.FileName} ({Util.FormatSize(new FileInfo(d.FileName).Length)}, {sw.ElapsedMilliseconds} ms)", false);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void ShowExport()
    {
        var v = Current;
        if (v?.Tree == null) return;
        using var f = new ExportForm(v);
        f.ShowDialog(this);
    }

    void ShowConfig()
    {
        using var f = new ConfigForm();
        f.ShowDialog(this);
    }

    void SetLang(string l) { var c = Config.Current.Clone(); c.Language = l; Config.Replace(c); }

    void RestartAsAdmin()
    {
        try
        {
            var args = Current?.ScanPath != null ? $"\"{Current.ScanPath}\"" : "";
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch { }
    }

    // ------------------------------------------------------------ state
    void UpdateTitle(ScanView v, TabPage page)
    {
        string name = v.Tree != null ? (v.ViewRoot == v.Tree.Root ? v.Tree.RootPath : v.Tree.Name(v.ViewRoot)) : v.ScanPath ?? "";
        string s = v.Scanning ? $"⏳ {name}" : v.Watching ? $"👁 {name}" : name;
        if (!v.Filter.IsEmpty) s += " ⧩";
        if (page.Text != s) page.Text = s;
    }

    void UpdateState()
    {
        var v = Current;
        if (_btnBack == null) return;
        _btnBack.Enabled = v?.CanBack == true;
        _btnUp.Enabled = v?.CanUp == true;
        _btnHome.Enabled = v?.Tree != null;
        _btnPause.Enabled = v?.Scanning == true;
        _btnPause.Text = v?.Pause.Paused == true ? "▶" : "⏸";
        _btnPause.ToolTipText = L.T(v?.Pause.Paused == true ? "Resume" : "Pause");
        _btnWatch.Checked = v?.Watching == true;
        _btnWatch.ToolTipText = L.T(v?.Watching == true ? "Stop watching" : "Start watching");
        if (v != null && _filterBox.Text != v.Filter.Text && !_filterBox.Focused) _filterBox.Text = v.Filter.Text;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var v = Current;
        if (v == null) { _stInfo.Text = L.T("Ready"); _stProg.Visible = false; return; }
        if (v.Scanning && v.Progress != null)
        {
            var p = v.Progress;
            _stProg.Visible = p.Percent >= 0;
            if (p.Percent >= 0) _stProg.Value = (int)p.Percent;
            _stInfo.Text = $"{L.T("Scanning")}… {p.Files:N0} {L.T("files")} · {p.Dirs:N0} {L.T("folders")} · {p.Clock.Elapsed.TotalSeconds:F1} s";
        }
        else if (v.Tree != null)
        {
            _stProg.Visible = false;
            var t = v.Tree;
            _stInfo.Text = $"{L.T("Method")}: {t.ScanMethod} · {L.T("Elapsed")}: {t.ScanTime.TotalSeconds:F2} s · {t.Files[t.Root]:N0} {L.T("files")} · {t.Dirs[t.Root]:N0} {L.T("folders")}"
                           + (t.RootIsVolume ? $" · {L.T("Free")}: {Util.FormatSize(t.VolumeFree)} / {Util.FormatSize(t.VolumeTotal)}" : "");
        }
        _stMem.Text = $"{L.T("Memory")}: {Util.FormatSize(Environment.WorkingSet)}";
    }

    void ShowHover(ScanView v, int n)
    {
        var t = v.Tree;
        if (t == null || n < 0) { _stHover.Text = ""; return; }
        _stHover.Text = t.IsVirtual(n) ? $"{L.T(t.Kind[n] == NodeKind.Free ? "Free space" : "Unknown space")}: {Util.FormatSize(t.Size[n])}"
            : $"{t.FullPath(n)}   {Util.FormatSize(v.SizeOf(n))}" + (t.IsContainer(n) ? $"   {t.Files[n]:N0} {L.T("files")}" : "");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_filterBox.Focused) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Control | Keys.C: CopyPath(); return true;
            case Keys.Delete: DeleteTarget(false); return true;
            case Keys.Shift | Keys.Delete: DeleteTarget(true); return true;
        }
        var v = Current;
        switch (keyData)
        {
            case Keys.Back: v?.Up(); return true;
            case Keys.Home: v?.Home(); return true;
            case Keys.Add: case Keys.Oemplus: v?.MoreDetail(); return true;
            case Keys.Subtract: case Keys.OemMinus: v?.LessDetail(); return true;
            case Keys.Control | Keys.F: _filterBox.Focus(); return true;
            case Keys.Escape: if (v?.Scanning == true) v.CancelScan(); return true;
            case Keys.Space: if (v?.Scanning == true) v.TogglePause(); return true;
            case Keys.Enter: if (v?.Tree != null && v.TargetNode >= 0) v.NavigateTo(v.TargetNode); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(43, 45, 49));
            g.FillRectangle(new SolidBrush(Color.FromArgb(230, 213, 154)), 2, 2, 17, 28);
            g.FillRectangle(new SolidBrush(Color.FromArgb(158, 195, 234)), 21, 2, 9, 16);
            g.FillRectangle(new SolidBrush(Color.FromArgb(150, 210, 150)), 21, 20, 9, 10);
            g.FillRectangle(new SolidBrush(Color.FromArgb(158, 195, 234)), 4, 12, 13, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
