using System.ComponentModel;
using System.Text;

namespace MoPLauncher.Patching;

// Fixes the character creation screen on screens wider than 16:9.
//
// The client's GlueXML caps the glue UI at 16:9: GlueParent_OnResize in
// GlueParent.lua shrinks GlueParent and leaves black bars at the sides. The
// class panel (CharCreateClassFrame) is anchored 117 px past GlueParent's
// right edge, and the customization preview column (CharCreatePreviewFrame)
// 77 px past it, so that at 16:9 their right parts are off-screen. On a wider
// screen those parts land in the right-hand black bar instead.
//
// GlueXML is read from the game's archives, so it can't be edited in memory
// before it loads. Instead this runs a small Lua snippet inside the glue UI
// each time the character creation screen opens. It covers both bars with
// black on that screen, which makes it look as it does at 16:9.
//
// How the snippet runs: the glue registers its Lua functions from a static
// table of { name, function } pairs. CharacterCreate_OnShow calls
// GetAvailableRaces, so that table entry is pointed at a stub that first
// calls the client's own script runner (the function the glue's RunScript
// calls) on the snippet, then jumps to the real GetAvailableRaces. The stub
// runs on the game's main thread, inside a Lua call, the same way RunScript
// does.
internal static class WideScreenGlueFix
{
    private const string HookedFunction = "GetAvailableRaces";
    private const string SourceName = "MoPLauncher";

    // Runs on every CharacterCreate_OnShow, so it must be cheap and must never
    // raise a Lua error (that would pop up the glue's error dialog).
    private const string Snippet = """
        pcall(function()
          if not GlueParent or not CharacterCreate then return end
          if CreateFrame then
            if MoPLauncherWideMask then return end
            MoPLauncherWideMask = true
            for _, side in ipairs({ "LEFT", "RIGHT" }) do
              local inner = side == "LEFT" and "RIGHT" or "LEFT"
              local mask = CreateFrame("Frame", nil, CharacterCreate)
              mask:SetFrameStrata("FULLSCREEN")
              mask:SetWidth(4096)
              mask:SetPoint("TOP" .. inner, GlueParent, "TOP" .. side)
              mask:SetPoint("BOTTOM" .. inner, GlueParent, "BOTTOM" .. side)
              local black = mask:CreateTexture(nil, "BACKGROUND")
              black:SetAllPoints(mask)
              black:SetTexture(0, 0, 0, 1)
            end
          elseif (GlueParent:GetLeft() or 0) > 1 then
            CharCreateClassFrame:ClearAllPoints()
            CharCreateClassFrame:SetPoint("TOPRIGHT", CharacterCreateFrame, "TOPRIGHT", 0, -3)
            CharCreateClassFrame:SetPoint("BOTTOMRIGHT", CharacterCreateFrame, "BOTTOMRIGHT", 0, 3)
            CharCreatePreviewFrame:ClearAllPoints()
            CharCreatePreviewFrame:SetPoint("RIGHT", CharacterCreateFrame, "RIGHT", 0, 0)
          end
        end)
        """;

    // The tail of the glue's RunScript: FrameScript_Execute(text, text, 0),
    // then return 0. The call's rel32 is at CallOffset.
    //   x64: xor r8d,r8d; mov rdx,rax; mov rcx,rax; call rel32; xor eax,eax; add rsp,20h; pop rbx; ret
    //   x86: push 0; push eax; push eax; call rel32; add esp,0Ch; xor eax,eax; pop ebp; ret
    private static readonly (int?[] Pattern, int CallOffset) RunScriptTailX64 =
        ([0x45, 0x33, 0xC0, 0x48, 0x8B, 0xD0, 0x48, 0x8B, 0xC8, 0xE8, null, null, null, null,
          0x33, 0xC0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3], 9);

    private static readonly (int?[] Pattern, int CallOffset) RunScriptTailX86 =
        ([0x6A, 0x00, 0x50, 0x50, 0xE8, null, null, null, null, 0x83, 0xC4, 0x0C, 0x33, 0xC0, 0x5D, 0xC3], 4);

