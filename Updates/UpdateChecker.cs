using System.Net.Http;

namespace BetterScanner.Updates;

/// <summary>A newer published release.</summary>
/// <param name="AssetUrl">Direct download of the file for this build (exe or Windows 7 zip); null if the release lacks it.</param>
/// <param name="AssetSha256">GitHub's SHA-256 of that file (lowercase hex), when GitHub provides one.</param>
public sealed record UpdateInfo(Version Version, string Tag, string Url, string? AssetUrl, string? AssetSha256)
{
    public string Display => UpdateChecker.Short(Version);
}

/// <summary>
/// Asks GitHub for the newest public release of this app. Sends nothing about
/// the PC or user — just an anonymous request for public release info. Any
/// failure (offline, GitHub down, rate limit) quietly means "no update".
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "Lime1-hue/better-scanner-app";
    public const string LatestPage = "https://github.com/" + Repo + "/releases/latest";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        EnableTls12();
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("BetterScanner-UpdateCheck");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>GitHub requires TLS 1.2, which Windows 7 doesn't enable by default.</summary>
    internal static void EnableTls12()
    {
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
    }

    /// <summary>This app's version (from &lt;Version&gt; in the .csproj).</summary>
    public static Version Current
    {
        get
        {
            // For testing the update bar: pretend to be an older version.
            var pretend = Environment.GetEnvironmentVariable("BETTERSCANNER_PRETEND_VERSION");
            if (!string.IsNullOrEmpty(pretend) && Version.TryParse(pretend, out var p)) return p;
            return typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);
        }
    }

    public static string Short(Version v) => v.Build >= 0 ? v.ToString(3) : v.ToString(2);

    public static Task<UpdateInfo?> CheckAsync() => CheckAsync(Current);

    public static async Task<UpdateInfo?> CheckAsync(Version current)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);
            var r = Json.Deserialize<GhRelease>(json);
            var tag = r?.tag_name ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (Normalize(latest) <= Normalize(current)) return null;

            var asset = r!.assets?.FirstOrDefault(a => string.Equals(a.name, SelfUpdater.AssetName, StringComparison.OrdinalIgnoreCase));
            string? sha = asset?.digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? d.Substring(7).ToLowerInvariant()
                : null;
            var page = r.html_url is { Length: > 0 } u && u.Contains("/releases/tag/") ? u : LatestPage;
            return new UpdateInfo(latest, tag, page, asset?.browser_download_url, sha);
        }
        catch
        {
            return null;
        }
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    // Shapes of GitHub's JSON. Property names match the JSON exactly (both JSON libraries map by name).
#pragma warning disable IDE1006
    private sealed class GhRelease
    {
        public string? tag_name { get; set; }
        public string? html_url { get; set; }
        public List<GhAsset>? assets { get; set; }
    }

    private sealed class GhAsset
    {
        public string? name { get; set; }
        public string? browser_download_url { get; set; }
        public string? digest { get; set; }
    }
#pragma warning restore IDE1006
}
