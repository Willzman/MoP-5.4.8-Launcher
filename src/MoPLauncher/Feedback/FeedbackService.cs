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
// closed is picked up the next time it starts. Each pass that has something
// to do (or hits a problem) leaves a line in feedback.log next to config.json;
// tokens are never written there.
public sealed partial class FeedbackService
{
    private static readonly string[] ScreenshotExtensions = [".jpg", ".jpeg", ".png", ".tga"];
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly FeedbackState _state;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _settle;
    private DateTime _lastWindowPass = DateTime.MinValue;

    // While the game runs, new screenshots are looked for this often anyway,
    // in case the watcher missed one.
    private static readonly TimeSpan SessionCheck = TimeSpan.FromMinutes(1);

    // statePath: where to keep what was sent (tests use their own file).
    public FeedbackService(string? statePath = null)
    {
        _state = FeedbackState.Load(statePath);
        LogPath = Path.Combine(Path.GetDirectoryName(statePath ?? FeedbackState.DefaultPath)!, "feedback.log");
    }

    // Tells the window what was sent ("1 screenshot and 2 crash logs").
    public event Action<string>? Sent;

    [GeneratedRegex(@"\[""token""\]\s*=\s*""([0-9A-Fa-f]{64})""")]
    private static partial Regex TokenPattern();

    // ---- Log -----------------------------------------------------------------

    private static readonly object LogLock = new();
    // Next to the state file (feedback.json), so tests keep their own.
    private static string LogPath = Path.Combine(Path.GetDirectoryName(FeedbackState.DefaultPath)!, "feedback.log");

    // One line in feedback.log; past 256 KB the older half is dropped.
    private static void Log(string line)
    {
        try
        {
            lock (LogLock)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 256 * 1024)
                {
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines[(lines.Length / 2)..]);
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never stop an upload.
        }
    }

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
                // Shared, in case the game has the file open.
                using var stream = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var match = TokenPattern().Match(reader.ReadToEnd());
                if (match.Success)
                    tokens[Path.GetFileName(folder)] = match.Groups[1].Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"can't read {Path.GetFileName(folder)}'s BugReport.lua: {ex.Message}");
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
            var watcher = new FileSystemWatcher(screenshots) { IncludeSubdirectories = false };
            watcher.Created += (_, e) => ScreenshotTaken(config, e.Name, "created");
            watcher.Renamed += (_, e) => ScreenshotTaken(config, e.Name, "renamed");
            watcher.Error += (_, e) => Log($"Screenshots watcher error: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            Log($"game started (process {processId}); watching {screenshots}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher = null;
            Log($"game started (process {processId}); Screenshots can't be watched: {ex.Message}");
        }

        _ = Task.Run(async () =>
        {
            // Polled rather than waited on, so there's a check every minute
            // while it plays, whatever the watcher does.
            var nextCheck = DateTime.UtcNow + SessionCheck;
            while (IsRunning(processId))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (DateTime.UtcNow >= nextCheck)
                {
                    nextCheck = DateTime.UtcNow + SessionCheck;
                    await RunAsync(config, sessionOver: false, "every minute");
                }
            }

            StopWatching();
            Log($"game closed (process {processId})");
            // The game writes its SavedVariables as it closes.
            await Task.Delay(TimeSpan.FromSeconds(5));
            await RunAsync(config, sessionOver: true, "game closed");
        });
    }

    // True while the process is there. One we may not look into still counts.
    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // True while any copy of the game runs, one started without the launcher too.
    public static bool IsGameRunning()
    {
        foreach (var name in new[] { "Wow-64", "Wow" })
        {
            var found = Process.GetProcessesByName(name);
            foreach (var process in found)
                process.Dispose();
            if (found.Length > 0)
                return true;
        }
        return false;
    }

    // When the launcher's window comes back to the front: a pass, at most
    // every 30 s. With no game running it's a full one (crash logs and Lua
    // errors too), which covers a game started without the launcher.
    public void WindowActivated(LauncherConfig config)
    {
        if (DateTime.UtcNow - _lastWindowPass < TimeSpan.FromSeconds(30))
            return;
        _lastWindowPass = DateTime.UtcNow;
        var sessionOver = !IsGameRunning();
        _ = Task.Run(() => RunAsync(config, sessionOver, "launcher window"));
    }

    // Screenshots still waiting to be matched with a bug report and sent:
    // Clear screenshots keeps these. None when uploads are off.
    public IReadOnlyList<FileInfo> WaitingScreenshots(LauncherConfig config) =>
        config.FeedbackUploads
            ? NewFiles(Path.Combine(config.ClientLocation, "Screenshots"), ScreenshotExtensions).ToList()
            : [];

    // A screenshot is written a moment before the report reaches the server;
    // look a few seconds later, once both are done. Several in a row wait
    // for the last one.
    private void ScreenshotTaken(LauncherConfig config, string? name, string how)
    {
        Log($"Screenshots: {name} {how}");
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
            await RunAsync(config, sessionOver: false, "new screenshot");
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
    public async Task RunAsync(LauncherConfig config, bool sessionOver, string trigger)
    {
        if (!config.FeedbackUploads || !Directory.Exists(config.ClientLocation))
            return;

        await _running.WaitAsync();
        try
        {
            using var api = FeedbackApi.For(config);
            if (api is null)
            {
                Log($"{trigger}: no feedback API for server '{config.ServerAddress}' (or no certificate to pin)");
                return;
            }

            var tokens = Tokens(config.ClientLocation);
            if (tokens.Count == 0)
            {
                if (sessionOver)
                    Log($"{trigger}: no account has an upload token yet");
                return;
            }

            var screenshots = await SendScreenshotsAsync(api, config.ClientLocation, tokens, sessionOver, trigger);
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
        catch (Exception ex)
        {
            // Feedback must never get in the way of playing; next time.
            Log($"{trigger}: failed: {ex.GetType().Name}: {ex.Message}");
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
    private async Task<int> SendScreenshotsAsync(FeedbackApi api, string clientLocation, Dictionary<string, string> tokens, bool sessionOver, string trigger)
    {
        var files = NewFiles(Path.Combine(clientLocation, "Screenshots"), ScreenshotExtensions).ToList();
        if (files.Count == 0)
            return 0;
        Log($"{trigger}: {files.Count} new screenshot(s); accounts with a token: {string.Join(", ", tokens.Keys)}");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var since = new DateTimeOffset(files[0].LastWriteTimeUtc).ToUnixTimeSeconds() - 3600;
        var sent = 0;
        var askedAll = true;
        var matched = new HashSet<FileInfo>();

        foreach (var (account, token) in tokens)
        {
            var reports = await api.GetReportsAsync(token, since);
            if (reports is null)
            {
                Log($"{trigger}: {account}: couldn't get its reports: {api.LastError}");
                askedAll = false;
                continue;
            }
            Log($"{trigger}: {account}: {reports.Reports.Count} recent report(s), PC clock off by {reports.ServerTime - now} s");

            // The PC's clock may be off from the server's.
            var offset = reports.ServerTime - now;
            foreach (var file in files.Where(f => !matched.Contains(f)))
            {
                var taken = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds() + offset;
                if (!reports.Reports.Any(r => r.CreatedAt >= taken - reports.WindowAfter && r.CreatedAt <= taken + reports.WindowBefore))
                    continue;

                matched.Add(file);
                var result = await api.UploadAsync("screenshot", file.FullName, token);
                Log($"{trigger}: {file.Name} -> {result}{(result == UploadResult.Failed ? ": " + api.LastError : string.Empty)}");
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
