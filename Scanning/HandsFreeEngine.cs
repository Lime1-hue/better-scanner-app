using BetterScanner.Escl;

namespace BetterScanner.Scanning;

public enum Phase { Stopped, Waiting, Settling, Scanning, Busy, ClearFeeder, Offline }

/// <summary>
/// Hands-free scanning: the "wait for paper" mode ported from an internal document system
/// (js/scan.js + scan_wait.php). Polls the printer every second; when paper
/// has sat in the feeder for a few seconds and the printer is idle, it scans
/// by itself, saves the PDF, and goes back to waiting for the next stack.
///
///   waiting → settling (paper seen, countdown) → scanning → waiting …
///   plus busy (someone copying), offline, and clear-feeder (after a failed
///   scan the paper must be taken out first, so a stuck sheet isn't re-fed in a loop).
///
/// Events fire on a background thread; the UI marshals them itself.
/// </summary>
public sealed class HandsFreeEngine
{
    public event Action<Phase, string, string>? StatusChanged;
    public event Action<ScanOutcome>? ScanSaved;
    public event Action<string>? ScanFailed;
    public event Action<ScannerInfo>? ScannerMoved;
    /// <summary>reason (null when the user stopped it), isError</summary>
    public event Action<string?, bool>? Stopped;

    private CancellationTokenSource? cts;
    private (Phase, string, string) lastReport;

    public bool IsRunning => Volatile.Read(ref cts) is not null;

    public void Start(ScannerInfo scanner, Func<ScanOptions> options)
    {
        if (IsRunning) return;
        var me = new CancellationTokenSource();
        Volatile.Write(ref cts, me);
        lastReport = default;
        _ = Task.Run(() => RunAsync(scanner, options, me));
    }

    public void Stop() => Interlocked.Exchange(ref cts, null)?.Cancel();

    private void Report(Phase p, string title, string detail)
    {
        var r = (p, title, detail);
        if (r == lastReport) return;
        lastReport = r;
        StatusChanged?.Invoke(p, title, detail);
    }

    private async Task RunAsync(ScannerInfo scanner, Func<ScanOptions> options, CancellationTokenSource me)
    {
        var ct = me.Token;
        string? reason = null;
        bool isError = false;
        bool needEmpty = false;
        DateTime? settleUntil = null;
        int offline = 0;
        var idleSince = DateTime.UtcNow;

        // True when nobody has used the feeder for the configured time: stop, so
        // the printer goes back to normal for everyone (copying, Epson Scan 2…).
        bool IdleTooLong(out string why)
        {
            int limit = options().IdleStopMinutes;
            why = $"Stopped after {limit} minute{(limit == 1 ? "" : "s")} with no paper, so others can use {scanner.Name}.";
            return limit > 0 && DateTime.UtcNow - idleSince > TimeSpan.FromMinutes(limit);
        }

        try
        {
            Report(Phase.Waiting, "Connecting to the printer…", scanner.Name);
            var first = await EsclClient.GetStatusAsync(scanner.Ip, TimeSpan.FromSeconds(5), ct);
            if (first is not null && !first.HasFeeder)
            {
                reason = $"{scanner.Name} doesn't report a document feeder, so it can't scan hands-free. Use \"Scan glass now\" instead.";
                isError = true;
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var st = await EsclClient.GetStatusAsync(scanner.Ip, TimeSpan.FromSeconds(4), ct);

                if (st is null)
                {
                    if (IdleTooLong(out var why)) { reason = why; return; }
                    offline++;
                    settleUntil = null;
                    Report(Phase.Offline, "Can't reach the printer",
                        $"Retrying… check that {scanner.Name} is switched on and connected to the network.");
                    // The printer may have a new address from DHCP — look for it again.
                    if (offline % 10 == 0 && await TryRediscoverAsync(scanner, ct) is { } moved)
                    {
                        scanner = moved;
                        ScannerMoved?.Invoke(moved);
                    }
                    await Task.Delay(1000, ct);
                    continue;
                }
                offline = 0;

                if (!st.PaperLoaded)
                {
                    if (IdleTooLong(out var why)) { reason = why; return; }
                    needEmpty = false;
                    settleUntil = null;
                    Report(Phase.Waiting, "Waiting for paper",
                        $"Put the pages face-up in the feeder of {scanner.Name}. Scanning starts by itself.");
                    await Task.Delay(1000, ct);
                    continue;
                }

                if (needEmpty)
                {
                    // Paper left behind after a failed scan doesn't count as use, so a
                    // forgotten jam can't hold the printer forever.
                    if (IdleTooLong(out var why)) { reason = why; return; }
                    Report(Phase.ClearFeeder, "Take the paper out of the feeder",
                        "The last scan failed. Remove the stack, check it, and put it back to try again.");
                    await Task.Delay(1000, ct);
                    continue;
                }

                idleSince = DateTime.UtcNow; // fresh paper is in the feeder

                if (!st.IsIdle)
                {
                    settleUntil = null;
                    Report(Phase.Busy, "Printer is busy",
                        $"{scanner.Name} is {st.State.ToLowerInvariant()} (maybe someone is copying). Scanning starts when it's free.");
                    await Task.Delay(1000, ct);
                    continue;
                }

                var opts = options();
                settleUntil ??= DateTime.UtcNow.AddSeconds(Num.Clamp(opts.SettleSeconds, 1, 30));
                var left = settleUntil.Value - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                {
                    Report(Phase.Settling, $"Paper detected — scanning in {Math.Ceiling(left.TotalSeconds)}…",
                        "Finish placing the stack in the feeder.");
                    await Task.Delay(250, ct);
                    continue;
                }
                settleUntil = null;

                Report(Phase.Scanning, "Scanning…", $"Feeding pages from {scanner.Name}.");
                var progress = new InlineProgress(msg => Report(Phase.Scanning, "Scanning…", msg));
                try
                {
                    var outcome = await ScanPipeline.RunAsync(scanner, feeder: true, opts, progress, ct);
                    if (outcome.Warning is not null) needEmpty = true; // leftover sheets after a jam
                    ScanSaved?.Invoke(outcome);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (EsclException e) when (e.PrinterBusy)
                {
                    ScanFailed?.Invoke(e.Message); // someone grabbed the printer — just keep waiting
                }
                catch (Exception e)
                {
                    needEmpty = true;
                    ScanFailed?.Invoke(e.Message);
                }
                idleSince = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            // stopped by the user
        }
        catch (Exception e)
        {
            reason = "Hands-free scanning stopped: " + e.Message;
            isError = true;
        }
        finally
        {
            Interlocked.CompareExchange(ref cts, null, me);
            me.Dispose();
            Stopped?.Invoke(reason, isError);
        }
    }

    private static async Task<ScannerInfo?> TryRediscoverAsync(ScannerInfo s, CancellationToken ct)
    {
        try
        {
            var found = await Discovery.DiscoverAsync(TimeSpan.FromSeconds(1.2), ct);
            var match = found.FirstOrDefault(f => f.Id == s.Id);
            return match is not null && match.Ip != s.Ip ? match : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
