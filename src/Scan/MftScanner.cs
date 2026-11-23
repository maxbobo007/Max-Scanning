using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MaxScanning.Core;

namespace MaxScanning.Scan;

/// <summary>
/// Reads the NTFS Master File Table straight from the volume in large sequential, unbuffered reads
/// (saturates NVMe/SATA bandwidth) and parses FILE records on all cores. No per-directory I/O at all.
/// Node index == MFT record number, so parent links resolve without any hashing.
/// </summary>
public static unsafe class MftScanner
{
    const uint AT_STANDARD_INFORMATION = 0x10, AT_ATTRIBUTE_LIST = 0x20, AT_FILE_NAME = 0x30, AT_DATA = 0x80, AT_INDEX_ALLOCATION = 0xA0, AT_END = 0xFFFFFFFF;
    const int ChunkBytes = 8 << 20; // 8 MiB per read

    struct Run { public long Vcn, Lcn, Count; }

    sealed class Ctx
    {
        public SafeFileHandle H;
        public int RecSize, Sector, Cluster;
        public long Records;
        public FileTree T;
        public ushort[] Seq;        // record sequence numbers
        public ushort[] ParentSeq;  // sequence expected by child's parent reference
        public byte[] NameNs;       // namespace of the chosen name (255 = none yet)
        public byte[] Worker;       // which worker pool holds the name
        public bool Ads;
        public ConcurrentQueue<ExtInfo> Ext = new();
        public ConcurrentQueue<StreamInfo> Streams = new();
        public ScanProgress P;
    }

    struct ExtInfo
    {
        public int Base;
        public bool HasData; public long Size, Alloc, AddSize, AddAlloc;
        public bool HasName; public int Parent; public ushort PSeq; public byte Ns; public string Name;
    }

    struct StreamInfo { public int Owner; public string Name; public long Size, Alloc; }

    sealed class Pool { public char[] Buf = new char[1 << 20]; public int Len; }

