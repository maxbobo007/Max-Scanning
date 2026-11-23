using System.Drawing;
using System.Text;
using MaxScanning.Core;
using MaxScanning.Settings;

namespace MaxScanning.UI;

/// <summary>"Choose a drive or enter a path" dialog.</summary>
public sealed class StartDialog : Form
{
    readonly ListView _drives = new() { View = System.Windows.Forms.View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    readonly ComboBox _path = new() { Dock = DockStyle.Fill };
    public string SelectedPath { get; private set; }

    public StartDialog()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.T("Choose a drive or enter a path");
        Size = new Size(720, 460);
        MinimumSize = new Size(520, 320);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        MinimizeBox = false;

        _drives.Columns.Add(L.T("Drive"), 70);
        _drives.Columns.Add(L.T("Label"), 150);
        _drives.Columns.Add(L.T("File system"), 80);
        _drives.Columns.Add(L.T("Total"), 90, HorizontalAlignment.Right);
        _drives.Columns.Add(L.T("Free"), 90, HorizontalAlignment.Right);
        _drives.Columns.Add(L.T("Used"), 160);
        _drives.OwnerDraw = true;
        _drives.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _drives.DrawSubItem += DrawSub;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var it = new ListViewItem(new[] { d.Name, d.VolumeLabel, d.DriveFormat, Util.FormatSize(d.TotalSize), Util.FormatSize(d.TotalFreeSpace), "" }) { Tag = d };
                _drives.Items.Add(it);
            }
            catch { }
        }
        _drives.SelectedIndexChanged += (_, _) => { if (_drives.SelectedItems.Count > 0) _path.Text = ((DriveInfo)_drives.SelectedItems[0].Tag).Name; };
        _drives.DoubleClick += (_, _) => Go();
        _path.Items.AddRange(Config.Current.RecentPaths.ToArray());
        _path.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(); } };

        var browse = new Button { Text = L.T("Browse..."), AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = L.T("Select a drive or folder to scan"), SelectedPath = _path.Text };
            if (fb.ShowDialog(this) == DialogResult.OK) _path.Text = fb.SelectedPath;
        };
        var start = new Button { Text = L.T("Start"), AutoSize = true, DialogResult = DialogResult.None };
        start.Click += (_, _) => Go();
        var cancel = new Button { Text = L.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = start; CancelButton = cancel;

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 3, Padding = new Padding(6, 6, 6, 0) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(new Label { Text = L.T("Path:").Replace("&", ""), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 0);
        pathRow.Controls.Add(_path, 1, 0);
        pathRow.Controls.Add(browse, 2, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(start);
        var hint = new Label
        {
            AutoSize = true, ForeColor = Native.IsAdmin() ? Color.DarkGreen : Color.DarkOrange, Margin = new Padding(6, 8, 20, 0),
            Text = Native.IsAdmin() ? "⚡ MFT turbo scan" : L.T("Not elevated: using parallel scan. Restart as administrator for MFT turbo scan.")
        };
        buttons.Controls.Add(hint);

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        listHost.Controls.Add(_drives);
        Controls.Add(listHost);
        Controls.Add(pathRow);
        Controls.Add(buttons);
        if (_drives.Items.Count > 0) _drives.Items[0].Selected = true;
    }

    void DrawSub(object s, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 5) { e.DrawDefault = true; return; }
        e.DrawBackground();
        var d = (DriveInfo)e.Item!.Tag;
        double used = d.TotalSize > 0 ? 1 - d.TotalFreeSpace / (double)d.TotalSize : 0;
        var r = Rectangle.Inflate(e.Bounds, -4, -4);
        e.Graphics.FillRectangle(Brushes.Gainsboro, r);
        using var b = new SolidBrush(used > 0.9 ? Color.IndianRed : Color.SteelBlue);
        e.Graphics.FillRectangle(b, r.X, r.Y, (int)(r.Width * used), r.Height);
        TextRenderer.DrawText(e.Graphics, $"{used * 100:F0}%", Font, r, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void Go()
    {
        string p = _path.Text.Trim().Trim('"');
        if (p.Length == 0) return;
        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (!Directory.Exists(p)) { MessageBox.Show(this, p, L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        SelectedPath = p;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Event log console (SpaceSniffer "日志控制台").</summary>
public sealed class ConsoleForm : Form
{
    readonly ListView _list = new() { View = System.Windows.Forms.View.Details, FullRowSelect = true, Dock = DockStyle.Fill, VirtualMode = false };
    readonly List<(DateTime t, string m, bool w)> _items = new();

    public ConsoleForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.T("Event log console");
        Size = new Size(820, 360);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        _list.Columns.Add("", 70); _list.Columns.Add("", 70); _list.Columns.Add("", 1200);
        _list.HeaderStyle = ColumnHeaderStyle.None;
        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        tool.Items.Add(L.T("Copy log"), null, (_, _) => { var t = Dump(); if (t.Length > 0) Clipboard.SetText(t); });
        tool.Items.Add(L.T("Clear log"), null, (_, _) => { _items.Clear(); _list.Items.Clear(); });
        tool.Items.Add(L.T("Save log..."), null, (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = $"maxscanning_log_{DateTime.Now:yyyyMMdd_HHmm}.txt" };
            if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, Dump(), Encoding.UTF8);
        });
        Controls.Add(_list);
        Controls.Add(tool);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Add(string msg, bool warn)
    {
        var now = DateTime.Now;
        _items.Add((now, msg, warn));
        var it = new ListViewItem(new[] { now.ToString("HH:mm:ss"), warn ? L.T("Warning") : L.T("Info"), msg }) { ForeColor = warn ? Color.DarkRed : Color.Black };
        _list.Items.Add(it);
        if (_list.Items.Count > 5000) _list.Items.RemoveAt(0);
        it.EnsureVisible();
    }

    string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (t, m, w) in _items) sb.AppendLine($"{t:yyyy-MM-dd HH:mm:ss}\t{(w ? "WARN" : "INFO")}\t{m}");
        return sb.ToString();
    }
}

/// <summary>Quick help / filter help.</summary>
public sealed class HelpForm : Form
{
    public HelpForm(bool filter)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.T(filter ? "Filter help" : "Quick help");
        Size = new Size(760, 600);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f), BackColor = Color.White, Text = (filter ? FilterHelp() : QuickHelp()).Replace("\n", "\r\n") };
        box.Select(0, 0);
        Controls.Add(box);
    }

    static string QuickHelp() => L.Zh ? """
Max Scanning — 快速帮助

【扫描】
  Ctrl+N 新建扫描：选择驱动器或输入路径（支持网络路径 \\server\share）。
  以管理员身份运行时，NTFS 卷使用 MFT 直读极速扫描（百万文件约 1 秒）；
  否则使用多线程目录枚举。扫描中：Space 暂停/继续，Esc 停止。
  F5 重新扫描，F6 开始/停止实时监听（文件变化自动更新视图）。

【浏览】
  双击方块：进入该文件夹；双击当前根：返回上一层。
  Backspace / 鼠标中键：上一层；Alt+← / 鼠标后退键：后退；Home：根目录。
  滚轮向上：向鼠标所在方向放大一层；滚轮向下：上一层。
  + / - 或 Ctrl+滚轮：增加/减少详细级别（嵌套层数）。
  顶部路径栏可点击跳转。Ctrl+T：以当前视图新建窗口（标签页）；Ctrl+W 关闭。

【操作】（右键菜单）
  打开、在资源管理器中显示、在此处打开命令提示符、复制路径、属性
  标签：Ctrl+1 红 / Ctrl+2 绿 / Ctrl+3 蓝 / Ctrl+4 黄，Ctrl+0 清除
  删除：Del 放入回收站，Shift+Del 永久删除
  重新扫描此文件夹

【过滤】 Ctrl+F 聚焦过滤框，回车应用。详见"过滤器帮助"。
【导出】 Ctrl+E：按模板导出（CSV / TXT / 树状 / HTML / JSON / 自定义）。
【快照】 Ctrl+S 保存、Ctrl+O 打开扫描快照（含标签）。可直接拖放 .mscan 文件或文件夹到窗口。

【命令行】
  MaxScanning.exe D:\                 打开并扫描
  MaxScanning.exe --load x.mscan      打开快照
  MaxScanning.exe D:\ --bench         无界面扫描并输出统计
  MaxScanning.exe D:\ --export a.csv --preset CSV --filter ">100mb"
  MaxScanning.exe D:\ --save-snapshot d.mscan
""" : """
Max Scanning — Quick help

SCANNING
  Ctrl+N new scan: pick a drive or type a path (UNC paths \\server\share supported).
  When running as administrator, NTFS volumes are scanned by reading the MFT directly
  (about one second per million files); otherwise a parallel directory enumeration is used.
  While scanning: Space pause/resume, Esc stop. F5 rescan, F6 start/stop live watching.

BROWSING
  Double-click a tile: zoom into that folder; double-click the current root: go up.
  Backspace / middle button: up one level; Alt+Left / mouse back button: back; Home: root.
  Wheel up: zoom one level toward the mouse; wheel down: up one level.
  + / - or Ctrl+wheel: more / less detail (nesting levels).
  The breadcrumb bar is clickable. Ctrl+T: new window (tab) on this view; Ctrl+W closes it.

ACTIONS (right-click menu)
  Open, open in Explorer, command prompt here, copy path, properties
  Tags: Ctrl+1 red / Ctrl+2 green / Ctrl+3 blue / Ctrl+4 yellow, Ctrl+0 clear
  Delete: Del to recycle bin, Shift+Del permanently. Rescan this folder.

FILTER  Ctrl+F focuses the filter box, Enter applies. See "Filter help".
EXPORT  Ctrl+E: template export (CSV / TXT / Tree / HTML / JSON / custom presets).
SNAPSHOTS  Ctrl+S save, Ctrl+O open (tags included). Drag & drop .mscan files or folders onto the window.

COMMAND LINE
  MaxScanning.exe D:\                 open and scan
  MaxScanning.exe --load x.mscan      open snapshot
  MaxScanning.exe D:\ --bench         headless scan with totals
  MaxScanning.exe D:\ --export a.csv --preset CSV --filter ">100mb"
  MaxScanning.exe D:\ --save-snapshot d.mscan
""";

    static string FilterHelp() => L.Zh ? """
过滤器语法（与 SpaceSniffer 兼容并扩展）

  用分号 ; 分隔多个条件。同类条件之间为"或"，不同类条件之间为"且"。
  在条件前加 | 表示排除（非）。过滤只作用于文件，文件夹显示其中匹配文件的大小。

  名称      *.jpg;*.png       通配符 * ?；不含通配符的词按包含匹配（log = *log*）
  大小      >100mb   <1kb   >=2gb   单位 b kb mb gb tb
  时间      <7d  >1y  <12h        按修改时间的"距今多久"：h 小时 d 天 w 周 m 月 y 年
                                  >3d = 3 天前以前修改；<3d = 最近 3 天内修改
  日期      modified>2024-01-01   created<2023-06-30   accessed>=2024-05-01
  标签      :red :green :blue :yellow   :tagged（任意标签）  （文件夹的标签会被其内容继承）
  属性      @h 隐藏  @s 系统  @r 只读  @c 压缩  @e 加密  @a 存档  @l 链接  @p 稀疏  @o 脱机
  文件夹    folder:node_modules   仅显示位于名称匹配的文件夹中的文件
  正则      regex:^IMG_\d+\.jpe?g$

示例
  *.mp4;*.mkv;>500mb            大于 500MB 的视频
  >1y;>100mb                    一年没改过、且大于 100MB 的文件
  |*.dll;|*.exe                 排除 dll 和 exe
  :red;|>1gb                    红色标签中小于 1GB 的部分
  folder:node_modules           所有 node_modules 里的文件
""" : """
Filter syntax (SpaceSniffer compatible, extended)

  Separate terms with ';'. Terms of the same kind are OR-ed, different kinds are AND-ed.
  Prefix a term with '|' to exclude it. Filters apply to files; folders show the size of matching files.

  name       *.jpg;*.png       wildcards * ?; a bare word matches as substring (log = *log*)
  size       >100mb  <1kb  >=2gb   units b kb mb gb tb
  age        <7d  >1y  <12h        time since modification: h d w m y
                                   >3d = modified more than 3 days ago; <3d = within the last 3 days
  date       modified>2024-01-01   created<2023-06-30   accessed>=2024-05-01
  tag        :red :green :blue :yellow   :tagged (any)   (folder tags are inherited by content)
  attribute  @h hidden @s system @r read-only @c compressed @e encrypted @a archive @l link @p sparse @o offline
  folder     folder:node_modules   only files inside folders whose name matches
  regex      regex:^IMG_\d+\.jpe?g$

Examples
  *.mp4;*.mkv;>500mb            videos larger than 500 MB
  >1y;>100mb                    files untouched for a year and larger than 100 MB
  |*.dll;|*.exe                 everything except dll and exe
  :red;|>1gb                    red-tagged content smaller than 1 GB
  folder:node_modules           everything inside node_modules folders
""";
}
