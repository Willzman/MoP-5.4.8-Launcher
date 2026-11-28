using System.Diagnostics;
using MoPLauncher;
using MoPLauncher.Config;
using MoPLauncher.Feedback;

// Runs the launcher's FeedbackService against a real feedback API, with a
// pretend game folder in %TEMP%:
//
//   dotnet run --project tests/FeedbackSmoke -- <server> <account> <token file> <crash .txt> <crash .dmp>
//
// <account> must have one bug report saved on the server in the minute
// before this starts (its screenshot window), and <token file> its upload
// token. Checks that only the screenshot taken at the report is sent, crash
// log + dump and Lua errors go once, nothing goes twice, nothing goes to a
// server with the wrong certificate or with uploads off, and the
// Screenshots watcher sends a picture taken while "the game" runs, the
// every-minute check runs during it, feedback.log says what happened, and
// RecycleBin moves a file to the Recycle Bin.

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: FeedbackSmoke <server> <account> <token file> <crash .txt> <crash .dmp>");
    return 2;
}

var (server, account, token) = (args[0], args[1].ToUpperInvariant(), File.ReadAllText(args[2]).Trim());
var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok)
        failures++;
}

var client = Path.Combine(Path.GetTempPath(), "feedbacksmoke-" + Guid.NewGuid().ToString("N"));
var statePath = Path.Combine(client, "feedback.json");
var saved = Path.Combine(client, "WTF", "Account", account, "SavedVariables");
var screenshots = Path.Combine(client, "Screenshots");
var errors = Path.Combine(client, "Errors");
foreach (var folder in new[] { saved, screenshots, errors })
    Directory.CreateDirectory(folder);

try
{
    var now = DateTime.UtcNow;
    File.WriteAllText(Path.Combine(client, "WTF", "Config.wtf"), $"SET accountName \"{account}\"\n");
    File.WriteAllText(Path.Combine(saved, "BugReport.lua"),
        $"BugReportDB = {{\n\t[\"token\"] = \"{token}\",\n\t[\"reports\"] = {{\n\t}},\n}}\n");
    File.WriteAllText(Path.Combine(saved, "!ErrorCatcher.lua"),
        "ErrorCatcherDB = {\n\t[\"errors\"] = {\n\t\t[\"Interface\\\\AddOns\\\\BugReport\\\\BugReport.lua:7: smoke test\"] = {\n" +
        "\t\t\t[\"count\"] = 2,\n\t\t\t[\"file\"] = \"Interface\\\\AddOns\\\\BugReport\\\\BugReport.lua\",\n\t\t\t[\"line\"] = 7,\n" +
        "\t\t\t[\"message\"] = \"Interface\\\\AddOns\\\\BugReport\\\\BugReport.lua:7: smoke test\",\n\t\t},\n\t},\n}\n");

    // Feedback was turned on two hours ago.
    File.WriteAllText(statePath, $"{{ \"Since\": {DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds()} }}");

    // A tiny JPEG (the server only checks the header).
    byte[] Jpeg(byte seed) => [0xFF, 0xD8, 0xFF, 0xE0, seed, seed, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9];
    string Picture(string name, DateTime time, byte seed)
    {
        var path = Path.Combine(screenshots, name);
        File.WriteAllBytes(path, Jpeg(seed));
        File.SetLastWriteTimeUtc(path, time);
        return path;
    }

    var atReport = Picture("WoWScrnShot_atreport.jpg", now.AddSeconds(-2), 1);
    var unrelated = Picture("WoWScrnShot_unrelated.jpg", now.AddHours(-1), 2);
    File.Copy(args[3], Path.Combine(errors, "smoke Crash - 1.txt"));
    File.Copy(args[4], Path.Combine(errors, "smoke Crash - 1.dmp"));
    File.SetLastWriteTimeUtc(Path.Combine(errors, "smoke Crash - 1.txt"), now);

    var config = new LauncherConfig
    {
        ClientLocation = client,
        ServerAddress = server,
        FeedbackUploads = true,
        FeedbackUploadDumps = true
    };

    // Wrong certificate: nothing is sent, nothing set aside.
    var wrongPin = new LauncherConfig { ClientLocation = client, ServerAddress = server, FeedbackCertificate = new string('0', 64) };
    var service = new FeedbackService(statePath);
    var sent = new List<string>();
    service.Sent += sent.Add;
    await service.RunAsync(wrongPin, sessionOver: true, "smoke");
    Check(sent.Count == 0, "wrong certificate: nothing sent");

    // Uploads off.
    await service.RunAsync(new LauncherConfig { ClientLocation = client, ServerAddress = server, FeedbackUploads = false }, sessionOver: true, "smoke");
    Check(sent.Count == 0, "uploads off: nothing sent");

    // The real thing.
    await service.RunAsync(config, sessionOver: true, "smoke");
    Console.WriteLine($"     sent: {string.Join(" | ", sent)}");
    Check(sent.Count == 1 && sent[0] == "a bug report screenshot, a crash log and the add-on errors", "screenshot, crash log and Lua errors sent");
    var state = FeedbackState.Load(statePath);
    Check(state.IsHandled(new FileInfo(atReport)), "screenshot at the report is done with");
    Check(state.IsHandled(new FileInfo(unrelated)), "unrelated screenshot set aside (not sent)");

    // Again: nothing new.
    sent.Clear();
    await service.RunAsync(config, sessionOver: true, "smoke");
    Check(sent.Count == 0, "second run sends nothing");

    // A session: a screenshot taken while "the game" runs goes out through the
    // watcher; the session's end is when the process exits.
    using var game = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command Start-Sleep 80") { CreateNoWindow = true })!;
    service.WatchSession(config, game.Id);
    await Task.Delay(1000);
    Picture("WoWScrnShot_watched.jpg", now.AddSeconds(-1), 3);
    // An ordinary screenshot: waits all session long, so the minute check sees it.
    Picture("WoWScrnShot_ordinary.jpg", DateTime.UtcNow.AddMinutes(-30), 4);
    await Task.Delay(TimeSpan.FromSeconds(15));
    Check(sent.Contains("a bug report screenshot"), "watcher sent the screenshot taken during the session");
    await game.WaitForExitAsync();
    await Task.Delay(TimeSpan.FromSeconds(15));
    Check(FeedbackState.Load(statePath).IsHandled(new FileInfo(Path.Combine(screenshots, "WoWScrnShot_watched.jpg"))), "watched screenshot done with");

    var log = File.ReadAllText(Path.Combine(client, "feedback.log"));
    Console.WriteLine("     feedback.log:");
    foreach (var line in log.Trim().Split('\n'))
        Console.WriteLine("       " + line.TrimEnd());
    foreach (var expected in new[] { "game started", "WoWScrnShot_watched.jpg created", "new screenshot:", "-> Sent", "every minute:", "game closed", "smoke: " + account })
        Check(log.Contains(expected), $"feedback.log has \"{expected}\"");
    Check(!log.Contains(token), "feedback.log never has the token");

    var junk = Path.Combine(client, "feedbacksmoke-recycle-test.txt");
    File.WriteAllText(junk, "FeedbackSmoke: safe to delete");
    RecycleBin.Send([junk]);
    Check(!File.Exists(junk), "RecycleBin moved the test file away");
}
finally
{
    Directory.Delete(client, recursive: true);
}

Console.WriteLine(failures == 0 ? "All passed." : $"{failures} failed.");
return failures == 0 ? 0 : 1;
