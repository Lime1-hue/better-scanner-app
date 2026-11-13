using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterScanner.Escl;

/// <summary>
/// Finds eSCL scanners on the LAN with an mDNS query for "_uscan._tcp.local"
/// (ported from an internal document system). Replies carry the printer's live
/// IP, so the list stays right even when DHCP hands out a new address.
/// </summary>
public static class Discovery
{
    private const string Service = "_uscan._tcp.local";

    private sealed class Instance
    {
        public string? Ip, Target, Name;
        public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Rec(string Name, int Type, object Value);

    public static async Task<List<ScannerInfo>> DiscoverAsync(TimeSpan listen, CancellationToken ct = default)
    {
        var query = BuildQuery(Service);
        var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);

        // One socket per network adapter so PCs with several NICs (Wi-Fi + LAN,
        // VPN) still send the query out on the office network.
        var clients = new List<UdpClient>();
        foreach (var addr in LocalIPv4Addresses())
        {
            try
            {
                var u = new UdpClient(new IPEndPoint(addr, 0));
                u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                clients.Add(u);
            }
            catch
            {
                // adapter not usable — skip it
            }
        }
        if (clients.Count == 0) clients.Add(new UdpClient(new IPEndPoint(IPAddress.Any, 0)));

        var replies = new List<(byte[] Data, IPAddress From)>();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                foreach (var u in clients)
                {
                    try { await u.SendAsync(query, query.Length, group).ConfigureAwait(false); } catch { }
                }
                await Task.Delay(120, ct).ConfigureAwait(false);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(listen);
            var tasks = clients.Select(u => Task.Run(async () =>
            {
                var got = new List<(byte[], IPAddress)>();
                try
                {
                    while (true)
                    {
                        var r = await u.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        got.Add((r.Buffer, r.RemoteEndPoint.Address));
                    }
                }
                catch
                {
                    // listen window over
                }
                return got;
            })).ToArray();

            foreach (var list in await Task.WhenAll(tasks).ConfigureAwait(false))
                replies.AddRange(list);
        }
        finally
        {
            foreach (var u in clients) u.Dispose();
        }

