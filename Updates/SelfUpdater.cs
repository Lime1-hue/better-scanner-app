using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
#if NETFRAMEWORK
using System.IO.Compression;
#endif

namespace BetterScanner.Updates;

/// <summary>
/// One-click update: download the new build next to the running exe, then swap it in.
///
/// Windows won't let a running exe be overwritten or deleted, but it can be renamed.
/// So: BetterScanner.exe → BetterScanner.exe.old, BetterScanner.exe.new → BetterScanner.exe,
/// restart, and the new copy deletes the .old file once the old process is gone.
/// Same file name and folder, so shortcuts, startup entries and firewall rules keep working.
/// </summary>
public static class SelfUpdater
{
    /// <summary>The release file this build updates from.</summary>
    public const string AssetName =
#if NETFRAMEWORK
        "BetterScanner-Windows7.zip";
#else
        "BetterScanner.exe";
#endif

    private static string ExePath => Application.ExecutablePath;
    private static string NewExe => ExePath + ".new";
    private static string OldExe => ExePath + ".old";
#if NETFRAMEWORK
    private static string ConfigPath => ExePath + ".config";
    private static string NewConfig => ConfigPath + ".new";
#endif

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        UpdateChecker.EnableTls12();
        var h = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan }; // limited by the caller's token
        h.DefaultRequestHeaders.UserAgent.ParseAdd("BetterScanner-Update");
        return h;
    }

    /// <summary>
    /// False when the app sits somewhere it can't write (e.g. Program Files without admin):
    /// then the user has to download and replace the file by hand.
    /// </summary>
    public static bool CanUpdateInPlace()
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(ExePath)!, ".bs-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads and checks the new build, leaving it ready as BetterScanner.exe.new.</summary>
    public static async Task DownloadAsync(UpdateInfo u, IProgress<int> percent, CancellationToken ct)
    {
        if (u.AssetUrl is null) throw new InvalidOperationException($"The release has no {AssetName}.");
        var download = ExePath + ".download";
        TryDelete(download);
        try
        {
            using (var resp = await Http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                using var sha = SHA256.Create();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(download))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n, last = -1;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (total > 0 && (int)(done * 100 / total.Value) is var p && p != last) percent.Report(last = p);
                    }
                }
                sha.TransformFinalBlock([], 0, 0);
                var hex = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
                if (u.AssetSha256 is { } want && hex != want)
                    throw new InvalidDataException("The download was damaged (checksum mismatch). Please try again.");
            }

#if NETFRAMEWORK
            TryDelete(NewExe);
            TryDelete(NewConfig);
            using (var zip = ZipFile.OpenRead(download))
            {
                var exe = zip.Entries.FirstOrDefault(e => e.Name.Equals("BetterScanner.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The Windows 7 zip doesn't contain BetterScanner.exe.");
                exe.ExtractToFile(NewExe, true);
                zip.Entries.FirstOrDefault(e => e.Name.Equals("BetterScanner.exe.config", StringComparison.OrdinalIgnoreCase))
                    ?.ExtractToFile(NewConfig, true);
            }
#else
            TryDelete(NewExe);
            File.Move(download, NewExe);
#endif
        }
        finally
        {
            TryDelete(download);
        }

        // Make sure it really is the version we expect, not an error page or an older build.
        var fv = FileVersionInfo.GetVersionInfo(NewExe);
        if (!Version.TryParse(fv.FileVersion, out var got) || got < new Version(u.Version.Major, u.Version.Minor, Math.Max(0, u.Version.Build)))
        {
            TryDelete(NewExe);
            throw new InvalidDataException($"The downloaded file isn't version {u.Display} (it says {fv.FileVersion ?? "nothing"}).");
        }
    }

    /// <summary>Puts the downloaded build in place of the running one. Rolls back if anything fails.</summary>
    private static void Swap()
    {
        TryDelete(OldExe);
        File.Move(ExePath, OldExe);
        try
        {
            File.Move(NewExe, ExePath);
        }
        catch
        {
            File.Move(OldExe, ExePath);
            throw;
        }
#if NETFRAMEWORK
        if (File.Exists(NewConfig))
        {
            try
            {
                File.Copy(NewConfig, ConfigPath, overwrite: true); // .config isn't locked while running
                File.Delete(NewConfig);
            }
            catch
            {
                // the existing .config still works with the new exe
            }
        }
#endif
    }

    /// <summary>
    /// Swaps in the new exe and starts it. Call last thing, after this copy has released the
    /// single-instance lock. The Windows 10/11 exe is a compressed single-file bundle that loads
    /// parts of itself from BetterScanner.exe on demand, so everything needed here is loaded
    /// before the swap, and nothing else should run in this process afterwards.
    /// </summary>
    /// <returns>False if the swap failed (the old version stays and nothing is started).</returns>
    public static bool SwapAndLaunch(bool resumeHandsFree, out Exception? error)
    {
        error = null;
        var psi = new ProcessStartInfo(ExePath, "--updated" + (resumeHandsFree ? " --resume" : ""))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        _ = psi.Environment.Count;              // loads what Process.Start uses to build the child's environment
        using (Process.GetCurrentProcess()) { } // loads System.Diagnostics.Process
        try
        {
            Swap();
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        return true;
    }

    /// <summary>Removes the previous version's .old file once that process has fully exited.</summary>
    public static void CleanUpInBackground()
    {
        if (!File.Exists(OldExe)) return;
        var t = new Thread(() =>
        {
            for (int i = 0; i < 60 && File.Exists(OldExe); i++)
            {
                if (TryDelete(OldExe)) return;
                Thread.Sleep(1000);
            }
        }) { IsBackground = true };
        t.Start();
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
