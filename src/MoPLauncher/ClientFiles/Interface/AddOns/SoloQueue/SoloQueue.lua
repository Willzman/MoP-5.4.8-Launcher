-- Solo Queue: a "Solo" checkbox next to the Dungeon Finder's "Find Group".
--
-- With it ticked, clicking Find Group asks the server (addon prefix SFSOLO,
-- whispered to yourself) to make that join a solo one, just before the game's
-- own Find Group code runs. The addon never joins the queue itself: the client
-- only lets its own code do that, and blocks it from an addon ("Interface
-- action failed because of an AddOn"). The server handles the request before
-- the join, so the queue goes in alone: it starts at once with just you and is
-- never matched with other players.
--
-- The server answers "PING" with ON or OFF; the checkbox only shows on realms
-- that have solo queue turned on.
--
-- Installed and removed by the MoP 5.4.8 Launcher.

local PREFIX = "SFSOLO"

local serverOn = false
local checkbox

local function Print(text)
    DEFAULT_CHAT_FRAME:AddMessage("|cff33ccffSolo Queue:|r " .. text)
end

local function Send(message)
    SendAddonMessage(PREFIX, message, "WHISPER", UnitName("player"))
end

local function FindGroupButton()
    return _G["LFDQueueFrameFindGroupButton"]
end

local function UpdateCheckbox()
    if not checkbox then
        return
    end

    if not serverOn then
        checkbox:Hide()
        return
    end

    checkbox:Show()
    if IsInGroup() then
        checkbox:Disable()
        _G[checkbox:GetName() .. "Text"]:SetTextColor(0.5, 0.5, 0.5)
    else
        checkbox:Enable()
        _G[checkbox:GetName() .. "Text"]:SetTextColor(1, 0.82, 0)
    end
end

local function Create()
    if checkbox then
        return true
    end

    local findGroup = FindGroupButton()
    if not findGroup then
        return false
    end

    checkbox = CreateFrame("CheckButton", "SoloQueueCheckbox", findGroup:GetParent(), "UICheckButtonTemplate")
    checkbox:SetSize(24, 24)
    checkbox:SetPoint("RIGHT", findGroup, "LEFT", -38, 0)
    _G[checkbox:GetName() .. "Text"]:SetText("Solo")
    checkbox:Hide()

    checkbox:SetScript("OnEnter", function(self)
        GameTooltip:SetOwner(self, "ANCHOR_TOP")
        GameTooltip:SetText("Solo")
        GameTooltip:AddLine("Tick this, then click Find Group to queue for the selected dungeon by yourself. You go in at once, alone; nobody else is added to your group.", 1, 1, 1, true)
        GameTooltip:Show()
    end)
    checkbox:SetScript("OnLeave", GameTooltip_Hide)

    -- Runs just before the game's own Find Group click, in the same click.
    -- Only sends an addon message, so the game's join stays its own.
    findGroup:HookScript("PreClick", function()
        if serverOn and checkbox:GetChecked() and not IsInGroup() then
            Send("QUEUE")
        end
    end)

    local elapsedSinceUpdate = 0
    checkbox:SetScript("OnUpdate", function(self, elapsed)
        elapsedSinceUpdate = elapsedSinceUpdate + elapsed
        if elapsedSinceUpdate >= 0.2 then
            elapsedSinceUpdate = 0
            UpdateCheckbox()
        end
    end)

    return true
end

local events = CreateFrame("Frame")
events:RegisterEvent("PLAYER_LOGIN")
events:RegisterEvent("ADDON_LOADED")
events:RegisterEvent("CHAT_MSG_ADDON")
events:SetScript("OnEvent", function(self, event, ...)
    if event == "PLAYER_LOGIN" then
        RegisterAddonMessagePrefix(PREFIX)
        if not Create() then
            Print("couldn't find the Dungeon Finder's Find Group button; the Solo checkbox is not available.")
        end
        Send("PING")
    elseif event == "ADDON_LOADED" then
        if not checkbox then
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
        end
        UpdateCheckbox()
    end
end)
