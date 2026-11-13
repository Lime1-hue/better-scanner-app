using BetterScanner.Escl;

namespace BetterScanner;

/// <summary>User preferences, stored in %AppData%\BetterScanner\settings.json.</summary>
public sealed class AppSettings
{
    public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans");
    public bool DailySubfolder { get; set; }
    public string FilePrefix { get; set; } = "Scan";

    public string? ScannerId { get; set; }
    public List<ScannerInfo> KnownScanners { get; set; } = [];

    public string Color { get; set; } = "color";
    public int Resolution { get; set; } = 300;
    public bool Duplex { get; set; }
    public string PageSize { get; set; } = "auto";
    public bool SkipBlank { get; set; }
    public string Split { get; set; } = "stack";
    public int PagesPerFile { get; set; } = 2;
    public int SettleSeconds { get; set; } = 3;
    public int IdleStopMinutes { get; set; } = 10;
    /// <summary>Shown to coworkers while you're using a printer. Empty = Windows user name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Last release tag we showed a tray notification for (one notification per version).</summary>
    public string NotifiedUpdate { get; set; } = "";

    public bool AutoStartHandsFree { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool PlaySounds { get; set; } = true;

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterScanner");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Json.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // corrupt file — start fresh
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(this, indented: true));
            File.Copy(tmp, FilePath, overwrite: true);
            File.Delete(tmp);
        }
        catch
        {
            // not fatal
        }
    }
}
