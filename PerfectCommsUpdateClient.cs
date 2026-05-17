using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace VoiceChatPlugin;

internal sealed class PerfectCommsUpdateInfo
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("test")] public bool Test { get; set; }
    [JsonPropertyName("latestVersion")] public string LatestVersion { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "Perfect Comms update available";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("releaseUrl")] public string ReleaseUrl { get; set; } = "";
    [JsonPropertyName("showEveryMainMenu")] public bool ShowEveryMainMenu { get; set; }
}

internal sealed class GitHubReleaseInfo
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
}

internal static class PerfectCommsUpdateClient
{
    private const string GitHubLatestReleaseUrl = "https://api.github.com/repos/artriy/Perfect-Comms/releases/latest";
    private const string LegacyCloudflareUpdateUrl = "https://perfect-comms-lobbies.edgetel.workers.dev/updates/latest";
    private const string GitHubReleasesUrl = "https://github.com/artriy/Perfect-Comms/releases/latest";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<PerfectCommsUpdateInfo?> GetLatestAsync(string configuredUrl)
    {
        var url = BuildUrl(configuredUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"PerfectComms/{VoiceChatPluginMain.Version}");

        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseUpdateInfo(text);
    }

    internal static bool IsNewerThanCurrent(string latestVersion)
        => CompareVersions(latestVersion, VoiceChatPluginMain.Version) > 0;

    private static string BuildUrl(string configuredUrl)
    {
        var url = string.IsNullOrWhiteSpace(configuredUrl) ? GitHubLatestReleaseUrl : configuredUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return GitHubLatestReleaseUrl;

        if (url.StartsWith(LegacyCloudflareUpdateUrl, StringComparison.OrdinalIgnoreCase))
            return GitHubLatestReleaseUrl;

        if (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?') ? '&' : '?';
        return url + separator + "current=" + Uri.EscapeDataString(VoiceChatPluginMain.Version);
    }

    private static PerfectCommsUpdateInfo? ParseUpdateInfo(string text)
    {
        var github = JsonSerializer.Deserialize<GitHubReleaseInfo>(text, JsonOptions);
        if (!string.IsNullOrWhiteSpace(github?.TagName))
        {
            return new PerfectCommsUpdateInfo
            {
                Enabled = true,
                LatestVersion = github.TagName,
                Title = "Perfect Comms update available",
                Message = "Click here to download the latest Perfect Comms release.",
                ReleaseUrl = string.IsNullOrWhiteSpace(github.HtmlUrl) ? GitHubReleasesUrl : github.HtmlUrl,
                ShowEveryMainMenu = false,
            };
        }

        return JsonSerializer.Deserialize<PerfectCommsUpdateInfo>(text, JsonOptions);
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = SplitVersion(left);
        var rightParts = SplitVersion(right);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var a = i < leftParts.Length ? leftParts[i] : 0;
            var b = i < rightParts.Length ? rightParts[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    private static int[] SplitVersion(string value)
    {
        value = (value ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        var dash = value.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) value = value[..dash];
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[Math.Min(parts.Length, 4)];
        for (var i = 0; i < numbers.Length; i++)
            numbers[i] = int.TryParse(parts[i], out var parsed) ? parsed : 0;
        return numbers;
    }
}
