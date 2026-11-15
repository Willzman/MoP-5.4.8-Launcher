using System.ComponentModel;
using System.Text;

namespace MoPLauncher.Patching;

// Fixes the character creation screen on screens wider than 16:9.
//
// The client's GlueXML caps the glue UI at 16:9: GlueParent_OnLoad in
// GlueParent.lua shrinks GlueParent once and leaves black bars at the sides. The
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
//
// The client refuses to call a Lua C function that isn't inside its own .text
// section ("ERROR #134 ... Invalid function pointer"). So the table entry
// points at a short jump written into int3 padding inside .text, and that
// jump goes on to the stub in the allocated block.
//
// Wide menus (optional): SetGlueScreen calls SetCurrentScreen on every screen
// change, the first login screen included. Hooked the same way, it anchors
// GlueParent back to the whole screen, undoing GlueParent_OnLoad's 16:9 cap
// for every menu screen. Nothing in the glue applies the cap again, but a
// reloaded glue (after leaving the world) does, so the next screen change
// widens it again.
internal static class WideScreenGlueFix
{
    private const string MaskHookedFunction = "GetAvailableRaces";
    private const string WideHookedFunction = "SetCurrentScreen";
    private const string SourceName = "MoPLauncher";

    // Runs on every screen change. The flag lives on the frame, so a reloaded
    // glue, which recreates GlueParent with the cap, is widened again.
    private const string WideSnippet = """
        pcall(function()
          if not GlueParent or GlueParent.MoPLauncherWide then return end
          GlueParent.MoPLauncherWide = true
          GlueParent:ClearAllPoints()
          GlueParent:SetPoint("TOPLEFT", 0, 0)
          GlueParent:SetPoint("BOTTOMRIGHT", 0, 0)
        end)
        """;

