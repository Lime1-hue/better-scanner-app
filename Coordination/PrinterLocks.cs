using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using BetterScanner.Escl;

namespace BetterScanner.Coordination;

/// <summary>Another copy of Better Scanner that is using a printer for hands-free scanning.</summary>
public sealed record PrinterClaim(
    string InstanceId, string PrinterId, string PrinterIp, string PrinterName,
    string User, string Machine, DateTime SinceUtc, DateTime LastSeenUtc)
{
    /// <summary>"Maria (PC-03)" â€” the PC name helps when everyone logs in as the same Windows user.</summary>
    public string Who => string.Equals(User, Machine, StringComparison.OrdinalIgnoreCase) ? User : $"{User} ({Machine})";

    public bool Matches(ScannerInfo s) => PrinterId == s.Id || PrinterIp == s.Ip;
}

/// <summary>
/// Makes sure only one person at a time runs hands-free scanning on a printer.
///
/// While hands-free is on, the app broadcasts a small "I'm using this printer"
/// message on the office network every 2 seconds. Other copies of the app hear
/// it and refuse to start hands-free on that printer, showing who has it. When
/// the messages stop (Stop pressed, app closed, PC switched off or unplugged)
/// the lock is gone within a few seconds â€” nothing can stay stuck, and no
/// server or shared folder is needed.
///
/// Timing uses "how long have you held it" rather than wall-clock time, so PCs
/// with wrong clocks still agree on who was first.
/// </summary>
public sealed class PrinterLocks : IDisposable
{
    public const int Port = 47653;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(7);

