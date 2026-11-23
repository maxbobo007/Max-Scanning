using MaxScanning.Core;

namespace MaxScanning.Scan;

public sealed class ScanOptions
{
    public bool AlternateStreams;
    public bool PreferMft = true;
    public int Threads = Math.Clamp(Environment.ProcessorCount * 3, 16, 64);
    public Action<FileTree> TreeCreated; // live preview hook (enumeration scanner publishes its tree immediately)
}

public sealed class ScanProgress
{
    public long Files, Dirs, Bytes, Errors;
    public long Total, Done;      // MFT records (for % bar)
    public volatile string Phase = "";
    public volatile string Current = "";
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public event Action<string, bool> Log; // message, isWarning
    public void Warn(string msg) { Interlocked.Increment(ref Errors); Log?.Invoke(msg, true); }
    public void Info(string msg) => Log?.Invoke(msg, false);
    public double Percent => Total > 0 ? Math.Min(100.0, Done * 100.0 / Total) : -1;
}

public sealed class ScanPauseToken
{
    readonly ManualResetEventSlim _gate = new(true);
    public bool Paused => !_gate.IsSet;
    public void Pause() => _gate.Reset();
    public void Resume() => _gate.Set();
    public void Wait(CancellationToken ct) { if (!_gate.IsSet) _gate.Wait(ct); }
}

public static class ScanEngine
{
    /// <summary>Scan a path choosing the fastest correct method: raw MFT for whole NTFS volumes or NTFS folders (admin), else parallel enumeration.</summary>
    public static FileTree Scan(string path, ScanOptions opt, ScanProgress prog, ScanPauseToken pause, CancellationToken ct)
    {
        path = NormalizePath(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FileTree tree = null;
        string root = Native.VolumeRoot(path) ?? path;
        bool isVolumeRoot = string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string fs = Native.FileSystemName(root);

        if (opt.PreferMft && fs == "NTFS" && Native.IsAdmin() && !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try
            {
                prog.Phase = "MFT";
                var full = MftScanner.ScanVolume(root, opt, prog, pause, ct);
                tree = isVolumeRoot ? full : SubtreeExtractor.Extract(full, path);
                if (tree != null) tree.ScanMethod = "NTFS MFT";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                prog.Warn("MFT scan unavailable (" + ex.Message + "), falling back to parallel enumeration.");
                tree = null;
            }
        }
        if (tree == null)
        {
            prog.Phase = "DIR";
            tree = DirScanner.Scan(path, opt, prog, pause, ct);
            tree.ScanMethod = $"Parallel enumeration ×{opt.Threads}";
        }

        tree.FileSystem = fs;
        tree.RootIsVolume = isVolumeRoot;
        if (Native.GetDiskFreeSpaceExW(root, out _, out long total, out long free)) { tree.VolumeTotal = total; tree.VolumeFree = free; }
        if (isVolumeRoot) tree.AddVirtualSpaceNodes();
        var fsw = System.Diagnostics.Stopwatch.StartNew();
        tree.Finish();
        prog.Info($"tree build (CSR + aggregate + sort) {fsw.ElapsedMilliseconds} ms");
        tree.ScanTime = sw.Elapsed;
        tree.ScanDate = DateTime.Now;
        prog.Phase = "DONE";
        return tree;
    }

    public static string NormalizePath(string p)
    {
        p = p.Trim().Trim('"');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (p.Length > 3 && p.EndsWith('\\')) p = p.TrimEnd('\\');
        try { if (!p.StartsWith(@"\\", StringComparison.Ordinal)) p = Path.GetFullPath(p); } catch { }
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }
}
