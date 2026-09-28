-- Remote-client reads for player_client_read (functions/sv_player_client_read.lua): the SERVER asks
-- a target CLIENT to run one of a fixed set of its own read tools, and the client nets the result
-- back. Nothing here carries code: the request names a tool from READ_TOOLS plus JSON args, and the
-- client runs its already-registered handler. The reply is result DATA, never executed.
--
-- The target player owns the decision. mcp_client_share (a client convar, off by default) is their
-- opt-in, checked on their own machine. It's also userinfo so the server can fail fast and list who
-- shares, but the client's own check is the one that counts.
--
-- Results can be large (a screenshot), so the client sends them in chunks, one per frame, and the
-- server reassembles them against the pending token.
--
-- In libraries/ (not functions/) so the headless README tool-list generator never runs
-- util.AddNetworkString / net at load.

MCP.clientread = MCP.clientread or {}

local MSG_REQ = "MCP_ClientRead"          -- server -> client: {token, tool, json args}
local MSG_CHUNK = "MCP_ClientReadChunk"   -- client -> server: {token, index, count, bytes}

local SHARE_CVAR = "mcp_client_share"
local CHUNK_BYTES = 32000
local MAX_CHUNKS = 128 -- ~4 MB compressed; far above a downscaled JPEG

-- Read tools a remote client will run for the server. All are ungated reads in the client realm.
-- screenshot is limited to player view: free-camera needs the host-only PVS extension, and
-- `trigger` runs caller Lua.
MCP.clientread.READ_TOOLS = {
    entity_find = true,
    entity_state = true,
    player_state = true,
    player_trace = true,
    world_trace = true,
    screenshot = true,
}

if CLIENT then
    CreateClientConVar(SHARE_CVAR, "0", true, true,
        "Let the server's MCP agent run read-only tools on this client (entity/player state, traces, screenshots). 0 = off.")
end

-- Tool args a remote client refuses, whatever the caller sends.
---@param tool string
---@param args table
---@return string? err
local function refuseArgs(tool, args)
    if args.async ~= nil then return "`async` is not supported for remote-client reads" end
    if tool == "screenshot" then
        if args.origin ~= nil or args.angles ~= nil then
            return "remote screenshots are player-view only (free-camera needs the listen-server host)"
        end
        if args.trigger ~= nil then return "`trigger` runs Lua and is not available for remote screenshots" end
    end
    return nil
end
MCP.clientread.RefuseArgs = refuseArgs

if SERVER then
    util.AddNetworkString(MSG_REQ)
    util.AddNetworkString(MSG_CHUNK)

    -- token -> { ply, onResult, chunks, count }. The token binds a reply to its target.
    MCP._clientreadPending = MCP._clientreadPending or {}
    MCP._clientreadSeq = MCP._clientreadSeq or 0

    ---@param ply Player
    ---@return boolean
    function MCP.clientread.Shares(ply)
        return ply:GetInfoNum(SHARE_CVAR, 0) ~= 0
    end

    ---@param ply Player
    ---@param tool string
    ---@param args table
    ---@param onResult fun(payload: table)
    ---@return string token
    function MCP.clientread.Send(ply, tool, args, onResult)
        MCP._clientreadSeq = MCP._clientreadSeq + 1
        local token = "pcr_" .. MCP._clientreadSeq
        MCP._clientreadPending[token] = { ply = ply, onResult = onResult, chunks = {} }

        net.Start(MSG_REQ)
        net.WriteString(token)
        net.WriteString(tool)
        net.WriteString(MCP.util.JsonEncode(args, false) or "{}")
        net.Send(ply)
        return token
    end

    ---@param token string
    function MCP.clientread.Cancel(token)
        MCP._clientreadPending[token] = nil
    end

    ---@param token string
    ---@return boolean
    function MCP.clientread.IsPending(token)
        return MCP._clientreadPending[token] ~= nil
    end

    net.Receive(MSG_CHUNK, function(_, ply)
        local token = net.ReadString()
        local pending = MCP._clientreadPending[token]
        if not pending or pending.ply ~= ply then return end

        local index = net.ReadUInt(16)
        local count = net.ReadUInt(16)
        local len = net.ReadUInt(16)
        local bytes = len > 0 and net.ReadData(len) or ""

        -- The chunk count is the client's claim; hold it to the first value and a sane ceiling.
        if count < 1 or count > MAX_CHUNKS or index < 1 or index > count
            or (pending.count and pending.count ~= count) then
            MCP._clientreadPending[token] = nil
            pending.onResult({ ok = false, error = "malformed reply from client" })
            return
        end
        pending.count = count
        pending.chunks[index] = bytes
        for i = 1, count do
            if not pending.chunks[i] then return end
        end

        MCP._clientreadPending[token] = nil
        local payload = MCP.util.JsonDecode(util.Decompress(table.concat(pending.chunks)) or "")
        if type(payload) ~= "table" then
            payload = { ok = false, error = "malformed result payload from client" }
        end
        pending.onResult(payload)
    end)
