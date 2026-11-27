using System.IO;
using System.Reflection;

namespace MoPLauncher.Addons;

// Files the launcher ships for the game folder: the server's add-ons (such as
// Solo Queue) and, later, MPQ patches. They are embedded in the exe from the
// project's ClientFiles folder, under the same path they get in the game
// folder (ClientFiles\Interface\AddOns\X\X.toc -> <game>\Interface\AddOns\X\X.toc).
//
// Every file the launcher writes is listed in a small text file in the game
// folder, so Uninstall removes exactly those (including ones a later version
// no longer ships) and nothing the player put there.
public static class ClientFiles
{
    private const string ResourcePrefix = "clientfiles/";
    public const string ManifestName = "MoPLauncher-installed.txt";

    // Paths relative to the game folder, with Windows separators.
    public static IReadOnlyList<string> Shipped { get; } = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
        .Select(n => Normalize(n[ResourcePrefix.Length..]))
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    // The add-ons the launcher ships (folder names under Interface\AddOns),
    // for telling the player what Install puts in. A leading "!" (which only
    // makes an add-on load first, like !ErrorCatcher) is left out.
    public static IReadOnlyList<string> AddOnNames { get; } = Shipped
        .Select(p => p.Split(Path.DirectorySeparatorChar))
        .Where(parts => parts.Length >= 4
                        && parts[0].Equals("Interface", StringComparison.OrdinalIgnoreCase)
                        && parts[1].Equals("AddOns", StringComparison.OrdinalIgnoreCase))
        .Select(parts => parts[2].TrimStart('!'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // True when a shipped file is missing or different in the game folder, or
    // a file an older launcher installed is still there and no longer shipped.
    public static bool NeedsInstall(string clientLocation)
    {
        if (string.IsNullOrWhiteSpace(clientLocation) || !Directory.Exists(clientLocation))
            return false;

        foreach (var relative in Shipped)
        {
            var target = Resolve(clientLocation, relative);
            if (!File.Exists(target))
                return true;

            var bytes = ReadResource(relative);
            if (new FileInfo(target).Length != bytes.Length || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                return true;
        }

        return ReadManifest(clientLocation)
            .Any(p => !Shipped.Contains(p, StringComparer.OrdinalIgnoreCase) && File.Exists(Resolve(clientLocation, p)));
    }

    // Writes every shipped file that is missing or different, and removes
    // files an older launcher installed that this one no longer ships.
    // Returns how many files were written or removed.
    public static int Install(string clientLocation)
    {
        var changed = 0;
        var installed = ReadManifest(clientLocation);

        foreach (var relative in Shipped)
        {
            var target = Resolve(clientLocation, relative);
            var bytes = ReadResource(relative);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                changed++;
            }
            installed.Add(relative);
        }

        foreach (var retired in installed.Where(p => !Shipped.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            if (Delete(clientLocation, retired))
                changed++;
            installed.Remove(retired);
        }

        WriteManifest(clientLocation, installed);
        return changed;
    }

    // Removes every file the launcher installed (and the folders that are
    // left empty by it). Returns how many files were removed.
    public static int Uninstall(string clientLocation)
    {
        var installed = ReadManifest(clientLocation);
        foreach (var relative in Shipped)
            installed.Add(relative);

        var removed = 0;
        var failed = new List<string>();
        foreach (var relative in installed)
        {
            try
            {
                if (Delete(clientLocation, relative))
                    removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(relative);
            }
        }

        if (failed.Count > 0)
        {
            // Keep the ones still there on the list for the next try.
            WriteManifest(clientLocation, new HashSet<string>(failed, StringComparer.OrdinalIgnoreCase));
            throw new IOException($"{failed.Count} file(s) are in use. Close the game and try again.");
        }

        var manifest = Path.Combine(clientLocation, ManifestName);
        if (File.Exists(manifest))
            File.Delete(manifest);

        return removed;
    }

    private static bool Delete(string clientLocation, string relative)
    {
        var target = Resolve(clientLocation, relative);
        if (!File.Exists(target))
            return false;

        File.Delete(target);

        // Remove folders this left empty, but never the game's own top-level
        // folders (Interface, Interface\AddOns, Data, ...).
        var root = Path.GetFullPath(clientLocation).TrimEnd(Path.DirectorySeparatorChar);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            root,
            Path.Combine(root, "Interface"),
            Path.Combine(root, "Interface", "AddOns"),
            Path.Combine(root, "Data")
        };
        var folder = Path.GetDirectoryName(target);
        while (folder is not null && !keep.Contains(folder) && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder);
        }

        return true;
    }

    // Refuses paths that would land outside the game folder.
    private static string Resolve(string clientLocation, string relative)
    {
        var root = Path.GetFullPath(clientLocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{relative}' is outside the game folder.");
        return full;
    }

    private static byte[] ReadResource(string relative)
    {
        var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .First(n => n.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Normalize(n[ResourcePrefix.Length..]), relative, StringComparison.OrdinalIgnoreCase));
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static HashSet<string> ReadManifest(string clientLocation)
    {
        var path = Path.Combine(clientLocation, ManifestName);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path))
                if (line.Trim() is { Length: > 0 } entry && !entry.StartsWith('#'))
                    set.Add(Normalize(entry));
        return set;
    }

    private static void WriteManifest(string clientLocation, HashSet<string> installed)
    {
        var path = Path.Combine(clientLocation, ManifestName);
        if (installed.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var lines = new List<string>
        {
            "# Files the MoP 5.4.8 Launcher installed in this game folder.",
            "# Its \"Remove server add-ons and patches\" removes exactly these."
        };
        lines.AddRange(installed.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        File.WriteAllLines(path, lines);
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}
