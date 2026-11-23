using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using MaxScanning.Core;

namespace MaxScanning.IO;

/// <summary>
/// Snapshot = the whole column store dumped as raw arrays through Brotli. Saving/loading millions of nodes
/// takes well under a second because there is no per-node serialization.
/// </summary>
public static class Snapshot
{
    const string Magic = "MSCAN\u0001";
    public const string Extension = ".mscan";

    public static void Save(FileTree t, string file)
    {
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        fs.Write(Encoding.ASCII.GetBytes(Magic));
        using var br = new BrotliStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(br, Encoding.UTF8);
        int n = t.Count;
        w.Write(n); w.Write(t.Root); w.Write(t.FreeNode); w.Write(t.UnknownNode);
        w.Write(t.RootPath); w.Write(t.RootIsVolume); w.Write(t.VolumeTotal); w.Write(t.VolumeFree); w.Write(t.ClusterSize);
        w.Write(t.FileSystem ?? ""); w.Write(t.ScanMethod ?? ""); w.Write(t.ScanDate.ToBinary()); w.Write(t.ScanTime.Ticks);
        Arr(w, t.Parent, n); Arr(w, t.OwnSize, n); Arr(w, t.OwnAlloc, n);
        Arr(w, t.CTime, n); Arr(w, t.MTime, n); Arr(w, t.ATime, n); Arr(w, t.Attr, n);
        Arr(w, MemoryMarshal.Cast<NodeKind, byte>(t.Kind.AsSpan(0, n)).ToArray(), n);
        Arr(w, MemoryMarshal.Cast<NodeFlags, byte>(t.Flags.AsSpan(0, n)).ToArray(), n);
        Arr(w, t.Tag, n); Arr(w, t.NameOff, n); Arr(w, t.NameLen, n);
        w.Write(t.PoolLength);
        w.Write(MemoryMarshal.AsBytes(t.Pool.AsSpan(0, t.PoolLength)));
    }

    public static FileTree Load(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var magic = new byte[Magic.Length];
        fs.ReadExactly(magic);
        if (Encoding.ASCII.GetString(magic) != Magic) throw new InvalidDataException("Not a Max Scanning snapshot");
        using var br = new BrotliStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(br, Encoding.UTF8);
        int n = r.ReadInt32();
        var t = new FileTree(n + 1024);
        t.EnsureCapacity(n + 1024);
        t.Count = n;
        t.Root = r.ReadInt32(); t.FreeNode = r.ReadInt32(); t.UnknownNode = r.ReadInt32();
        t.RootPath = r.ReadString(); t.RootIsVolume = r.ReadBoolean(); t.VolumeTotal = r.ReadInt64(); t.VolumeFree = r.ReadInt64(); t.ClusterSize = r.ReadInt32();
        t.FileSystem = r.ReadString(); t.ScanMethod = r.ReadString(); t.ScanDate = DateTime.FromBinary(r.ReadInt64()); t.ScanTime = TimeSpan.FromTicks(r.ReadInt64());
        Read(r, t.Parent, n); Read(r, t.OwnSize, n); Read(r, t.OwnAlloc, n);
        Read(r, t.CTime, n); Read(r, t.MTime, n); Read(r, t.ATime, n); Read(r, t.Attr, n);
        var kinds = new byte[n]; Read(r, kinds, n); MemoryMarshal.Cast<byte, NodeKind>(kinds).CopyTo(t.Kind);
        var flags = new byte[n]; Read(r, flags, n); MemoryMarshal.Cast<byte, NodeFlags>(flags).CopyTo(t.Flags);
        Read(r, t.Tag, n); Read(r, t.NameOff, n); Read(r, t.NameLen, n);
        int plen = r.ReadInt32();
        var pool = new char[Math.Max(plen + 4096, 16)];
        ReadBytes(r, MemoryMarshal.AsBytes(pool.AsSpan(0, plen)));
        t.SetPool(pool, plen);
        t.ScanMethod = (t.ScanMethod ?? "") + " (snapshot)";
        t.Finish();
        return t;
    }

    static void Arr<T>(BinaryWriter w, T[] a, int n) where T : unmanaged => w.Write(MemoryMarshal.AsBytes(a.AsSpan(0, n)));
    static void Read<T>(BinaryReader r, T[] a, int n) where T : unmanaged => ReadBytes(r, MemoryMarshal.AsBytes(a.AsSpan(0, n)));
    static void ReadBytes(BinaryReader r, Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            int got = r.BaseStream.Read(dst);
            if (got <= 0) throw new EndOfStreamException();
            dst = dst[got..];
        }
    }
}
