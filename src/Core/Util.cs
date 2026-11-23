namespace MaxScanning.Core;

public static class Util
{
    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string FormatSize(long b)
    {
        if (b < 1024) return b + " B";
        double v = b; int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return v >= 100 ? $"{v:F0} {Units[u]}" : v >= 10 ? $"{v:F1} {Units[u]}" : $"{v:F2} {Units[u]}";
    }

    public static string FormatTime(long ft)
    {
        if (ft <= 0) return "";
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } catch { return ""; }
    }

    public static string AttrString(uint a)
    {
        Span<char> s = stackalloc char[8];
        int n = 0;
        if ((a & Native.FILE_ATTRIBUTE_READONLY) != 0) s[n++] = 'R';
        if ((a & Native.FILE_ATTRIBUTE_HIDDEN) != 0) s[n++] = 'H';
        if ((a & Native.FILE_ATTRIBUTE_SYSTEM) != 0) s[n++] = 'S';
        if ((a & Native.FILE_ATTRIBUTE_ARCHIVE) != 0) s[n++] = 'A';
        if ((a & Native.FILE_ATTRIBUTE_COMPRESSED) != 0) s[n++] = 'C';
        if ((a & Native.FILE_ATTRIBUTE_ENCRYPTED) != 0) s[n++] = 'E';
        if ((a & Native.FILE_ATTRIBUTE_SPARSE) != 0) s[n++] = 'P';
        if ((a & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) s[n++] = 'L';
        return new string(s[..n]);
    }

    public static string TagName(int tag) => tag switch { 1 => "red", 2 => "green", 3 => "blue", 4 => "yellow", _ => "" };

    public static string Extension(ReadOnlySpan<char> name)
    {
        int d = name.LastIndexOf('.');
        return d > 0 && d < name.Length - 1 ? name[(d + 1)..].ToString().ToLowerInvariant() : "";
    }
}
