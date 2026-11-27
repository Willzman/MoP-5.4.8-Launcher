-- Error Catcher: keeps every distinct Lua error the UI hits, with its stack
-- and how many times it happened, in ErrorCatcherDB
-- (WTF\Account\<ACCOUNT>\SavedVariables\!ErrorCatcher.lua). After the game
-- closes, the launcher sends new ones to the realm's developers. The "!"
-- makes it load before other add-ons, so their loading errors are caught too.
--
-- Errors are grouped by file, line and message: the same error hit every
-- frame is one entry with a big count. Memory addresses in messages
-- ("table: 0x1a2b3c") are blanked first so they don't split a group.
--
-- It wraps the game's own error handler and calls it after, so the error
-- frame (with "Display Lua Errors" on) works as before. Blocked actions
-- ("Interface action failed because of an AddOn") are kept too.
--
-- Installed and removed by the MoP 5.4.8 Launcher.

local MAX_ERRORS = 200
local MAX_MESSAGE = 1000
local MAX_STACK = 4000

local db

local function Key(file, line, text)
    return (file or "?") .. ":" .. (line or "?") .. ": " .. text
end

-- "Interface\AddOns\Foo\Foo.lua:12: attempt to ..." -> file, line, the rest.
local function Split(message)
    local file, line, text = message:match("^(.-):(%d+): (.*)$")
    if not file then
        return nil, nil, message
    end
    return file, tonumber(line), text
end

local function Prune()
    local count = 0
    for _ in pairs(db.errors) do
        count = count + 1
    end
    -- Drops the ones seen longest ago.
    while count > MAX_ERRORS do
        local oldestKey, oldest
        for key, e in pairs(db.errors) do
            if not oldest or e.last < oldest then
                oldestKey, oldest = key, e.last
            end
        end
        db.errors[oldestKey] = nil
        count = count - 1
    end
end

local function Record(kind, message, stack)
    if not db then
        return
    end
    message = tostring(message or ""):sub(1, MAX_MESSAGE)
    local file, line, text = Split(message)
    text = text:gsub("0x%x+", "0x?")
    local key = Key(file, line, text)

    local now = time()
    local e = db.errors[key]
    if e then
        e.count = e.count + 1
        e.last = now
        e.session = db.session
        return
    end

    local version, build = GetBuildInfo()
    db.errors[key] = {
        kind = kind,
        message = message,
        file = file,
        line = line,
        stack = stack and stack:sub(1, MAX_STACK) or "",
        count = 1,
        first = now,
        last = now,
        session = db.session,
        build = version .. " " .. build,
    }
    Prune()
end

-- Errors before ADDON_LOADED (other add-ons loading first) wait here.
local early = {}

local original = geterrorhandler()
seterrorhandler(function(message, ...)
    local stack = debugstack(2)
    pcall(function()
        if db then
            Record("error", message, stack)
        elseif #early < 50 then
            early[#early + 1] = { message, stack }
        end
    end)
    if original then
        return original(message, ...)
    end
    return message
end)

SLASH_ERRORCATCHER1 = "/errorcatcher"
SlashCmdList["ERRORCATCHER"] = function(args)
    local count, total = 0, 0
    for _, e in pairs(db.errors) do
        count = count + 1
        total = total + e.count
    end
    if strlower(strtrim(args or "")) == "clear" then
        wipe(db.errors)
        DEFAULT_CHAT_FRAME:AddMessage("|cff33ccffError Catcher:|r cleared.")
    else
        DEFAULT_CHAT_FRAME:AddMessage(format("|cff33ccffError Catcher:|r %d different errors kept (%d in all). /errorcatcher clear empties the list.", count, total))
    end
end

local events = CreateFrame("Frame")
events:RegisterEvent("ADDON_LOADED")
events:RegisterEvent("ADDON_ACTION_BLOCKED")
events:RegisterEvent("ADDON_ACTION_FORBIDDEN")
events:SetScript("OnEvent", function(self, event, arg1, arg2)
    if event == "ADDON_LOADED" then
        if arg1 ~= "!ErrorCatcher" then
            return
        end
        ErrorCatcherDB = ErrorCatcherDB or {}
        db = ErrorCatcherDB
        db.version = 1
        db.errors = db.errors or {}
        -- Counts game sessions, so the launcher can tell what's new.
        db.session = (db.session or 0) + 1
        for _, e in ipairs(early) do
            Record("error", e[1], e[2])
        end
        early = nil
    else
        -- arg1: the add-on, arg2: the protected function it called.
        local kind = event == "ADDON_ACTION_FORBIDDEN" and "forbidden" or "blocked"
        Record(kind, format("%s tried to call %s", tostring(arg1), tostring(arg2)), debugstack(2))
    end
end)
