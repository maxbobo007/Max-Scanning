using System.Globalization;
using System.Text.RegularExpressions;
using MaxScanning.Core;

namespace MaxScanning.Filter;

/// <summary>
/// SpaceSniffer-compatible filter language.
///   *.jpg;*.png        name patterns (wildcards * ?), OR-ed together
///   >100mb  <1kb       size (b kb mb gb tb), "=" or ">=" "<=" also accepted
///   >3days  <2w        age by modification (h d w m y; hours days weeks months years)
///   :red :green :blue :yellow   tagged entries (also :1 .. :4, :tagged)
///   |term              exclude (NOT) — e.g. |*.tmp  |:red  |<1mb
///   @h @s @r @c @e @a @l  attributes: hidden system readonly compressed encrypted archive link(reparse)
///   modified>2024-01-01 created<2023-06-30 accessed>=2024-05-01   absolute dates
///   folder:name        restrict to entries under folders whose name matches
///   regex:pattern      .NET regex on the name
/// Different term kinds are AND-ed, terms of the same kind are OR-ed. Excludes always AND NOT.
/// Filters apply to files (and streams); folders are shown when they contain matching files.
/// </summary>
public sealed class FilterSet
{
    enum K { Name, Size, Age, Tag, Attr, Date, Folder, Regex }
    enum Op { Lt, Le, Gt, Ge, Eq }

    sealed class Term
    {
        public K Kind; public bool Not; public Op Op;
        public Regex Rx; public long Num; public uint Attr; public int TagId; public int DateField; public string Raw;
    }

    readonly List<Term> _terms = new();
    public string Text { get; }
    public bool IsEmpty => _terms.Count == 0;
    public string Error { get; private set; }
    readonly long _now = DateTime.UtcNow.ToFileTimeUtc();

