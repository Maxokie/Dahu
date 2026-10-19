using System.Net.Http;
using System.Text.Json;
using Dahu.Core;

namespace Dahu.Services;

public sealed record UpdateInfo(string Tag, Version Version, string Url);

public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/maxokie/dahu/releases/latest";
    private const string ReleasesUrl = "https://github.com/maxokie/dahu/releases";

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dahu/" + current.ToString(3));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(ApiUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Info($"Vérification de mise à jour : HTTP {(int)resp.StatusCode}"); return null; }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (!TryParseVersion(tag, out var latest)) { Log.Info($"Vérification de mise à jour : étiquette illisible '{tag}'"); return null; }

            if (Normalize(latest) <= Normalize(current)) return null;
            if (url == null || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesUrl;
            Log.Info($"Mise à jour disponible : {tag}");
            return new UpdateInfo(tag, latest, url);
        }
        catch (Exception ex)
        {
            Log.Info("Vérification de mise à jour impossible : " + ex.Message);
            return null;
        }
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var end = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (end >= 0) s = s[..end];
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out version!);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
