using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace BatteryLife.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer version of the app.
/// </summary>
public class UpdateService
{
    // The GitHub repository where releases are published.
    private const string ApiUrl =
        "https://api.github.com/repos/asanaliov/windows-battery-life/releases/latest";

    private static readonly HttpClient Http = new();

    static UpdateService()
    {
        // GitHub API requires a User-Agent header.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryLife-UpdateChecker/1.0");
    }

    /// <summary>
    /// Returns the latest release info if it is newer than the running assembly version,
    /// or null if the app is already up to date or the check fails.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        GitHubRelease? release;
        try
        {
            release = await Http.GetFromJsonAsync<GitHubRelease>(ApiUrl);
        }
        catch
        {
            // Network is unavailable or quota exceeded - silently ignore.
            return null;
        }

        if (release is null) return null;

        // Tag names are expected to follow "v1.2.3" convention.
        var tagName = release.TagName?.TrimStart('v');
        if (!Version.TryParse(tagName, out var latestVersion)) return null;

        var runningVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        if (latestVersion <= runningVersion) return null;

        return new UpdateInfo(
            $"v{latestVersion}",
            release.HtmlUrl ?? ApiUrl);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }
}

public sealed record UpdateInfo(string Version, string Url);
