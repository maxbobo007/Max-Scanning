using System.Collections.Concurrent;
using MaxScanning.Core;

namespace MaxScanning.Scan;

/// <summary>
/// Work-stealing parallel directory enumeration (GetFileInformationByHandleEx FileFullDirectoryInfo, 256 KB batches).
/// Used for non-NTFS volumes, network shares and when not elevated. Reparse points are not followed
/// (same as SpaceSniffer) so junction loops and double counting cannot happen.
/// </summary>
public static unsafe class DirScanner
{
    struct Entry { public int NameStart, NameLen; public uint Attr, Tag; public long Size, Alloc, C, M, A; }

    static bool IsLinkTag(uint tag) => tag is 0xA0000003 /*MOUNT_POINT*/ or 0xA000000C /*SYMLINK*/ or 0x8000001B /*APPEXECLINK*/ or 0xA0000019 /*GLOBAL_REPARSE*/ or 0xA000001D /*LX_SYMLINK*/;

    public static FileTree Scan(string path, ScanOptions opt, ScanProgress prog, ScanPauseToken pause, CancellationToken ct)
    {
        var t = new FileTree(1 << 16);
        string root = Native.VolumeRoot(path) ?? path;
        int cluster = 4096;
        if (Native.GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _)) cluster = (int)(spc * bps);
        t.ClusterSize = cluster;

        var rootInfo = new DirectoryInfo(path);
        long rc = 0, rm = 0, ra = 0;
        try { rc = rootInfo.CreationTimeUtc.ToFileTimeUtc(); rm = rootInfo.LastWriteTimeUtc.ToFileTimeUtc(); ra = rootInfo.LastAccessTimeUtc.ToFileTimeUtc(); } catch { }
        t.Root = t.Add(-1, path.TrimEnd('\\').Length == 2 ? path.TrimEnd('\\') : path, NodeKind.Root, 0, 0, Native.FILE_ATTRIBUTE_DIRECTORY, rc, rm, ra);
        t.RootPath = path;
        opt.TreeCreated?.Invoke(t);

