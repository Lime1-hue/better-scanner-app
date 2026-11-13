using BetterScanner.Escl;
using BetterScanner.Imaging;

namespace BetterScanner.Scanning;

/// <summary>Everything that decides how a scan is captured and saved. Immutable snapshot.</summary>
public sealed class ScanOptions
{
    public string Color { get; init; } = "color";      // color | gray | bw
    public int Resolution { get; init; } = 300;
    public bool Duplex { get; init; }
    public string PageSize { get; init; } = "auto";    // auto | a4 | letter | folio | legal | full
    public bool SkipBlank { get; init; }
    public string Split { get; init; } = "stack";      // stack | page | fixed
    public int PagesPerFile { get; init; } = 2;
    public string Folder { get; init; } = "";
    public bool DailySubfolder { get; init; }
    public string Prefix { get; init; } = "Scan";
    public int SettleSeconds { get; init; } = 2;
    /// <summary>Hands-free stops by itself after this long with no paper, so the printer is freed for others.</summary>
    public int IdleStopMinutes { get; init; } = 5;
}

/// <param name="Warning">Set when the job broke part-way and only some pages were saved.</param>
/// <param name="FolderNote">Set when the chosen folder couldn't be written and the scan was saved somewhere else instead.</param>
public sealed record ScanOutcome(IReadOnlyList<string> Files, int Pages, int SkippedBlank, string? Warning, string? FolderNote = null);

/// <summary>Scan → trim/size pages → write PDF(s) into the chosen folder.</summary>
public static class ScanPipeline
{
    // One scan at a time per app: hands-free and "Scan glass now" never collide.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ScanOutcome> RunAsync(ScannerInfo scanner, bool feeder, ScanOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        var tmp = Path.Combine(Path.GetTempPath(), "BetterScanner", Guid.NewGuid().ToString("N"));
        try
        {
            var (w, h) = await EsclClient.GetMaxRegionAsync(scanner.Ip, feeder, ct).ConfigureAwait(false);
            var req = new ScanRequest
            {
                Feeder = feeder,
                Duplex = feeder && o.Duplex,
                // Black & white is scanned in grey and thresholded here — cleaner than the printer's 1-bit mode.
                ColorMode = o.Color == "color" ? "RGB24" : "Grayscale8",
                Resolution = Math.Min(o.Resolution, feeder ? 600 : 1200),
                Width = w,
                Height = h,
            };

            var raw = await EsclClient.ScanAsync(scanner.Ip, req, tmp, progress, ct).ConfigureAwait(false);
            progress?.Report($"Saving {raw.Files.Count} page{(raw.Files.Count == 1 ? "" : "s")}…");

            var outcome = await Task.Run(() => AssembleWithFallback(raw.Files, req.Resolution, o, ct), ct).ConfigureAwait(false);
            return raw.Error is null
                ? outcome
                : outcome with { Warning = $"{raw.Error} The {outcome.Pages} page(s) received before that were saved — check the feeder." };
        }
        finally
        {
            Gate.Release();
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    public static string ResolveFolder(ScanOptions o)
    {
        var root = string.IsNullOrWhiteSpace(o.Folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans")
            : o.Folder;
        return o.DailySubfolder ? Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd")) : root;
    }

    /// <summary>
    /// The pages have already gone through the feeder, so losing them means rescanning.
    /// If the chosen folder can't be written (network share gone, drive removed, no
    /// permission), save to Documents\Scans — or the Desktop — instead of failing.
    /// </summary>
    private static ScanOutcome AssembleWithFallback(List<string> files, int dpi, ScanOptions o, CancellationToken ct)
    {
        var wanted = ResolveFolder(o);
        var candidates = new[]
        {
            wanted,
            ResolveFolder(new ScanOptions { Folder = "", DailySubfolder = o.DailySubfolder }),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        }.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Exception? first = null;
        foreach (var folder in candidates)
        {
            try
            {
                // A pass-through PDF moved by a failed attempt is no longer in the temp folder.
                var outcome = Assemble(files.Where(File.Exists).ToList(), dpi, o, folder, ct);
                return first is null ? outcome : outcome with
                {
                    FolderNote = $"Couldn't save to {wanted} ({first.Message}), so it was saved to {folder} instead.",
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                first ??= e;
            }
        }
        throw first!;
    }

    private static ScanOutcome Assemble(List<string> files, int dpi, ScanOptions o, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var prefix = SafePrefix(o.Prefix);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var saved = new List<string>();
        var pages = new List<PdfImage>();
        int skipped = 0;
        var popts = new PageOptions(o.PageSize, o.Color == "bw", o.SkipBlank, dpi);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // Printer ignored the JPEG request and sent a finished PDF — keep it as-is.
                var dest = UniquePath(folder, $"{prefix}_{stamp}", ".pdf");
                File.Move(f, dest);
                saved.Add(dest);
                continue;
            }
            var img = PageProcessor.Process(f, popts);
            if (img is null) skipped++;
            else pages.Add(img);
        }

        if (pages.Count == 0 && saved.Count == 0)
            throw new InvalidOperationException(skipped > 0
                ? "Every page looked blank, so nothing was saved. (Turn off \"Skip blank pages\" if that's wrong.)"
                : "No usable pages were scanned.");

        var groups = (o.Split switch
        {
            "page" => pages.Chunk(1),
            "fixed" => pages.Chunk(Num.Clamp(o.PagesPerFile, 1, 999)),
            _ => new[] { pages.ToArray() },
        }).Where(g => g.Length > 0).ToList();

        for (int i = 0; i < groups.Count; i++)
        {
            var name = groups.Count > 1 ? $"{prefix}_{stamp}_{i + 1:D2}" : $"{prefix}_{stamp}";
            var path = UniquePath(folder, name, ".pdf");
            PdfWriter.Write(path, groups[i]);
            saved.Add(path);
        }

        return new ScanOutcome(saved, pages.Count, skipped, null);
    }

    public static string SafePrefix(string? prefix)
    {
        var p = (prefix ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) p = p.Replace(c, '-');
        return p.Length == 0 ? "Scan" : p;
    }

    private static string UniquePath(string folder, string name, string ext)
    {
        var path = Path.Combine(folder, name + ext);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}){ext}");
        return path;
    }
}
