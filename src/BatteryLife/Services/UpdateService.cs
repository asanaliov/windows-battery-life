using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.IO;

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
    private const long MaximumUpdateSize = 250 * 1024 * 1024;
    private const string Sha256DigestPrefix = "sha256:";

    /// <summary>Passed to the app when the updater had to restart the old version.</summary>
    public const string UpdateFailedArgument = "--update-failed";

    static UpdateService()
    {
        // GitHub API requires a User-Agent header.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryLife-UpdateChecker/1.0");
        Http.Timeout = TimeSpan.FromMinutes(10);
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

        // Tag names and asset names are expected to follow "v1.2.3" convention.
        var tagName = release.TagName?.TrimStart('v');
        if (!Version.TryParse(tagName, out var latestVersion)) return null;

        var runningVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        if (latestVersion <= runningVersion) return null;

        var expectedAssetName = $"BatteryLife-v{latestVersion}-win-x64.exe";
        var asset = release.Assets?
            .FirstOrDefault(asset => string.Equals(asset.Name, expectedAssetName, StringComparison.Ordinal));
        if (!Uri.TryCreate(asset?.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            downloadUri.Host != "github.com" ||
            !downloadUri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))
        {
            return null;
        }

        // Without a published checksum the download couldn't be verified, so don't offer it.
        var digest = asset?.Digest;
        if (digest is null || !digest.StartsWith(Sha256DigestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new UpdateInfo(
            $"v{latestVersion}",
            downloadUri,
            digest[Sha256DigestPrefix.Length..]);
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumUpdateSize)
        {
            throw new InvalidOperationException("The update download is larger than expected.");
        }

        var destination = Path.Combine(Path.GetTempPath(), $"BatteryLife-update-{Guid.NewGuid():N}.exe");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > MaximumUpdateSize)
                {
                    throw new InvalidOperationException("The update download is larger than expected.");
                }

                hash.AppendData(buffer, 0, bytesRead);
                await output.WriteAsync(buffer.AsMemory(0, bytesRead));
                if (response.Content.Headers.ContentLength is long contentLength and > 0)
                {
                    progress?.Report((double)totalRead / contentLength);
                }
            }

            if (totalRead == 0)
            {
                throw new InvalidOperationException("The update download was empty.");
            }

            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actualSha256, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The update download failed its checksum check.");
            }

            return destination;
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    public void LaunchUpdater(string downloadedUpdate)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !string.Equals(Path.GetFileName(executablePath), "BatteryLife.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Updates can only be installed from the published BatteryLife.exe.");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"BatteryLife-updater-{Guid.NewGuid():N}.ps1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $source = '{{EscapePowerShellString(downloadedUpdate)}}'
            $destination = '{{EscapePowerShellString(executablePath)}}'
            $logPath = Join-Path $env:LOCALAPPDATA 'BatteryLife-update.log'
            try {
                $parent = Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
                if ($null -ne $parent) { $parent.WaitForExit() }
                for ($attempt = 0; $attempt -lt 20; $attempt++) {
                    try {
                        Copy-Item -LiteralPath $source -Destination $destination -Force
                        break
                    } catch {
                        if ($attempt -eq 19) { throw }
                        Start-Sleep -Milliseconds 500
                    }
                }
                Start-Process -FilePath $destination
            } catch {
                try { $_ | Out-File -LiteralPath $logPath -Append } catch { }
                # Bring the existing version back so the app doesn't just disappear.
                try { Start-Process -FilePath $destination -ArgumentList '{{UpdateFailedArgument}}' } catch { }
            } finally {
                Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            }
            """;

        // Windows PowerShell 5.1 reads scripts without a BOM as ANSI, which would corrupt any
        // non-ASCII characters in the paths above.
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("Could not start the update installer.");
            }
        }
        catch
        {
            File.Delete(scriptPath);
            throw;
        }
    }

    private static string EscapePowerShellString(string value) => value.Replace("'", "''");

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}

public sealed record UpdateInfo(string Version, Uri DownloadUrl, string Sha256);