        var work = new ConcurrentStack<(int node, string path)>();
        work.Push((t.Root, path));
        int pending = 1;
        using var signal = new SemaphoreSlim(0);
        int threads = Math.Max(1, opt.Threads);
        // network shares like more parallelism (latency bound)
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) threads = Math.Min(64, threads * 2);
        int idle = 0;
        var workers = new Thread[threads];
        Exception fatal = null;

        for (int w = 0; w < threads; w++)
        {
            workers[w] = new Thread(() =>
            {
                var entries = new List<Entry>(256);
                var names = new char[16384];
                var subdirs = new List<(int, string)>(64);
                var allocs = new long[256];
                byte* buffer = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(BufSize, 8);
                try
                {
                    while (true)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!work.TryPop(out var item))
                        {
                            if (Volatile.Read(ref pending) == 0) break;
                            Interlocked.Increment(ref idle);
                            signal.Wait(20);
                            Interlocked.Decrement(ref idle);
                            continue;
                        }
                        pause.Wait(ct);
                        entries.Clear(); subdirs.Clear();
                        int nlen = 0;
                        string dir = item.path;
                        prog.Current = dir;
                        int err = EnumerateDir(dir, buffer, entries, ref names, ref nlen);
                        if (err != 0)
                        {
                            if (err != 2 && err != 18 && err != 3)
                            {
                                lock (t.Sync) t.Flags[item.node] |= NodeFlags.AccessDenied;
                                prog.Warn($"Cannot scan {dir} (error {err})");
                            }
                        }

                        // compute allocation outside the lock (may hit the disk for compressed files)
                        if (allocs.Length < entries.Count) allocs = new long[entries.Count * 2];
                        long files = 0, dirs = 0, bytes = 0;
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i];
                            if ((e.Attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) { dirs++; continue; }
                            files++; bytes += e.Size;
                            allocs[i] = (e.Attr & (Native.FILE_ATTRIBUTE_OFFLINE | Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0 && e.Alloc == 0 ? 0 : e.Alloc;
                        }

                        List<(int owner, string name, long size)> streams = null;
                        if (opt.AlternateStreams) streams = ReadStreams(dir, entries, names, cluster);

                        lock (t.Sync)
                        {
                            for (int i = 0; i < entries.Count; i++)
                            {
                                var e = entries[i];
                                bool isDir = (e.Attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                                var name = names.AsSpan(e.NameStart, e.NameLen);
                                int ni = t.Add(item.node, name, isDir ? NodeKind.Dir : NodeKind.File, isDir ? 0 : e.Size, isDir ? 0 : allocs[i], e.Attr, e.C, e.M, e.A);
                                if ((e.Attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) t.Flags[ni] |= NodeFlags.Reparse;
                                // follow cloud-file placeholders (OneDrive/Dropbox), never junctions or symlinks (loops / double counting)
                                if (isDir && ((e.Attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) == 0 || !IsLinkTag(e.Tag)))
                                    subdirs.Add((ni, Path.Combine(dir, name.ToString())));
                                if (streams != null)
                                    foreach (var s in streams)
                                        if (s.owner == i) t.Add(ni, s.name, NodeKind.Stream, s.size, RoundUp(s.size, cluster), 0, e.C, e.M, e.A);
                            }
                        }
                        Interlocked.Add(ref prog.Files, files);
                        Interlocked.Add(ref prog.Dirs, dirs);
                        Interlocked.Add(ref prog.Bytes, bytes);
                        if (subdirs.Count > 0)
                        {
                            Interlocked.Add(ref pending, subdirs.Count);
                            // push in reverse so the stack pops in natural order (better locality)
                            for (int k = subdirs.Count - 1; k >= 0; k--) work.Push(subdirs[k]);
                            if (Volatile.Read(ref idle) > 0) signal.Release(Math.Min(subdirs.Count, threads));
                        }
                        Interlocked.Decrement(ref pending);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { fatal = ex; }
                finally { System.Runtime.InteropServices.NativeMemory.AlignedFree(buffer); }
            }) { IsBackground = true, Name = "DirScan " + w };
            workers[w].Start();
        }
        foreach (var th in workers) th.Join();
        ct.ThrowIfCancellationRequested();
        if (fatal != null) throw fatal;
        return t;
    }

    const int BufSize = 256 * 1024;

    /// <summary>
    /// Reads a whole directory with GetFileInformationByHandleEx(FileFullDirectoryInfo): 256 KB batches, and the
    /// entries carry AllocationSize, so compressed/sparse/cloud files need no extra per-file open.
    /// </summary>
    static int EnumerateDir(string dir, byte* buf, List<Entry> entries, ref char[] names, ref int nlen)
    {
        using var h = Native.CreateFileW(LongPath(dir), Native.FILE_LIST_DIRECTORY, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        int cls = Native.FileFullDirectoryRestartInfo;
        while (true)
        {
            if (!Native.GetFileInformationByHandleEx(h, cls, buf, BufSize))
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return err == 18 ? 0 : err; // ERROR_NO_MORE_FILES = done
            }
            cls = Native.FileFullDirectoryInfo;
            byte* p = buf;
            while (true)
            {
                uint next = *(uint*)p;
                uint attr = *(uint*)(p + 56);
                int len = (int)(*(uint*)(p + 60) / 2);
                char* fn = (char*)(p + 68);
                if (!((len == 1 && fn[0] == '.') || (len == 2 && fn[0] == '.' && fn[1] == '.')))
                {
                    if (nlen + len > names.Length) Array.Resize(ref names, names.Length * 2 + len);
                    new ReadOnlySpan<char>(fn, len).CopyTo(names.AsSpan(nlen));
                    entries.Add(new Entry
                    {
                        NameStart = nlen, NameLen = len, Attr = attr,
                        Tag = (attr & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? *(uint*)(p + 64) : 0,
                        C = *(long*)(p + 8), A = *(long*)(p + 16), M = *(long*)(p + 24),
                        Size = *(long*)(p + 40), Alloc = *(long*)(p + 48)
                    });
                    nlen += len;
                }
                if (next == 0) break;
                p += next;
            }
        }
    }

    static List<(int, string, long)> ReadStreams(string dir, List<Entry> entries, char[] names, int cluster)
    {
        var list = new List<(int, string, long)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if ((e.Attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
            string fp = LongPath(Path.Combine(dir, new string(names, e.NameStart, e.NameLen)));
            IntPtr h = Native.FindFirstStreamW(fp, 0, out Native.FindStreamData sd, 0);
            if (h == Native.INVALID_HANDLE_VALUE) continue;
            do
            {
                string sn = new(sd.StreamName);
                // "::$DATA" is the main stream; named ones look like ":Zone.Identifier:$DATA"
                if (sn == "::$DATA") continue;
                if (sn.StartsWith(':')) sn = sn[1..];
                if (sn.EndsWith(":$DATA", StringComparison.Ordinal)) sn = sn[..^6];
                list.Add((i, sn, sd.StreamSize));
            } while (Native.FindNextStreamW(h, out sd));
            Native.FindClose(h);
        }
        return list;
    }

    public static long RoundUp(long v, int c) => c <= 0 ? v : (v + c - 1) / c * c;

    public static string LongPath(string p)
    {
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p;
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + p[2..];
        return @"\\?\" + p;
    }

    /// <summary>Rescan one directory (for live watch / manual refresh). Returns a standalone subtree.</summary>
    public static FileTree ScanSub(string path, ScanOptions opt, CancellationToken ct)
    {
        var prog = new ScanProgress();
        return Scan(path, opt, prog, new ScanPauseToken(), ct);
    }
}
