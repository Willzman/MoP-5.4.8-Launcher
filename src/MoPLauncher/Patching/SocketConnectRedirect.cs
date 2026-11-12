using System.ComponentModel;

namespace MoPLauncher.Patching;

// Redirects IPv4 socket connections after hostname resolution, by pointing
// the client's ws2_32.dll!connect import at a stub that rewrites the
// destination address and then jumps to the real connect. The client uses
// more than one resolver API, and the realm list can name an address the
// player can't reach; enforcing the destination at connect time keeps both
// the login and the world connection on the configured server.
//
// Ported from the Project SkyFire launcher (GPL-3.0).
internal static class SocketConnectRedirect
{
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] exeFile, bool is64Bit, byte[] ipBytes)
    {
        // connect is ordinal 4 in ws2_32.dll.
        var iatSlotRva = PeImportTable.FindIatSlotRva(exeFile, "ws2_32.dll", "connect", knownOrdinal: 4);
        if (iatSlotRva == 0)
            throw new InvalidOperationException("Could not find the connect import in the client.");

        var iatSlotAddress = IntPtr.Add(baseAddress, iatSlotRva);
        var pointerSize = is64Bit ? 8 : 4;
        var originalPointerBytes = new byte[pointerSize];
        if (!Native.ReadProcessMemory(hProcess, iatSlotAddress, originalPointerBytes, pointerSize, out _))
            throw new Win32Exception("Could not read the client's connect import");

        var originalConnect = is64Bit
            ? BitConverter.ToInt64(originalPointerBytes)
            : BitConverter.ToUInt32(originalPointerBytes);
        var shellcode = BuildShellcode(is64Bit, originalConnect, ipBytes);

        var shellcodeAddress = RemoteMemory.AllocateExecutable(hProcess, shellcode.Length, "the connection redirect");
        if (!Native.WriteProcessMemory(hProcess, shellcodeAddress, shellcode, shellcode.Length, out _))
            throw new Win32Exception("Could not write the connection redirect into client memory");

        RemoteMemory.Write(hProcess, iatSlotAddress,
            RemoteMemory.PointerBytes(shellcodeAddress, is64Bit), "the client's connect import");
    }

    private static byte[] BuildShellcode(bool is64Bit, long originalConnect, byte[] ipBytes)
    {
        if (is64Bit)
        {
            // sockaddr is in RDX. Replace sin_addr only for AF_INET, then tail
            // call the original connect with every argument otherwise intact.
            var shellcode = new byte[]
            {
                0x66, 0x83, 0x3A, 0x02,                   // cmp word ptr [rdx], AF_INET
                0x75, 0x07,                               // jne call_original
                0xC7, 0x42, 0x04, 0, 0, 0, 0,             // mov dword ptr [rdx+4], target
                0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,       // mov rax, original connect
                0xFF, 0xE0                                // jmp rax
            };
            Array.Copy(ipBytes, 0, shellcode, 9, 4);
            BitConverter.GetBytes(originalConnect).CopyTo(shellcode, 15);
            return shellcode;
        }

        // sockaddr is the second stack argument on x86.
        var x86Shellcode = new byte[]
        {
            0x8B, 0x44, 0x24, 0x08,                       // mov eax, [esp+8]
            0x66, 0x83, 0x38, 0x02,                       // cmp word ptr [eax], AF_INET
            0x75, 0x07,                                   // jne call_original
            0xC7, 0x40, 0x04, 0, 0, 0, 0,                 // mov dword ptr [eax+4], target
            0xB8, 0, 0, 0, 0,                             // mov eax, original connect
            0xFF, 0xE0                                    // jmp eax
        };
        Array.Copy(ipBytes, 0, x86Shellcode, 13, 4);
        BitConverter.GetBytes((uint)originalConnect).CopyTo(x86Shellcode, 18);
        return x86Shellcode;
    }
}
