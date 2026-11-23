using System.Runtime.CompilerServices;

namespace MaxScanning.Core;

public enum NodeKind : byte { File = 0, Dir = 1, Root = 2, Free = 3, Unknown = 4, Stream = 5 }

[Flags]
public enum NodeFlags : byte { None = 0, Dead = 1, AccessDenied = 2, Reparse = 4, Hardlink = 8 }

/// <summary>
/// Column-oriented (struct-of-arrays) file tree. One node = ~90 bytes incl. name; no per-node objects,
/// so millions of entries stay cache friendly and GC-free. Children are stored in CSR form, sorted by size.
/// </summary>
public sealed class FileTree
{
    public int Count;
    int _cap;

    public int[] Parent;
    public long[] OwnSize, OwnAlloc;   // values of the entry itself
    public long[] Size, Alloc;         // aggregated (dirs = subtree)
    public long[] CTime, MTime, ATime; // FILETIME
    public uint[] Attr;
    public int[] Files, Dirs;          // aggregated counts
    public NodeKind[] Kind;
    public NodeFlags[] Flags;
    public byte[] Tag;                 // 0 none, 1..4 = red, green, blue, yellow
    public int[] NameOff;
    public ushort[] NameLen;
    char[] _pool;
    int _poolLen;

    // CSR children
    public int[] ChildStart = Array.Empty<int>(), ChildCount = Array.Empty<int>(), Children = Array.Empty<int>();

    public int Root = -1;
    public int FreeNode = -1, UnknownNode = -1;
    public string RootPath = "";
    public bool RootIsVolume;
    public long VolumeTotal, VolumeFree;
    public int ClusterSize = 4096;
    public string FileSystem = "";
    public string ScanMethod = "";
    public TimeSpan ScanTime;
    public DateTime ScanDate = DateTime.Now;
    public readonly object Sync = new();
    public int Version; // bumped on every structural change

    public FileTree(int capacity = 1 << 16)
    {
        _cap = Math.Max(capacity, 1024);
        Parent = new int[_cap]; OwnSize = new long[_cap]; OwnAlloc = new long[_cap];
        Size = new long[_cap]; Alloc = new long[_cap];
        CTime = new long[_cap]; MTime = new long[_cap]; ATime = new long[_cap];
        Attr = new uint[_cap]; Files = new int[_cap]; Dirs = new int[_cap];
        Kind = new NodeKind[_cap]; Flags = new NodeFlags[_cap]; Tag = new byte[_cap];
        NameOff = new int[_cap]; NameLen = new ushort[_cap];
        _pool = new char[_cap * 16];
    }

    public void EnsureCapacity(int n)
    {
        if (n <= _cap) return;
        int nc = Math.Max(n, (int)Math.Min(int.MaxValue - 64, (long)_cap * 2));
        Array.Resize(ref Parent, nc); Array.Resize(ref OwnSize, nc); Array.Resize(ref OwnAlloc, nc);
        Array.Resize(ref Size, nc); Array.Resize(ref Alloc, nc);
        Array.Resize(ref CTime, nc); Array.Resize(ref MTime, nc); Array.Resize(ref ATime, nc);
        Array.Resize(ref Attr, nc); Array.Resize(ref Files, nc); Array.Resize(ref Dirs, nc);
        Array.Resize(ref Kind, nc); Array.Resize(ref Flags, nc); Array.Resize(ref Tag, nc);
        Array.Resize(ref NameOff, nc); Array.Resize(ref NameLen, nc);
        _cap = nc;
    }

    void EnsurePool(int extra)
    {
        if (_poolLen + extra <= _pool.Length) return;
        long nl = Math.Max((long)_pool.Length * 2, _poolLen + extra + 4096);
        if (nl > Array.MaxLength) nl = Array.MaxLength;
        Array.Resize(ref _pool, (int)nl);
    }

    /// <summary>Not thread safe; callers serialize (scanners batch per directory).</summary>
    public int Add(int parent, ReadOnlySpan<char> name, NodeKind kind, long size, long alloc, uint attr, long ctime, long mtime, long atime)
    {
        int i = Count;
        if (i >= _cap) EnsureCapacity(i + 1);
        Count = i + 1;
        SetNode(i, parent, name, kind, size, alloc, attr, ctime, mtime, atime);
        return i;
    }

