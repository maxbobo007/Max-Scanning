using System.Drawing;
using System.Text;
using System.Text.Json;
using MaxScanning.IO;
using MaxScanning.Settings;
using MaxScanning.View;

namespace MaxScanning.UI;

/// <summary>Template export of the current view (header / detail / footer, sort, depth, presets, live preview).</summary>
public sealed class ExportForm : Form
{
    readonly ScanView _view;
    readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly TextBox _header = Box(), _detail = Box(), _footer = Box();
    readonly ComboBox _sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly ComboBox _escape = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly NumericUpDown _depth = new() { Minimum = 0, Maximum = 64, Width = 60 };
    readonly NumericUpDown _minSize = new() { Minimum = 0, Maximum = 1_000_000_000, Width = 90, ThousandsSeparator = true };
    readonly CheckBox _files = new() { Text = L.T("Include files"), AutoSize = true }, _folders = new() { Text = L.T("Include folders"), AutoSize = true };
    readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.White };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    List<ExportPreset> _presets;
    bool _loading;

    static TextBox Box() => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), AcceptsReturn = true };

    public ExportForm(ScanView view)
    {
        _view = view;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.T("Export current view");
        Size = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);

        _sort.Items.AddRange(new object[] { L.T("Unsorted"), L.T("Size (desc)"), L.T("Size (asc)"), L.T("Name (A-Z)"), L.T("Name (Z-A)"), L.T("Newest first") });
        _escape.Items.AddRange(Enum.GetNames<ExportEscape>());

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 6, 0) };
        top.Controls.Add(new Label { Text = L.T("Preset"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_preset);
        var save = new Button { Text = L.T("Save preset"), AutoSize = true };
        var del = new Button { Text = L.T("Delete preset"), AutoSize = true };
        var imp = new Button { Text = L.T("Import presets..."), AutoSize = true };
        var exp = new Button { Text = L.T("Export presets..."), AutoSize = true };
        top.Controls.AddRange(new Control[] { save, del, imp, exp });

        var opts = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 4, 6, 0) };
        opts.Controls.Add(new Label { Text = L.T("Sort"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) }); opts.Controls.Add(_sort);
        opts.Controls.Add(new Label { Text = L.T("Max depth (0 = all)"), AutoSize = true, Margin = new Padding(12, 6, 4, 0) }); opts.Controls.Add(_depth);
        opts.Controls.Add(new Label { Text = L.T("Minimum size") + " (B)", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }); opts.Controls.Add(_minSize);
        opts.Controls.Add(_files); opts.Controls.Add(_folders);
        opts.Controls.Add(new Label { Text = L.T("Escape"), AutoSize = true, Margin = new Padding(12, 6, 4, 0) }); opts.Controls.Add(_escape);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(6) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        left.Controls.Add(new Label { Text = L.T("Header"), AutoSize = true });
        left.Controls.Add(_header);
        left.Controls.Add(new Label { Text = L.T("Detail (one line per item)"), AutoSize = true });
        left.Controls.Add(_detail);
        left.Controls.Add(new Label { Text = L.T("Footer"), AutoSize = true });
        left.Controls.Add(_footer);
        left.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, Font = new Font("Consolas", 8.5f),
            Text = L.T("Placeholders") + ":\r\n{path} {name} {ext} {type} {size} {size_h} {alloc} {alloc_h} {files} {dirs}\r\n{modified} {created} {accessed} {attr} {tag} {depth} {indent} {tree} {percent}\r\n" +
                   "header/footer: {root} {date} {total} {total_h} {total_files} {total_dirs} {method}\r\n{key,12} = right-align in 12 chars, {key,-12} = left-align. \\n \\t accepted."
        });
        var right = new GroupBox { Text = L.T("Preview (may show partial data)"), Dock = DockStyle.Fill };
        right.Controls.Add(_preview);
        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var close = new Button { Text = L.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var go = new Button { Text = L.T("Export to file..."), AutoSize = true };
        var copy = new Button { Text = "📋 " + L.T("Copy to clipboard"), AutoSize = true };
        bottom.Controls.AddRange(new Control[] { close, go, copy });
        CancelButton = close;

        Controls.Add(grid);
        Controls.Add(opts);
        Controls.Add(top);
        Controls.Add(bottom);

        LoadPresets(Config.Current.LastExportPreset);
        _preset.SelectedIndexChanged += (_, _) => ShowPreset();
        foreach (var c in new Control[] { _header, _detail, _footer }) c.TextChanged += (_, _) => Changed();
        _sort.SelectedIndexChanged += (_, _) => Changed(); _escape.SelectedIndexChanged += (_, _) => Changed();
        _depth.ValueChanged += (_, _) => Changed(); _minSize.ValueChanged += (_, _) => Changed();
        _files.CheckedChanged += (_, _) => Changed(); _folders.CheckedChanged += (_, _) => Changed();
        _debounce.Tick += (_, _) => { _debounce.Stop(); UpdatePreview(); };
        save.Click += (_, _) => SavePreset();
        del.Click += (_, _) => DeletePreset();
        imp.Click += (_, _) => ImportPresets();
        exp.Click += (_, _) => ExportPresets();
        go.Click += (_, _) => DoExport();
        copy.Click += (_, _) => { var s = Run(int.MaxValue); if (s.Length > 0) Clipboard.SetText(s); };
        UpdatePreview();
    }

    void LoadPresets(string select)
    {
        _presets = Config.Current.AllPresets();
        _preset.Items.Clear();
        foreach (var p in _presets) _preset.Items.Add(p.Name + (p.BuiltInPreset ? "" : " *"));
        int i = Math.Max(0, _presets.FindIndex(p => p.Name == select));
        _preset.SelectedIndex = i;
        ShowPreset();
    }

    void ShowPreset()
    {
        var p = _presets[Math.Max(0, _preset.SelectedIndex)];
        _loading = true;
        _header.Text = p.Header.Replace("\n", "\r\n"); _detail.Text = p.Detail.Replace("\n", "\r\n"); _footer.Text = p.Footer.Replace("\n", "\r\n");
        _sort.SelectedIndex = (int)p.Sort; _escape.SelectedIndex = (int)p.Escape;
        _depth.Value = p.MaxDepth; _minSize.Value = Math.Min(_minSize.Maximum, p.MinSize);
        _files.Checked = p.IncludeFiles; _folders.Checked = p.IncludeFolders;
        _loading = false;
        Changed();
    }

    ExportPreset Current()
    {
        var b = _presets[Math.Max(0, _preset.SelectedIndex)];
        return new ExportPreset
        {
            Name = b.Name, FileExtension = b.FileExtension,
            Header = _header.Text.Replace("\r\n", "\n"), Detail = _detail.Text.Replace("\r\n", "\n"), Footer = _footer.Text.Replace("\r\n", "\n"),
            Sort = (ExportSort)Math.Max(0, _sort.SelectedIndex), Escape = (ExportEscape)Math.Max(0, _escape.SelectedIndex),
            MaxDepth = (int)_depth.Value, MinSize = (long)_minSize.Value, IncludeFiles = _files.Checked, IncludeFolders = _folders.Checked
        };
    }

    void Changed() { if (_loading) return; _debounce.Stop(); _debounce.Start(); }

    string Run(int maxLines) => Exporter.Export(_view.Tree, _view.ViewRoot, Current(), _view.Filter, maxLines, _view.SizeOf);

    void UpdatePreview()
    {
        try { _preview.Text = Run(400).Replace("\r\n", "\n").Replace("\n", "\r\n"); }
        catch (Exception ex) { _preview.Text = ex.Message; }
    }

    void SavePreset()
    {
        string name = Prompt(L.T("Preset name"), _presets[_preset.SelectedIndex].BuiltInPreset ? _presets[_preset.SelectedIndex].Name + " (custom)" : _presets[_preset.SelectedIndex].Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = Current(); p.Name = name.Trim(); p.BuiltInPreset = false;
        var c = Config.Current.Clone();
        c.ExportPresets.RemoveAll(x => x.Name == p.Name);
        c.ExportPresets.Add(p);
        c.LastExportPreset = p.Name;
        Config.Replace(c);
        LoadPresets(p.Name);
    }

    void DeletePreset()
    {
        var p = _presets[_preset.SelectedIndex];
        if (p.BuiltInPreset) { MessageBox.Show(this, L.T("Built-in presets cannot be deleted")); return; }
        var c = Config.Current.Clone();
        c.ExportPresets.RemoveAll(x => x.Name == p.Name);
        Config.Replace(c);
        LoadPresets("CSV");
    }

    void ImportPresets()
    {
        using var d = new OpenFileDialog { Filter = "JSON|*.json" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<ExportPreset>>(File.ReadAllText(d.FileName)) ?? new();
            var c = Config.Current.Clone();
            foreach (var p in list) { p.BuiltInPreset = false; c.ExportPresets.RemoveAll(x => x.Name == p.Name); c.ExportPresets.Add(p); }
            Config.Replace(c);
            LoadPresets(list.FirstOrDefault()?.Name ?? "CSV");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }

    void ExportPresets()
    {
        using var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = "maxscanning_export_presets.json" };
        if (d.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(d.FileName, JsonSerializer.Serialize(Config.Current.ExportPresets, new JsonSerializerOptions { WriteIndented = true }));
    }

    void DoExport()
    {
        var p = Current();
        string ext = p.FileExtension.Contains('|') ? p.FileExtension : "Text|*.txt";
        using var d = new SaveFileDialog { Filter = ext + "|All|*.*", FileName = Path.GetFileName(_view.Tree.FullPath(_view.ViewRoot).TrimEnd('\\', ':')) + "_" + DateTime.Now.ToString("yyyyMMdd") };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = Run(int.MaxValue);
        File.WriteAllText(d.FileName, text, new UTF8Encoding(true));
        var c = Config.Current.Clone(); c.LastExportPreset = p.Name; Config.Replace(c);
        MessageBox.Show(this, $"{L.T("Exported")}: {d.FileName}\n{text.Count(ch => ch == '\n'):N0} {L.T("lines")}, {sw.ElapsedMilliseconds} ms", L.T("Done"));
    }

    static string Prompt(string title, string value)
    {
        using var f = new Form { Text = title, Size = new Size(420, 140), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var tb = new TextBox { Text = value, Left = 12, Top = 14, Width = 380 };
        var ok = new Button { Text = L.T("OK"), Left = 236, Top = 48, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = L.T("Cancel"), Left = 318, Top = 48, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }
}
