using System.Runtime.InteropServices;
using MoPLauncher.Music;

// Builds a pretend client Data folder with format-4 MPQ archives, then asks
// the launcher's own code to find and extract the login music from it.

if (!StormLib.EnsureLoaded())
    return Fail("storm.dll could not be loaded");

Check(ClientMusic.Score(@"Sound\Music\Pandaria\MUS_50_HeartofPandaria_MainTitle.mp3") >= 150, "main title scores highest");
Check(ClientMusic.Score(@"Sound\Music\GlueScreenMusic\wow_main_theme.mp3") == 0, "other expansions' themes are ignored");
Check(ClientMusic.Score(@"Sound\Music\Pandaria\MUS_50_HeartofPandaria_MainTitle.ogg") == 0, "non-mp3 is ignored");

var client = Path.Combine(Path.GetTempPath(), "mpqsmoke-" + Guid.NewGuid().ToString("N"));
var data = Path.Combine(client, "Data");
Directory.CreateDirectory(data);

var random = new Random(548);
byte[] Bytes(int length)
{
    var bytes = new byte[length];
    random.NextBytes(bytes);
    return bytes;
}

var theme = Bytes(700_000);

CreateArchive(Path.Combine(data, "sound.MPQ"),
    (@"Sound\Music\GlueScreenMusic\wow_main_theme.mp3", Bytes(50_000)),
    (@"Sound\Music\GlueScreenMusic\BC_main_theme.mp3", Bytes(50_000)));
CreateArchive(Path.Combine(data, "expansion4.MPQ"),
    (@"Sound\Music\Pandaria\MUS_50_HeartofPandaria_01.mp3", Bytes(900_000)),
    (@"Sound\Music\Pandaria\MUS_50_HeartofPandaria_MainTitle.mp3", theme),
    (@"Interface\Glues\Models\UI_MainMenu_Pandaria\foo.blp", Bytes(10_000)));
CreateArchive(Path.Combine(data, "misc.MPQ"),
    (@"Sound\Music\Pandaria\MUS_50_Wandering_01.mp3", Bytes(20_000)));

var found = ClientMusic.FindLoginMusic(client);
Check(found is not null, "login music was found");
Check(found is not null && File.ReadAllBytes(found).AsSpan().SequenceEqual(theme), "extracted bytes match the archived file");

var again = ClientMusic.FindLoginMusic(client);
Check(again == found, "second lookup uses the cached copy");

if (found is not null)
    File.Delete(found);
Directory.Delete(client, recursive: true);

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

void CreateArchive(string path, params (string Name, byte[] Data)[] files)
{
    const uint MPQ_CREATE_LISTFILE = 0x00100000, MPQ_CREATE_ARCHIVE_V4 = 0x03000000;
    const uint MPQ_FILE_COMPRESS = 0x00000200, MPQ_COMPRESSION_ZLIB = 0x02;

    if (!Native.SFileCreateArchive(path, MPQ_CREATE_LISTFILE | MPQ_CREATE_ARCHIVE_V4, 64, out var archive))
        throw new InvalidOperationException($"SFileCreateArchive failed for {path}: {Marshal.GetLastWin32Error()}");

    foreach (var (name, bytes) in files)
    {
        if (!Native.SFileCreateFile(archive, name, 0, (uint)bytes.Length, 0, MPQ_FILE_COMPRESS, out var file) ||
            !Native.SFileWriteFile(file, bytes, (uint)bytes.Length, MPQ_COMPRESSION_ZLIB) ||
            !Native.SFileFinishFile(file))
        {
            throw new InvalidOperationException($"Could not add {name}: {Marshal.GetLastWin32Error()}");
        }
    }

    if (!StormLib.SFileCloseArchive(archive))
        throw new InvalidOperationException($"SFileCloseArchive failed for {path}");
}

void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")}  {what}");
    if (!condition)
        failures++;
}

int Fail(string message)
{
    Console.WriteLine($"FAIL  {message}");
    return 1;
}

partial class Program
{
    private static int failures;
}

// StormLib calls only the test needs (the launcher never writes archives).
internal static class Native
{
    [DllImport("storm", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileCreateArchive(
        [MarshalAs(UnmanagedType.LPWStr)] string mpqName, uint createFlags, uint maxFileCount, out IntPtr archive);

    [DllImport("storm", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileCreateFile(
        IntPtr archive, [MarshalAs(UnmanagedType.LPStr)] string archivedName, ulong fileTime, uint fileSize,
        uint locale, uint flags, out IntPtr file);

    [DllImport("storm", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileWriteFile(IntPtr file, byte[] data, uint size, uint compression);

    [DllImport("storm", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileFinishFile(IntPtr file);
}
