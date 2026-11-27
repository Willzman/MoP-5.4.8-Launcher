-- Solo Queue: "Solo" and "Duo" checkboxes next to the Dungeon Finder's
-- "Find Group".
--
--   Solo: not in a group. You go in by yourself, at once.
--   Duo:  two players go in as a pair. Either the leader of a group of 2 ticks
--         it, or two players each tick it on their own and are matched with
--         each other (and nobody else).
--
-- With one of them ticked, clicking Find Group asks the server (addon prefix
-- SFSOLO, whispered to yourself) to make that join a Solo or Duo one, just
-- before the game's own Find Group code runs. The addon never joins the queue
-- itself: the client only lets its own code do that, and blocks it from an
-- addon ("Interface action failed because of an AddOn"). The server handles
-- the request before the join.
--
-- The server answers "PING" with ON or OFF; the checkboxes only show on realms
-- that have solo queue turned on.
--
-- Installed and removed by the MoP 5.4.8 Launcher.

local PREFIX = "SFSOLO"

local serverOn = false
local solo, duo

local function Print(text)
    DEFAULT_CHAT_FRAME:AddMessage("|cff33ccffSolo Queue:|r " .. text)
end

local function Send(message)
    SendAddonMessage(PREFIX, message, "WHISPER", UnitName("player"))
end

local function FindGroupButton()
    return _G["LFDQueueFrameFindGroupButton"]
end

-- Solo: only when not in a group. Duo: not in a group, or leader of a group of 2.
local function CanSolo()
    return not IsInGroup()
end

local function CanDuo()
    if not IsInGroup() then
        return true
    end
    return GetNumGroupMembers() == 2 and UnitIsGroupLeader("player")
end

local function SetUsable(box, usable)
    if usable then
        box:Enable()
        _G[box:GetName() .. "Text"]:SetTextColor(1, 0.82, 0)
    else
        box:SetChecked(false)
        box:Disable()
        _G[box:GetName() .. "Text"]:SetTextColor(0.5, 0.5, 0.5)
    end
end

local function Update()
    if not solo then
        return
    end

    if not serverOn then
        solo:Hide()
        duo:Hide()
        return
    end

    solo:Show()
    duo:Show()
    SetUsable(solo, CanSolo())
    SetUsable(duo, CanDuo())
end

local function MakeBox(name, label, tooltip, findGroup, side)
    local box = CreateFrame("CheckButton", name, findGroup:GetParent(), "UICheckButtonTemplate")
    box:SetSize(24, 24)
    -- Solo left of Find Group (room for its label before the button), Duo right of it.
    if side == "LEFT" then
        box:SetPoint("RIGHT", findGroup, "LEFT", -38, 0)
    else
        box:SetPoint("LEFT", findGroup, "RIGHT", 4, 0)
    end
    _G[name .. "Text"]:SetText(label)
    box:Hide()

    box:SetScript("OnEnter", function(self)
        GameTooltip:SetOwner(self, "ANCHOR_TOP")
        GameTooltip:SetText(label)
        GameTooltip:AddLine(tooltip, 1, 1, 1, true)
        GameTooltip:Show()
    end)
    box:SetScript("OnLeave", GameTooltip_Hide)
    return box
end

local function Create()
    if solo then
        return true
    end

    local findGroup = FindGroupButton()
    if not findGroup then
        return false
    end

    duo = MakeBox("SoloQueueDuoCheckbox", "Duo",
        "Tick this, then click Find Group to go in as a pair. In a group of 2, the leader ticks it and you go in together at once. On your own, you're matched with one other Duo player, and nobody else.",
        findGroup, "RIGHT")
    solo = MakeBox("SoloQueueCheckbox", "Solo",
        "Tick this, then click Find Group to queue for the selected dungeon by yourself. You go in at once, alone; nobody else is added to your group.",
        findGroup, "LEFT")

    -- Only one of them at a time.
    solo:HookScript("OnClick", function(self) if self:GetChecked() then duo:SetChecked(false) end end)
    duo:HookScript("OnClick", function(self) if self:GetChecked() then solo:SetChecked(false) end end)

    -- Runs just before the game's own Find Group click, in the same click.
    -- Only sends an addon message, so the game's join stays its own.
    findGroup:HookScript("PreClick", function()
        if not serverOn then
            return
        end
        if solo:GetChecked() and CanSolo() then
            Send("QUEUE")
        elseif duo:GetChecked() and CanDuo() then
            Send("DUO")
        end
    end)

    local elapsedSinceUpdate = 0
    solo:SetScript("OnUpdate", function(self, elapsed)
        elapsedSinceUpdate = elapsedSinceUpdate + elapsed
        if elapsedSinceUpdate >= 0.2 then
            elapsedSinceUpdate = 0
            Update()
        end
    end)
    return true
end

local events = CreateFrame("Frame")
events:RegisterEvent("PLAYER_LOGIN")
events:RegisterEvent("ADDON_LOADED")
events:RegisterEvent("CHAT_MSG_ADDON")
events:RegisterEvent("GROUP_ROSTER_UPDATE")
events:SetScript("OnEvent", function(self, event, ...)
    if event == "PLAYER_LOGIN" then
        RegisterAddonMessagePrefix(PREFIX)
        if not Create() then
            Print("couldn't find the Dungeon Finder's Find Group button; the Solo and Duo checkboxes are not available.")
        end
        Send("PING")
    elseif event == "ADDON_LOADED" then
        if not solo then
            Create()
        end
    elseif event == "CHAT_MSG_ADDON" then
        local prefix, message = ...
        if prefix ~= PREFIX then
            return
        end

        if message == "ON" or message == "READY" then
            serverOn = true
        elseif message == "OFF" then
            if serverOn then
                Print("solo queue is turned off on this realm, so you joined the normal queue.")
            end
            serverOn = false
        elseif message == "GROUP" then
            Print("you're in a group, so you joined the normal queue.")
        elseif message == "DUOGROUP" then
            Print("Duo needs a group of exactly 2 with you as leader, so you joined the normal queue.")
        end
    end
    Update()
end)
