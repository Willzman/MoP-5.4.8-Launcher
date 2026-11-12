using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MoPLauncher.Music;

// Bindings for StormLib (MIT, Ladislav Zezula), the reference library for
// reading Blizzard's MPQ archives. storm.dll is built by
// tools/build-stormlib.ps1 and embedded in the launcher; it is unpacked to
// the user's app data on first use so the launcher stays a single exe.
internal static class StormLib
{
    private const string Library = "storm";
    private const string ResourceName = "storm.dll";

    public const uint MPQ_OPEN_READ_ONLY = 0x00000100;
    public const uint MPQ_FILE_PATCH_FILE = 0x00100000;
    public const uint MPQ_FILE_DELETE_MARKER = 0x02000000;

    // SFILE_FIND_DATA on 64-bit Windows: char cFileName[260], padding to 264,
    // char* szPlainName, then eight DWORDs.
    public const int FindDataSize = 304;
    public const int FindDataFileSizeOffset = 280;
    public const int FindDataFileFlagsOffset = 284;

    private static readonly Lazy<bool> Loaded = new(Load);

    // False when this build has no storm.dll in it or it can't be unpacked;
    // callers then simply go without.
    public static bool EnsureLoaded() => Loaded.Value;

    private static bool Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resource = assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
                return false;

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();

            // Named by content, so a newer launcher never trips over a copy
            // an older one still has loaded.
            var tag = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoPLauncher", "native");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"storm-{tag}.dll");

            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                File.WriteAllBytes(path, bytes);

            var handle = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(assembly,
                (name, _, _) => name == Library ? handle : IntPtr.Zero);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileOpenArchive(
        [MarshalAs(UnmanagedType.LPWStr)] string mpqName, uint priority, uint flags, out IntPtr archive);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileCloseArchive(IntPtr archive);

    [DllImport(Library, SetLastError = true)]
    public static extern IntPtr SFileFindFirstFile(
        IntPtr archive, [MarshalAs(UnmanagedType.LPStr)] string mask, IntPtr findData, IntPtr listFile);

    [DllImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileFindNextFile(IntPtr find, IntPtr findData);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileFindClose(IntPtr find);

    [DllImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SFileExtractFile(
        IntPtr archive, [MarshalAs(UnmanagedType.LPStr)] string toExtract,
        [MarshalAs(UnmanagedType.LPWStr)] string extracted, uint searchScope);
}
