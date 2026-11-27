using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MoPLauncher.Config;
using MoPLauncher.Patching;

namespace MoPLauncher.Feedback;

// Sends the realm what the game leaves behind for its bug reports:
//
//   - the screenshot the Bug Report add-on takes when a report is sent. While
//     the game runs, a FileSystemWatcher on Screenshots notices new pictures;
//     only those taken just before one of the account's reports (the server
//     says when its reports were saved) are sent. Every other screenshot is
//     left alone and never leaves the PC.
//   - after the game closes: new crash logs from Errors (the .txt; the .dmp
//     only with "include crash dumps" on), and the Error Catcher add-on's
//     list of Lua errors (WTF\Account\<ACCOUNT>\SavedVariables\!ErrorCatcher.lua).
//
// Uploads are tied to an account by the upload token the server gave the Bug
// Report add-on (WTF\Account\<ACCOUNT>\SavedVariables\BugReport.lua); without
// one, nothing is sent. The game writes SavedVariables when it closes, so the
// very first token shows up only then. What happened while the launcher was
// closed is picked up the next time it starts.
public sealed partial class FeedbackService
{
    private static readonly string[] ScreenshotExtensions = [".jpg", ".jpeg", ".png", ".tga"];
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly FeedbackState _state;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _settle;

    // statePath: where to keep what was sent (tests use their own file).
    public FeedbackService(string? statePath = null) => _state = FeedbackState.Load(statePath);

    // Tells the window what was sent ("1 screenshot and 2 crash logs").
    public event Action<string>? Sent;

    [GeneratedRegex(@"\[""token""\]\s*=\s*""([0-9A-Fa-f]{64})""")]
    private static partial Regex TokenPattern();

    // ---- Tokens --------------------------------------------------------------

    // Account folder (as the game names it, upper case) -> upload token, for
    // every account on this PC whose Bug Report add-on has one.
    private static Dictionary<string, string> Tokens(string clientLocation)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var accounts = Path.Combine(clientLocation, "WTF", "Account");
        if (!Directory.Exists(accounts))
            return tokens;

