using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MoPLauncher.Music;

// Finds the Mists of Pandaria login screen music ("Heart of Pandaria") in
// the player's own client and copies it out so the launcher can play it.
// The track is never shipped with the launcher: it is read from the MPQ
// archives in the game's Data folder, and cached in the user's app data.
public static class ClientMusic
{
    // Returns the path of a playable mp3, or null when the client doesn't
    // have the track (or this build can't read archives).
    public static string? FindLoginMusic(string clientLocation)
    {
        try
        {
            var dataDirectory = Path.Combine(clientLocation, "Data");
            if (!Directory.Exists(dataDirectory))
                return null;

            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoPLauncher", "music");
            var key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(clientLocation).ToLowerInvariant())))[..16].ToLowerInvariant();
            var cached = Path.Combine(cacheDirectory, $"login-{key}.mp3");

            if (File.Exists(cached) && new FileInfo(cached).Length > 0)
                return cached;

            if (!StormLib.EnsureLoaded())
                return null;

            Directory.CreateDirectory(cacheDirectory);
            return Extract(dataDirectory, cached) ? cached : null;
        }
        catch (Exception)
        {
            // Music is a nicety; never let it get in the way of playing.
            return null;
        }
    }

    private static bool Extract(string dataDirectory, string destination)
    {
        var archives = Directory.EnumerateFiles(dataDirectory, "*.MPQ", SearchOption.TopDirectoryOnly)
            .OrderBy(ArchiveRank)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        (string Archive, string Name, int Score, uint Size)? best = null;

        foreach (var archive in archives)
        {
            var candidate = BestCandidate(archive);
            if (candidate is null)
                continue;

            if (best is null || (candidate.Value.Score, candidate.Value.Size).CompareTo((best.Value.Score, best.Value.Size)) > 0)
                best = (archive, candidate.Value.Name, candidate.Value.Score, candidate.Value.Size);

            // The main theme itself: no need to open the remaining archives.
            if (candidate.Value.Score >= 150)
                break;
        }

        return best is not null && ExtractFile(best.Value.Archive, best.Value.Name, destination);
    }

    // Where the track is most likely to be, so it's usually found in the
    // first archive or two. Update archives go last: they mostly hold
    // patches to files rather than files.
    private static int ArchiveRank(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.StartsWith("wow-update")) return 4;
        if (name.StartsWith("sound")) return 0;
        if (name.StartsWith("expansion4")) return 1;
        if (name.StartsWith("expansion")) return 2;
        return 3;
    }

    // How much an archived file looks like the MoP login music. 0 = not it.
    internal static int Score(string archivedName)
    {
        var name = archivedName.ToLowerInvariant().Replace('/', '\\');
        if (!name.EndsWith(".mp3") || !name.Contains("sound\\music\\"))
            return 0;

        var isHeartOfPandaria = name.Contains("heartofpandaria") || name.Contains("heart_of_pandaria");
        var isMainTitle = name.Contains("maintitle") || name.Contains("main_title") ||
                          name.Contains("maintheme") || name.Contains("main_theme");
        var isPandaria = isHeartOfPandaria || name.Contains("pandaria") || name.Contains("mop_") ||
                         name.Contains("_mop") || name.Contains("_50_");

        var score = 0;
        if (isHeartOfPandaria) score += 100;
        if (isMainTitle && isPandaria) score += 50;
        if (isPandaria && name.Contains("gluescreenmusic")) score += 20;
        return score >= 50 ? score : 0;
    }

    private static (string Name, int Score, uint Size)? BestCandidate(string archivePath)
    {
        if (!StormLib.SFileOpenArchive(archivePath, 0, StormLib.MPQ_OPEN_READ_ONLY, out var archive))
            return null;

        var findData = Marshal.AllocHGlobal(StormLib.FindDataSize);
        try
        {
            (string Name, int Score, uint Size)? best = null;

            var find = StormLib.SFileFindFirstFile(archive, "*.mp3", findData, IntPtr.Zero);
            if (find == IntPtr.Zero)
                return null;

            try
            {
                do
                {
                    var flags = (uint)Marshal.ReadInt32(findData, StormLib.FindDataFileFlagsOffset);
                    if ((flags & (StormLib.MPQ_FILE_PATCH_FILE | StormLib.MPQ_FILE_DELETE_MARKER)) != 0)
                        continue; // an incremental patch, not a whole file

                    var name = Marshal.PtrToStringAnsi(findData) ?? string.Empty;
                    var score = Score(name);
                    if (score == 0)
                        continue;

                    var size = (uint)Marshal.ReadInt32(findData, StormLib.FindDataFileSizeOffset);
                    if (best is null || (score, size).CompareTo((best.Value.Score, best.Value.Size)) > 0)
                        best = (name, score, size);
                }
                while (StormLib.SFileFindNextFile(find, findData));
            }
            finally
            {
                StormLib.SFileFindClose(find);
            }

            return best;
        }
        finally
        {
            Marshal.FreeHGlobal(findData);
            StormLib.SFileCloseArchive(archive);
        }
    }

    private static bool ExtractFile(string archivePath, string archivedName, string destination)
    {
        if (!StormLib.SFileOpenArchive(archivePath, 0, StormLib.MPQ_OPEN_READ_ONLY, out var archive))
            return false;

        var temporary = destination + ".tmp";
        try
        {
            if (!StormLib.SFileExtractFile(archive, archivedName, temporary, 0))
                return false;

            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        finally
        {
            StormLib.SFileCloseArchive(archive);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