    // `image` is the client's main module as read from its memory, so the
    // pointers in it are already relocated to where the module is loaded.
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] image, bool is64Bit)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var moduleBase = baseAddress.ToInt64();

        var entryOffset = FindRegistrationEntry(image, moduleBase, HookedFunction, ptrSize);
        if (entryOffset < 0)
            throw new InvalidOperationException($"Could not find {HookedFunction} in the client.");
        var original = ReadPointer(image, entryOffset + ptrSize, ptrSize);
        if (original < moduleBase || original >= moduleBase + image.Length)
            throw new InvalidOperationException($"{HookedFunction} doesn't point into the client.");

        var (pattern, callOffset) = is64Bit ? RunScriptTailX64 : RunScriptTailX86;
        var tail = FindUnique(image, pattern);
        if (tail < 0)
            throw new InvalidOperationException("Could not find the client's script runner.");
        var callEnd = tail + callOffset + 5;
        var execute = moduleBase + callEnd + BitConverter.ToInt32(image, tail + callOffset + 1);

        var code = Encoding.ASCII.GetBytes(Snippet.Replace("\r\n", "\n") + '\0');
        var name = Encoding.ASCII.GetBytes(SourceName + '\0');
        var stubSize = is64Bit ? 64 : 32;
        var block = new byte[stubSize + name.Length + code.Length];
        var allocBase = RemoteMemory.AllocateExecutable(hProcess, block.Length, "the wide screen fix");
        var nameAddress = allocBase.ToInt64() + stubSize;
        var codeAddress = nameAddress + name.Length;

        var stub = is64Bit
            ? StubX64(codeAddress, nameAddress, execute, original)
            : StubX86((uint)codeAddress, (uint)nameAddress, (uint)execute, (uint)original);
        stub.CopyTo(block, 0);
        name.CopyTo(block, stubSize);
        code.CopyTo(block, stubSize + name.Length);

        if (!Native.WriteProcessMemory(hProcess, allocBase, block, block.Length, out _))
            throw new Win32Exception("Could not write the wide screen fix into client memory");

        RemoteMemory.Write(hProcess, IntPtr.Add(baseAddress, entryOffset + ptrSize),
            RemoteMemory.PointerBytes(allocBase, is64Bit), $"the client's {HookedFunction} entry");
    }

    // Lua C functions take the lua_State in rcx. It's kept across the call
    // and handed on to the real function untouched.
    private static byte[] StubX64(long code, long name, long execute, long original)
    {
        var s = new List<byte>();
        s.Add(0x51);                                   // push rcx (also aligns rsp to 16)
        s.AddRange([0x48, 0x83, 0xEC, 0x20]);          // sub rsp, 20h (shadow space)
        s.AddRange([0x48, 0xB9]); s.AddRange(BitConverter.GetBytes(code));      // mov rcx, code
        s.AddRange([0x48, 0xBA]); s.AddRange(BitConverter.GetBytes(name));      // mov rdx, name
        s.AddRange([0x45, 0x33, 0xC0]);                // xor r8d, r8d
        s.AddRange([0x48, 0xB8]); s.AddRange(BitConverter.GetBytes(execute));   // mov rax, execute
        s.AddRange([0xFF, 0xD0]);                      // call rax
        s.AddRange([0x48, 0x83, 0xC4, 0x20]);          // add rsp, 20h
        s.Add(0x59);                                   // pop rcx
        s.AddRange([0x48, 0xB8]); s.AddRange(BitConverter.GetBytes(original));  // mov rax, original
        s.AddRange([0xFF, 0xE0]);                      // jmp rax
        return [.. s];
    }

    // cdecl: the lua_State stays at [esp+4] for the real function.
    private static byte[] StubX86(uint code, uint name, uint execute, uint original)
    {
        var s = new List<byte>();
        s.AddRange([0x6A, 0x00]);                                              // push 0
        s.Add(0x68); s.AddRange(BitConverter.GetBytes(name));                  // push name
        s.Add(0x68); s.AddRange(BitConverter.GetBytes(code));                  // push code
        s.Add(0xB8); s.AddRange(BitConverter.GetBytes(execute));               // mov eax, execute
        s.AddRange([0xFF, 0xD0]);                                              // call eax
        s.AddRange([0x83, 0xC4, 0x0C]);                                        // add esp, 0Ch
        s.Add(0xB8); s.AddRange(BitConverter.GetBytes(original));              // mov eax, original
        s.AddRange([0xFF, 0xE0]);                                              // jmp eax
        return [.. s];
    }

    // Finds the { name, function } entry whose name is `functionName` and
    // returns its offset in the image, or -1.
    private static int FindRegistrationEntry(byte[] image, long moduleBase, string functionName, int ptrSize)
    {
        var needle = Encoding.ASCII.GetBytes(functionName + '\0');
        var found = -1;

        for (var at = 0; (at = IndexOf(image, needle, at)) >= 0; at += needle.Length)
        {
            if (at == 0 || image[at - 1] != 0)
                continue; // the tail of a longer name

            var pointer = moduleBase + at;
            for (var i = 0; i + 2 * ptrSize <= image.Length; i += ptrSize)
            {
                if (ReadPointer(image, i, ptrSize) != pointer)
                    continue;
                if (found >= 0)
                    return -1; // more than one: not the table this was written for
                found = i;
            }
        }

        return found;
    }

    private static long ReadPointer(byte[] image, int offset, int ptrSize) =>
        ptrSize == 8 ? BitConverter.ToInt64(image, offset) : BitConverter.ToUInt32(image, offset);

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        if (startIndex > haystack.Length - needle.Length)
            return -1;

        var index = haystack.AsSpan(startIndex).IndexOf(needle);
        return index < 0 ? -1 : startIndex + index;
    }

    // Returns the only offset where `pattern` matches (null = any byte), or -1
    // when it matches nowhere or more than once.
    private static int FindUnique(byte[] image, int?[] pattern)
    {
        var found = -1;
        var first = (byte)pattern[0]!.Value;

        for (var i = 0; (i = IndexOf(image, [first], i)) >= 0 && i <= image.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 1; j < pattern.Length && match; j++)
                match = pattern[j] is not { } b || image[i + j] == b;

            if (!match)
                continue;
            if (found >= 0)
                return -1;
            found = i;
        }

        return found;
    }
}
