using System.IO;
using System.Text.Json;

namespace MoPLauncher.Feedback;

// What has been sent already, kept next to config.json (feedback.json), so a
// file is never sent twice and nothing from before feedback uploads were on
// is sent at all.
public sealed class FeedbackState
{
    // Unix time feedback uploads started on this PC. Screenshots and crash
    // logs older than this are left alone.
    public long Since { get; set; }

    // Files dealt with for good (sent, or not wanted), as "path|size|time".
    public List<string> Handled { get; set; } = [];

    // Per account folder: SHA-256 of the Error Catcher file last sent.
    public Dictionary<string, string> LuaErrorsSent { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxHandled = 3000;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoPLauncher", "feedback.json");

    private string _path = DefaultPath;
    private string FilePath => _path;

    public static FeedbackState Load(string? path = null)
    {
        path ??= DefaultPath;
        FeedbackState? state = null;
        try
        {
            if (File.Exists(path))
                state = JsonSerializer.Deserialize<FeedbackState>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            // A broken file starts over (from now, so nothing old is sent).
        }

        state ??= new FeedbackState();
        state._path = path;
        state.Handled ??= [];
        state.LuaErrorsSent = new Dictionary<string, string>(state.LuaErrorsSent ?? [], StringComparer.OrdinalIgnoreCase);
        if (state.Since == 0)
        {
            state.Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            state.Save();
        }
        return state;
    }

    public void Save()
    {
        try
        {
            if (Handled.Count > MaxHandled)
                Handled.RemoveRange(0, Handled.Count - MaxHandled);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Not saved: at worst a file is offered again, and the server
            // knows it already.
        }
    }

    public static string Key(FileInfo file) => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    public bool IsHandled(FileInfo file) => Handled.Contains(Key(file));

    public void MarkHandled(FileInfo file)
    {
        var key = Key(file);
        if (!Handled.Contains(key))
            Handled.Add(key);
    }
}
