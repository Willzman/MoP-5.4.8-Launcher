using System.ComponentModel;

namespace MoPLauncher.Patching;

// Small helpers over a process handle opened with VM read/write/operation.
internal static class RemoteMemory
{
    // Overwrites bytes that may sit in a read-only section: unprotect, write,
    // then put the original protection back.
    public static void Write(IntPtr hProcess, IntPtr address, byte[] data, string what)
    {
        if (!Native.VirtualProtectEx(hProcess, address, (UIntPtr)data.Length, Native.PAGE_READWRITE, out var oldProtect))
            throw new Win32Exception($"Could not unprotect {what}");

        try
        {
            if (!Native.WriteProcessMemory(hProcess, address, data, data.Length, out _))
                throw new Win32Exception($"Could not write {what}");
        }
        finally
        {
            Native.VirtualProtectEx(hProcess, address, (UIntPtr)data.Length, oldProtect, out _);
        }
    }

    // Allocates an executable block in the target and returns its address.
    public static IntPtr AllocateExecutable(IntPtr hProcess, int size, string what)
    {
        var address = Native.VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)size,
            Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
        if (address == IntPtr.Zero)
            throw new Win32Exception($"Could not allocate {what}");

        return address;
    }

    public static byte[] PointerBytes(IntPtr value, bool is64Bit) =>
        is64Bit ? BitConverter.GetBytes(value.ToInt64()) : BitConverter.GetBytes((uint)value.ToInt64());
}