    private sealed class Message
    {
        public string App { get; set; } = "BetterScanner";
        public int V { get; set; } = 1;
        public string Type { get; set; } = "";       // claim | release | ask
        public string From { get; set; } = "";
        public string User { get; set; } = "";
        public string Machine { get; set; } = "";
        public string? PrinterId { get; set; }
        public string? PrinterIp { get; set; }
        public string? PrinterName { get; set; }
        public long HeldMs { get; set; }
        public string? Target { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, PrinterClaim> claims = new();
    private readonly CancellationTokenSource cts = new();
    private UdpClient? udp;
    private System.Threading.Timer? timer;
    private ScannerInfo? myPrinter;
    private long myStartTick;
    private List<IPEndPoint>? targets;
    private DateTime targetsAt;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public string UserName { get; set; } = Environment.UserName;

    /// <summary>False when the network port couldn't be opened â€” locking is then off.</summary>
    public bool Available => udp is not null;

    /// <summary>Raised (on a background thread) whenever someone takes or releases a printer.</summary>
    public event Action? ClaimsChanged;
    /// <summary>Someone asked us to release our printer. Argument: who is asking.</summary>
    public event Action<string>? ReleaseRequested;

    public void Start()
    {
        try
        {
            var u = new UdpClient(AddressFamily.InterNetwork);
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            u.EnableBroadcast = true;
            udp = u;
        }
        catch
        {
            udp = null;
            return;
        }
        _ = Task.Run(ReceiveLoopAsync);
        timer = new System.Threading.Timer(_ => Tick(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>The colleague currently holding this printer, or null when it's free.</summary>
    public PrinterClaim? ClaimFor(ScannerInfo s)
    {
        var cutoff = DateTime.UtcNow - Expiry;
        lock (gate)
        {
            return claims.Values
                .Where(c => c.LastSeenUtc >= cutoff && c.Matches(s))
                .OrderBy(c => c.SinceUtc)
                .ThenBy(c => c.InstanceId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Tries to take the printer. Returns null on success, or the colleague who
    /// already has it. Waits a little over one heartbeat so a copy that started
    /// at the same moment is heard, then the older claim wins.
    /// </summary>
    public async Task<PrinterClaim?> TryClaimAsync(ScannerInfo s, CancellationToken ct = default)
    {
        if (ClaimFor(s) is { } existing) return existing;
        Hold(s);
        if (udp is null) return null; // can't coordinate â€” don't block the user
        SendClaim();
        await Task.Delay(HeartbeatInterval + TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
        if (LostTo() is { } rival)
        {
            Release();
            return rival;
        }
        return null;
    }

    /// <summary>
    /// While holding a printer: the colleague who started on it at the same moment
    /// and was (marginally) first, or null when we keep it.
    /// </summary>
    public PrinterClaim? LostTo()
    {
        ScannerInfo? mine;
        long start;
        lock (gate)
        {
            mine = myPrinter;
            start = myStartTick;
        }
        if (mine is null) return null;
        var rival = ClaimFor(mine);
        if (rival is null) return null;

        var mySince = DateTime.UtcNow - TimeSpan.FromMilliseconds(MonoClock.Ms - start);
        var diff = (rival.SinceUtc - mySince).TotalMilliseconds;
        bool rivalWins = diff < -300 || (Math.Abs(diff) <= 300 && string.CompareOrdinal(rival.InstanceId, InstanceId) < 0);
        return rivalWins ? rival : null;
    }

    /// <summary>The printer got a new IP (DHCP) while we hold it.</summary>
    public void UpdatePrinter(ScannerInfo s)
    {
        lock (gate)
        {
            if (myPrinter is not null) myPrinter = s;
        }
    }

    public void Release()
    {
        ScannerInfo? had;
        lock (gate)
        {
            had = myPrinter;
            myPrinter = null;
        }
        if (had is not null)
            Send(new Message { Type = "release", PrinterId = had.Id, PrinterIp = had.Ip, PrinterName = had.Name });
    }

    public void AskRelease(PrinterClaim c) => Send(new Message { Type = "ask", Target = c.InstanceId });

    private void Hold(ScannerInfo s)
    {
        lock (gate)
        {
            myPrinter = s;
            myStartTick = MonoClock.Ms;
        }
    }

    private void SendClaim()
    {
        ScannerInfo? mine;
        long start;
        lock (gate)
        {
            mine = myPrinter;
            start = myStartTick;
        }
        if (mine is null) return;
        Send(new Message
        {
            Type = "claim", PrinterId = mine.Id, PrinterIp = mine.Ip, PrinterName = mine.Name,
            HeldMs = MonoClock.Ms - start,
        });
    }

    private void Tick()
    {
        bool changed;
        lock (gate)
        {
            var cutoff = DateTime.UtcNow - Expiry;
            var dead = claims.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
            foreach (var k in dead) claims.Remove(k);
            changed = dead.Count > 0;
        }
        SendClaim();
        if (changed) ClaimsChanged?.Invoke();
    }

    private async Task ReceiveLoopAsync()
    {
        var u = udp!;
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await u.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            Message? m;
            try { m = Json.Deserialize<Message>(Encoding.UTF8.GetString(r.Buffer)); }
            catch { continue; }
            if (m is null || m.App != "BetterScanner" || m.From.Length == 0 || m.From == InstanceId) continue;

            try { Handle(m); } catch { /* never let one bad packet stop the listener */ }
        }
    }

    private void Handle(Message m)
    {
        bool changed = false;
        switch (m.Type)
        {
            case "claim" when m.PrinterId is not null && m.PrinterIp is not null:
                lock (gate)
                {
                    var now = DateTime.UtcNow;
                    claims.TryGetValue(m.From, out var old);
                    bool fresh = old is null || old.PrinterIp != m.PrinterIp || old.PrinterId != m.PrinterId || old.User != m.User;
                    var since = fresh ? now - TimeSpan.FromMilliseconds(Math.Max(0, m.HeldMs)) : old!.SinceUtc;
                    claims[m.From] = new PrinterClaim(m.From, m.PrinterId, m.PrinterIp, m.PrinterName ?? m.PrinterIp,
                        m.User.Length > 0 ? m.User : m.Machine, m.Machine, since, now);
                    changed = fresh;
                }
                break;
            case "release":
                lock (gate) changed = claims.Remove(m.From);
                break;
            case "ask" when m.Target == InstanceId:
                var who = string.Equals(m.User, m.Machine, StringComparison.OrdinalIgnoreCase) || m.Machine.Length == 0 ? m.User : $"{m.User} ({m.Machine})";
                ReleaseRequested?.Invoke(who);
                break;
        }
        if (changed) ClaimsChanged?.Invoke();
    }

    private void Send(Message m)
    {
        var u = udp;
        if (u is null) return;
        m.From = InstanceId;
        m.User = UserName;
        m.Machine = Environment.MachineName;
        var data = Encoding.UTF8.GetBytes(Json.Serialize(m));
        foreach (var ep in BroadcastTargets())
        {
            try { u.Send(data, data.Length, ep); } catch { /* adapter gone */ }
        }
    }

    /// <summary>255.255.255.255 plus each adapter's own subnet broadcast (reaches the LAN on multi-NIC PCs).</summary>
    private List<IPEndPoint> BroadcastTargets()
    {
        if (targets is not null && DateTime.UtcNow - targetsAt < TimeSpan.FromSeconds(30)) return targets;
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, Port) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var ep = new IPEndPoint(new IPAddress(bc), Port);
                    if (!list.Contains(ep)) list.Add(ep);
                }
            }
        }
        catch
        {
            // fall back to the global broadcast only
        }
        targets = list;
        targetsAt = DateTime.UtcNow;
        return list;
    }

    public void Dispose()
    {
        Release();
        cts.Cancel();
        timer?.Dispose();
        udp?.Dispose();
    }
}
