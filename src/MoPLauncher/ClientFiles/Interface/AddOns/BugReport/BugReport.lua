-- Bug Report: a bug button on the minimap's edge (drag it around the edge;
-- /bug if it's hidden or lost) opens a "Report Bug" window with a category and
-- a description. The add-on fills in the rest: zone and subzone, map
-- coordinates, target, class, level and spec, the quests in the quest log and
-- the client build.
--
-- On Submit the window hides, a screenshot is taken (the launcher uploads it
-- with the report) and the report goes to the server, whispered to yourself
-- with the addon prefix SFBUG. An addon message carries at most 255 bytes, so
-- the report is cut into pieces the server joins back together. The server
-- adds what it knows itself (account, character, map, exact position, time),
-- saves the report and answers with its number, shown as BUG-<n>.
--
-- The server also gives each account an upload token, kept in BugReportDB
-- (WTF\Account\<ACCOUNT>\SavedVariables\BugReport.lua). The launcher reads it
-- from there to upload screenshots and crash logs for that account.
--
-- Installed and removed by the MoP 5.4.8 Launcher.

local PREFIX = "SFBUG"
local VERSION = "1.0.0"

-- Bytes of report per message: 255, less room for "R:<nonce>:<nn>:<nn>:".
local PIECE_BYTES = 238
local MAX_PIECES = 32
local MIN_LENGTH = 10
local MAX_LETTERS = 1000
local ANSWER_TIMEOUT = 20

local CATEGORIES = {
    { key = "quest",    text = "Quest" },
    { key = "creature", text = "Creature / NPC" },
    { key = "spell",    text = "Spell / Ability" },
    { key = "item",     text = "Item" },
    { key = "zone",     text = "Zone / Map" },
    { key = "other",    text = "Other" },
}

local serverOn = nil  -- nil until the server answers PING
local frame, button
local category = "other"
local sending                -- the report being sent, waiting for its answer

local function Print(text)
    DEFAULT_CHAT_FRAME:AddMessage("|cff33ccffBug Report:|r " .. text)
end

local function Send(message)
    SendAddonMessage(PREFIX, message, "WHISPER", UnitName("player"))
end

-- Values go in "key=value;" fields: no control characters, and "\" and ";"
-- escaped so the server can split them back.
local function Escape(value)
    value = tostring(value or "")
    value = value:gsub("\r\n", "\n"):gsub("\r", "\n")
    value = value:gsub("\\", "\\\\"):gsub(";", "\\;"):gsub("\n", "\\n")
    return (value:gsub("%c", " "))
end

local function CategoryText(key)
    for _, c in ipairs(CATEGORIES) do
        if c.key == key then
            return c.text
        end
    end
    return key
end

------------------------------------------------------------------------------
-- What the add-on fills in for the player
------------------------------------------------------------------------------

local function Coordinates()
    -- SetMapToCurrentZone would move the world map the player is looking at.
    if not (WorldMapFrame and WorldMapFrame:IsShown()) then
        SetMapToCurrentZone()
    end
    local x, y = GetPlayerMapPosition("player")
    if not x or (x == 0 and y == 0) then
        return ""
    end
    return format("%.1f, %.1f", x * 100, y * 100)
end

