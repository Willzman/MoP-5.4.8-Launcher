using System.IO;
namespace MoPLauncher.Patching;

// Edits CVars in the client's WTF\Config.wtf.
public static class ConfigWtf
{
    public static void Set(string clientLocation, string cvarName, string value)
    {
        var wtfDirectory = Path.Combine(clientLocation, "WTF");
        Directory.CreateDirectory(wtfDirectory);

        var configPath = Path.Combine(wtfDirectory, "Config.wtf");
        var line = $"SET {cvarName} \"{value}\"";

        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : [];
        var index = lines.FindIndex(l => l.TrimStart().StartsWith($"SET {cvarName} ", StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            if (lines[index] == line)
                return;

            lines[index] = line;
        }
        else
        {
            lines.Insert(0, line);
        }

        File.WriteAllLines(configPath, lines);
    }

    // The value of a CVar, or null when the file or the CVar isn't there.
    public static string? Get(string clientLocation, string cvarName)
    {
        try
        {
            var configPath = Path.Combine(clientLocation, "WTF", "Config.wtf");
            if (!File.Exists(configPath))
                return null;

            var prefix = $"SET {cvarName} ";
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmed[prefix.Length..].Trim().Trim('"');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable is the same as not there.
        }

        return null;
    }

    public static void Remove(string clientLocation, string cvarName)
    {
        var configPath = Path.Combine(clientLocation, "WTF", "Config.wtf");
        if (!File.Exists(configPath))
            return;

        var lines = File.ReadAllLines(configPath).ToList();
        var originalCount = lines.Count;
        lines.RemoveAll(l => l.TrimStart().StartsWith($"SET {cvarName} ", StringComparison.OrdinalIgnoreCase));

        if (lines.Count != originalCount)
            File.WriteAllLines(configPath, lines);
    }
}