    /// <summary>Write node at a fixed index (MFT scanner uses record number as index).</summary>
    public void SetNode(int i, int parent, ReadOnlySpan<char> name, NodeKind kind, long size, long alloc, uint attr, long ctime, long mtime, long atime)
    {
        Parent[i] = parent; Kind[i] = kind; OwnSize[i] = size; OwnAlloc[i] = alloc; Attr[i] = attr;
        CTime[i] = ctime; MTime[i] = mtime; ATime[i] = atime; Flags[i] = NodeFlags.None; Tag[i] = 0;
        SetName(i, name);
    }

    public void SetName(int i, ReadOnlySpan<char> name)
    {
        int len = Math.Min(name.Length, ushort.MaxValue);
        EnsurePool(len);
        name[..len].CopyTo(_pool.AsSpan(_poolLen));
        NameOff[i] = _poolLen; NameLen[i] = (ushort)len;
        _poolLen += len;
    }

    /// <summary>Append a pre-built name pool segment (used by parallel MFT scanner merge).</summary>
    public int AppendPool(ReadOnlySpan<char> chars)
    {
        EnsurePool(chars.Length);
        int off = _poolLen;
        chars.CopyTo(_pool.AsSpan(off));
        _poolLen += chars.Length;
        return off;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> NameSpan(int i) => _pool.AsSpan(NameOff[i], NameLen[i]);
    public string Name(int i) => new(NameSpan(i));

    public bool IsContainer(int i) => Kind[i] is NodeKind.Dir or NodeKind.Root || (Kind[i] == NodeKind.File && ChildCount.Length > i && ChildCount[i] > 0);
    public bool IsDirLike(int i) => Kind[i] is NodeKind.Dir or NodeKind.Root;
    public bool IsVirtual(int i) => Kind[i] is NodeKind.Free or NodeKind.Unknown;

    public string FullPath(int i)
    {
        if (i < 0) return "";
        if (i == Root) return RootPath;
        Span<int> chain = stackalloc int[512];
        int n = 0;
        for (int p = i; p >= 0 && p != Root && n < 512; p = Parent[p]) chain[n++] = p;
        var sb = new System.Text.StringBuilder(RootPath.Length + n * 24);
        sb.Append(RootPath);
        for (int k = n - 1; k >= 0; k--)
        {
            int c = chain[k];
            if (Kind[c] == NodeKind.Stream) { sb.Append(':').Append(NameSpan(c)); continue; }
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            sb.Append(NameSpan(c));
        }
        return sb.ToString();
    }

    public int Depth(int i) { int d = 0; for (int p = i; p >= 0 && p != Root; p = Parent[p]) d++; return d; }

    public bool IsAncestor(int anc, int node)
    {
        for (int p = node; p >= 0; p = Parent[p]) if (p == anc) return true;
        return false;
    }

    public ReadOnlySpan<int> ChildrenOf(int i) =>
        i >= 0 && i < ChildCount.Length ? Children.AsSpan(ChildStart[i], ChildCount[i]) : ReadOnlySpan<int>.Empty;

    /// <summary>
    /// Builds CSR child lists, aggregates sizes/counts bottom-up (only nodes reachable from Root),
    /// and sorts children by size (desc). Parallel where it pays off. O(n log k).
    /// </summary>
    public void Finish()
    {
        int n = Count;
        var cnt = new int[n];
        for (int i = 0; i < n; i++)
        {
            int p = Parent[i];
            if (p >= 0 && p < n && p != i && (Flags[i] & NodeFlags.Dead) == 0) cnt[p]++;
        }
        var start = new int[n];
        int acc = 0;
        for (int i = 0; i < n; i++) { start[i] = acc; acc += cnt[i]; }
        var children = new int[acc];
        var fill = new int[n];
        for (int i = 0; i < n; i++)
        {
            int p = Parent[i];
            if (p >= 0 && p < n && p != i && (Flags[i] & NodeFlags.Dead) == 0) children[start[p] + fill[p]++] = i;
        }
        ChildStart = start; ChildCount = cnt; Children = children;
        Aggregate();
        SortChildren();
        Version++;
    }

    /// <summary>Recompute aggregates (after tagging nothing changes; after deletes/edits call this).</summary>
    public void Aggregate()
    {
        int n = Count;
        if (Root < 0) return;
        // iterative pre-order, then reverse for post-order accumulation
        var order = new int[n];
        int on = 0;
        var stack = new int[Math.Max(64, n)];
        int sp = 0;
        stack[sp++] = Root;
        var seen = new bool[n];
        while (sp > 0)
        {
            int v = stack[--sp];
            if (seen[v]) continue; // cycle guard (corrupt MFT)
            seen[v] = true;
            order[on++] = v;
            int s = ChildStart[v], e = s + ChildCount[v];
            for (int k = s; k < e; k++) { int c = Children[k]; if (!seen[c]) stack[sp++] = c; }
        }
        for (int k = 0; k < on; k++)
        {
            int v = order[k];
            Size[v] = OwnSize[v]; Alloc[v] = OwnAlloc[v];
            bool dir = Kind[v] is NodeKind.Dir or NodeKind.Root;
            Files[v] = Kind[v] == NodeKind.File ? 1 : 0;
            Dirs[v] = 0;
            _ = dir;
        }
        for (int k = on - 1; k > 0; k--)
        {
            int v = order[k];
            if (Kind[v] is NodeKind.Free or NodeKind.Unknown) continue;
            int p = Parent[v];
            Size[p] += Size[v]; Alloc[p] += Alloc[v];
            if (Kind[v] != NodeKind.Stream) { Files[p] += Files[v]; Dirs[p] += Dirs[v] + (Kind[v] == NodeKind.Dir ? 1 : 0); }
            if (MTime[v] > MTime[p] && Kind[p] != NodeKind.File) { /* keep folder own time (SpaceSniffer shows folder times) */ }
        }
        UpdateVirtualSpace();
    }

    public void UpdateVirtualSpace()
    {
        if (FreeNode >= 0) { OwnSize[FreeNode] = Size[FreeNode] = Alloc[FreeNode] = OwnAlloc[FreeNode] = VolumeFree; }
        if (UnknownNode >= 0)
        {
            long unk = Math.Max(0, VolumeTotal - VolumeFree - Alloc[Root]);
            OwnSize[UnknownNode] = Size[UnknownNode] = Alloc[UnknownNode] = OwnAlloc[UnknownNode] = unk;
        }
    }

    void SortChildren()
    {
        var size = Size; var start = ChildStart; var cnt = ChildCount; var ch = Children;
        Parallel.For(0, Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            int c = cnt[i];
            if (c < 2) return;
            var span = ch.AsSpan(start[i], c);
            span.Sort((a, b) => size[b].CompareTo(size[a]));
        });
    }

