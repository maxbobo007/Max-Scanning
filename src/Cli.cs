using System.Diagnostics;
using System.Text;
using MaxScanning.Core;
using MaxScanning.Scan;

namespace MaxScanning;

/// <summary>Headless commands: --bench, --export, --snapshot. Output goes to the attached console or --out file.</summary>
internal static class Cli
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    public static bool IsCli(string[] args) => args.Any(a => a is "--bench" or "--verify" or "--export" or "--save-snapshot" or "--help" or "-h" or "/?");

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        string Arg(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var outPath = Arg("--out");
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }
        try
        {
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
            {
                W(HelpText);
                return 0;
            }
            string path = args.FirstOrDefault(a => !a.StartsWith("--") && a != outPath && a != Arg("--export") && a != Arg("--save-snapshot") && a != Arg("--filter") && a != Arg("--threads") && a != Arg("--preset")) ?? "C:\\";
            var opt = new ScanOptions { AlternateStreams = args.Contains("--ads"), PreferMft = !args.Contains("--no-mft") };
            if (int.TryParse(Arg("--threads"), out int th)) opt.Threads = th;

            if (args.Contains("--verify"))
            {
                // accuracy check: MFT vs enumeration on the same path
                W($"verify {path}  admin={Native.IsAdmin()}");
                var a = Measure(path, new ScanOptions { PreferMft = true, Threads = opt.Threads }, W);
                var b = Measure(path, new ScanOptions { PreferMft = false, Threads = opt.Threads }, W);
                Compare(a, b, W);
                return 0;
            }

            var tree = Measure(path, opt, W);
            if (args.Contains("--bench")) Top(tree, W, 15);

            string exp = Arg("--export");
            if (exp != null)
            {
                var preset = IO.ExportPreset.BuiltIn().FirstOrDefault(p => p.Name.Equals(Arg("--preset") ?? "CSV", StringComparison.OrdinalIgnoreCase)) ?? IO.ExportPreset.BuiltIn()[0];
                var filter = new Filter.FilterSet(Arg("--filter") ?? "");
                File.WriteAllText(exp, IO.Exporter.Export(tree, tree.Root, preset, filter), new UTF8Encoding(true));
                W("exported " + exp);
            }
            string snap = Arg("--save-snapshot");
            if (snap != null) { IO.Snapshot.Save(tree, snap); W("snapshot " + snap); }
            return 0;
        }
        catch (Exception ex) { W("ERROR: " + ex); return 1; }
        finally { if (outPath != null) File.WriteAllText(outPath, sb.ToString()); }
    }

    static FileTree Measure(string path, ScanOptions opt, Action<string> W)
    {
        GC.Collect();
        long mem0 = GC.GetTotalMemory(true);
        var prog = new ScanProgress();
        prog.Log += (m, warn) => { if (!warn || prog.Errors < 5) W((warn ? "  warn: " : "  info: ") + m); };
        var sw = Stopwatch.StartNew();
        var t = ScanEngine.Scan(path, opt, prog, new ScanPauseToken(), CancellationToken.None);
        sw.Stop();
        long mem = GC.GetTotalMemory(true) - mem0;
        int r = t.Root;
        W($"[{t.ScanMethod}] {path}: {sw.Elapsed.TotalMilliseconds:F0} ms | files {t.Files[r]:N0} dirs {t.Dirs[r]:N0} | size {t.Size[r]:N0} B ({Fmt(t.Size[r])}) | on disk {t.Alloc[r]:N0} ({Fmt(t.Alloc[r])}) | nodes {t.Count:N0} | heap {Fmt(mem)} | errors {prog.Errors}");
        if (t.RootIsVolume) W($"   used on volume {Fmt(t.VolumeTotal - t.VolumeFree)} vs scanned on-disk {Fmt(t.Alloc[r])}");
        if (t.RootIsVolume) W($"   volume total {Fmt(t.VolumeTotal)} free {Fmt(t.VolumeFree)} unknown {Fmt(t.UnknownNode >= 0 ? t.Size[t.UnknownNode] : 0)}");
        return t;
    }

    static void Top(FileTree t, Action<string> W, int n)
    {
        foreach (int c in t.ChildrenOf(t.Root).ToArray().Take(n))
            W($"   {Fmt(t.Size[c]),10}  {t.Files[c],10:N0} files  {t.Name(c)}");
    }

    static void Compare(FileTree a, FileTree b, Action<string> W)
    {
        W($"delta size {a.Size[a.Root] - b.Size[b.Root]:N0} B, files {a.Files[a.Root] - b.Files[b.Root]:N0}, dirs {a.Dirs[a.Root] - b.Dirs[b.Root]:N0}");
        var mapB = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (int c in b.ChildrenOf(b.Root)) mapB[b.Name(c)] = c;
        int shown = 0;
        foreach (int c in a.ChildrenOf(a.Root))
        {
            if (a.IsVirtual(c)) continue;
            string nm = a.Name(c);
            if (!mapB.TryGetValue(nm, out int d)) { if (shown++ < 25) W($"   only in A: {nm} {Fmt(a.Size[c])}"); continue; }
            if (a.Size[c] != b.Size[d] || a.Files[c] != b.Files[d] || a.Alloc[c] != b.Alloc[d])
                if (shown++ < 25) W($"   diff {nm}: A size {Fmt(a.Size[c])} alloc {Fmt(a.Alloc[c])} files {a.Files[c]:N0} hl {HardlinkBytes(a, c)} | B size {Fmt(b.Size[d])} alloc {Fmt(b.Alloc[d])} files {b.Files[d]:N0}");
        }
        W(shown == 0 ? "   top-level entries identical" : $"   {shown} differences");
    }

    static string HardlinkBytes(FileTree t, int root)
    {
        long n = 0, bytes = 0;
        var st = new Stack<int>(); st.Push(root);
        while (st.Count > 0) { int v = st.Pop(); if ((t.Flags[v] & NodeFlags.Hardlink) != 0) { n++; bytes += t.OwnSize[v]; } foreach (int c in t.ChildrenOf(v)) st.Push(c); }
        return n == 0 ? "-" : $"{n:N0}/{Fmt(bytes)}";
    }

    public static string Fmt(long b) => Util.FormatSize(b);

    public const string HelpText = """
Max Scanning — command line
  MaxScanning.exe [path]                         open GUI and scan path
  MaxScanning.exe --load file.mscan              open GUI with a snapshot
  MaxScanning.exe <path> --bench [--out f.txt]   headless scan, print totals and top folders
  MaxScanning.exe <path> --verify                compare MFT scan with enumeration (accuracy check)
  MaxScanning.exe <path> --export out.csv [--preset CSV|TXT|Tree|HTML|JSON] [--filter "*.mp4;>100mb"]
  MaxScanning.exe <path> --save-snapshot out.mscan
  options: --ads (alternate data streams)  --no-mft  --threads N  --filter "<filter>"
""";
}