    // Runs on every CharacterCreate_OnShow, so it must be cheap and must never
    // raise a Lua error (that would pop up the glue's error dialog).
    //
    // It also closes the right-hand border of the class panel and of the
    // customization column, once per glue load. Both are laid out to run off
    // the right edge (by 117 and 77 px, from CharacterCreate.xml), so their
    // right border is never seen. Nothing is moved: the top and bottom edge
    // pieces are cut to the left half of the visible width, and a mirrored
    // copy of that half (the art is symmetric) ends at the screen edge, with a
    // side edge under it. The customization column never had a right edge; the
    // new one sits over the last few pixels of its portraits.
    private const string MaskSnippet = """
        pcall(function()
          if not GlueParent or not CharacterCreate then return end
          if CreateFrame then
            if not MoPLauncherBorders then
              MoPLauncherBorders = true
              pcall(function()
                local art = "Interface\\Glues\\CharacterCreate\\UI-CharacterCreatePatchwerk"
                local left = 0.42382813
                local function close(f, holder, layer, cut, top, bottom, edge)
                  local half = (243 - cut) / 2
                  local mid = left + half / 512
                  local ends = {}
                  for i, p in ipairs({ { top, 0.68847656, 0.73535156, "TOP" }, { bottom, 0.73730469, 0.78222656, "BOTTOM" } }) do
                    local tex, t1, t2, side = p[1], p[2], p[3], p[4]
                    local height = tex:GetHeight()
                    tex:ClearAllPoints()
                    tex:SetPoint(side .. "LEFT", f, side .. "LEFT", 0, 0)
                    tex:SetWidth(half)
                    tex:SetTexCoord(left, mid, t1, t2)
                    local mirror = holder:CreateTexture(nil, layer)
                    mirror:SetTexture(art)
                    mirror:SetWidth(half)
                    mirror:SetHeight(height)
                    mirror:SetPoint(side .. "RIGHT", f, side .. "RIGHT", -cut, 0)
                    mirror:SetTexCoord(mid, left, t1, t2)
                    ends[i] = mirror
                  end
                  if not edge then
                    edge = holder:CreateTexture(nil, layer)
                    edge:SetTexture("Interface\\Glues\\CharacterCreate\\Frame-Sides")
                    edge:SetWidth(16)
                    edge:SetTexCoord(0, 0.5, 0, 1)
                    pcall(edge.SetDrawLayer, edge, layer, -1)
                  end
                  edge:ClearAllPoints()
                  edge:SetPoint("TOPRIGHT", ends[1], "BOTTOMRIGHT", 0, 10)
                  edge:SetPoint("BOTTOMRIGHT", ends[2], "TOPRIGHT", 0, -10)
                  if f.factionBg then
                    f.factionBg:SetPoint("BOTTOMRIGHT", f, "BOTTOMRIGHT", -8 - cut, 10)
                  end
                end

                if CharCreateClassFrameTopEdge and CharCreateClassFrameBottomEdge and CharCreateClassFrameRightEdge then
                  close(CharCreateClassFrame, CharCreateClassFrame, "ARTWORK", 117,
                    CharCreateClassFrameTopEdge, CharCreateClassFrameBottomEdge, CharCreateClassFrameRightEdge)
                end

                local border = CharCreatePreviewFrame and CharCreatePreviewFrame.border
                if border then
                  local top, bottom, extra = nil, nil, {}
                  for _, r in ipairs({ border:GetRegions() }) do
                    if r:GetObjectType() == "Texture" then
                      local w, h = math.floor(r:GetWidth() + 0.5), math.floor(r:GetHeight() + 0.5)
                      if w == 183 and h == 48 then top = r
                      elseif w == 183 and h == 46 then bottom = r
                      elseif w == 50 and (h == 48 or h == 46) then extra[#extra + 1] = r end
                    end
                  end
                  if top and bottom and #extra == 2 then
                    for _, r in ipairs(extra) do r:Hide() end
                    close(CharCreatePreviewFrame, border, "BORDER", 77, top, bottom, nil)
                  end
                end
              end)
            end
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
    // With `wideMenus` the menus also use the full width of the screen.
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] image, bool is64Bit, bool wideMenus)
    {
        var (pattern, callOffset) = is64Bit ? RunScriptTailX64 : RunScriptTailX86;
        var tail = FindUnique(image, pattern);
        if (tail < 0)
            throw new InvalidOperationException("Could not find the client's script runner.");
        var callEnd = tail + callOffset + 5;
        var execute = baseAddress.ToInt64() + callEnd + BitConverter.ToInt32(image, tail + callOffset + 1);

        // The masks stay on with wide menus: they then sit off-screen, and
        // they still cover the bars if the widening doesn't take.
        var caveSearchStart = 0;
        InstallHook(hProcess, baseAddress, image, is64Bit, execute, MaskHookedFunction, MaskSnippet, ref caveSearchStart);
        if (wideMenus)
            InstallHook(hProcess, baseAddress, image, is64Bit, execute, WideHookedFunction, WideSnippet, ref caveSearchStart);
    }

    // Points `functionName`'s registration entry at a jump in .text, which
    // goes to a stub that runs `snippet` and then the real function.
    private static void InstallHook(IntPtr hProcess, IntPtr baseAddress, byte[] image, bool is64Bit, long execute,
        string functionName, string snippet, ref int caveSearchStart)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var moduleBase = baseAddress.ToInt64();

        var entryOffset = FindRegistrationEntry(image, moduleBase, functionName, ptrSize);
        if (entryOffset < 0)
            throw new InvalidOperationException($"Could not find {functionName} in the client.");
        var original = ReadPointer(image, entryOffset + ptrSize, ptrSize);
        if (original < moduleBase || original >= moduleBase + image.Length)
            throw new InvalidOperationException($"{functionName} doesn't point into the client.");

        var code = Encoding.ASCII.GetBytes(snippet.Replace("\r\n", "\n") + '\0');
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

        // mov rax/eax, stub; jmp rax/eax
        byte[] jump = is64Bit
            ? [0x48, 0xB8, .. BitConverter.GetBytes(allocBase.ToInt64()), 0xFF, 0xE0]
            : [0xB8, .. BitConverter.GetBytes((uint)allocBase.ToInt64()), 0xFF, 0xE0];
        var cave = FindCodeCave(image, jump.Length, caveSearchStart);
        if (cave < 0)
            throw new InvalidOperationException("Could not find room for the wide screen fix in the client's code.");
        caveSearchStart = cave + ((jump.Length + 15) & ~15) + 16;
        RemoteMemory.WriteCode(hProcess, IntPtr.Add(baseAddress, cave), jump, "the wide screen fix jump");

        RemoteMemory.Write(hProcess, IntPtr.Add(baseAddress, entryOffset + ptrSize),
            RemoteMemory.PointerBytes(IntPtr.Add(baseAddress, cave), is64Bit), $"the client's {functionName} entry");
    }

    // Returns the offset of a 16-byte-aligned run of int3 padding inside the
    // image's first executable section (.text), with int3 on both sides, that
    // has room for `size` bytes, at or after `from`; or -1. The padding between
    // functions is never executed, so it can hold the jump.
    private static int FindCodeCave(byte[] image, int size, int from)
    {
        const uint ImageScnMemExecute = 0x20000000;
        var needed = (size + 15) & ~15;

        var pe = BitConverter.ToInt32(image, 0x3C);
        var sectionCount = BitConverter.ToUInt16(image, pe + 6);
        var sectionTable = pe + 24 + BitConverter.ToUInt16(image, pe + 20);

        for (var s = 0; s < sectionCount; s++)
        {
            var header = sectionTable + 40 * s;
            if ((BitConverter.ToUInt32(image, header + 36) & ImageScnMemExecute) == 0)
                continue;

            var start = (int)BitConverter.ToUInt32(image, header + 12);
            var end = Math.Min(start + (int)BitConverter.ToUInt32(image, header + 8), image.Length);

            for (var at = (Math.Max(start, from) + 16) & ~15; at + needed < end; at += 16)
            {
                var free = true;
                for (var i = -1; i < needed && free; i++)
                    free = image[at + i] == 0xCC;
                if (free)
                    return at;
            }

            return -1; // only .text is accepted by the client's check
        }

        return -1;
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
