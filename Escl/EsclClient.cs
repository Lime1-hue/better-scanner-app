using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterScanner.Escl;

/// <summary>A network scanner that speaks eSCL (AirScan / Mopria).</summary>
public sealed record ScannerInfo(string Id, string Name, string Ip)
{
    /// <summary>For the settings file reader on .NET Framework, which needs a parameterless constructor.</summary>
    public ScannerInfo() : this("", "", "") { }

    public override string ToString() => $"{Name}   ({Ip})";
}

/// <summary>What the printer reports at /eSCL/ScannerStatus.</summary>
public sealed record ScannerStatus(string State, string AdfState)
{
    public bool IsIdle => State.Length == 0 || State.Equals("Idle", StringComparison.OrdinalIgnoreCase);
    public bool HasFeeder => AdfState.Length > 0;
    public bool PaperLoaded => AdfState.Contains("Loaded", StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var state = State.Length == 0 ? "Ready" : State.Equals("Idle", StringComparison.OrdinalIgnoreCase) ? "Ready" : State;
        if (!HasFeeder) return state;
        string feeder = PaperLoaded ? "paper in the feeder"
            : AdfState.Contains("Empty", StringComparison.OrdinalIgnoreCase) ? "feeder empty"
            : AdfState.Replace("ScannerAdf", "feeder: ");
        return $"{state} · {feeder}";
    }
}

/// <summary>One eSCL scan request. Sizes are in 1/300 inch, as eSCL expects.</summary>
public sealed class ScanRequest
{
    public bool Feeder { get; init; } = true;
    public bool Duplex { get; init; }
    public string ColorMode { get; init; } = "RGB24";
    public int Resolution { get; init; } = 300;
    public int Width { get; init; } = 2550;
    public int Height { get; init; } = 3300;
}

/// <summary>Pages received from the printer, plus an error if the job broke part-way (e.g. a jam).</summary>
public sealed record RawScan(List<string> Files, string? Error);

public sealed class EsclException(string message, int httpCode = 0) : Exception(message)
{
    public int HttpCode { get; } = httpCode;
    /// <summary>503 / 409: the printer is busy (someone copying, previous job finishing).</summary>
    public bool PrinterBusy => HttpCode is 503 or 409;
}

/// <summary>
/// Driverless scanning over eSCL (AirScan), the protocol built into the printers.
///   1. POST   /eSCL/ScanJobs        → 201 + Location: …/ScanJobs/&lt;id&gt;
///   2. GET    &lt;job&gt;/NextDocument    → one page per call, 404 when done
///   3. DELETE &lt;job&gt;                 → clean up
/// Epson serves this over HTTPS with a self-signed certificate, so the
/// certificate is not validated (the printer is on the local network).
/// </summary>
public static class EsclClient
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
#if NETFRAMEWORK
        // Windows 7: TLS 1.2 is supported but not on by default, the default limit
        // of 2 connections per printer would stall status checks during a scan,
        // and "Expect: 100-continue" confuses some printers' web servers.
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls11 | System.Net.SecurityProtocolType.Tls;
        System.Net.ServicePointManager.DefaultConnectionLimit = 16;
        System.Net.ServicePointManager.Expect100Continue = false;
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = static (_, _, _, _) => true };
#else
        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