else
    -- Send a reply as compressed JSON, one chunk per frame so a screenshot doesn't flood the
    -- reliable channel in a single tick.
    ---@param token string
    ---@param payload table
    local function reply(token, payload)
        -- Serialize as the bridge does, so vectors and entities arrive in the same shape a _cl call returns.
        local data = util.Compress(MCP.util.JsonEncode(MCP.util.Serialize(payload), false) or "{}") or ""
        local count = math.max(1, math.ceil(#data / CHUNK_BYTES))
        if count > MAX_CHUNKS then
            data = util.Compress(MCP.util.JsonEncode({ ok = false, error = "result too large to send (" .. #data .. " bytes compressed)" }, false) or "{}") or ""
            count = 1
        end

        local index = 0
        local hookId = "MCP_ClientReadSend_" .. token
        local function sendNext()
            index = index + 1
            local part = string.sub(data, (index - 1) * CHUNK_BYTES + 1, index * CHUNK_BYTES)
            net.Start(MSG_CHUNK)
            net.WriteString(token)
            net.WriteUInt(index, 16)
            net.WriteUInt(count, 16)
            net.WriteUInt(#part, 16)
            net.WriteData(part, #part)
            net.SendToServer()
            if index >= count then hook.Remove("Think", hookId) end
        end
        sendNext()
        if index < count then hook.Add("Think", hookId, sendNext) end
    end

    -- net messages only ever come from the server, so there's no sender to authenticate here.
    net.Receive(MSG_REQ, function()
        local token = net.ReadString()
        local tool = net.ReadString()
        local args = MCP.util.JsonDecode(net.ReadString())
        if type(args) ~= "table" then args = {} end

        if not GetConVar(SHARE_CVAR):GetBool() then
            reply(token, { ok = false, error = "this player has not opted in (" .. SHARE_CVAR .. " is 0 on their client)" })
            return
        end
        local fn = MCP.clientread.READ_TOOLS[tool] and MCP._functions[tool] or nil
        if not fn then
            reply(token, { ok = false, error = "`" .. tool .. "` is not available as a remote-client read" })
            return
        end
        local refused = refuseArgs(tool, args)
        if refused then
            reply(token, { ok = false, error = refused })
            return
        end
        local capOk, capErr = MCP:CheckCapabilities(fn, args)
        if not capOk then
            reply(token, { ok = false, error = capErr })
            return
        end

        local responded = false
        ---@type mcp_ctx
        local ctx = {
            deferred = MCP._DEFERRED,
            session = "remote",
            respond = function(result)
                if responded then return end
                responded = true
                reply(token, type(result) == "table" and result or { ok = false, error = "handler returned no table" })
            end,
            onCancel = function() end,
        }
        local ok, ret = pcall(fn.handler, args, ctx)
        if not ok then
            ctx.respond({ ok = false, error = "handler error: " .. tostring(ret) })
        elseif ret ~= MCP._DEFERRED then
            ctx.respond(ret)
        end
    end)
end
