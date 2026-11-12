using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MoPLauncher.Patching;

// Logs in for the player once the client's login screen is up.
//
// The account name is put in Config.wtf before the client starts, so the
// login screen opens with it filled in and the cursor in the password box.
// The password is then typed into the game window as key messages, followed
// by Enter. Nothing is injected into the client for this.
//
// There is no outside sign that the login screen has finished loading, and
// keys sent before it has are simply dropped. So the password is sent again
// every few seconds until the client opens a connection to the login port,
// which is the sign that the login went out. It stops there whatever the
// server answers, so a wrong password is tried once, not over and over.
public static class AutoLogin
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WaitAfterTyping = TimeSpan.FromSeconds(3);

    // True when the client started logging in; false when it never did
    // (the game was closed, or the login screen never took the keys).
    public static async Task<bool> RunAsync(int processId, string password, int loginPort)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < GiveUpAfter)
        {
            if (!IsRunning(processId))
                return false;

            // Also true when the player was quicker and logged in themselves.
            if (HasLoginConnection(processId, loginPort))
                return true;

            var window = FindGameWindow(processId);
            if (window == IntPtr.Zero)
            {
                await Task.Delay(250);
                continue;
            }

            TypePassword(window, password);

            var typedAt = clock.Elapsed;
            while (clock.Elapsed - typedAt < WaitAfterTyping)
            {
                // Checked often: a refused login on a nearby server is over
                // in a few hundredths of a second.
                await Task.Delay(10);
                if (HasLoginConnection(processId, loginPort))
                    return true;
            }
        }

        return false;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IntPtr FindGameWindow(int processId)
    {
        var found = IntPtr.Zero;
        var className = new StringBuilder(64);

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !IsWindowVisible(window))
                return true;

            className.Clear();
            GetClassName(window, className, className.Capacity);
            if (className.ToString() != "GxWindowClass")
                return true;

            found = window;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static void TypePassword(IntPtr window, string password)
    {
        foreach (var character in password)
            PostMessage(window, WM_CHAR, character, 1);

        // lParam: repeat count 1 and Enter's scan code, plus the "was down"
        // and "being released" bits for the key-up.
        nint enterDown = 0x001C0001;
        nint enterUp = unchecked((nint)0xC01C0001u);
        PostMessage(window, WM_KEYDOWN, VK_RETURN, enterDown);
        PostMessage(window, WM_KEYUP, VK_RETURN, enterUp);
    }

    // Whether the process has an IPv4 connection (open or being opened) to
    // the login port. With the connect redirect on, the address is always the
    // server's, so only the port is compared.
    private static bool HasLoginConnection(int processId, int loginPort)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        if (size <= 0)
            return false;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0)
                return false;

            // MIB_TCPTABLE_OWNER_PID: a count, then rows of six DWORDs
            // (state, local address, local port, remote address, remote
            // port, owning process). Ports are in network byte order.
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24);
                if (Marshal.ReadInt32(row, 20) != processId)
                    continue;

                var port = Marshal.ReadInt32(row, 16);
                if ((((port & 0xFF) << 8) | ((port >> 8) & 0xFF)) == loginPort)
                    return true;
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const nint VK_RETURN = 0x0D;
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, nint wParam, nint lParam);

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int addressFamily, int tableClass, int reserved);
}
