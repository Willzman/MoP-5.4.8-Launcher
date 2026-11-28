using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace MoPLauncher;

// Moves files to the Recycle Bin (instead of deleting them for good), the way
// Explorer does, with Windows' own SHFileOperation.
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    // Throws when Windows couldn't move them.
    public static void Send(IEnumerable<string> paths)
    {
        var list = paths.Select(Path.GetFullPath).ToList();
        if (list.Count == 0)
            return;

        // A list of paths, each ending in \0, and one more \0 at the end.
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', list) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
            throw new Win32Exception(result, $"Windows couldn't move them to the Recycle Bin (error {result}).");
        if (operation.fAnyOperationsAborted)
            throw new OperationCanceledException("Moving them to the Recycle Bin was stopped.");
    }
}
