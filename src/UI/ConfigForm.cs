using System.Drawing;
using MaxScanning.Settings;

namespace MaxScanning.UI;

/// <summary>Configuration dialog: Graphics / Effects / Actions / Colors, import/export, defaults.</summary>
public sealed class ConfigForm : Form
{
    Config _c = Config.Current.Clone();
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly List<Action> _apply = new();

    public ConfigForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Max Scanning — " + L.T("Configuration...").TrimEnd('.');
        Size = new Size(560, 560);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);
        Build();

        var ok = new Button { Text = L.T("OK"), AutoSize = true };
        ok.Click += (_, _) => { foreach (var a in _apply) a(); Config.Replace(_c); DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button { Text = L.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var defaults = new Button { Text = L.T("Defaults"), AutoSize = true };
        defaults.Click += (_, _) => { var lang = _c.Language; var rec = _c.RecentPaths; var pre = _c.ExportPresets; _c = new Config { Language = lang, RecentPaths = rec, ExportPresets = pre }; Rebuild(); };
        var imp = new Button { Text = L.T("Import..."), AutoSize = true };
        imp.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "JSON|*.json" };
            if (d.ShowDialog(this) == DialogResult.OK) { _c = Config.Load(d.FileName); Rebuild(); }
        };
        var exp = new Button { Text = L.T("Export config..."), AutoSize = true };
        exp.Click += (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = "maxscanning_config.json" };
            if (d.ShowDialog(this) == DialogResult.OK) { foreach (var a in _apply) a(); _c.Save(d.FileName); }
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bar.Controls.AddRange(new Control[] { cancel, ok, defaults, exp, imp });
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(_tabs);
        Controls.Add(bar);
    }

    void Rebuild() { _tabs.TabPages.Clear(); _apply.Clear(); Build(); }

    TableLayoutPanel Page(string title)
    {
        var page = new TabPage(L.T(title)) { Padding = new Padding(10), UseVisualStyleBackColor = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        page.Controls.Add(t);
        _tabs.TabPages.Add(page);
        return t;
    }

    static void Row(TableLayoutPanel t, string label, Control c)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = L.T(label), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0) });
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Controls.Add(c);
    }

    void Num(TableLayoutPanel t, string label, decimal value, decimal min, decimal max, Action<decimal> set, int decimals = 0, decimal inc = 1)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = inc, Value = Math.Clamp(value, min, max) };
        Row(t, label, n);
        _apply.Add(() => set(n.Value));
    }

    void Slider(TableLayoutPanel t, string label, float value, float min, float max, Action<float> set)
    {
        var tb = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Value = (int)Math.Clamp((value - min) / (max - min) * 100, 0, 100), AutoSize = false, Height = 32 };
        Row(t, label, tb);
        _apply.Add(() => set(min + (max - min) * tb.Value / 100f));
    }

    void Check(TableLayoutPanel t, string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Text = L.T(label), Checked = value, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(cb);
        t.SetColumnSpan(cb, 2);
        _apply.Add(() => set(cb.Checked));
    }

    void Color(TableLayoutPanel t, string label, int argb, Action<int> set)
    {
        int cur = argb;
        var b = new Button { Text = "", Height = 26, BackColor = System.Drawing.Color.FromArgb(argb), FlatStyle = FlatStyle.Flat };
        b.Click += (_, _) =>
        {
            using var d = new ColorDialog { Color = System.Drawing.Color.FromArgb(cur), FullOpen = true };
            if (d.ShowDialog(this) == DialogResult.OK) { cur = d.Color.ToArgb(); b.BackColor = d.Color; }
        };
        Row(t, label, b);
        _apply.Add(() => set(cur));
    }

    void Build()
    {
        var g = Page("Graphics");
        Num(g, "Minimum cell size (px)", _c.MinCellSize, 2, 64, v => _c.MinCellSize = (int)v);
        Slider(g, "Aspect ratio (left = tall, right = wide)", _c.AspectRatio, 0.4f, 2.5f, v => _c.AspectRatio = v);
        Num(g, "Initial detail level", _c.InitialDetail, 1, 12, v => _c.InitialDetail = (int)v);
        Num(g, "Font size", (decimal)_c.FontSize, 6, 20, v => _c.FontSize = (float)v, 1, 0.5m);
        Check(g, "Show free space", _c.ShowFreeSpace, v => _c.ShowFreeSpace = v);
        Check(g, "Show unknown space", _c.ShowUnknownSpace, v => _c.ShowUnknownSpace = v);
        Check(g, "Sort items by size", _c.SortBySize, v => _c.SortBySize = v);
        Check(g, "Cushion shading", _c.CushionShading, v => _c.CushionShading = v);
        Check(g, "Use size on disk instead of size", _c.ShowSizeOnDisk, v => _c.ShowSizeOnDisk = v);

        var e = Page("Effects");
        Num(e, "Refresh delay (ms, low = smooth)", _c.RefreshDelayMs, 50, 5000, v => _c.RefreshDelayMs = (int)v, 0, 50);
        Num(e, "Zoom time (ms)", _c.ZoomTimeMs, 0, 2000, v => _c.ZoomTimeMs = (int)v, 0, 50);
        Check(e, "Dynamic zoom (relayout while zooming)", _c.DynamicZoom, v => _c.DynamicZoom = v);

        var a = Page("Actions");
        Check(a, "Use NTFS MFT turbo scan when possible", _c.PreferMft, v => _c.PreferMft = v);
        Num(a, "Scan threads", _c.ScanThreads, 1, 128, v => _c.ScanThreads = (int)v);
        Check(a, "Scan alternate data streams (slow)", _c.AlternateStreams, v => _c.AlternateStreams = v);
        Check(a, "Pop up event log console on new events", _c.PopupConsoleOnEvent, v => _c.PopupConsoleOnEvent = v);
        Check(a, "Log scan warnings", _c.LogScanWarnings, v => _c.LogScanWarnings = v);
        Check(a, "Debug log", _c.DebugLog, v => _c.DebugLog = v);
        Check(a, "Flash window when scan completes", _c.FlashWhenDone, v => _c.FlashWhenDone = v);
        Check(a, "Keep tooltip when mouse moves between items", _c.KeepTooltip, v => _c.KeepTooltip = v);
        Check(a, "Confirm before delete", _c.ConfirmDelete, v => _c.ConfirmDelete = v);

        var c = Page("Colors");
        Color(c, "Drive", _c.ColorDrive, v => _c.ColorDrive = v);
        Color(c, "Folder", _c.ColorFolder, v => _c.ColorFolder = v);
        Color(c, "File", _c.ColorFile, v => _c.ColorFile = v);
        Color(c, "Free space", _c.ColorFree, v => _c.ColorFree = v);
        Color(c, "Unknown space", _c.ColorUnknown, v => _c.ColorUnknown = v);
        Color(c, "Stream", _c.ColorStream, v => _c.ColorStream = v);
        Slider(c, "Level contrast", _c.LevelContrast, 0, 0.3f, v => _c.LevelContrast = v);
        Slider(c, "Border contrast", _c.BorderContrast, 0, 0.8f, v => _c.BorderContrast = v);
        Slider(c, "Highlight on hover", _c.HighlightHover, 0, 0.6f, v => _c.HighlightHover = v);
        Slider(c, "Fade on mouse leave", _c.HighlightLeave, 0, 0.5f, v => _c.HighlightLeave = v);
        Num(c, "Highlight levels", _c.HighlightLevels, 0, 8, v => _c.HighlightLevels = (int)v);
    }
}
