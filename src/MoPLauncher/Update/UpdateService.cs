using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MoPLauncher.Update;

public sealed record ReleaseInfo(
    Version Version,
    string Notes,
    string PageUrl,
    string? DownloadUrl,
    string? Sha256,
    string? ChecksumUrl);

// Keeps the launcher current from its GitHub releases. A release is a single
// MoPLauncher.exe; updating downloads it next to the running exe, checks its
// SHA-256, swaps the two and restarts. Nothing needs reinstalling.
public static class UpdateService
{
    public const string Repository = "Willzman/MoP-5.4.8-Launcher-";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    private const string AssetName = "MoPLauncher.exe";
    private const string ChecksumAssetName = AssetName + ".sha256";

    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    // Declared after CurrentVersion: the user agent below needs it.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MoPLauncher", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // The newest published release, or null when the repository has none yet.
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        using var response = await Http.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            return null;

        string? downloadUrl = null, sha256 = null, checksumUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();

                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url;
                    if (asset.TryGetProperty("digest", out var digest) &&
                        digest.ValueKind == JsonValueKind.String &&
                        digest.GetString() is { } value &&
                        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        sha256 = value["sha256:".Length..];
                    }
                }
                else if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        return new ReleaseInfo(
            Normalize(version),
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? RepositoryUrl : RepositoryUrl,
            downloadUrl, sha256, checksumUrl);
    }

    // Downloads the release, verifies it, puts it in place of the running
    // exe and starts it. The caller should shut the app down when this returns.
    public static async Task DownloadAndApplyAsync(ReleaseInfo release, IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        if (release.DownloadUrl is null)
            throw new InvalidOperationException("This release has no launcher download attached.");

        var expectedHash = release.Sha256;
        if (expectedHash is null && release.ChecksumUrl is not null)
        {
            var text = await Http.GetStringAsync(release.ChecksumUrl, cancellationToken);
            expectedHash = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(expectedHash))
            throw new InvalidOperationException("This release has no checksum, so the download can't be verified.");

        var currentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Couldn't work out where the launcher is running from.");
        var newPath = currentPath + ".new";
        var oldPath = currentPath + ".old";

        try
        {
            await DownloadAsync(release.DownloadUrl, newPath, progress, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The launcher can't write to its own folder. Move it somewhere like your Desktop or Documents and try again.");
        }

        string actualHash;
        await using (var stream = File.OpenRead(newPath))
            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));

        if (!string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(newPath);
            throw new InvalidOperationException("The downloaded update didn't match its checksum, so it was discarded.");
        }

        try
        {
            // Windows lets a running exe be renamed, just not overwritten.
            File.Delete(oldPath);
            File.Move(currentPath, oldPath);
            try
            {
                File.Move(newPath, currentPath);
            }
            catch
            {
                File.Move(oldPath, currentPath);
                throw;
            }

            Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't swap while running: let a tiny script do it right
            // after this process exits.
            var script = $"/c timeout /t 2 /nobreak >nul & move /y \"{newPath}\" \"{currentPath}\" >nul & start \"\" \"{currentPath}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", script)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
    }

    // Removes what the previous update left behind.
    public static void CleanUpAfterUpdate()
    {
        var currentPath = Environment.ProcessPath;
        if (currentPath is null)
            return;

        foreach (var leftover in new[] { currentPath + ".old", currentPath + ".new" })
        {
            try
            {
                File.Delete(leftover);
            }
            catch (Exception)
            {
                // Still locked by the instance that's shutting down; the
                // next start gets it.
            }
        }
    }

    private static async Task DownloadAsync(string url, string destination, IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);

        var buffer = new byte[1 << 16];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (total > 0)
                progress.Report((double)received / total);
        }
    }
}