    public static FileTree ScanVolume(string root, ScanOptions opt, ScanProgress prog, ScanPauseToken pause, CancellationToken ct)
    {
        try { return ScanVolume(root, opt, prog, pause, ct, Native.FILE_FLAG_NO_BUFFERING); }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            // some filter drivers reject unbuffered raw reads; the cached path is slower but still far faster than enumeration
            prog.Info("MFT unbuffered read rejected (" + ex.Message + "), retrying with cached reads");
            prog.Done = 0;
            return ScanVolume(root, opt, prog, pause, ct, Native.FILE_FLAG_SEQUENTIAL_SCAN);
        }
    }

    static FileTree ScanVolume(string root, ScanOptions opt, ScanProgress prog, ScanPauseToken pause, CancellationToken ct, uint flags)
    {
        string dev = @"\\.\" + root.TrimEnd('\\');
        using var h = Native.CreateFileW(dev, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
            IntPtr.Zero, Native.OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException("open volume failed, error " + Marshal.GetLastWin32Error());

        Native.NTFS_VOLUME_DATA_BUFFER vd;
        if (!Native.DeviceIoControl(h, Native.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &vd, sizeof(Native.NTFS_VOLUME_DATA_BUFFER), out _, IntPtr.Zero))
            throw new IOException("FSCTL_GET_NTFS_VOLUME_DATA failed, error " + Marshal.GetLastWin32Error());

        var c = new Ctx
        {
            H = h, RecSize = (int)vd.BytesPerFileRecordSegment, Sector = (int)vd.BytesPerSector, Cluster = (int)vd.BytesPerCluster,
            Ads = opt.AlternateStreams, P = prog
        };
        c.Records = vd.MftValidDataLength / c.RecSize;
        if (c.Records <= 16 || c.Records > int.MaxValue - 1024) throw new IOException("unexpected MFT size");

        // ---- locate $MFT data runs from record 0
        var runs = ReadMftRuns(c, vd.MftStartLcn);
        prog.Total = c.Records;

        int n = (int)c.Records;
        var t = new FileTree(n + 1024);
        t.EnsureCapacity(n + 1024);
        t.Count = n;
        t.ClusterSize = c.Cluster;
        c.T = t;
        c.Seq = new ushort[n]; c.ParentSeq = new ushort[n]; c.NameNs = new byte[n]; c.Worker = new byte[n];
        Array.Fill(c.NameNs, (byte)255);
        Array.Fill(t.Flags, NodeFlags.Dead, 0, n); // until a valid in-use record is seen
        Array.Fill(t.Parent, -1, 0, n);

        // ---- pipeline: 1 sequential reader -> N parsers
        int workers = Math.Clamp(Math.Min(opt.Threads, Environment.ProcessorCount), 1, 63);
        var pools = new Pool[workers];
        for (int i = 0; i < workers; i++) pools[i] = new Pool();
        var queue = new BlockingCollection<(IntPtr buf, long firstRec, int count)>(workers * 2);
        var free = new BlockingCollection<IntPtr>();
        for (int i = 0; i < workers * 2 + 2; i++) free.Add((IntPtr)NativeMemory.AlignedAlloc(ChunkBytes, 4096));
        Exception readerError = null;

        var reader = new Thread(() =>
        {
            try
            {
                long recBytes = c.Records * c.RecSize;
                foreach (var r in runs)
                {
                    long runBytesStart = r.Vcn * c.Cluster;
                    long runBytes = r.Count * c.Cluster;
                    for (long off = 0; off < runBytes; off += ChunkBytes)
                    {
                        ct.ThrowIfCancellationRequested();
                        pause.Wait(ct);
                        long fileOff = runBytesStart + off;
                        if (fileOff >= recBytes) return;
                        int len = (int)Math.Min(ChunkBytes, Math.Min(runBytes - off, recBytes - fileOff));
                        int alignedLen = (len + c.Sector - 1) / c.Sector * c.Sector;
                        var buf = free.Take(ct);
                        long disk = r.Lcn * c.Cluster + off;
                        ReadChunk(h, disk, (byte*)buf, alignedLen, len, c.Sector);
                        queue.Add((buf, fileOff / c.RecSize, len / c.RecSize), ct);
                    }
                }
            }
            catch (Exception ex) { readerError = ex; }
            finally { queue.CompleteAdding(); }
        }) { IsBackground = true, Name = "MFT reader", Priority = ThreadPriority.AboveNormal };
        reader.Start();

        var threads = new Thread[workers];
        for (int w = 0; w < workers; w++)
        {
            int wid = w;
            threads[w] = new Thread(() =>
            {
                var pool = pools[wid];
                byte* rec = (byte*)NativeMemory.AlignedAlloc((nuint)c.RecSize, 64);
                try
                {
                    foreach (var (buf, first, count) in queue.GetConsumingEnumerable())
                    {
                        for (int k = 0; k < count; k++)
                        {
                            long idx = first + k;
                            if (idx >= n) break;
                            Buffer.MemoryCopy((byte*)buf + (long)k * c.RecSize, rec, c.RecSize, c.RecSize);
                            ParseRecord(c, rec, (int)idx, pool, (byte)wid);
                        }
                        Interlocked.Add(ref prog.Done, count);
                        free.Add(buf);
                    }
                }
                finally { NativeMemory.AlignedFree(rec); }
            }) { IsBackground = true, Name = "MFT parser " + w };
            threads[w].Start();
        }
        reader.Join();
        foreach (var th in threads) th.Join();
        while (free.TryTake(out var b)) NativeMemory.AlignedFree((void*)b);
        while (queue.TryTake(out var q)) NativeMemory.AlignedFree((void*)q.buf);
        ct.ThrowIfCancellationRequested();
        if (readerError != null) throw readerError;

        prog.Info($"MFT: {c.Records:N0} records ({Core.Util.FormatSize(c.Records * c.RecSize)}) read+parsed in {prog.Clock.ElapsedMilliseconds} ms on {workers} threads");
        long tMerge = prog.Clock.ElapsedMilliseconds;
        // ---- merge worker name pools into the tree pool (one big copy per worker)
        prog.Phase = "MERGE";
        long totalChars = 0;
        foreach (var p in pools) totalChars += p.Len;
        long want = Math.Min((long)Array.MaxLength, totalChars + 65536);
        var poolAll = new char[want];
        var baseOff = new int[workers];
        int pos = 0;
        for (int w = 0; w < workers; w++) { baseOff[w] = pos; pools[w].Buf.AsSpan(0, pools[w].Len).CopyTo(poolAll.AsSpan(pos)); pos += pools[w].Len; }
        for (int i = 0; i < n; i++) if (c.NameNs[i] != 255) t.NameOff[i] += baseOff[c.Worker[i]];
        t.SetPool(poolAll, pos);

        // ---- extension records (attribute lists): rare, apply serially
        while (c.Ext.TryDequeue(out var e))
        {
            int b = e.Base;
            if (b < 0 || b >= n) continue;
            if (e.HasData) { t.OwnSize[b] = e.Size; t.OwnAlloc[b] = e.Alloc; }
            t.OwnSize[b] += e.AddSize; t.OwnAlloc[b] += e.AddAlloc;
            if (e.HasName && Better(e.Ns, c.NameNs[b]))
            {
                c.NameNs[b] = e.Ns; t.Parent[b] = e.Parent; c.ParentSeq[b] = e.PSeq; t.SetName(b, e.Name);
            }
        }

        // ---- validate parents (sequence numbers), fix root, count
        t.Root = 5;
        t.RootPath = root.EndsWith('\\') ? root : root + "\\";
        long files = 0, dirs = 0;
        for (int i = 0; i < n; i++)
        {
            if ((t.Flags[i] & NodeFlags.Dead) != 0) continue;
            if (i == 5) { t.Parent[i] = -1; continue; }
            int p = t.Parent[i];
            if (c.NameNs[i] == 255 || p < 0 || p >= n || (t.Flags[p] & NodeFlags.Dead) != 0 || t.Kind[p] == NodeKind.File
                || (c.ParentSeq[i] != 0 && c.Seq[p] != 0 && c.ParentSeq[i] != c.Seq[p]))
            { t.Flags[i] |= NodeFlags.Dead; continue; }
            if (t.Kind[i] == NodeKind.Dir) dirs++; else files++;
        }
        t.Kind[5] = NodeKind.Root;
        t.SetName(5, root.TrimEnd('\\'));
        prog.Files = files; prog.Dirs = dirs;
        prog.Info($"MFT: merge+validate {prog.Clock.ElapsedMilliseconds - tMerge} ms");

        // ---- alternate data streams as child nodes
        if (c.Ads)
        {
            while (c.Streams.TryDequeue(out var s))
            {
                if (s.Owner < 0 || s.Owner >= n || (t.Flags[s.Owner] & NodeFlags.Dead) != 0) continue;
                t.Add(s.Owner, s.Name, NodeKind.Stream, s.Size, s.Alloc, 0, t.CTime[s.Owner], t.MTime[s.Owner], t.ATime[s.Owner]);
            }
        }
        return t;
    }

    static int _maxIo = ChunkBytes;

    /// <summary>Large unbuffered read; some USB/RAID bridges reject big transfers (ERROR_NOT_SUPPORTED / INVALID_PARAMETER) — then split.</summary>
    static void ReadChunk(SafeFileHandle h, long disk, byte* buf, int alignedLen, int need, int sector)
    {
        int done = 0;
        while (done < alignedLen)
        {
            int part = Math.Min(_maxIo, alignedLen - done);
            if (!Native.SetFilePointerEx(h, disk + done, out _, 0)) throw new IOException("seek failed " + Marshal.GetLastWin32Error());
            if (Native.ReadFile(h, buf + done, part, out int got, IntPtr.Zero) && got > 0) { done += got; continue; }
            int err = Marshal.GetLastWin32Error();
            if ((err is 50 or 87 or 1784 or 665 or 1450) && _maxIo > 64 * 1024)
            {
                _maxIo = Math.Max(64 * 1024, _maxIo / 4 / sector * sector);
                continue;
            }
            throw new IOException("MFT read failed " + err);
        }
        if (done < need) throw new IOException("short MFT read");
    }

    static bool Better(byte ns, byte cur)
    {
        // prefer Win32 (1) / Win32&DOS (3) / POSIX (0) over DOS-only (2); 255 = none
        if (cur == 255) return true;
        int Rank(byte x) => x switch { 1 => 3, 3 => 3, 0 => 2, 2 => 1, _ => 0 };
        return Rank(ns) > Rank(cur);
    }

    static List<Run> ReadMftRuns(Ctx c, long mftLcn)
    {
        int len = Math.Max(c.RecSize, c.Cluster);
        len = (len + c.Sector - 1) / c.Sector * c.Sector;
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)len, 4096);
        try
        {
            ReadAt(c, mftLcn * c.Cluster, buf, len);
            if (!Fixup(buf, c.RecSize, c.Sector)) throw new IOException("bad $MFT record 0");
            var runs = new List<Run>();
            var extRecs = new List<long>();
            CollectDataRuns(buf, c.RecSize, runs, extRecs);
            // follow attribute-list extensions of $MFT itself (heavily fragmented MFT)
            foreach (long rn in extRecs)
            {
                long fileOff = rn * c.RecSize;
                long vcn = fileOff / c.Cluster;
                var r = runs.FirstOrDefault(x => vcn >= x.Vcn && vcn < x.Vcn + x.Count);
                if (r.Count == 0) continue;
                long disk = (r.Lcn + (vcn - r.Vcn)) * c.Cluster + fileOff % c.Cluster;
                long alignedDisk = disk / c.Sector * c.Sector;
                ReadAt(c, alignedDisk, buf, len);
                byte* rec = buf + (disk - alignedDisk);
                if (Fixup(rec, c.RecSize, c.Sector)) CollectDataRuns(rec, c.RecSize, runs, null);
            }
            runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            if (runs.Count == 0) throw new IOException("no $MFT data runs");
            return runs;
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    static void ReadAt(Ctx c, long off, byte* buf, int len)
    {
        if (!Native.SetFilePointerEx(c.H, off, out _, 0)) throw new IOException("seek failed");
        if (!Native.ReadFile(c.H, buf, len, out int got, IntPtr.Zero) || got < len) throw new IOException("read failed " + Marshal.GetLastWin32Error());
    }

    static void CollectDataRuns(byte* rec, int recSize, List<Run> runs, List<long> extRecs)
    {
        int off = *(ushort*)(rec + 0x14);
        while (off + 16 <= recSize)
        {
            uint type = *(uint*)(rec + off);
            if (type == AT_END) break;
            int alen = *(int*)(rec + off + 4);
            if (alen <= 0 || off + alen > recSize) break;
            byte nonRes = rec[off + 8];
            byte nameLen = rec[off + 9];
            if (type == AT_DATA && nonRes == 1 && nameLen == 0)
            {
                long vcn = *(long*)(rec + off + 0x10);
                int runOff = *(ushort*)(rec + off + 0x20);
                DecodeRuns(rec + off + runOff, alen - runOff, vcn, runs);
            }
            else if (type == AT_ATTRIBUTE_LIST && nonRes == 0 && extRecs != null)
            {
                int vlen = *(int*)(rec + off + 0x10);
                int voff = *(ushort*)(rec + off + 0x14);
                byte* p = rec + off + voff;
                int q = 0;
                while (q + 26 <= vlen)
                {
                    uint at = *(uint*)(p + q);
                    int elen = *(ushort*)(p + q + 4);
                    if (elen <= 0) break;
                    long rref = *(long*)(p + q + 0x10) & 0xFFFFFFFFFFFFL;
                    if (at == AT_DATA && rref != 0 && !extRecs.Contains(rref)) extRecs.Add(rref);
                    q += elen;
                }
            }
            off += alen;
        }
    }

    static void DecodeRuns(byte* p, int max, long startVcn, List<Run> runs)
    {
        long vcn = startVcn, lcn = 0;
        int i = 0;
        while (i < max)
        {
            byte hdr = p[i++];
            if (hdr == 0) break;
            int ls = hdr & 0xF, os = hdr >> 4;
            if (ls == 0 || ls > 8 || os > 8 || i + ls + os > max) break;
            long length = 0;
            for (int k = 0; k < ls; k++) length |= (long)p[i + k] << (8 * k);
            i += ls;
            long delta = 0;
            if (os > 0)
            {
                for (int k = 0; k < os; k++) delta |= (long)p[i + k] << (8 * k);
                if ((p[i + os - 1] & 0x80) != 0) delta |= -1L << (8 * os); // sign extend
                i += os;
                lcn += delta;
                runs.Add(new Run { Vcn = vcn, Lcn = lcn, Count = length });
            }
            vcn += length;
        }
    }

    static bool Fixup(byte* rec, int recSize, int sector)
    {
        if (*(uint*)rec != 0x454C4946) return false; // "FILE"
        int usaOff = *(ushort*)(rec + 4), usaCnt = *(ushort*)(rec + 6);
        if (usaOff + usaCnt * 2 > recSize || usaCnt < 2) return false;
        ushort usn = *(ushort*)(rec + usaOff);
        int stride = recSize / (usaCnt - 1);
        for (int i = 1; i < usaCnt; i++)
        {
            ushort* end = (ushort*)(rec + i * stride - 2);
            if (*end != usn) return false; // torn write
            *end = *(ushort*)(rec + usaOff + i * 2);
        }
        return true;
    }

    static void ParseRecord(Ctx c, byte* rec, int idx, Pool pool, byte wid)
    {
        if (!Fixup(rec, c.RecSize, c.Sector)) return;
        ushort flags = *(ushort*)(rec + 0x16);
        if ((flags & 1) == 0) return; // not in use
        var t = c.T;
        ushort seq = *(ushort*)(rec + 0x10);
        long baseRef = *(long*)(rec + 0x20) & 0xFFFFFFFFFFFFL;
        bool isExt = baseRef != 0;
        bool isDir = (flags & 2) != 0;

        long size = 0, alloc = 0; bool hasData = false;
        long ct = 0, mt = 0, at = 0; uint attr = 0; bool hasSi = false;
        long metaSize = 0, metaAlloc = 0;
        byte bestNs = 255; int bestParent = -1; ushort bestPSeq = 0; char* bestName = null; int bestLen = 0;
        int nameCount = 0;

        int off = *(ushort*)(rec + 0x14);
        int recSize = c.RecSize;
        while (off + 16 <= recSize)
        {
            uint type = *(uint*)(rec + off);
            if (type == AT_END) break;
            int alen = *(int*)(rec + off + 4);
            if (alen < 16 || off + alen > recSize) break;
            byte nonRes = rec[off + 8];
            byte nameLen = rec[off + 9];
            if (nonRes == 0)
            {
                int vlen = *(int*)(rec + off + 0x10);
                int voff = *(ushort*)(rec + off + 0x14);
                byte* v = rec + off + voff;
                if (voff + vlen > alen) { off += alen; continue; }
                switch (type)
                {
                    case AT_STANDARD_INFORMATION when vlen >= 0x24:
                        ct = *(long*)v; mt = *(long*)(v + 8); at = *(long*)(v + 0x18); attr = *(uint*)(v + 0x20); hasSi = true;
                        break;
                    case AT_FILE_NAME when vlen >= 0x42:
                        {
                            byte ns = v[0x41];
                            int fl = v[0x40];
                            if (0x42 + fl * 2 > vlen) break;
                            if (ns != 2) nameCount++;
                            if (Better(ns, bestNs))
                            {
                                long pref = *(long*)v;
                                bestNs = ns; bestParent = (int)(pref & 0xFFFFFFFF); bestPSeq = (ushort)(pref >> 48);
                                bestName = (char*)(v + 0x42); bestLen = fl;
                            }
                            break;
                        }
                    case AT_DATA:
                        if (nameLen == 0) { size = vlen; alloc = 0; hasData = true; }
                        else if (c.Ads) AddStream(c, isExt ? (int)baseRef : idx, rec + off, nameLen, vlen, 0);
                        else if (IsMeta(idx, bestParent)) metaSize += vlen;
                        break;
                }
            }
            else
            {
                if (type == AT_INDEX_ALLOCATION && alen >= 0x40 && *(long*)(rec + off + 0x10) == 0)
                    metaAlloc += *(long*)(rec + off + 0x28); // folder index blocks: real disk usage of directories
                else if (type == AT_DATA && alen >= 0x40)
                {
                    long lowVcn = *(long*)(rec + off + 0x10);
                    if (lowVcn == 0)
                    {
                        ushort aflags = *(ushort*)(rec + off + 0x0C);
                        long allocSz = *(long*)(rec + off + 0x28);
                        long dataSz = *(long*)(rec + off + 0x30);
                        if ((aflags & 0x8001) != 0 && alen >= 0x48) allocSz = *(long*)(rec + off + 0x40); // compressed/sparse: real allocation
                        if (nameLen == 0) { size = dataSz; alloc = allocSz; hasData = true; }
                        else if (c.Ads) AddStream(c, isExt ? (int)baseRef : idx, rec + off, nameLen, dataSz, allocSz);
                        else if (IsMeta(idx, bestParent)) { metaSize += allocSz; metaAlloc += allocSz; } // sparse logical sizes ($BadClus:$Bad = whole volume) would mislead; count real allocation
                    }
                }
            }
            off += alen;
        }

        if (isExt)
        {
            if (hasData || bestNs != 255 || metaSize != 0 || metaAlloc != 0)
                c.Ext.Enqueue(new ExtInfo
                {
                    Base = (int)baseRef, HasData = hasData, Size = size, Alloc = alloc, AddSize = metaSize, AddAlloc = metaAlloc,
                    HasName = bestNs != 255, Parent = bestParent, PSeq = bestPSeq, Ns = bestNs,
                    Name = bestNs != 255 ? new string(bestName, 0, bestLen) : null
                });
            return;
        }

        c.Seq[idx] = seq;
        t.Kind[idx] = isDir ? NodeKind.Dir : NodeKind.File;
        t.Flags[idx] = nameCount > 1 && !isDir ? NodeFlags.Hardlink : NodeFlags.None;
        if (hasSi) { t.CTime[idx] = ct; t.MTime[idx] = mt; t.ATime[idx] = at; }
        t.Attr[idx] = attr | (isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : 0);
        if (!isDir && hasData) { t.OwnSize[idx] = size; t.OwnAlloc[idx] = alloc; }
        t.OwnSize[idx] += metaSize; t.OwnAlloc[idx] += metaAlloc;
        if (bestNs != 255)
        {
            t.Parent[idx] = bestParent;
            c.ParentSeq[idx] = bestPSeq;
            c.NameNs[idx] = bestNs;
            c.Worker[idx] = wid;
            if (pool.Len + bestLen > pool.Buf.Length) Array.Resize(ref pool.Buf, Math.Max(pool.Buf.Length * 2, pool.Len + bestLen + 1024));
            new ReadOnlySpan<char>(bestName, bestLen).CopyTo(pool.Buf.AsSpan(pool.Len));
            t.NameOff[idx] = pool.Len; // relative to worker pool, rebased in merge
            t.NameLen[idx] = (ushort)bestLen;
            pool.Len += bestLen;
        }
    }

    // NTFS metadata ($MFT, $Secure:$SDS, $Extend\$UsnJrnl:$J, ...): their named streams are real disk usage, always counted
    static bool IsMeta(int idx, int parent) => (idx < 24 && idx != 8 /* $BadClus:$Bad spans the volume */) || parent == 11;

    static void AddStream(Ctx c, int owner, byte* attrHdr, int nameLen, long size, long alloc)
    {
        int nameOff = *(ushort*)(attrHdr + 0x0A);
        string name = new((char*)(attrHdr + nameOff), 0, nameLen);
        c.Streams.Enqueue(new StreamInfo { Owner = owner, Name = name, Size = size, Alloc = alloc });
    }
}

/// <summary>Copies the subtree under a path out of a full-volume tree (used when scanning an NTFS folder via MFT).</summary>
public static class SubtreeExtractor
{
    public static FileTree Extract(FileTree full, string path)
    {
        full.Finish();
        int src = full.FindPath(path);
        if (src < 0) return null;
        var t = new FileTree(1 << 16) { ClusterSize = full.ClusterSize };
        var map = new Dictionary<int, int>();
        var st = new Stack<int>();
        st.Push(src);
        while (st.Count > 0)
        {
            int v = st.Pop();
            int np = v == src ? -1 : map[full.Parent[v]];
            int ni = t.Add(np, full.NameSpan(v), v == src ? NodeKind.Root : full.Kind[v], full.OwnSize[v], full.OwnAlloc[v], full.Attr[v], full.CTime[v], full.MTime[v], full.ATime[v]);
            t.Flags[ni] = full.Flags[v];
            map[v] = ni;
            foreach (int ch in full.ChildrenOf(v)) if (!full.IsVirtual(ch)) st.Push(ch);
        }
        t.Root = 0;
        t.RootPath = path;
        t.SetName(0, path);
        return t;
    }
}
