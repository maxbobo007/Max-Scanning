using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MaxScanning;

internal static unsafe class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    public const uint FILE_ATTRIBUTE_READONLY = 0x1, FILE_ATTRIBUTE_HIDDEN = 0x2, FILE_ATTRIBUTE_SYSTEM = 0x4,
        FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_ARCHIVE = 0x20, FILE_ATTRIBUTE_SPARSE = 0x200,
        FILE_ATTRIBUTE_REPARSE_POINT = 0x400, FILE_ATTRIBUTE_COMPRESSED = 0x800, FILE_ATTRIBUTE_OFFLINE = 0x1000,
        FILE_ATTRIBUTE_ENCRYPTED = 0x4000, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    [StructLayout(LayoutKind.Sequential)]
    public struct NTFS_VOLUME_DATA_BUFFER
    {
        public long VolumeSerialNumber, NumberSectors, TotalClusters, FreeClusters, TotalReserved;
        public uint BytesPerSector, BytesPerCluster, BytesPerFileRecordSegment, ClustersPerFileRecordSegment;
        public long MftValidDataLength, MftStartLcn, Mft2StartLcn, MftZoneStart, MftZoneEnd;
    }

    // Native WIN32_FIND_DATAW has FILETIMEs at 4-byte alignment; explicit layout keeps it exact.
    [StructLayout(LayoutKind.Explicit, Size = 592, CharSet = CharSet.Unicode)]
    public struct FindData
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(4)] public uint CreationLow; [FieldOffset(8)] public uint CreationHigh;
        [FieldOffset(12)] public uint AccessLow; [FieldOffset(16)] public uint AccessHigh;
        [FieldOffset(20)] public uint WriteLow; [FieldOffset(24)] public uint WriteHigh;
        [FieldOffset(28)] public uint SizeHigh; [FieldOffset(32)] public uint SizeLow;
        [FieldOffset(36)] public uint Reserved0; [FieldOffset(40)] public uint Reserved1;
        [FieldOffset(44)] public fixed char FileName[260];
    }

    [StructLayout(LayoutKind.Explicit, Size = 600, CharSet = CharSet.Unicode)]
    public struct FindStreamData
    {
        [FieldOffset(0)] public long StreamSize;
        [FieldOffset(8)] public fixed char StreamName[296];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, void* buf, int size);
    public const int FileFullDirectoryInfo = 14, FileFullDirectoryRestartInfo = 15;
    public const uint FILE_LIST_DIRECTORY = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, void* buf, int toRead, out int read, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPos, uint method);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstFileExW(string name, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextFileW(IntPtr h, out FindData data);

    [DllImport("kernel32.dll")]
    public static extern bool FindClose(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstStreamW(string name, int level, out FindStreamData data, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindNextStreamW(IntPtr h, out FindStreamData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetCompressedFileSizeW(string name, out uint high);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceW(string root, out uint secPerClus, out uint bytesPerSec, out uint freeClus, out uint totalClus);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string dir, out long freeToCaller, out long total, out long totalFree);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformationW(string root, char* volName, int volNameSize, out uint serial, out uint maxComp, out uint fsFlags, char* fsName, int fsNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumePathNameW(string path, char* buf, int len);

    [DllImport("user32.dll")]
    public static extern bool FlashWindowEx(ref FLASHWINFO fi);

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        public string lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
    }

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public static string VolumeRoot(string path)
    {
        char* buf = stackalloc char[1024];
        return GetVolumePathNameW(path, buf, 1024) ? new string(buf) : Path.GetPathRoot(path);
    }

    public static string FileSystemName(string root)
    {
        char* fs = stackalloc char[64];
        return GetVolumeInformationW(root, null, 0, out _, out _, out _, fs, 64) ? new string(fs) : "";
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static void ShowProperties(string path)
    {
        var info = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = 0x0000000C, lpVerb = "properties", lpFile = path, nShow = 1 };
        ShellExecuteExW(ref info);
    }

    public static void Flash(IntPtr hwnd)
    {
        var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 3 | 12, uCount = 5, dwTimeout = 0 };
        FlashWindowEx(ref fi);
    }
}