        var instances = new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase);
        var aRecords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var suffix = "." + Service;

        foreach (var (data, from) in replies)
        {
            var ip = from.MapToIPv4().ToString();
            foreach (var rec in Parse(data))
            {
                switch (rec.Type)
                {
                    case 12 when rec.Name.Equals(Service, StringComparison.OrdinalIgnoreCase):
                    {
                        var inst = ((string)rec.Value).ToLowerInvariant();
                        var e = Get(instances, inst);
                        e.Ip ??= ip;
                        e.Name ??= inst.EndsWith(suffix) ? inst.Substring(0, inst.Length - suffix.Length) : inst;
                        break;
                    }
                    case 33 when rec.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase):
                    {
                        var e = Get(instances, rec.Name);
                        e.Ip ??= ip;
                        var srv = ((string Target, int Port))rec.Value;
                        e.Target = srv.Target.ToLowerInvariant();
                        break;
                    }
                    case 16 when rec.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase):
                    {
                        var e = Get(instances, rec.Name);
                        e.Ip ??= ip;
                        foreach (var kv in (Dictionary<string, string>)rec.Value) e.Txt[kv.Key] = kv.Value;
                        break;
                    }
                    case 1:
                        aRecords[rec.Name] = (string)rec.Value;
                        break;
                }
            }
        }

        var result = new List<ScannerInfo>();
        foreach (var (key, info) in instances)
        {
            var ip = info.Ip ?? "";
            if (info.Target is not null && aRecords.TryGetValue(info.Target, out var a)) ip = a;
            if (!IPAddress.TryParse(ip, out _)) continue;

            var display = info.Txt.TryGetValue("ty", out var ty) ? ty : info.Name ?? key;
            display = Regex.Replace(display, @"\s+", " ").Trim();
            display = Regex.Replace(display, @"^EPSON\s+", "", RegexOptions.IgnoreCase);
            display = Regex.Replace(display, @"\s+Series$", "", RegexOptions.IgnoreCase);
            if (display.Length == 0) continue;

            var host = Regex.Replace(info.Target ?? "", @"\.?local\.?$", "");
            var id = Regex.Replace(host.Length > 0 ? host : display, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();

            if (result.Any(s => s.Ip == ip)) continue;
            result.Add(new ScannerInfo(id, display, ip));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Instance Get(Dictionary<string, Instance> d, string key)
    {
        if (!d.TryGetValue(key, out var e)) d[key] = e = new Instance();
        return e;
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    yield return ua.Address;
            }
        }
    }

    private static byte[] BuildQuery(string name)
    {
        var ms = new MemoryStream();
        ms.WriteAll(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id 0, flags 0, 1 question
        foreach (var label in name.Trim('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.WriteAll(b);
        }
        ms.WriteByte(0);
        ms.WriteAll(new byte[] { 0, 12, 0, 1 }); // PTR, IN
        return ms.ToArray();
    }

    private static string ReadName(byte[] d, ref int off)
    {
        var labels = new List<string>();
        bool jumped = false;
        int resume = 0, safety = 0;
        while (off < d.Length && safety++ < 128)
        {
            int b = d[off];
            if (b == 0) { off++; break; }
            if ((b & 0xC0) == 0xC0)
            {
                if (off + 1 >= d.Length) break;
                int ptr = ((b & 0x3F) << 8) | d[off + 1];
                if (!jumped) { resume = off + 2; jumped = true; }
                off = ptr;
                continue;
            }
            off++;
            if (off + b > d.Length) break;
            labels.Add(Encoding.UTF8.GetString(d, off, b));
            off += b;
        }
        if (jumped) off = resume;
        return string.Join(".", labels);
    }

    private static List<Rec> Parse(byte[] d)
    {
        var list = new List<Rec>();
        if (d.Length < 12) return list;
        int qd = (d[4] << 8) | d[5];
        int total = ((d[6] << 8) | d[7]) + ((d[8] << 8) | d[9]) + ((d[10] << 8) | d[11]);
        int off = 12;
        for (int i = 0; i < qd && off < d.Length; i++)
        {
            ReadName(d, ref off);
            off += 4;
        }

        for (int i = 0; i < total && off + 10 <= d.Length; i++)
        {
            var name = ReadName(d, ref off).ToLowerInvariant();
            if (off + 10 > d.Length) break;
            int type = (d[off] << 8) | d[off + 1];
            off += 8; // type, class, ttl
            int rdlen = (d[off] << 8) | d[off + 1];
            off += 2;
            int start = off;
            if (off + rdlen > d.Length) break;

            object? value = null;
            switch (type)
            {
                case 1 when rdlen == 4:
                    value = $"{d[off]}.{d[off + 1]}.{d[off + 2]}.{d[off + 3]}";
                    break;
                case 12:
                {
                    int p = off;
                    value = ReadName(d, ref p);
                    break;
                }
                case 16:
                {
                    var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int p = off;
                    while (p < start + rdlen)
                    {
                        int l = d[p++];
                        if (l > 0 && p + l <= start + rdlen)
                        {
                            var s = Encoding.UTF8.GetString(d, p, l);
                            int eq = s.IndexOf('=');
                            if (eq > 0) kv[s.Substring(0, eq)] = s.Substring(eq + 1);
                        }
                        p += l;
                    }
                    value = kv;
                    break;
                }
                case 33 when rdlen >= 6:
                {
                    int port = (d[off + 4] << 8) | d[off + 5];
                    int p = off + 6;
                    value = (Target: ReadName(d, ref p), Port: port);
                    break;
                }
            }

            off = start + rdlen;
            if (value is not null) list.Add(new Rec(name, type, value));
        }
        return list;
    }
}
