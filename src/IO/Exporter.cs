using System.Text;
using MaxScanning.Core;
using MaxScanning.Filter;

namespace MaxScanning.IO;

public enum ExportSort { None, SizeDesc, SizeAsc, NameAsc, NameDesc, ModifiedDesc }
public enum ExportEscape { None, Csv, Html, Json }

/// <summary>Template based text export (SpaceSniffer "Export" dialog: header / detail / footer, sorting, presets).</summary>
public sealed class ExportPreset
{
    public string Name { get; set; } = "Custom";
    public string FileExtension { get; set; } = "Text file|*.txt";
    public string Header { get; set; } = "";
    public string Detail { get; set; } = "{path}\t{size}\n";
    public string Footer { get; set; } = "";
    public ExportSort Sort { get; set; } = ExportSort.SizeDesc;
    public int MaxDepth { get; set; } = 0; // 0 = unlimited
    public bool IncludeFiles { get; set; } = true;
    public bool IncludeFolders { get; set; } = true;
    public long MinSize { get; set; }
    public ExportEscape Escape { get; set; } = ExportEscape.None;
    public bool BuiltInPreset { get; set; }

    public ExportPreset Clone() => (ExportPreset)MemberwiseClone();

    public static List<ExportPreset> BuiltIn() => new()
    {
        new() { Name = "CSV", BuiltInPreset = true, FileExtension = "CSV|*.csv", Escape = ExportEscape.Csv,
            Header = "Path,Name,Type,Size,SizeOnDisk,Files,Folders,Modified,Created,Accessed,Attributes,Tag\n",
            Detail = "{path},{name},{type},{size},{alloc},{files},{dirs},{modified},{created},{accessed},{attr},{tag}\n" },
        new() { Name = "TXT", BuiltInPreset = true, FileExtension = "Text|*.txt",
            Header = "Max Scanning report — {root}\nScanned {date}   Total {total_h} in {total_files} files\n\n",
            Detail = "{size_h,12}  {percent,6}  {indent}{name}\n", Footer = "\n-- end --\n" },
        new() { Name = "Tree", BuiltInPreset = true, FileExtension = "Text|*.txt", IncludeFiles = false, MaxDepth = 4,
            Header = "{root}  ({total_h})\n", Detail = "{tree}{name}  [{size_h}]\n" },
        new() { Name = "HTML", BuiltInPreset = true, FileExtension = "HTML|*.html", Escape = ExportEscape.Html,
            Header = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{root}</title><style>body{font:13px Segoe UI,sans-serif}table{border-collapse:collapse}td,th{padding:2px 8px;border-bottom:1px solid #ddd;text-align:left}td.n{text-align:right}</style></head><body><h3>{root} — {total_h}</h3><table><tr><th>Path</th><th>Size</th><th>%</th><th>Files</th><th>Modified</th></tr>\n",
            Detail = "<tr><td>{path}</td><td class=n>{size_h}</td><td class=n>{percent}</td><td class=n>{files}</td><td>{modified}</td></tr>\n",
            Footer = "</table></body></html>\n" },
        new() { Name = "JSON", BuiltInPreset = true, FileExtension = "JSON|*.json", Escape = ExportEscape.Json,
            Header = "[\n", Detail = "{\"path\":\"{path}\",\"type\":\"{type}\",\"size\":{size},\"alloc\":{alloc},\"files\":{files},\"modified\":\"{modified}\"},\n", Footer = "{}]\n" },
    };
}

public static class Exporter
{
    public static string Export(FileTree t, int start, ExportPreset p, FilterSet filter, int maxLines = int.MaxValue, Func<int, long> sizeOf = null)
    {
        var sb = new StringBuilder(1 << 16);
        sizeOf ??= i => t.Size[i];
        long total = sizeOf(start);
        string rootPath = t.FullPath(start);
        sb.Append(Global(p.Header, t, start, total, rootPath, p.Escape));
        int lines = 0;
        var stack = new Stack<(int node, int depth, string treePrefix, bool last)>();
        stack.Push((start, 0, "", true));
        while (stack.Count > 0 && lines < maxLines)
        {
            var (v, depth, prefix, last) = stack.Pop();
            bool dir = t.IsDirLike(v);
            bool include = v != start && (dir ? p.IncludeFolders : p.IncludeFiles) && sizeOf(v) >= p.MinSize
                           && (dir || filter == null || filter.IsEmpty || filter.Match(t, v)) && !(dir && sizeOf(v) == 0 && filter != null && !filter.IsEmpty);
            if (t.IsVirtual(v)) include = false;
            if (include) { sb.Append(Detail(p, t, v, depth, prefix + (last ? "└─ " : "├─ "), total)); lines++; }
            if (p.MaxDepth > 0 && depth >= p.MaxDepth) continue;
            var kids = t.ChildrenOf(v).ToArray();
            if (kids.Length == 0) continue;
            Sort(t, kids, p.Sort, sizeOf);
            string childPrefix = v == start ? "" : prefix + (last ? "   " : "│  ");
            for (int k = kids.Length - 1; k >= 0; k--) stack.Push((kids[k], depth + 1, childPrefix, k == kids.Length - 1));
        }
        if (lines >= maxLines) sb.Append("…\n");
        sb.Append(Global(p.Footer, t, start, total, rootPath, p.Escape));
        return sb.ToString();
    }