        foreach (var folder in Directory.EnumerateDirectories(accounts))
        {
            try
            {
                var saved = Path.Combine(folder, "SavedVariables", "BugReport.lua");
                if (!File.Exists(saved))
                    continue;
                var match = TokenPattern().Match(File.ReadAllText(saved));
                if (match.Success)
                    tokens[Path.GetFileName(folder)] = match.Groups[1].Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or unreadable: next time.
            }
        }
        return tokens;
    }

    // The token of the account last played (Config.wtf), else any.
    private static string? TokenOfLastAccount(string clientLocation, Dictionary<string, string> tokens)
    {
        var account = ConfigWtf.Get(clientLocation, "accountName")?.Trim();
        if (!string.IsNullOrEmpty(account) && tokens.TryGetValue(account, out var token))
            return token;
        return tokens.Values.FirstOrDefault();
    }

    // ---- While the game runs ---------------------------------------------------

    // Watches the Screenshots folder until the game (processId) closes, then
    // sends what the session left. Does nothing when uploads are off.
    public void WatchSession(LauncherConfig config, int processId)
    {
        if (!config.FeedbackUploads)
            return;

        var clientLocation = config.ClientLocation;
        StopWatching();
        try
        {
            var screenshots = Path.Combine(clientLocation, "Screenshots");
            Directory.CreateDirectory(screenshots);
            _watcher = new FileSystemWatcher(screenshots) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            _watcher.Created += (_, _) => ScreenshotTaken(config);
            _watcher.Renamed += (_, _) => ScreenshotTaken(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var game = Process.GetProcessById(processId);
                await game.WaitForExitAsync();
            }
            catch (ArgumentException)
            {
                // Already gone.
            }

            StopWatching();
            // The game writes its SavedVariables as it closes.
            await Task.Delay(TimeSpan.FromSeconds(5));
            await RunAsync(config, sessionOver: true);
        });
    }

    // A screenshot is written a moment before the report reaches the server;
    // look a few seconds later, once both are done. Several in a row wait
    // for the last one.
    private void ScreenshotTaken(LauncherConfig config)
    {
        _settle?.Cancel();
        var settle = _settle = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), settle.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            await RunAsync(config, sessionOver: false);
        });
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // ---- Sending -------------------------------------------------------------

    // One pass: screenshots, then (once the session is over, or when the
    // launcher starts) crash logs and Lua errors. Never throws.
    public async Task RunAsync(LauncherConfig config, bool sessionOver)
    {
        if (!config.FeedbackUploads || !Directory.Exists(config.ClientLocation))
            return;

        await _running.WaitAsync();
        try
        {
            using var api = FeedbackApi.For(config);
            if (api is null)
                return;

            var tokens = Tokens(config.ClientLocation);
            if (tokens.Count == 0)
                return;

            var screenshots = await SendScreenshotsAsync(api, config.ClientLocation, tokens, sessionOver);
            var crashes = 0;
            var luaErrors = false;
            if (sessionOver)
            {
                crashes = await SendCrashLogsAsync(api, config, tokens);
                luaErrors = await SendLuaErrorsAsync(api, config.ClientLocation, tokens);
            }
            _state.Save();

            var parts = new List<string>();
            if (screenshots > 0)
                parts.Add(screenshots == 1 ? "a bug report screenshot" : $"{screenshots} bug report screenshots");
            if (crashes > 0)
                parts.Add(crashes == 1 ? "a crash log" : $"{crashes} crash logs");
            if (luaErrors)
                parts.Add("the add-on errors");
            if (parts.Count > 0)
                Sent?.Invoke(parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1]);
        }
        catch (Exception)
        {
            // Feedback must never get in the way of playing; next time.
        }
        finally
        {
            _running.Release();
        }
    }

    private IEnumerable<FileInfo> NewFiles(string folder, IEnumerable<string> extensions)
    {
        if (!Directory.Exists(folder))
            return [];

        var since = DateTimeOffset.FromUnixTimeSeconds(_state.Since).UtcDateTime;
        var oldest = DateTime.UtcNow - MaxAge;
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => f.LastWriteTimeUtc >= since && f.LastWriteTimeUtc >= oldest && !_state.IsHandled(f))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
    }

    // Sends the screenshots taken just before one of an account's reports.
    // At the end of a session, the others are set aside for good.
    private async Task<int> SendScreenshotsAsync(FeedbackApi api, string clientLocation, Dictionary<string, string> tokens, bool sessionOver)
    {
        var files = NewFiles(Path.Combine(clientLocation, "Screenshots"), ScreenshotExtensions).ToList();
        if (files.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var since = new DateTimeOffset(files[0].LastWriteTimeUtc).ToUnixTimeSeconds() - 3600;
        var sent = 0;
        var askedAll = true;
        var matched = new HashSet<FileInfo>();

        foreach (var token in tokens.Values.Distinct())
        {
            var reports = await api.GetReportsAsync(token, since);
            if (reports is null)
            {
                askedAll = false;
                continue;
            }

            // The PC's clock may be off from the server's.
            var offset = reports.ServerTime - now;
            foreach (var file in files.Where(f => !matched.Contains(f)))
            {
                var taken = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds() + offset;
                if (!reports.Reports.Any(r => r.CreatedAt >= taken - reports.WindowAfter && r.CreatedAt <= taken + reports.WindowBefore))
                    continue;

                matched.Add(file);
                var result = await api.UploadAsync("screenshot", file.FullName, token);
                if (result == UploadResult.Sent)
                    sent++;
                if (result is UploadResult.Sent or UploadResult.Duplicate or UploadResult.Refused or UploadResult.NotMatched)
                    _state.MarkHandled(file);
            }
        }

        // Once the game is closed (and every account could be asked), a
        // screenshot that goes with no report is an ordinary screenshot.
        if (sessionOver && askedAll)
        {
            var settled = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var file in files.Where(f => !matched.Contains(f) && f.LastWriteTimeUtc < settled))
                _state.MarkHandled(file);
        }
        return sent;
    }

    // Sends new crash logs (and their dumps, if wanted) under the account
    // that played last.
    private async Task<int> SendCrashLogsAsync(FeedbackApi api, LauncherConfig config, Dictionary<string, string> tokens)
    {
        var token = TokenOfLastAccount(config.ClientLocation, tokens);
        if (token is null)
            return 0;

        var sent = 0;
        foreach (var log in NewFiles(Path.Combine(config.ClientLocation, "Errors"), [".txt"]))
        {
            var result = await api.UploadAsync("crash", log.FullName, token);
            if (result is UploadResult.Failed or UploadResult.Unauthorized)
                continue;

            if (result == UploadResult.Sent)
                sent++;
            if (config.FeedbackUploadDumps && result is UploadResult.Sent or UploadResult.Duplicate)
            {
                var dump = Path.ChangeExtension(log.FullName, ".dmp");
                if (File.Exists(dump) && await api.UploadAsync("crash-dump", dump, token) == UploadResult.Failed)
                    continue; // log and dump next time; the server knows the log already
            }
            _state.MarkHandled(log);
        }
        return sent;
    }

    // Sends each account's Error Catcher list when it changed since last time.
    private async Task<bool> SendLuaErrorsAsync(FeedbackApi api, string clientLocation, Dictionary<string, string> tokens)
    {
        var sentAny = false;
        foreach (var (account, token) in tokens)
        {
            var path = Path.Combine(clientLocation, "WTF", "Account", account, "SavedVariables", "!ErrorCatcher.lua");
            if (!File.Exists(path))
                continue;

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (_state.LuaErrorsSent.TryGetValue(account, out var last) && last == hash)
                continue;

            // Every kept error has a count; no count, no errors to send.
            if (!System.Text.Encoding.Latin1.GetString(bytes).Contains("[\"count\"]"))
            {
                _state.LuaErrorsSent[account] = hash;
                continue;
            }

            var result = await api.UploadAsync("lua-errors", path, token);
            if (result is UploadResult.Sent or UploadResult.Duplicate or UploadResult.Refused)
            {
                _state.LuaErrorsSent[account] = hash;
                sentAny |= result == UploadResult.Sent;
            }
        }
        return sentAny;
    }
}
