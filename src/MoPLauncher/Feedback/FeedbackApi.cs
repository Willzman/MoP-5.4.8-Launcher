using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Reflection;
using MoPLauncher.Config;

namespace MoPLauncher.Feedback;

// What an upload came to.
public enum UploadResult
{
    Sent,        // stored on the server
    Duplicate,   // the server already had it
    NotMatched,  // a screenshot no bug report of the account goes with
    Refused,     // not wanted as it is (too big, not a crash log, ...): don't send again
    Unauthorized,// the upload token isn't known (any more)
    Failed       // network or server trouble: try again another time
}

// A bug report the server has for the account, and the time window its
// screenshot must fall in.
public sealed record ReportList(long ServerTime, int WindowBefore, int WindowAfter, IReadOnlyList<(int Id, long CreatedAt)> Reports);

// The realm's feedback upload API (tools/feedback/feedback_api.py in the
// server repository). HTTPS with the server's own self-signed certificate,
// which is pinned by its SHA-256 instead of checked against Windows' trusted
// roots, so no other server can pose as it. Every request carries the
// account's upload token, which the server gave the Bug Report add-on.
public sealed class FeedbackApi : IDisposable
{
    public const int DefaultPort = 8443;

    private readonly HttpClient _http;

    // Why the last request failed (for feedback.log), or null.
    public string? LastError { get; private set; }

    // The innermost message: "The SSL connection could not be established"
    // alone doesn't say that the certificate didn't match.
    private static string Describe(Exception ex) => ex.GetBaseException() is var inner && inner != ex
        ? $"{ex.GetType().Name}: {ex.Message} ({inner.Message})"
        : $"{ex.GetType().Name}: {ex.Message}";

    private FeedbackApi(Uri baseAddress, string certificateSha256)
    {
        var pin = certificateSha256.Replace(":", string.Empty).Trim();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                // Only the pinned certificate; its name and issuer don't matter.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null &&
                    string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase)
            }
        };

        // Long enough for a crash dump on a slow line.
        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MoPLauncher", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0"));
    }

    // The API for this config: FeedbackUrl when set in config.json, else the
    // login server's host on port 8443. Null when there's no address or no
    // certificate to pin (uploads are never sent to an unpinned server).
    public static FeedbackApi? For(LauncherConfig config)
    {
        var pin = string.IsNullOrWhiteSpace(config.FeedbackCertificate) ? ServerDefaults.FeedbackCertificate : config.FeedbackCertificate;
        if (string.IsNullOrWhiteSpace(pin))
            return null;

        Uri? address;
        if (!string.IsNullOrWhiteSpace(config.FeedbackUrl))
        {
            if (!Uri.TryCreate(config.FeedbackUrl.Trim(), UriKind.Absolute, out address) || address.Scheme != Uri.UriSchemeHttps)
                return null;
        }
        else if (ServerEndpoint.TryParse(config.ServerAddress, out var host, out _))
            address = new UriBuilder(Uri.UriSchemeHttps, host, DefaultPort).Uri;
        else
            return null;

        return new FeedbackApi(address, pin);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    // The account's bug reports since `since` (server time, at most a week
    // back). Null when the server can't be reached or doesn't know the token.
    public async Task<ReportList?> GetReportsAsync(string token, long since, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = Request(HttpMethod.Get, $"/v1/reports?since={since}", token);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}";
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            var reports = root.GetProperty("reports").EnumerateArray()
                .Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("created_at").GetInt64()))
                .ToList();
            return new ReportList(root.GetProperty("server_time").GetInt64(),
                root.GetProperty("window_before").GetInt32(), root.GetProperty("window_after").GetInt32(), reports);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            LastError = Describe(ex);
            return null;
        }
    }

    // Sends one file as it is to /v1/<route>, with its name and its time on
    // this PC; the PC's clock goes along so the server can correct for it.
    public async Task<UploadResult> UploadAsync(string route, string path, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var request = Request(HttpMethod.Post, "/v1/" + route, token);
            request.Content = new StreamContent(stream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            // Headers are ASCII; the game's own file names always are.
            request.Headers.Add("X-File-Name", new string(file.Name.Select(c => c is >= ' ' and < (char)127 ? c : '_').ToArray()));
            request.Headers.Add("X-File-Time", new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds().ToString());
            request.Headers.Add("X-Client-Time", Now.ToString());

            using var response = await _http.SendAsync(request, cancellationToken);
            LastError = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
            return response.StatusCode switch
            {
                HttpStatusCode.Created => UploadResult.Sent,
                HttpStatusCode.OK => UploadResult.Duplicate,
                HttpStatusCode.NotFound => UploadResult.NotMatched,
                HttpStatusCode.Unauthorized => UploadResult.Unauthorized,
                HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType => UploadResult.Refused,
                _ => UploadResult.Failed
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            LastError = Describe(ex);
            return UploadResult.Failed;
        }
    }

    public void Dispose() => _http.Dispose();
}
