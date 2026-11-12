using System.IO;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace MoPLauncher.Patching;

// Starts the 5.4.8 client and immediately patches it in process memory so it
// talks to a private server. Nothing on disk (the exe, the hosts file) is
// touched: the patches exist only in the running process, for its lifetime.
//
// The patch set is the classic-login one from the Project SkyFire launcher
// (GPL-3.0), which this is ported from:
//   1. the hardcoded region logon hostnames are overwritten,
//   2. gethostbyname is hooked so any hostname resolves to the server,
//   3. connect is hooked so every IPv4 connection goes to the server,
//   4. five small login-flow patches make the client use the classic
//      realmlist login instead of the Battle.net one.
//
// The process is started normally rather than suspended: a suspended 32-bit
// client isn't far enough along for its modules to be enumerated from a
// 64-bit launcher. Patching right after start is still far ahead of the
// client's first connection, which can't happen before its UI has loaded.
public static class ClientLauncher
{
    // The client's hardcoded region logon hostnames, and what replaces each
    // one ({0} = server IP, {1} = login port). The host:port variant is its
    // own string in the client, not a substring of the plain one.
    private static readonly (string Pattern, string ReplacementFormat)[] HostnamePatches =
    [
        ("us.logon.worldofwarcraft.com", "{0}"),
        ("kr.logon.worldofwarcraft.com", "{0}"),
        ("eu.logon.worldofwarcraft.com", "{0}"),
        ("tw.logon.worldofwarcraft.com", "{0}"),
        ("cn.logon.warcraftchina.com", "{0}"),
        ("us.logon.worldofwarcraft.com:3724", "{0}:{1}")
    ];

    // Returns the process id of the started client.
    public static int Launch(string exePath, string workingDirectory, IPAddress server, int loginPort, bool redirectAllConnections)
    {
        if (server.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("Only IPv4 server addresses are supported.");

        var exeFile = File.ReadAllBytes(exePath);
        var is64Bit = PeImportTable.IsPe64Bit(exeFile);

        var startupInfo = new Native.STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf<Native.STARTUPINFO>();

        if (!Native.CreateProcess(exePath, $"\"{exePath}\"", IntPtr.Zero, IntPtr.Zero, false, 0,
                IntPtr.Zero, workingDirectory, ref startupInfo, out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the game");
        }

        try
        {
            Patch(processInfo.dwProcessId, exePath, exeFile, is64Bit, server, loginPort, redirectAllConnections);
            return processInfo.dwProcessId;
        }
        catch
        {
            // Never leave an unpatched client running: it would try to reach
            // the addresses built into it.
            Native.TerminateProcess(processInfo.hProcess, 1);
            throw;
        }
        finally
        {
            Native.CloseHandle(processInfo.hThread);
            Native.CloseHandle(processInfo.hProcess);
        }
    }

    private static void Patch(int processId, string exePath, byte[] exeFile, bool is64Bit,
        IPAddress server, int loginPort, bool redirectAllConnections)
    {
        var (baseAddress, moduleSize) = GetMainModule(processId, Path.GetFileName(exePath));

        var hProcess = Native.OpenProcess(
            Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION, false, processId);
        if (hProcess == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the game process");

        try
        {
            var image = new byte[moduleSize];
            if (!Native.ReadProcessMemory(hProcess, baseAddress, image, image.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the game's memory");

            var ip = server.ToString();
            var ipBytes = server.GetAddressBytes();

            foreach (var (patternText, replacementFormat) in HostnamePatches)
            {
                var pattern = Encoding.ASCII.GetBytes(patternText + '\0');
                var replacement = Encoding.ASCII.GetBytes(string.Format(replacementFormat, ip, loginPort) + '\0');
                if (replacement.Length > pattern.Length)
                    continue; // can't happen for an IPv4 address; never write past the original string

                var offset = 0;
                while ((offset = IndexOf(image, pattern, offset)) >= 0)
                {
                    RemoteMemory.Write(hProcess, IntPtr.Add(baseAddress, offset), replacement, "a logon hostname");
                    offset += pattern.Length;
                }
            }

            // The import table is read from the exe file, not from the live
            // image: the loader has already overwritten the live table with
            // resolved addresses. RVAs are the same in both.
            DnsResolverHook.Install(hProcess, baseAddress, exeFile, is64Bit, ipBytes, ip);

            if (redirectAllConnections)
                SocketConnectRedirect.Install(hProcess, baseAddress, exeFile, is64Bit, ipBytes);

            var matched = 0;
            var loginFlowPatches = is64Bit ? LoginFlowPatches.X64 : LoginFlowPatches.X86;
            foreach (var (_, pattern, replacement) in loginFlowPatches)
            {
                var offset = IndexOfWildcard(image, pattern, 0);
                if (offset < 0)
                    continue;

                RemoteMemory.Write(hProcess, IntPtr.Add(baseAddress, offset), replacement, "the login flow");
                matched++;
            }

            // None matching means this isn't the 5.4.8 (18414) client the
            // patches were written for; starting it anyway would only leave
            // the player at a login screen that can't connect.
            if (matched == 0)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exePath)} doesn't look like a Mists of Pandaria 5.4.8 (build 18414) client.");
        }
        finally
        {
            Native.CloseHandle(hProcess);
        }
    }

    private static (IntPtr BaseAddress, int Size) GetMainModule(int processId, string exeFileName)
    {
        // TH32CS_SNAPMODULE32 is needed alongside TH32CS_SNAPMODULE to list a
        // 32-bit process's modules from a 64-bit launcher. The list may not
        // be available in the first instant after the process starts, so
        // retry for a few seconds.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(50);

            var snapshot = Native.CreateToolhelp32Snapshot(
                Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, (uint)processId);
            if (snapshot == Native.InvalidHandleValue)
                continue;

            try
            {
                var entry = new Native.MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32>() };
                if (!Native.Module32First(snapshot, ref entry))
                    continue;

                do
                {
                    if (string.Equals(entry.szModule, exeFileName, StringComparison.OrdinalIgnoreCase))
                        return (entry.modBaseAddr, (int)entry.modBaseSize);
                }
                while (Native.Module32Next(snapshot, ref entry));
            }
            finally
            {
                Native.CloseHandle(snapshot);
            }
        }

        throw new InvalidOperationException("The game started but couldn't be patched (its modules never became readable).");
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        if (startIndex > haystack.Length - needle.Length)
            return -1;

        var index = haystack.AsSpan(startIndex).IndexOf(needle);
        return index < 0 ? -1 : startIndex + index;
    }

    // Like IndexOf, but a 0x00 byte in the pattern matches anything.
    private static int IndexOfWildcard(byte[] haystack, byte[] pattern, int startIndex)
    {
        for (var i = startIndex; i <= haystack.Length - pattern.Length; i++)
        {
            var match = true;

            for (var j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] != 0 && haystack[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }
}
