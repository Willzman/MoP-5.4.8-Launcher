using System.ComponentModel;
using System.Text;

namespace MoPLauncher.Patching;

// The client builds its logon hostname at runtime, so there's no static string
// to find-and-replace for it. Instead, this hooks the client's import of
// ws2_32.dll!gethostbyname so that ANY hostname it asks to resolve comes
// back as the target address - sidestepping the string entirely.
//
// Mechanism: allocate a small executable block in the target process holding
// a few bytes of shellcode plus a fake `hostent` structure describing the
// target address, then overwrite the client's Import Address Table slot for
// gethostbyname to point at that shellcode instead of the real function.
//
// Ported from the Project SkyFire launcher (GPL-3.0).
internal static class DnsResolverHook
{
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] exeFile, bool is64Bit, byte[] ipBytes, string name)
    {
        // gethostbyname is ordinal 52 in ws2_32.dll, and the client imports
        // it by ordinal rather than by name.
        var iatSlotRva = PeImportTable.FindIatSlotRva(exeFile, "ws2_32.dll", "gethostbyname", knownOrdinal: 52);
        if (iatSlotRva == 0)
            throw new InvalidOperationException("Could not find the gethostbyname import in the client.");

        var block = BuildBlock(is64Bit, ipBytes, name);
        var allocBase = RemoteMemory.AllocateExecutable(hProcess, block.Length, "the resolver hook");
        PatchBlockPointers(block, allocBase, is64Bit);

        if (!Native.WriteProcessMemory(hProcess, allocBase, block, block.Length, out _))
            throw new Win32Exception("Could not write the resolver hook into client memory");

        // The shellcode sits at the very start of the block.
        RemoteMemory.Write(hProcess, IntPtr.Add(baseAddress, iatSlotRva),
            RemoteMemory.PointerBytes(allocBase, is64Bit), "the client's DNS resolver import");
    }

    // Layout: [shellcode][hostent][addrList: 2 pointers][ip bytes: 4][name string]
    private static (int Hostent, int AddrList, int Ip, int Name) Layout(bool is64Bit)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var hostentSize = is64Bit ? 32 : 16;
        var shellcodeSize = is64Bit ? 16 : 8;

        var hostent = Align(shellcodeSize, ptrSize);
        var addrList = hostent + hostentSize;
        var ip = addrList + 2 * ptrSize;
        return (hostent, addrList, ip, ip + 4);
    }

    private static byte[] BuildBlock(bool is64Bit, byte[] ipBytes, string name)
    {
        var layout = Layout(is64Bit);
        var nameBytes = Encoding.ASCII.GetBytes(name + '\0');

        var block = new byte[layout.Name + nameBytes.Length];
        Array.Copy(ipBytes, 0, block, layout.Ip, 4);
        Array.Copy(nameBytes, 0, block, layout.Name, nameBytes.Length);
        return block;
    }

    private static void PatchBlockPointers(byte[] block, IntPtr allocBase, bool is64Bit)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var layout = Layout(is64Bit);

        long Abs(int offset) => is64Bit ? allocBase.ToInt64() + offset : (uint)(allocBase.ToInt64() + offset);

        // struct hostent { char *h_name; char **h_aliases; short h_addrtype;
        //                  short h_length; char **h_addr_list; };
        // (x64 pads h_addr_list to 8-byte alignment after the two shorts)
        WritePointer(block, layout.Hostent + 0 * ptrSize, Abs(layout.Name), is64Bit);   // h_name
        WritePointer(block, layout.Hostent + 1 * ptrSize, 0, is64Bit);                  // h_aliases = NULL
        WriteInt16(block, layout.Hostent + 2 * ptrSize, 2);                             // h_addrtype = AF_INET
        WriteInt16(block, layout.Hostent + 2 * ptrSize + 2, 4);                         // h_length = 4
        var addrListField = is64Bit ? layout.Hostent + 24 : layout.Hostent + 12;
        WritePointer(block, addrListField, Abs(layout.AddrList), is64Bit);              // h_addr_list

        // addrList[0] -> ip bytes, addrList[1] = NULL terminator
        WritePointer(block, layout.AddrList, Abs(layout.Ip), is64Bit);
        WritePointer(block, layout.AddrList + ptrSize, 0, is64Bit);

        // Shellcode: ignore whatever hostname was asked for, always return a
        // pointer to our fake hostent.
        //   x64 (arg in rcx, return in rax):
        //     48 B8 <imm64>   mov rax, imm64
        //     C3              ret
        //   x86 (stdcall, one 4-byte stack arg, callee cleans up):
        //     B8 <imm32>      mov eax, imm32
        //     C2 04 00        ret 4
        if (is64Bit)
        {
            block[0] = 0x48;
            block[1] = 0xB8;
            BitConverter.GetBytes(Abs(layout.Hostent)).CopyTo(block, 2);
            block[10] = 0xC3;
        }
        else
        {
            block[0] = 0xB8;
            BitConverter.GetBytes((uint)Abs(layout.Hostent)).CopyTo(block, 1);
            block[5] = 0xC2;
            block[6] = 0x04;
            block[7] = 0x00;
        }
    }

    private static void WritePointer(byte[] block, int offset, long value, bool is64Bit)
    {
        if (is64Bit)
            BitConverter.GetBytes(value).CopyTo(block, offset);
        else
            BitConverter.GetBytes((uint)value).CopyTo(block, offset);
    }

    private static void WriteInt16(byte[] block, int offset, short value) =>
        BitConverter.GetBytes(value).CopyTo(block, offset);

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
