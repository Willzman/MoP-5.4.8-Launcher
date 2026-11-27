-- Solo Queue: a "Solo" button next to the Dungeon Finder's "Find Group".
--
-- Clicking it asks the server (addon prefix SFSOLO, whispered to yourself) to
-- make your next dungeon finder join a solo one, then presses the game's own
-- Find Group button in the same click, with the dungeon and roles you picked.
-- Joining the dungeon finder is only allowed straight from a mouse click, so
-- both happen in the click; the server handles the request before the join.
-- A solo queue starts at once with just you and is never matched with other
-- players. The server answers "PING" with ON or OFF; the button only shows on
-- realms that have solo queue turned on.
--
-- Installed and removed by the MoP 5.4.8 Launcher.

local PREFIX = "SFSOLO"

local serverOn = false
local button

local function Print(text)
    DEFAULT_CHAT_FRAME:AddMessage("|cff33ccffSolo Queue:|r " .. text)
end

local function Send(message)
    SendAddonMessage(PREFIX, message, "WHISPER", UnitName("player"))
end

local function FindGroupButton()
    return _G["LFDQueueFrameFindGroupButton"]
end

local function UpdateButton()
    if not button then
        return
    end

    if not serverOn then
        button:Hide()
        return
    end

    button:Show()
    local findGroup = FindGroupButton()
    if findGroup and findGroup:IsEnabled() and not IsInGroup() then
        button:Enable()
    else
        button:Disable()
    end
end

local function CreateButton()
    if button then
        return true
    end

    local findGroup = FindGroupButton()
    if not findGroup then
        return false
    end

    button = CreateFrame("Button", "SoloQueueButton", findGroup:GetParent(), "UIPanelButtonTemplate")
    button:SetSize(80, findGroup:GetHeight() > 0 and findGroup:GetHeight() or 22)
    button:SetPoint("RIGHT", findGroup, "LEFT", -4, 0)
    button:SetText("Solo")
    button:Hide()

    button:SetScript("OnClick", function()
        if IsInGroup() then
            Print("leave your group to queue solo.")
            return
        end

        local findGroup = FindGroupButton()
        if not (findGroup and findGroup:IsEnabled()) then
            Print("pick a dungeon and a role first.")
            return
        end

        -- Same click: the request goes out first, then the game's own join.
        Send("QUEUE")
        findGroup:Click()
    end)

    button:SetScript("OnEnter", function(self)
        GameTooltip:SetOwner(self, "ANCHOR_TOP")
        GameTooltip:SetText("Solo")
        GameTooltip:AddLine("Queue for the selected dungeon by yourself. You go in at once, alone; nobody else is added to your group.", 1, 1, 1, true)
        GameTooltip:Show()
    end)
    button:SetScript("OnLeave", GameTooltip_Hide)

    local elapsedSinceUpdate = 0
    button:SetScript("OnUpdate", function(self, elapsed)
        elapsedSinceUpdate = elapsedSinceUpdate + elapsed
        if elapsedSinceUpdate >= 0.2 then
            elapsedSinceUpdate = 0
            UpdateButton()
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
        if not CreateButton() then
            Print("couldn't find the Dungeon Finder's Find Group button; the Solo button is not available.")
        end
        Send("PING")
    elseif event == "ADDON_LOADED" then
        if not button then
            CreateButton()
        end
    elseif event == "CHAT_MSG_ADDON" then
        local prefix, message = ...
        if prefix ~= PREFIX then
            return
        end

        if message == "ON" or message == "READY" then
            serverOn = true
        elseif message == "OFF" then
            -- Only after a click if the realm turned it off meanwhile: that
            -- join went into the normal queue.
            if serverOn then
                Print("solo queue is turned off on this realm, so you joined the normal queue.")
            end
            serverOn = false
        elseif message == "GROUP" then
            Print("you're in a group, so you joined the normal queue.")
        end
        UpdateButton()
    end
end)