    public FilterSet(string text)
    {
        Text = text?.Trim() ?? "";
        foreach (var raw0 in Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var t = Parse(raw0); if (t != null) _terms.Add(t); }
            catch (Exception ex) { Error = $"'{raw0}': {ex.Message}"; }
        }
    }

    static readonly Regex SizeRx = new(@"^(<=|>=|<|>|=)\s*([\d.,]+)\s*(b|k|kb|m|mb|g|gb|t|tb|kib|mib|gib|tib)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AgeRx = new(@"^(<=|>=|<|>|=)\s*([\d.,]+)\s*(h|hour|hours|d|day|days|w|week|weeks|m|month|months|y|year|years)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DateRx = new(@"^(modified|created|accessed|m|c|a)\s*(<=|>=|<|>|=)\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    Term Parse(string raw)
    {
        var t = new Term { Raw = raw };
        if (raw.StartsWith('|')) { t.Not = true; raw = raw[1..].Trim(); }
        if (raw.Length == 0) return null;

        Match m;
        if ((m = DateRx.Match(raw)).Success)
        {
            t.Kind = K.Date; t.Op = ParseOp(m.Groups[2].Value);
            t.DateField = char.ToLowerInvariant(m.Groups[1].Value[0]) switch { 'c' => 1, 'a' => 2, _ => 0 };
            var d = DateTime.Parse(m.Groups[3].Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            t.Num = d.ToUniversalTime().ToFileTimeUtc();
            return t;
        }
        if ((m = AgeRx.Match(raw)).Success)
        {
            t.Kind = K.Age; t.Op = ParseOp(m.Groups[1].Value);
            double v = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            double hours = char.ToLowerInvariant(m.Groups[3].Value[0]) switch { 'h' => 1, 'd' => 24, 'w' => 168, 'm' => 730.5, 'y' => 8766, _ => 24 };
            t.Num = (long)(v * hours * 36_000_000_000L); // FILETIME ticks (100ns)
            return t;
        }
        if ((m = SizeRx.Match(raw)).Success)
        {
            t.Kind = K.Size; t.Op = ParseOp(m.Groups[1].Value);
            double v = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            string u = m.Groups[3].Value.ToLowerInvariant();
            long mul = u.Length == 0 || u == "b" ? 1 : u[0] switch { 'k' => 1L << 10, 'm' => 1L << 20, 'g' => 1L << 30, 't' => 1L << 40, _ => 1 };
            t.Num = (long)(v * mul);
            return t;
        }
        if (raw.StartsWith(':'))
        {
            t.Kind = K.Tag;
            string n = raw[1..].ToLowerInvariant();
            t.TagId = n switch { "red" or "r" or "1" or "a" => 1, "green" or "g" or "2" or "b" => 2, "blue" or "3" or "c" => 3, "yellow" or "y" or "4" or "d" => 4, "tagged" or "any" or "*" => -1, _ => throw new FormatException("unknown tag") };
            return t;
        }
        if (raw.StartsWith('@'))
        {
            t.Kind = K.Attr;
            foreach (char ch in raw[1..].ToLowerInvariant())
                t.Attr |= ch switch
                {
                    'h' => Native.FILE_ATTRIBUTE_HIDDEN, 's' => Native.FILE_ATTRIBUTE_SYSTEM, 'r' => Native.FILE_ATTRIBUTE_READONLY,
                    'c' => Native.FILE_ATTRIBUTE_COMPRESSED, 'e' => Native.FILE_ATTRIBUTE_ENCRYPTED, 'a' => Native.FILE_ATTRIBUTE_ARCHIVE,
                    'l' => Native.FILE_ATTRIBUTE_REPARSE_POINT, 'p' => Native.FILE_ATTRIBUTE_SPARSE, 'o' => Native.FILE_ATTRIBUTE_OFFLINE,
                    _ => throw new FormatException("unknown attribute " + ch)
                };
            return t;
        }
        if (raw.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            t.Kind = K.Regex; t.Rx = new Regex(raw[6..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            return t;
        }
        if (raw.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
        {
            t.Kind = K.Folder; t.Rx = Wild(raw[7..]);
            return t;
        }
        t.Kind = K.Name;
        // a bare word without wildcards matches as substring like SpaceSniffer ("log" -> *log*)
        string pat = raw.IndexOfAny(new[] { '*', '?' }) >= 0 ? raw : "*" + raw + "*";
        t.Rx = Wild(pat);
        return t;
    }

    static Regex Wild(string pat) =>
        new("^" + Regex.Escape(pat).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static Op ParseOp(string s) => s switch { "<" => Op.Lt, "<=" => Op.Le, ">" => Op.Gt, ">=" => Op.Ge, _ => Op.Eq };

    static bool Cmp(long a, Op op, long b) => op switch { Op.Lt => a < b, Op.Le => a <= b, Op.Gt => a > b, Op.Ge => a >= b, _ => a == b };

    /// <summary>Thread safe. Evaluate for a file/stream node.</summary>
    public bool Match(FileTree t, int i)
    {
        if (_terms.Count == 0) return true;
        Span<int> kindHit = stackalloc int[8]; // 0 = no positive term of kind, 1 = has terms but no hit, 2 = hit
        foreach (var term in _terms)
        {
            bool hit = Eval(term, t, i);
            if (term.Not) { if (hit) return false; continue; }
            int k = (int)term.Kind;
            if (hit) kindHit[k] = 2; else if (kindHit[k] == 0) kindHit[k] = 1;
        }
        foreach (int v in kindHit) if (v == 1) return false;
        return true;
    }

    bool Eval(Term term, FileTree t, int i)
    {
        switch (term.Kind)
        {
            case K.Name: return term.Rx.IsMatch(t.NameSpan(i));
            case K.Regex: return term.Rx.IsMatch(t.NameSpan(i));
            case K.Size: return Cmp(t.Size[i], term.Op, term.Num);
            case K.Age:
                {
                    long age = _now - t.MTime[i];
                    // ">3d" means older than 3 days
                    return Cmp(age, term.Op, term.Num);
                }
            case K.Date:
                {
                    long v = term.DateField switch { 1 => t.CTime[i], 2 => t.ATime[i], _ => t.MTime[i] };
                    return Cmp(v, term.Op, term.Num);
                }
            case K.Tag: return term.TagId == -1 ? TagOf(t, i) != 0 : TagOf(t, i) == term.TagId;
            case K.Attr: return (t.Attr[i] & term.Attr) == term.Attr;
            case K.Folder:
                for (int p = t.Parent[i]; p >= 0 && p != t.Root; p = t.Parent[p])
                    if (term.Rx.IsMatch(t.NameSpan(p))) return true;
                return false;
        }
        return true;
    }

    /// <summary>Tag inheritance: a file is considered tagged if itself or any ancestor carries the tag (SpaceSniffer behaviour).</summary>
    static int TagOf(FileTree t, int i)
    {
        for (int p = i; p >= 0; p = t.Parent[p]) if (t.Tag[p] != 0) return t.Tag[p];
        return 0;
    }
}
