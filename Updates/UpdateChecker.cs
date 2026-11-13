using System.Net.Http;
using System.Text.RegularExpressions;

namespace BetterScanner.Updates;

/// <summary>A newer published release.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Url)
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
#if NETFRAMEWORK
        // GitHub requires TLS 1.2, which Windows 7 doesn't enable by default.
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("BetterScanner-UpdateCheck");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
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
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            var url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+/releases/tag/[^\"]+)\"").Groups[1].Value;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (Normalize(latest) <= Normalize(current)) return null;
            return new UpdateInfo(latest, tag, url.Length > 0 ? url : LatestPage);
        }
        catch
        {
            return null;
        }
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
