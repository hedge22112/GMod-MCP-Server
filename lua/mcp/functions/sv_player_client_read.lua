-- player_client_read: run one of a fixed set of read tools on a remote player's CLIENT and return
-- its result. The _cl tools only reach the listen-server host's own client, and a dedicated server
-- has none, so this is how to see what a connected player's client sees. The request and the
-- chunked reply live in libraries/sh_clientread_net.lua.
--
-- Carries no code: the client runs its own registered handler for a whitelisted read tool. The
-- target player must opt in on their own client (mcp_client_share 1).

local TIMEOUT = 20 -- reply-wait cap; keep < the declared per-tool timeout below
local SCREENSHOT_DIR = "mcp/screenshots"

MCP._clientShotSeq = MCP._clientShotSeq or {}

-- Save a remote screenshot's image on the server and rebuild the result around the saved file.
---@param payload table
---@param ply Player
---@param session string
---@param inline boolean
---@return table
local function saveScreenshot(payload, ply, session, inline)
    local image, text
    for _, block in ipairs(istable(payload.content) and payload.content or {}) do
        if istable(block) and block.type == "image" and isstring(block.data) then image = block end
        if istable(block) and block.type == "text" and isstring(block.text) then text = block.text end
    end
    if not image then return { ok = false, error = "client returned no image" } end

    local bytes = util.Base64Decode(image.data)
    if not bytes or bytes == "" then return { ok = false, error = "client returned an undecodable image" } end

    local dir = SCREENSHOT_DIR .. "/" .. session
    local seq = (MCP._clientShotSeq[session] or 0) + 1
    MCP._clientShotSeq[session] = seq
    local path = string.format("%s/%04d_%s_client%d.jpg", dir, seq, os.date("%H%M%S"), ply:UserID())
    file.CreateDir(dir)
    file.Write(path, bytes)
    if not file.Exists(path, "DATA") then
        return { ok = false, error = "could not save the screenshot to data/" .. path }
    end

    local content = { { type = "text", text = (text or "Remote client screenshot.") .. " Captured on " .. ply:Nick() .. "'s client." } }
    if inline then table.insert(content, 1, image) end
    return { ok = true, content = content, path = path }
end

MCP:AddFunction({
    id = "player_client_read",
    timeout = TIMEOUT + 3,
    description = "Run a read tool on a remote player's client and return its result: what that client sees, which the server realm can't (client prediction, PVS/dormancy, the rendered frame). The _cl tools only reach the listen-server host's own client and a dedicated server has none, so this is the way to look through a connected player's client. `tool` is one of entity_find, entity_state, player_state, player_trace, world_trace, screenshot, and `args` are that tool's usual arguments (screenshot is player-view only, with no free camera or trigger). The target must have opted in on their own client with `mcp_client_share 1`; the error lists who has. Select the target with exactly one of name/userid/entindex/steamid. A screenshot is saved on the server under data/mcp/screenshots/ and its path returned; pass `inline` to also get the image.",
    schema = {
        type = "object",
        properties = {
            tool = {
                type = "string",
                enum = { "entity_find", "entity_state", "player_state", "player_trace", "world_trace", "screenshot" },
                description = "The client-realm read tool to run on the target.",
            },
            args = { type = "object", description = "Arguments for that tool, as you'd pass them to its _cl version." },
            inline = { type = "boolean", description = "Screenshot only: also return the image inline (default false, just the saved path)." },
            name = { type = "string", description = "Target by player name (partial match allowed when unambiguous)." },
            userid = { type = "number", description = "Target by UserID()." },
            entindex = { type = "number", description = "Target by entity index." },
            steamid = { type = "string", description = "Target by SteamID (STEAM_0:1:23...) or SteamID64." },
        },
        required = { "tool" },
    },
    handler = function(args, ctx)
        local tool = args.tool
        if not (isstring(tool) and MCP.clientread.READ_TOOLS[tool]) then
            return { ok = false, error = "`tool` must be one of: entity_find, entity_state, player_state, player_trace, world_trace, screenshot" }
        end
        local toolArgs = istable(args.args) and table.Copy(args.args) or {}
        local refused = MCP.clientread.RefuseArgs(tool, toolArgs)
        if refused then return { ok = false, error = refused } end
        -- The image has to travel back to be saved here, whatever the caller wants inline.
        if tool == "screenshot" then toolArgs.inline = true end

        local players, err = MCP.player.Resolve(args, { allow_all = false })
        if not players then return { ok = false, error = err } end
        local ply = players[1]
        if ply:IsBot() then return { ok = false, error = "target is a bot; bots have no client" } end
        if not MCP.clientread.Shares(ply) then
            local sharing = {}
            for _, p in ipairs(player.GetHumans()) do
                if MCP.clientread.Shares(p) then sharing[#sharing + 1] = p:Nick() end
            end
            return {
                ok = false,
                error = ply:Nick() .. " has not opted in: they need to run `mcp_client_share 1` in their own console. "
                    .. (#sharing > 0 and ("Sharing now: " .. table.concat(sharing, ", ")) or "Nobody is sharing right now."),
            }
        end

        local identity = MCP.player.Identity(ply)
        local session = string.gsub(tostring(ctx.session or ""), "[^%w%.%-]", "")
        if session == "" then session = "local" end

        local token = MCP.clientread.Send(ply, tool, toolArgs, function(payload)
            if tool == "screenshot" and payload.ok ~= false then
                payload = saveScreenshot(payload, ply, session, args.inline == true)
            end
            payload.target = identity
            ctx.respond(payload)
        end)

        -- A reply that lands first wins; ctx.respond is single-shot, so this is then a no-op.
        MCP:RunFor({
            seconds = TIMEOUT,
            stop = function() return not MCP.clientread.IsPending(token) or not IsValid(ply) end,
        }, function()
            if not MCP.clientread.IsPending(token) then return end
            MCP.clientread.Cancel(token)
            local gone = not IsValid(ply)
            ctx.respond({
                ok = false,
                error = gone and "target disconnected before replying" or ("no reply from the client within " .. TIMEOUT .. "s"),
                target = identity,
            })
        end)

        return ctx.deferred
    end,
})
