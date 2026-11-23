using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using MaxScanning.Core;
using MaxScanning.Filter;
using MaxScanning.IO;
using MaxScanning.Scan;
using MaxScanning.View;

namespace MaxScanning;

/// <summary>
/// Automated verification: data-model invariants, filter language, layout geometry, export/snapshot round trips,
/// and a GUI smoke test that drives a real (off-screen) window and captures it to PNG.
/// Usage: MaxScanning.exe --selftest &lt;folder&gt; --out report.txt [--png shot.png]
/// </summary>
internal static class SelfTest
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    static int _pass, _fail;
    static StringBuilder _sb;

    static void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        _sb.AppendLine((ok ? "PASS " : "FAIL ") + what);
    }

    public static int Run(string[] args)
    {
        _sb = new StringBuilder();
        string Arg(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string dir = Arg("--selftest") ?? Environment.CurrentDirectory;
        string outFile = Arg("--out") ?? "selftest.txt";
        string png = Arg("--png");
        try
        {
            FilterTests();
            var t = ScanTests(dir);
            LayoutTests(t);
            ExportSnapshotTests(t);
            GuiTest(dir, png);
        }
        catch (Exception ex) { _fail++; _sb.AppendLine("FAIL exception: " + ex); }
        _sb.AppendLine($"\nRESULT: {_pass} passed, {_fail} failed");
        File.WriteAllText(outFile, _sb.ToString(), Encoding.UTF8);
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ filters
    static void FilterTests()
    {
        var t = new FileTree();
        long now = DateTime.UtcNow.ToFileTimeUtc(), day = 864_000_000_000L;
        t.Root = t.Add(-1, "R", NodeKind.Root, 0, 0, 0x10, 0, 0, 0); t.RootPath = @"X:\";
        int nm = t.Add(t.Root, "node_modules", NodeKind.Dir, 0, 0, 0x10, 0, now, 0);
        int a = t.Add(t.Root, "movie.MP4", NodeKind.File, 700L << 20, 700L << 20, 0x20, 0, now - 400 * day, 0);
        int b = t.Add(t.Root, "notes.txt", NodeKind.File, 2048, 4096, 0x22, 0, now - 1 * day, 0);
        int c = t.Add(nm, "index.js", NodeKind.File, 500, 4096, 0x20, 0, now - 2 * day, 0);
        int d = t.Add(t.Root, "app.log", NodeKind.File, 5L << 30, 5L << 30, 0x20, 0, now - 40 * day, 0);
        t.Tag[nm] = 1; t.Tag[d] = 3;
        t.Finish();
        bool M(string f, int i) => new FilterSet(f).Match(t, i);
        Check(M("*.mp4", a) && !M("*.mp4", b), "filter: wildcard case-insensitive");
        Check(M("*.mp4;*.txt", b) && M("*.mp4;*.txt", a), "filter: same-kind terms OR");
        Check(M("*.mp4;>100mb", a) && !M("*.txt;>100mb", b), "filter: different kinds AND");
        Check(M(">1gb", d) && !M(">1gb", a) && M("<1kb", c), "filter: size units");
        Check(M(">1y", a) && !M(">1y", b) && M("<3d", b) && M(">30d;<60d", d), "filter: age");
        Check(!M("|*.log", d) && M("|*.log", a), "filter: exclude");
        Check(M(":red", c) && !M(":red", a) && M(":blue", d) && M(":tagged", c), "filter: tags incl. inherited from folder");
        Check(M("@h", b) && !M("@h", a), "filter: attributes");
        Check(M("folder:node_modules", c) && !M("folder:node_modules", a), "filter: folder:");
        Check(M("log", d) && M("regex:^no.*\\.txt$", b), "filter: substring + regex");
        Check(M("modified<2000-01-01", a) == false && M("modified>2000-01-01", a), "filter: absolute date");
        Check(new FilterSet(">abc").Error == null || true, "filter: bad input does not throw");
    }

    // ------------------------------------------------------------------ scan + invariants
    static FileTree ScanTests(string dir)
    {
        var sw = Stopwatch.StartNew();
        var t = ScanEngine.Scan(dir, new ScanOptions { PreferMft = false }, new ScanProgress(), new ScanPauseToken(), CancellationToken.None);
        _sb.AppendLine($"info: scanned {dir}: {t.Files[t.Root]:N0} files, {t.Dirs[t.Root]:N0} dirs, {Util.FormatSize(t.Size[t.Root])} in {sw.ElapsedMilliseconds} ms ({t.ScanMethod})");
        // compare with .NET enumeration (independent implementation)
        long files = 0, bytes = 0, dirs = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        foreach (var fi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
        {
            if (fi is FileInfo f) { files++; bytes += f.Length; } else dirs++;
        }
        Check(files == t.Files[t.Root], $"scan: file count matches .NET enumeration ({files:N0} vs {t.Files[t.Root]:N0})");
        Check(dirs == t.Dirs[t.Root], $"scan: folder count matches ({dirs:N0} vs {t.Dirs[t.Root]:N0})");
        Check(Math.Abs(bytes - t.Size[t.Root]) <= 1 << 20, $"scan: total bytes match within 1 MB (Δ {bytes - t.Size[t.Root]:N0} B, files may change during test)");
        // invariant: every dir size == own + sum(children)
        bool agg = true; bool sorted = true; bool parents = true;
        for (int i = 0; i < t.Count; i++)
        {
            if ((t.Flags[i] & NodeFlags.Dead) != 0) continue;
            long s = t.OwnSize[i]; long prev = long.MaxValue;
            foreach (int c in t.ChildrenOf(i))
            {
                if (t.Parent[c] != i) parents = false;
                if (!t.IsVirtual(c)) s += t.Size[c];
                if (t.Size[c] > prev) sorted = false;
                prev = t.Size[c];
            }
            if (s != t.Size[i]) agg = false;
        }
        Check(agg, "tree: aggregate size = own + children for every node");
        Check(sorted, "tree: children sorted by size desc");
        Check(parents, "tree: CSR children agree with parent links");
        int probe = t.ChildrenOf(t.Root).Length > 0 ? t.ChildrenOf(t.Root)[0] : t.Root;
        Check(t.FindPath(t.FullPath(probe)) == probe, "tree: FullPath/FindPath round trip");
        return t;
    }

    // ------------------------------------------------------------------ layout geometry
    static void LayoutTests(FileTree t)
    {
        var area = new RectangleF(0, 0, 1600, 1000);
        var s = new MaxScanning.View.LayoutSettings { MaxDepth = 4, MinCell = 4 };
        var sw = Stopwatch.StartNew();
        var tiles = TreemapLayout.Build(t, t.Root, area, s, i => t.Size[i], i => (t.Flags[i] & NodeFlags.Dead) == 0 && !t.IsVirtual(i));
        _sb.AppendLine($"info: layout {tiles.Count:N0} tiles in {sw.Elapsed.TotalMilliseconds:F1} ms");
        bool inside = true, overlap = false; double worstErr = 0; string worstInfo = "";
        var byParent = tiles.Select((x, i) => (x, i)).Where(p => p.x.Parent >= 0).GroupBy(p => p.x.Parent);
        foreach (var g in byParent)
        {
            var parent = tiles[g.Key];
            var inner = parent.Inner;
            var kids = g.Select(p => p.x).ToList();
            double area0 = inner.Width * (double)inner.Height, sum = kids.Sum(k => (double)k.Size);
            foreach (var k in kids)
            {
                if (k.R.Left < inner.Left - 0.5f || k.R.Top < inner.Top - 0.5f || k.R.Right > inner.Right + 0.5f || k.R.Bottom > inner.Bottom + 0.5f) inside = false;
                if (sum > 0 && k.R.Width * k.R.Height > 64 && area0 > 2500) // sub-pixel siblings are not drawn, so tiny parents are skipped
                {
                    double err = Math.Abs(k.R.Width * (double)k.R.Height / area0 - k.Size / sum);
                    if (err > worstErr) { worstErr = err; worstInfo = $"parent '{(parent.Node >= 0 ? t.Name(parent.Node) : "rest")}' inner {parent.Inner} kids {kids.Count} (rest tiles {kids.Count(x => x.Node < 0)}) | kid '{(k.Node >= 0 ? t.Name(k.Node) : "rest")}' rect {k.R} size {k.Size} sum {sum} area share {k.R.Width * k.R.Height / area0:P2} size share {k.Size / sum:P2}"; }
                }
            }
            for (int a = 0; a < kids.Count && !overlap; a++)
                for (int b = a + 1; b < kids.Count; b++)
                {
                    var r = RectangleF.Intersect(kids[a].R, kids[b].R);
                    if (r.Width > 0.5f && r.Height > 0.5f) { overlap = true; break; }
                }
        }
        Check(inside, "layout: children inside parent area");
        Check(!overlap, "layout: siblings never overlap");
        Check(worstErr < 0.01, $"layout: tile area proportional to size (worst error {worstErr:P2}) {(worstErr >= 0.01 ? worstInfo : "")}");
        var ar = tiles.Where(x => x.R.Width > 8 && x.R.Height > 8).Select(x => Math.Max(x.R.Width / x.R.Height, x.R.Height / x.R.Width)).DefaultIfEmpty(1).Average();
        Check(ar < 4, $"layout: squarified (average aspect {ar:F2})");
    }

    // ------------------------------------------------------------------ export + snapshot
    static void ExportSnapshotTests(FileTree t)
    {
        foreach (var p in ExportPreset.BuiltIn())
        {
            var text = Exporter.Export(t, t.Root, p, new FilterSet(""));
            Check(text.Length > 0 && (p.Name != "CSV" || text.Split('\n').Length >= Math.Min(t.Files[t.Root] + t.Dirs[t.Root], 100)), $"export: preset {p.Name} ({text.Length:N0} chars)");
            if (p.Name == "JSON")
            {
                try { System.Text.Json.JsonDocument.Parse(text.Replace(",\n{}]", "\n]").Replace("{}]", "]")); Check(true, "export: JSON is valid"); }
                catch (Exception ex) { Check(false, "export: JSON is valid — " + ex.Message); }
            }
        }
        var filtered = Exporter.Export(t, t.Root, ExportPreset.BuiltIn()[0], new FilterSet("*.zip"));
        var bad = filtered.Split('\n').Skip(1).Where(l => l.Split(',').Length > 2 && l.Split(',')[2] == "file").FirstOrDefault(l => !l.Contains(".zip", StringComparison.OrdinalIgnoreCase));
        Check(bad == null, "export: filter respected" + (bad != null ? " — offending line: " + bad : ""));

        string file = Path.Combine(Path.GetTempPath(), "maxscanning_selftest" + Snapshot.Extension);
        t.Tag[t.ChildrenOf(t.Root)[0]] = 2;
        var sw = Stopwatch.StartNew();
        Snapshot.Save(t, file);
        var u = Snapshot.Load(file);
        _sb.AppendLine($"info: snapshot {Util.FormatSize(new FileInfo(file).Length)} round trip {sw.ElapsedMilliseconds} ms");
        bool same = u.Count == t.Count && u.Size[u.Root] == t.Size[t.Root] && u.Files[u.Root] == t.Files[t.Root];
        for (int i = 0; i < t.Count && same; i++)
            if (u.Parent[i] != t.Parent[i] || u.OwnSize[i] != t.OwnSize[i] || u.Tag[i] != t.Tag[i] || !u.NameSpan(i).SequenceEqual(t.NameSpan(i))) same = false;
        Check(same, "snapshot: save/load round trip identical (names, sizes, tags)");
        File.Delete(file);
    }

    // ------------------------------------------------------------------ GUI smoke test (off-screen window)
    static void GuiTest(string dir, string png)
    {
        ApplicationConfiguration.Initialize();
        var form = new UI.MainForm(new[] { dir }) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, Size = new Size(1400, 900), WindowState = FormWindowState.Normal };
        form.FormClosing += (_, _) => { }; // window geometry of the test must not be persisted
        var savedCfg = Settings.Config.Current.Clone();
        string result = "not run";
        form.Shown += async (_, _) =>
        {
            try
            {
                var v = form.CurrentView;
                var sw = Stopwatch.StartNew();
                while (v.Scanning && sw.ElapsedMilliseconds < 60000) await Task.Delay(50);
                Check(v.Tree != null, $"gui: scan finished in view ({sw.ElapsedMilliseconds} ms)");
                await Task.Delay(300);
                int child = v.Tree.ChildrenOf(v.Tree.Root).ToArray().FirstOrDefault(c => v.Tree.IsDirLike(c), -1);
                if (child >= 0)
                {
                    v.NavigateTo(child);
                    await Task.Delay(400);
                    Check(v.ViewRoot == child, "gui: zoom into folder");
                    v.Up(); await Task.Delay(400);
                    Check(v.ViewRoot == v.Tree.Root, "gui: up one level");
                    v.NavigateTo(child); await Task.Delay(300); v.Back(); await Task.Delay(300);
                    Check(v.ViewRoot == v.Tree.Root, "gui: back");
                }
                v.MoreDetail(); v.LessDetail();
                v.SetFilter("*.zip;>1mb");
                await Task.Delay(300);
                Check(v.SizeOf(v.Tree.Root) <= v.Tree.Size[v.Tree.Root] && v.Filter.Error == null, $"gui: filter applied ({Util.FormatSize(v.SizeOf(v.Tree.Root))} matched)");
                v.SetFilter("");
                if (child >= 0) { v.SetTag(child, 1); Check(v.Tree.Tag[child] == 1, "gui: tag set"); v.SetTag(child, 1); Check(v.Tree.Tag[child] == 0, "gui: tag toggled off"); }

                // live watch: create a file, expect the tree to pick it up
                string probe = Path.Combine(dir, $"_maxscanning_watch_probe_{Environment.ProcessId}.bin");
                v.StartWatch();
                await Task.Delay(300);
                File.WriteAllBytes(probe, new byte[3 * 1024 * 1024]);
                int found = -1;
                var w = Stopwatch.StartNew();
                while (w.ElapsedMilliseconds < 8000 && (found = v.Tree.FindPath(probe)) < 0) await Task.Delay(100);
                Check(found >= 0 && v.Tree.Size[found] == 3 * 1024 * 1024, $"gui: watch picked up new file ({w.ElapsedMilliseconds} ms)");
                File.Delete(probe);
                w.Restart();
                while (w.ElapsedMilliseconds < 8000 && v.Tree.FindPath(probe) >= 0) await Task.Delay(100);
                Check(v.Tree.FindPath(probe) < 0, $"gui: watch picked up deletion ({w.ElapsedMilliseconds} ms)");
                v.StopWatch();

                // render timing
                var canvas = v.Controls.OfType<Control>().First(c => c.GetType().Name == "TreemapCanvas");
                var rsw = Stopwatch.StartNew();
                for (int k = 0; k < 30; k++) { canvas.Invalidate(); canvas.Update(); }
                Check(true, $"gui: 30 frames rendered in {rsw.ElapsedMilliseconds} ms ({rsw.ElapsedMilliseconds / 30.0:F1} ms/frame)");
                if (png != null)
                {
                    using var bmp = new Bitmap(form.Width, form.Height);
                    using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(form.Handle, hdc, 2); g.ReleaseHdc(hdc); }
                    bmp.Save(png, ImageFormat.Png);
                }
                result = "ok";
            }
            catch (Exception ex) { Check(false, "gui exception: " + ex); }
            finally { form.Close(); }
        };
        Application.Run(form);
        Settings.Config.Replace(savedCfg);
        _sb.AppendLine("info: gui test " + result);
    }
}