    static void Sort(FileTree t, int[] kids, ExportSort s, Func<int, long> sizeOf)
    {
        switch (s)
        {
            case ExportSort.SizeDesc: Array.Sort(kids, (a, b) => sizeOf(b).CompareTo(sizeOf(a))); break;
            case ExportSort.SizeAsc: Array.Sort(kids, (a, b) => sizeOf(a).CompareTo(sizeOf(b))); break;
            case ExportSort.NameAsc: Array.Sort(kids, (a, b) => t.NameSpan(a).CompareTo(t.NameSpan(b), StringComparison.OrdinalIgnoreCase)); break;
            case ExportSort.NameDesc: Array.Sort(kids, (a, b) => t.NameSpan(b).CompareTo(t.NameSpan(a), StringComparison.OrdinalIgnoreCase)); break;
            case ExportSort.ModifiedDesc: Array.Sort(kids, (a, b) => t.MTime[b].CompareTo(t.MTime[a])); break;
        }
    }

    static string Global(string tpl, FileTree t, int start, long total, string root, ExportEscape esc) =>
        Fill(tpl, key => key switch
        {
            "root" => Esc(root, esc),
            "date" => t.ScanDate.ToString("yyyy-MM-dd HH:mm"),
            "total" => total.ToString(),
            "total_h" => Util.FormatSize(total),
            "total_files" => t.Files[start].ToString("N0"),
            "total_dirs" => t.Dirs[start].ToString("N0"),
            "method" => t.ScanMethod,
            _ => null
        });

    static string Detail(ExportPreset p, FileTree t, int i, int depth, string treePrefix, long total)
    {
        var esc = p.Escape;
        return Fill(p.Detail, key => key switch
        {
            "path" => Esc(t.FullPath(i), esc),
            "name" => Esc(t.Name(i), esc),
            "ext" => Util.Extension(t.NameSpan(i)),
            "type" => t.Kind[i] switch { NodeKind.Dir => "folder", NodeKind.Stream => "stream", NodeKind.Root => "root", _ => "file" },
            "size" => t.Size[i].ToString(),
            "size_h" => Util.FormatSize(t.Size[i]),
            "alloc" => t.Alloc[i].ToString(),
            "alloc_h" => Util.FormatSize(t.Alloc[i]),
            "files" => t.Files[i].ToString(),
            "dirs" => t.Dirs[i].ToString(),
            "modified" => Util.FormatTime(t.MTime[i]),
            "created" => Util.FormatTime(t.CTime[i]),
            "accessed" => Util.FormatTime(t.ATime[i]),
            "attr" => Util.AttrString(t.Attr[i]),
            "tag" => Util.TagName(t.Tag[i]),
            "depth" => depth.ToString(),
            "indent" => new string(' ', Math.Max(0, depth - 1) * 2),
            "tree" => treePrefix,
            "percent" => total > 0 ? (t.Size[i] * 100.0 / total).ToString("F1") + "%" : "",
            _ => null
        });
    }

    /// <summary>Replace {key} and {key,width} (right aligned, negative = left) placeholders.</summary>
    public static string Fill(string tpl, Func<string, string> get)
    {
        if (string.IsNullOrEmpty(tpl)) return "";
        tpl = tpl.Replace("\\n", "\n").Replace("\\t", "\t");
        var sb = new StringBuilder(tpl.Length + 64);
        int i = 0;
        while (i < tpl.Length)
        {
            char ch = tpl[i];
            if (ch == '{')
            {
                int e = tpl.IndexOf('}', i + 1);
                if (e > i)
                {
                    string spec = tpl[(i + 1)..e];
                    int comma = spec.IndexOf(',');
                    string key = comma >= 0 ? spec[..comma] : spec;
                    string val = get(key.Trim().ToLowerInvariant());
                    if (val != null)
                    {
                        if (comma >= 0 && int.TryParse(spec[(comma + 1)..], out int width))
                            val = width >= 0 ? val.PadLeft(width) : val.PadRight(-width);
                        sb.Append(val);
                        i = e + 1;
                        continue;
                    }
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    static string Esc(string s, ExportEscape e) => e switch
    {
        ExportEscape.Csv => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s,
        ExportEscape.Html => System.Net.WebUtility.HtmlEncode(s),
        ExportEscape.Json => System.Text.Json.JsonEncodedText.Encode(s).ToString(),
        _ => s
    };
}
