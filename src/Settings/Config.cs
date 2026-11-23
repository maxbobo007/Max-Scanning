using System.Text.Json;
using System.Text.Json.Serialization;
using MaxScanning.IO;

namespace MaxScanning.Settings;

/// <summary>All SpaceSniffer configuration options (Graphics / Effects / Actions / Colors) + app state.</summary>
public sealed class Config
{
    // ---- Graphics
    public int MinCellSize { get; set; } = 6;         // px
    public float AspectRatio { get; set; } = 1.0f;    // <1 tall, >1 wide
    public int InitialDetail { get; set; } = 3;       // nesting levels
    public float FontSize { get; set; } = 9f;         // pt
    public bool ShowFreeSpace { get; set; } = true;
    public bool ShowUnknownSpace { get; set; } = true;
    public bool SortBySize { get; set; } = true;
    public bool CushionShading { get; set; } = true;
    public bool ShowSizeOnDisk { get; set; } = false; // size metric: logical vs allocated

    // ---- Effects
    public int RefreshDelayMs { get; set; } = 500;
    public int ZoomTimeMs { get; set; } = 250;
    public bool DynamicZoom { get; set; } = false;

    // ---- Actions
    public bool AlternateStreams { get; set; } = false;
    public bool PopupConsoleOnEvent { get; set; } = false;
    public bool LogScanWarnings { get; set; } = true;
    public bool DebugLog { get; set; } = false;
    public bool FlashWhenDone { get; set; } = true;
    public bool KeepTooltip { get; set; } = true;
    public bool PreferMft { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public int ScanThreads { get; set; } = Math.Clamp(Environment.ProcessorCount * 3, 16, 64); // enumeration is latency bound

    // ---- Colors (ARGB)
    public int ColorDrive { get; set; } = unchecked((int)0xFFB8BCC4);
    public int ColorFolder { get; set; } = unchecked((int)0xFFE6D59A);
    public int ColorFile { get; set; } = unchecked((int)0xFF9EC3EA);
    public int ColorFree { get; set; } = unchecked((int)0xFF96D296);
    public int ColorUnknown { get; set; } = unchecked((int)0xFFDB9C9C);
    public int ColorStream { get; set; } = unchecked((int)0xFFC9A8E6);
    public float LevelContrast { get; set; } = 0.06f;   // background change per nesting level
    public float BorderContrast { get; set; } = 0.35f;  // border darkness
    public float HighlightHover { get; set; } = 0.25f;  // brighten on hover
    public float HighlightLeave { get; set; } = 0.12f;  // fade amount when mouse leaves
    public int HighlightLevels { get; set; } = 1;       // how many ancestor levels light up

    // ---- App
    public string Language { get; set; } = "zh";         // "", "zh", "en"  ("" = follow Windows)
    public List<string> RecentPaths { get; set; } = new();
    public List<ExportPreset> ExportPresets { get; set; } = new();
    public string LastExportPreset { get; set; } = "CSV";
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowW { get; set; } = 1280;
    public int WindowH { get; set; } = 820;
    public bool WindowMax { get; set; }

    [JsonIgnore] public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaxScanning");
    [JsonIgnore] public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Config Current { get; private set; } = Load();

    public static Config Load(string file = null)
    {
        try
        {
            file ??= FilePath;
            if (File.Exists(file)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(file), Json) ?? new Config();
        }
        catch { }
        return new Config();
    }

    public void Save(string file = null)
    {
        try
        {
            file ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, Json), Json);
    public static void Replace(Config c) { Current = c; Current.Save(); Changed?.Invoke(); }
    public static event Action Changed;

    public void AddRecent(string p)
    {
        RecentPaths.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
        RecentPaths.Insert(0, p);
        if (RecentPaths.Count > 15) RecentPaths.RemoveRange(15, RecentPaths.Count - 15);
        Save();
    }

    public List<ExportPreset> AllPresets()
    {
        var list = ExportPreset.BuiltIn();
        list.AddRange(ExportPresets);
        return list;
    }
}