local function Quests()
    local ids = {}
    for i = 1, GetNumQuestLogEntries() do
        local _, _, _, _, isHeader, _, isComplete, _, questID = GetQuestLogTitle(i)
        if not isHeader and questID and questID > 0 then
            -- c: ready to turn in, f: failed.
            local state = (isComplete == 1 and "c") or (isComplete == -1 and "f") or ""
            ids[#ids + 1] = questID .. state
        end
    end
    return table.concat(ids, ",")
end

local function Context()
    local c = {}
    c[#c + 1] = { "zone", GetRealZoneText() }
    c[#c + 1] = { "subzone", GetSubZoneText() }
    c[#c + 1] = { "coords", Coordinates() }

    if UnitExists("target") then
        c[#c + 1] = { "target", UnitName("target") }
        c[#c + 1] = { "targetguid", UnitGUID("target") }
    end

    local _, classToken = UnitClass("player")
    c[#c + 1] = { "class", classToken }
    c[#c + 1] = { "level", UnitLevel("player") }
    local spec = GetSpecialization()
    if spec then
        c[#c + 1] = { "spec", select(2, GetSpecializationInfo(spec)) }
    end

    c[#c + 1] = { "quests", Quests() }
    local version, build = GetBuildInfo()
    c[#c + 1] = { "build", version .. " " .. build }
    c[#c + 1] = { "addon", VERSION }
    return c
end

------------------------------------------------------------------------------
-- Sending
------------------------------------------------------------------------------

-- Cuts the report into pieces of at most PIECE_BYTES, never inside a UTF-8
-- character (the client won't send a broken one).
local function Pieces(payload)
    local pieces = {}
    local start = 1
    while start <= #payload do
        local stop = math.min(start + PIECE_BYTES - 1, #payload)
        while stop < #payload and stop > start do
            local nextByte = payload:byte(stop + 1)
            if nextByte < 0x80 or nextByte >= 0xC0 then
                break
            end
            stop = stop - 1
        end
        pieces[#pieces + 1] = payload:sub(start, stop)
        start = stop + 1
    end
    return pieces
end

local sender = CreateFrame("Frame")
sender:Hide()
sender:SetScript("OnUpdate", function(self, elapsed)
    if not sending then
        self:Hide()
        return
    end

    -- First, a frame or two for the window to be gone, then the screenshot.
    if not sending.shot then
        sending.wait = sending.wait - elapsed
        if sending.wait > 0 then
            return
        end
        Screenshot()
        sending.shot = true
        return
    end

    -- A few pieces a frame.
    for _ = 1, 4 do
        local i = sending.next
        if i > #sending.pieces then
            break
        end
        Send(format("R:%s:%d:%d:%s", sending.nonce, i, #sending.pieces, sending.pieces[i]))
        sending.next = i + 1
    end

    sending.timeout = sending.timeout - elapsed
    if sending.timeout <= 0 then
        Print("the server didn't answer, so your report may not have been saved. Please try again.")
        sending = nil
        self:Hide()
    end
end)

local function Submit(text)
    local fields = { "cat=" .. Escape(category), "desc=" .. Escape(text) }
    for _, kv in ipairs(Context()) do
        fields[#fields + 1] = kv[1] .. "=" .. Escape(kv[2])
    end

    local pieces = Pieces(table.concat(fields, ";"))
    if #pieces > MAX_PIECES then
        Print("that report is too long to send. Please shorten the description.")
        return false
    end

    sending = {
        nonce = format("%04x%04x", math.random(0, 0xFFFF), math.random(0, 0xFFFF)),
        pieces = pieces,
        next = 1,
        wait = 0.1,
        timeout = ANSWER_TIMEOUT,
        category = category,
    }
    sender:Show()
    return true
end

local function Remember(id)
    BugReportDB.reports = BugReportDB.reports or {}
    table.insert(BugReportDB.reports, 1, {
        id = id,
        time = time(),
        category = sending and sending.category or "other",
        character = UnitName("player"),
    })
    -- The newest 50 are enough for the launcher to match screenshots.
    while #BugReportDB.reports > 50 do
        table.remove(BugReportDB.reports)
    end
end

------------------------------------------------------------------------------
-- The "Report Bug" window
------------------------------------------------------------------------------

local function UpdateSubmit()
    if not frame then
        return
    end
    local length = strlenutf8 and strlenutf8(frame.edit:GetText()) or #frame.edit:GetText()
    local ready = length >= MIN_LENGTH and serverOn ~= false and not sending
    if ready then
        frame.submit:Enable()
    else
        frame.submit:Disable()
    end
    frame.count:SetText(format("%d / %d", length, MAX_LETTERS))

    if serverOn == false then
        frame.status:SetText("|cffff6060This realm isn't taking bug reports right now.|r")
    elseif sending then
        frame.status:SetText("Sending your last report...")
    elseif length < MIN_LENGTH then
        frame.status:SetText(format("Describe the bug in at least %d characters.", MIN_LENGTH))
    else
        frame.status:SetText("")
    end
end

local function ShowContext()
    local lines = {}
    for _, kv in ipairs(Context()) do
        local key, value = kv[1], tostring(kv[2] or "")
        if value ~= "" and key ~= "quests" and key ~= "addon" and key ~= "targetguid" then
            lines[#lines + 1] = "|cffffd100" .. key .. ":|r " .. value
        end
    end
    frame.context:SetText(table.concat(lines, "   "))
end

local function CreateWindow()
    frame = CreateFrame("Frame", "BugReportFrame", UIParent, "BasicFrameTemplateWithInset")
    frame:SetSize(420, 340)
    frame:SetPoint("CENTER")
    frame:SetFrameStrata("DIALOG")
    frame:SetMovable(true)
    frame:EnableMouse(true)
    frame:RegisterForDrag("LeftButton")
    frame:SetScript("OnDragStart", frame.StartMoving)
    frame:SetScript("OnDragStop", frame.StopMovingOrSizing)
    frame:SetClampedToScreen(true)
    frame:Hide()
    frame.TitleText:SetText("Report Bug")
    tinsert(UISpecialFrames, "BugReportFrame")  -- Escape closes it

    local label = frame:CreateFontString(nil, "OVERLAY", "GameFontNormal")
    label:SetPoint("TOPLEFT", 16, -36)
    label:SetText("Category")

    local dropDown = CreateFrame("Frame", "BugReportCategoryDropDown", frame, "UIDropDownMenuTemplate")
    dropDown:SetPoint("LEFT", label, "RIGHT", -6, -3)
    UIDropDownMenu_SetWidth(dropDown, 150)
    UIDropDownMenu_Initialize(dropDown, function()
        for _, c in ipairs(CATEGORIES) do
            local info = UIDropDownMenu_CreateInfo()
            info.text = c.text
            info.value = c.key
            info.checked = (c.key == category)
            info.func = function(self)
                category = self.value
                UIDropDownMenu_SetSelectedValue(dropDown, self.value)
                UIDropDownMenu_SetText(dropDown, CategoryText(self.value))
            end
            UIDropDownMenu_AddButton(info)
        end
    end)
    frame.dropDown = dropDown

    local describe = frame:CreateFontString(nil, "OVERLAY", "GameFontNormal")
    describe:SetPoint("TOPLEFT", 16, -70)
    describe:SetText("What happened, and what did you expect?")

    local box = CreateFrame("Frame", nil, frame)
    box:SetPoint("TOPLEFT", 14, -88)
    box:SetPoint("BOTTOMRIGHT", -14, 96)
    box:SetBackdrop({
        bgFile = "Interface\\Tooltips\\UI-Tooltip-Background",
        edgeFile = "Interface\\Tooltips\\UI-Tooltip-Border",
        tile = true, tileSize = 16, edgeSize = 16,
        insets = { left = 4, right = 4, top = 4, bottom = 4 },
    })
    box:SetBackdropColor(0, 0, 0, 0.8)

    local scroll = CreateFrame("ScrollFrame", "BugReportScrollFrame", box, "UIPanelScrollFrameTemplate")
    scroll:SetPoint("TOPLEFT", 8, -8)
    scroll:SetPoint("BOTTOMRIGHT", -28, 8)

    local edit = CreateFrame("EditBox", "BugReportEditBox", scroll)
    edit:SetMultiLine(true)
    edit:SetMaxLetters(MAX_LETTERS)
    edit:SetAutoFocus(false)
    edit:SetFontObject(ChatFontNormal)
    edit:SetWidth(340)
    edit:SetScript("OnEscapePressed", function() frame:Hide() end)
    edit:SetScript("OnTextChanged", function(self)
        -- Scrolls to the cursor; needs a cursor position, which comes with the first OnCursorChanged.
        if self.cursorOffset then
            ScrollingEdit_OnTextChanged(self, scroll)
        end
        UpdateSubmit()
    end)
    edit:SetScript("OnCursorChanged", ScrollingEdit_OnCursorChanged)
    edit:SetScript("OnUpdate", function(self, elapsed) ScrollingEdit_OnUpdate(self, elapsed, scroll) end)
    scroll:SetScrollChild(edit)
    box:EnableMouse(true)
    box:SetScript("OnMouseDown", function() edit:SetFocus() end)
    frame.edit = edit

    frame.count = frame:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
    frame.count:SetPoint("TOPRIGHT", box, "BOTTOMRIGHT", -4, -2)

    frame.context = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    frame.context:SetPoint("TOPLEFT", box, "BOTTOMLEFT", 4, -16)
    frame.context:SetPoint("RIGHT", frame, "RIGHT", -18, 0)
    frame.context:SetJustifyH("LEFT")
    frame.context:SetHeight(28)

    frame.status = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
    frame.status:SetPoint("BOTTOMLEFT", 18, 42)
    frame.status:SetPoint("RIGHT", frame, "RIGHT", -18, 0)
    frame.status:SetJustifyH("LEFT")

    local cancel = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
    cancel:SetSize(96, 22)
    cancel:SetPoint("BOTTOMRIGHT", -12, 12)
    cancel:SetText("Cancel")
    cancel:SetScript("OnClick", function() frame:Hide() end)

    frame.submit = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
    frame.submit:SetSize(96, 22)
    frame.submit:SetPoint("RIGHT", cancel, "LEFT", -6, 0)
    frame.submit:SetText("Submit")
    frame.submit:SetScript("OnClick", function()
        if Submit(frame.edit:GetText()) then
            frame.edit:SetText("")
            frame:Hide()
        end
    end)

    frame:SetScript("OnShow", function()
        UIDropDownMenu_SetSelectedValue(dropDown, category)
        UIDropDownMenu_SetText(dropDown, CategoryText(category))
        ShowContext()
        UpdateSubmit()
        edit:SetFocus()
        if serverOn == nil then
            Send("PING")
        end
    end)
    frame:SetScript("OnHide", function() edit:ClearFocus() end)
end

local function Toggle()
    if not frame then
        CreateWindow()
    end
    if frame:IsShown() then
        frame:Hide()
    else
        frame:Show()
    end
end

------------------------------------------------------------------------------
-- The minimap button: self-contained, no libraries
------------------------------------------------------------------------------

local function PlaceButton()
    local angle = math.rad(BugReportCharDB.minimapAngle or 200)
    local x, y = math.cos(angle), math.sin(angle)
    local radius = Minimap:GetWidth() / 2 + 8

    -- On a square minimap (some UI add-ons) keep to its edge instead of a circle.
    if GetMinimapShape and GetMinimapShape() == "SQUARE" then
        local scale = 1 / math.max(math.abs(x), math.abs(y))
        x, y = x * scale, y * scale
        radius = Minimap:GetWidth() / 2 + 6
    end

    button:ClearAllPoints()
    button:SetPoint("CENTER", Minimap, "CENTER", x * radius, y * radius)
end

local function CreateButton()
    button = CreateFrame("Button", "BugReportMinimapButton", Minimap)
    button:SetSize(31, 31)
    button:SetFrameStrata("MEDIUM")
    button:SetFrameLevel(8)
    button:RegisterForClicks("LeftButtonUp")
    button:RegisterForDrag("LeftButton")
    button:SetHighlightTexture("Interface\\Minimap\\UI-Minimap-ZoomButton-Highlight")

    local background = button:CreateTexture(nil, "BACKGROUND")
    background:SetTexture("Interface\\Minimap\\UI-Minimap-Background")
    background:SetSize(20, 20)
    background:SetPoint("TOPLEFT", 7, -5)

    local icon = button:CreateTexture(nil, "ARTWORK")
    icon:SetTexture("Interface\\HelpFrame\\HelpIcon-Bug")
    icon:SetSize(20, 20)
    icon:SetPoint("TOPLEFT", 7, -6)
    icon:SetTexCoord(0.1, 0.9, 0.1, 0.9)

    local border = button:CreateTexture(nil, "OVERLAY")
    border:SetTexture("Interface\\Minimap\\MiniMap-TrackingBorder")
    border:SetSize(53, 53)
    border:SetPoint("TOPLEFT")

    button:SetScript("OnClick", Toggle)
    button:SetScript("OnEnter", function(self)
        GameTooltip:SetOwner(self, "ANCHOR_LEFT")
        GameTooltip:SetText("Report a Bug")
        GameTooltip:AddLine("Click to report a bug. Drag to move this button.", 1, 1, 1, true)
        GameTooltip:Show()
    end)
    button:SetScript("OnLeave", GameTooltip_Hide)

    -- Dragging moves it round the minimap's edge.
    button:SetScript("OnDragStart", function(self)
        self:SetScript("OnUpdate", function()
            local mx, my = Minimap:GetCenter()
            local cx, cy = GetCursorPosition()
            local scale = Minimap:GetEffectiveScale()
            BugReportCharDB.minimapAngle = math.deg(math.atan2(cy / scale - my, cx / scale - mx)) % 360
            PlaceButton()
        end)
        GameTooltip:Hide()
    end)
    button:SetScript("OnDragStop", function(self)
        self:SetScript("OnUpdate", nil)
    end)

    PlaceButton()
    if BugReportCharDB.hideButton then
        button:Hide()
    end
end

------------------------------------------------------------------------------
-- /bug
------------------------------------------------------------------------------

SLASH_BUGREPORT1 = "/bug"
SLASH_BUGREPORT2 = "/bugreport"
SlashCmdList["BUGREPORT"] = function(args)
    args = strlower(strtrim(args or ""))
    if args == "button" or args == "show" then
        BugReportCharDB.hideButton = false
        BugReportCharDB.minimapAngle = 200
        button:Show()
        PlaceButton()
        Print("the minimap button is back.")
    elseif args == "hide" then
        BugReportCharDB.hideButton = true
        button:Hide()
        Print("the minimap button is hidden. /bug opens the window, /bug button brings the button back.")
    elseif args == "help" then
        Print("/bug opens the Report Bug window. /bug button brings back the minimap button, /bug hide hides it.")
    else
        Toggle()
    end
end

------------------------------------------------------------------------------
-- Events
------------------------------------------------------------------------------

local function OnAnswer(message)
    if message == "ON" then
        serverOn = true
        if not BugReportDB.token then
            Send("TOKEN")
        end
    elseif message == "OFF" then
        serverOn = false
    elseif message:sub(1, 6) == "TOKEN:" then
        BugReportDB.token = message:sub(7)
    else
        local answer, nonce, rest = strsplit(":", message, 3)
        if not sending or nonce ~= sending.nonce then
            return
        end
        if answer == "OK" then
            local id = "BUG-" .. rest
            Remember(id)
            Print(format("thank you! Your report was saved as |cffffd100%s|r.", id))
            UIErrorsFrame:AddMessage(format("Bug report saved as %s", id), 0.2, 0.8, 1)
        elseif rest == "SHORT" then
            Print(format("please describe the bug in at least %d characters.", MIN_LENGTH))
        elseif rest == "RATE" then
            Print("you've sent a report very recently. Please wait a minute before sending another.")
        elseif rest == "OFF" then
            serverOn = false
            Print("this realm isn't taking bug reports right now.")
        else
            Print("the server couldn't save your report (" .. tostring(rest) .. "). Please try again later.")
        end
        sending = nil
    end
    UpdateSubmit()
end

local events = CreateFrame("Frame")
events:RegisterEvent("ADDON_LOADED")
events:RegisterEvent("PLAYER_LOGIN")
events:RegisterEvent("CHAT_MSG_ADDON")
events:SetScript("OnEvent", function(self, event, ...)
    if event == "ADDON_LOADED" then
        if ... == "BugReport" then
            BugReportDB = BugReportDB or {}
            BugReportCharDB = BugReportCharDB or {}
        end
    elseif event == "PLAYER_LOGIN" then
        RegisterAddonMessagePrefix(PREFIX)
        CreateButton()
        Send("PING")
    elseif event == "CHAT_MSG_ADDON" then
        local prefix, message = ...
        if prefix == PREFIX then
            OnAnswer(message)
        end
    end
end)