#endif
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static readonly ConcurrentDictionary<string, string> CapsCache = new();

    /// <summary>Longest wait for the next page before giving up on a job.</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(90);

    private static string BaseUrl(string ip) => $"https://{ip}/eSCL";

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EsclException("The printer did not answer in time.");
        }
        catch (HttpRequestException e)
        {
            throw new EsclException("Could not reach the printer (" + e.Message + ").");
        }
    }

    private static string Tag(string xml, string localName)
    {
        var m = Regex.Match(xml, $@"<(?:[\w-]+:)?{localName}>\s*([^<]*?)\s*</(?:[\w-]+:)?{localName}>");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Reads the reply as UTF-8 ourselves. Epson's Content-Type carries a charset
    /// label that .NET Framework rejects ("invalid character set"), so
    /// ReadAsStringAsync fails there; the XML is always UTF-8 anyway.
    /// </summary>
    private static async Task<string> ReadTextAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Printer + feeder state, or null when the printer can't be reached.</summary>
    public static async Task<ScannerStatus?> GetStatusAsync(string ip, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl(ip) + "/ScannerStatus");
            using var res = await SendAsync(req, timeout, ct).ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.OK) return null;
            var xml = await ReadTextAsync(res, ct).ConfigureAwait(false);
            return new ScannerStatus(Tag(xml, "State"), Tag(xml, "AdfState"));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static async Task<string?> GetCapabilitiesAsync(string ip, CancellationToken ct = default)
    {
        if (CapsCache.TryGetValue(ip, out var cached)) return cached;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl(ip) + "/ScannerCapabilities");
            using var res = await SendAsync(req, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.OK) return null;
            var xml = await ReadTextAsync(res, ct).ConfigureAwait(false);
            if (xml.Length > 0) CapsCache[ip] = xml;
            return xml;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Make and model as the printer reports it, e.g. "EPSON WF-C878R Series".</summary>
    public static async Task<string?> GetModelAsync(string ip, CancellationToken ct = default)
    {
        var caps = await GetCapabilitiesAsync(ip, ct).ConfigureAwait(false);
        if (caps is null) return null;
        var model = Tag(caps, "MakeAndModel");
        return model.Length > 0 ? model : null;
    }

    /// <summary>
    /// Largest area (1/300 inch) the device can capture from a source. We scan
    /// all of it and trim in software, so a page is never clipped at the printer.
    /// </summary>
    public static async Task<(int Width, int Height)> GetMaxRegionAsync(string ip, bool feeder, CancellationToken ct = default)
    {
        var caps = await GetCapabilitiesAsync(ip, ct).ConfigureAwait(false);
        if (caps is not null)
        {
            var tag = feeder ? "AdfSimplexInputCaps" : "PlatenInputCaps";
            var m = Regex.Match(caps, $@"<(?:[\w-]+:)?{tag}>(.*?)</(?:[\w-]+:)?{tag}>", RegexOptions.Singleline);
            if (m.Success
                && int.TryParse(Tag(m.Groups[1].Value, "MaxWidth"), out var w)
                && int.TryParse(Tag(m.Groups[1].Value, "MaxHeight"), out var h)
                && w > 0 && h > 0)
            {
                return (w, h);
            }
        }
        return (3510, 5100); // A3 — the WF-C878R's glass
    }

    private static string BuildSettingsXml(ScanRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<scan:ScanSettings xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\"")
          .Append(" xmlns:escl=\"http://schemas.hp.com/imaging/escl/2011/05/03\"")
          .Append(" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\">\n");
        sb.Append("  <pwg:Version>2.6</pwg:Version>\n");
        sb.Append("  <pwg:ScanRegions><pwg:ScanRegion>\n");
        sb.Append("    <pwg:XOffset>0</pwg:XOffset><pwg:YOffset>0</pwg:YOffset>\n");
        sb.Append($"    <pwg:Width>{r.Width}</pwg:Width><pwg:Height>{r.Height}</pwg:Height>\n");
        sb.Append("    <pwg:ContentRegionUnits>escl:ThreeHundredthsOfInches</pwg:ContentRegionUnits>\n");
        sb.Append("  </pwg:ScanRegion></pwg:ScanRegions>\n");
        sb.Append($"  <scan:InputSource>{(r.Feeder ? "Feeder" : "Platen")}</scan:InputSource>\n");
        sb.Append($"  <scan:ColorMode>{r.ColorMode}</scan:ColorMode>\n");
        sb.Append("  <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>\n");
        sb.Append($"  <scan:XResolution>{r.Resolution}</scan:XResolution>\n");
        sb.Append($"  <scan:YResolution>{r.Resolution}</scan:YResolution>\n");
        if (r.Feeder) sb.Append($"  <scan:Duplex>{(r.Duplex ? "true" : "false")}</scan:Duplex>\n");
        sb.Append("</scan:ScanSettings>\n");
        return sb.ToString();
    }

    private static string ExtensionFor(string? mediaType)
    {
        var ct = (mediaType ?? "").ToLowerInvariant();
        if (ct.Contains("pdf")) return "pdf";
        if (ct.Contains("png")) return "png";
        if (ct.Contains("tif")) return "tif";
        return "jpg";
    }

    /// <summary>
    /// Runs one scan job and writes each page into <paramref name="tmpDir"/>.
    /// Throws when nothing was scanned; if the job breaks after some pages
    /// (jam, misfeed) those pages are returned along with the error.
    /// </summary>
    public static async Task<RawScan> ScanAsync(string ip, ScanRequest r, string tmpDir, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(tmpDir);
        var body = Encoding.UTF8.GetBytes(BuildSettingsXml(r));

        // 503 = busy finishing a previous job, 409 = transient firmware state.
        string job;
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl(ip) + "/ScanJobs");
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            using var res = await SendAsync(req, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            int code = (int)res.StatusCode;

            if (code == 201 && res.Headers.Location is { } loc)
            {
                // Some Epsons hand back an http:// Location although eSCL only
                // answers on https, so rebuild the URL on the origin we used.
                var path = loc.IsAbsoluteUri ? loc.AbsolutePath : loc.OriginalString;
                if (!path.StartsWith("/")) path = "/" + path;
                job = $"https://{ip}{path.TrimEnd('/')}";
                break;
            }
            if ((code == 503 || code == 409) && attempt < 4)
            {
                progress?.Report("Printer is busy — retrying…");
                await Task.Delay(2000 * (attempt + 1), ct).ConfigureAwait(false);
                continue;
            }
            throw new EsclException(code == 503
                ? "The printer is busy. Try again in a moment."
                : $"The printer refused the scan (HTTP {code}).", code);
        }

        var files = new List<string>();
        string? error = null;
        try
        {
            var lastPage = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - lastPage > PageTimeout)
                {
                    error = "The printer stopped sending pages.";
                    break;
                }

                progress?.Report(files.Count == 0 ? "Scanning the first page…" : $"Scanning… {files.Count} page{(files.Count == 1 ? "" : "s")} so far");

                using var req = new HttpRequestMessage(HttpMethod.Get, job + "/NextDocument");
                using var res = await SendAsync(req, PageTimeout, ct).ConfigureAwait(false);
                int code = (int)res.StatusCode;

                if (code is 404 or 410) break; // no more pages
                if (code == 503)
                {
                    // Job is running but the next page isn't ready yet.
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    continue;
                }
                if (code != 200)
                {
                    error = $"The scan stopped while receiving a page (HTTP {code}) — maybe a paper jam.";
                    break;
                }

                var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    error = "The printer sent an empty page.";
                    break;
                }

                var ext = ExtensionFor(res.Content.Headers.ContentType?.MediaType);
                var file = Path.Combine(tmpDir, $"page_{files.Count + 1:D4}.{ext}");
                File.WriteAllBytes(file, bytes);
                files.Add(file);
                lastPage = DateTime.UtcNow;

                if (ext == "pdf" || files.Count >= 999) break; // a PDF already holds every page
            }
        }
        catch (EsclException e) when (files.Count > 0)
        {
            error = e.Message;
        }
        finally
        {
            try
            {
                using var del = new HttpRequestMessage(HttpMethod.Delete, job);
                using var _ = await SendAsync(del, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        if (files.Count == 0)
            throw new EsclException(error ?? "No pages came back. Is the paper loaded and the printer ready?");

        return new RawScan(files, error);
    }
}