    /// <summary>Re-sort one directory's children (after incremental change).</summary>
    public void SortChildrenOf(int i)
    {
        if (i < 0 || ChildCount[i] < 2) return;
        var size = Size;
        Children.AsSpan(ChildStart[i], ChildCount[i]).Sort((a, b) => size[b].CompareTo(size[a]));
    }

    public void AddVirtualSpaceNodes()
    {
        if (!RootIsVolume || VolumeTotal <= 0 || Root < 0) return;
        FreeNode = Add(Root, "<Free space>", NodeKind.Free, VolumeFree, VolumeFree, 0, 0, 0, 0);
        UnknownNode = Add(Root, "<Unknown>", NodeKind.Unknown, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Mark a subtree dead (deleted or about to be replaced by a rescan).</summary>
    public void KillSubtree(int i)
    {
        var st = new Stack<int>();
        st.Push(i);
        while (st.Count > 0)
        {
            int v = st.Pop();
            Flags[v] |= NodeFlags.Dead;
            foreach (int c in ChildrenOf(v)) st.Push(c);
        }
    }

    public long MemoryBytes() =>
        (long)_cap * (4 + 8 * 7 + 4 + 4 + 4 + 1 + 1 + 1 + 4 + 2) + (long)_pool.Length * 2 + Children.Length * 4L + ChildStart.Length * 8L;

    /// <summary>Find node by full path (case-insensitive walk). Returns -1 if not found.</summary>
    public int FindPath(string path)
    {
        if (Root < 0) return -1;
        string rel = path;
        if (!rel.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase)) return -1;
        rel = rel[RootPath.Length..].Trim('\\');
        int cur = Root;
        if (rel.Length == 0) return cur;
        foreach (var part in rel.Split('\\'))
        {
            int found = -1;
            foreach (int c in ChildrenOf(cur))
                if (NameSpan(c).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = c; break; }
            if (found < 0) return -1;
            cur = found;
        }
        return cur;
    }

    internal char[] Pool => _pool;
    internal int PoolLength => _poolLen;
    internal void SetPool(char[] pool, int len) { _pool = pool; _poolLen = len; }
}
