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
    public int SettleSeconds { get; set; } = 2;
    public int IdleStopMinutes { get; set; } = 5;
    /// <summary>Shown to coworkers while you're using a printer. Empty = Windows user name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Last release tag we showed a tray notification for (one notification per version).</summary>
    public string NotifiedUpdate { get; set; } = "";

    public bool AutoStartHandsFree { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool PlaySounds { get; set; } = true;

    /// <summary>Settings-file format. Missing (0) in files written before 1.0.2.</summary>
    public int Schema { get; set; }
    private const int CurrentSchema = 2;

    /// <summary>
    /// Testing aid: BETTERSCANNER_PROFILE=test runs a separate copy next to the normal one,
    /// with its own settings folder. Empty for everyone else.
    /// </summary>
    public static readonly string ProfileSuffix =
        Environment.GetEnvironmentVariable("BETTERSCANNER_PROFILE") is { Length: > 0 } p ? "-" + string.Concat(p.Where(char.IsLetterOrDigit)) : "";

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterScanner" + ProfileSuffix);
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath) ? Json.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings() : new AppSettings();
        }
        catch
        {
            s = new AppSettings(); // corrupt file — start fresh
        }
        s.Migrate();
        return s;
    }

    /// <summary>Moves files from older versions to the new defaults, unless the user changed them.</summary>
    private void Migrate()
    {
        if (Schema < 2)
        {
            if (SettleSeconds == 3) SettleSeconds = 2;     // 1.0.2: shorter wait after paper is put in
            if (IdleStopMinutes == 10) IdleStopMinutes = 5; // 1.0.2: auto-stop sooner when forgotten
        }
        Schema = CurrentSchema;
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
