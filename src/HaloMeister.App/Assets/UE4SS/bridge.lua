-- HALOMEISTER SCRIPTING BRIDGE:BEGIN
-- HALOMEISTER SCRIPTING BRIDGE:VERSION 107
do
    local hm_ok, hm_error = pcall(function()
        -- UE4SS can load a mod before its shared helper module becomes available.
        -- Keep the mailbox heartbeat alive and retry this recoverable startup phase
        -- instead of making the whole bridge require a game restart.
        local UEHelpers = nil
        local local_app_data = os.getenv("LOCALAPPDATA")
        if not local_app_data then
            error("LOCALAPPDATA is unavailable")
        end

        -- Keep in step with the VERSION marker above; Halo Meister compares the
        -- version reported here against the copy it ships so it can tell you when
        -- the game is still running a stale bridge.
        local bridge_version = 107
        -- User scripts execute in a dedicated environment. Expose the UE4SS
        -- helper module there while retaining normal access to global UE4SS
        -- APIs and preserving the historical global assignment behavior.
        local user_script_environment = setmetatable(
            { UEHelpers = UEHelpers },
            { __index = _ENV, __newindex = _ENV }
        )

        local root = local_app_data .. "\\Meteorite\\Saved\\HaloMeister\\Scripting\\"
        local request_path = root .. "request.hm"
        local processing_path = root .. "processing.hm"
        local result_path = root .. "result.hm"
        local status_path = root .. "status.hm"
        local native_request_path = root .. "blam_spawn_request.hm"
        local native_result_path = root .. "blam_spawn_result.hm"
        local cheat_request_path = root .. "cheat_hook_request.hm"
        local cheat_result_path = root .. "cheat_hook_result.hm"
        local script_source = debug.getinfo(1, "S").source or ""
        local script_path = script_source:sub(1, 1) == "@"
            and script_source:sub(2)
            or script_source
        local script_directory = script_path:match("^(.*[\\/])[^\\/]+$") or ""
        local native_module_path = script_directory .. "halomeister_blam_v45.dll"
        local request_magic = "HMREQ1"
        local result_magic = "HMRES1"
        local status_magic = "HMSTATUS1"
        local maximum_code_bytes = 64 * 1024
        local poll_delay_ms = 250
        local last_heartbeat = 0
        local root_ready = false
        -- Imported tag data assets must stay rooted for the rest of the mission.
        -- Blam may retain the binary blob after this request completes.
        local loaded_tag_assets = {}
        local ai_capture_bootstrap = nil
        local ue_helpers_ready = false
        local next_ue_helpers_attempt = 0
        local next_ai_capture_attempt = 0

        -- Halo Meister normally creates this folder on launch. When the game starts
        -- first, the bridge must create it itself or every heartbeat write fails.
        local function ensure_root()
            if root_ready then return true end
            local probe_path = root .. ".hm_dir"
            local probe = io.open(probe_path, "ab")
            if probe then
                probe:close()
                os.remove(probe_path)
                root_ready = true
                return true
            end
            local dir = root:gsub("[/\\]+$", "")
            os.execute('cmd /d /c mkdir "' .. dir .. '" >nul 2>nul')
            probe = io.open(probe_path, "ab")
            if not probe then return false end
            probe:close()
            os.remove(probe_path)
            root_ready = true
            return true
        end

        -- A game crash can leave a claimed request behind. It must never block the
        -- single-slot mailbox after the next launch.
        if ensure_root() then
            os.remove(processing_path)
        end

        local function write_atomic(path, contents)
            if not ensure_root() then
                return false, "scripting mailbox folder is unavailable"
            end
            local temporary_path = path .. ".tmp"
            local file, open_error = io.open(temporary_path, "wb")
            if not file then return false, open_error end
            local wrote, write_error = file:write(contents)
            file:close()
            if not wrote then
                os.remove(temporary_path)
                return false, write_error
            end
            -- Prefer replace-in-place when the target is locked for reading: delete
            -- + rename can succeed at delete and then fail at rename, wiping the
            -- only heartbeat Halo Meister can see.
            os.remove(path)
            local renamed, rename_error = os.rename(temporary_path, path)
            if renamed then return true end
            local fallback, fallback_error = io.open(path, "wb")
            if fallback then
                local fallback_wrote = fallback:write(contents)
                fallback:close()
                os.remove(temporary_path)
                if fallback_wrote then return true end
                return false, fallback_error or "fallback write failed"
            end
            os.remove(temporary_path)
            return false, rename_error
        end

        local function write_result(request_id, status, message)
            message = tostring(message or "")
            if #message > maximum_code_bytes then
                message = message:sub(1, maximum_code_bytes)
                    .. "\n[output truncated by Halo Meister]"
            end
            local ok, err = write_atomic(
                result_path,
                result_magic .. "\n" .. request_id .. "\n" .. status .. "\n" .. message
            )
            if not ok then
                print(string.format(
                    "[HaloMeister] Could not write scripting result: %s\n",
                    tostring(err)
                ))
            end
        end

        local function ensure_ue_helpers()
            if ue_helpers_ready then return true end
            local now = os.time()
            if now < next_ue_helpers_attempt then return false end
            next_ue_helpers_attempt = now + 5
            local ok, helpers_or_error = pcall(require, "UEHelpers")
            if not ok then
                print(string.format(
                    "[HaloMeister] UEHelpers is not ready; retrying bridge initialization: %s\n",
                    tostring(helpers_or_error)
                ))
                return false
            end
            UEHelpers = helpers_or_error
            user_script_environment.UEHelpers = UEHelpers
            ue_helpers_ready = true
            print("[HaloMeister] UEHelpers ready; scripting mailbox is accepting requests\n")
            return true
        end

        local function valid_remote_object(object)
            return object and object:IsValid()
        end

        -- Cache native exports and the controlled Blam unit across spawn/load
        -- requests. FindAllOf + package.loadlib on every weapon/vehicle click is
        -- a measurable hitch on the UE game thread.
        local cached_blam_invoke = nil
        local cached_cheat_invoke = nil
        local cached_controlled_unit = nil

        local function get_blam_invoke()
            if cached_blam_invoke then
                return cached_blam_invoke
            end
            local invoke, load_error = package.loadlib(
                native_module_path,
                "HaloMeisterBlamInvoke"
            )
            if not invoke then
                error(
                    "Could not load the native Blam bridge at "
                        .. native_module_path .. ": " .. tostring(load_error)
                )
            end
            cached_blam_invoke = invoke
            return invoke
        end

        local function get_cheat_invoke()
            if cached_cheat_invoke then
                return cached_cheat_invoke
            end
            local invoke, load_error = package.loadlib(
                native_module_path,
                "HaloMeisterCheatInvoke"
            )
            if not invoke then
                error(
                    "Could not load the separate gameplay-cheat hook: "
                        .. tostring(load_error)
                )
            end
            cached_cheat_invoke = invoke
            return invoke
        end

        local function find_controlled_unit_component()
            if valid_remote_object(cached_controlled_unit) then
                local ok, controlled = pcall(function()
                    return cached_controlled_unit:IsControlledByAnyPlayer()
                end)
                if ok and controlled then
                    return cached_controlled_unit
                end
            end

            cached_controlled_unit = nil
            for _, candidate in ipairs(FindAllOf("BlamUnitComponent") or {}) do
                if valid_remote_object(candidate)
                    and candidate:IsControlledByAnyPlayer() then
                    cached_controlled_unit = candidate
                    return candidate
                end
            end
            return nil
        end

        -- ExecuteConsoleCommand returns void and Unreal silently discards a command
        -- it does not recognize, so a successful call proves only that the string was
        -- handed to the console. Never report this as a confirmed execution.
        local function execute_console(request_id, command, label)
            ExecuteInGameThread(function()
                local ok, err = xpcall(function()
                    local kismet = StaticFindObject(
                        "/Script/Engine.Default__KismetSystemLibrary"
                    )
                    local world = UEHelpers.GetWorld()
                    if not valid_remote_object(kismet)
                        or not valid_remote_object(world) then
                        error("KismetSystemLibrary or the active World is unavailable")
                    end
                    print(string.format("[HaloMeister] %s: %s\n", label, command))
                    kismet:ExecuteConsoleCommand(world, command, nil)
                end, debug.traceback)

                if ok then
                    write_result(
                        request_id,
                        "submitted",
                        label .. " was handed to ExecuteConsoleCommand as:\n"
                            .. command .. "\n\n"
                            .. "Unreal does not report whether it recognized the "
                            .. "command or what it did, so this is NOT a "
                            .. "confirmation that anything ran. Verify in game."
                    )
                else
                    write_result(request_id, "error", err)
                end
            end)
        end

        local function trim(value)
            return value:match("^%s*(.-)%s*$")
        end

        -- Campaign Evolved's console accepts HaloScript through the `hs:` verb.
        -- Also accept command-style and parenthesized source forms in the editor.
        local function normalize_haloscript_line(line)
            local expression = trim(line)
            if expression == "" or expression:match("^;") then return nil end
            if expression:lower():match("^hs:") then return expression end
            if expression:sub(1, 1) == "(" and expression:sub(-1) == ")" then
                expression = trim(expression:sub(2, -2))
            end
            return "hs:" .. expression
        end

        local function execute_haloscript(request_id, code)
            ExecuteInGameThread(function()
                local ok, result_or_error = xpcall(function()
                    local kismet = StaticFindObject(
                        "/Script/Engine.Default__KismetSystemLibrary"
                    )
                    local world = UEHelpers.GetWorld()
                    if not valid_remote_object(kismet)
                        or not valid_remote_object(world) then
                        error("KismetSystemLibrary or the active World is unavailable")
                    end

                    local commands = {}
                    for line in (code .. "\n"):gmatch("([^\r\n]*)\r?\n") do
                        local command = normalize_haloscript_line(line)
                        if command then
                            table.insert(commands, command)
                            print(string.format("[HaloMeister] HaloScript: %s\n", command))
                            kismet:ExecuteConsoleCommand(world, command, nil)
                        end
                    end
                    if #commands == 0 then
                        error("The HaloScript input contains no executable lines.")
                    end
                    return table.concat(commands, "\n")
                end, debug.traceback)

                if ok then
                    write_result(
                        request_id,
                        "submitted",
                        "HaloScript was handed to Campaign Evolved as:\n"
                            .. result_or_error .. "\n\n"
                            .. "The console API does not return evaluation output. "
                            .. "Verify the result in game."
                    )
                else
                    write_result(request_id, "error", result_or_error)
                end
            end)
        end

        local function execute_lua(request_id, code)
            local chunk, compile_error = load(
                code,
                "@HaloMeister/" .. request_id,
                "t",
                user_script_environment
            )
            if not chunk then
                write_result(request_id, "error", compile_error)
                return
            end
            ExecuteInGameThread(function()
                local ok, value = xpcall(chunk, debug.traceback)
                if ok then
                    local message = value == nil
                        and "Lua completed successfully."
                        or "Lua completed successfully.\nReturn value: " .. tostring(value)
                    write_result(request_id, "ok", message)
                else
                    write_result(request_id, "error", value)
                end
            end)
        end

        local function execute_player_unit_tag_read(request_id)
            ExecuteInGameThread(function()
                local ok, value = xpcall(function()
                    local unit_component = find_controlled_unit_component()
                    if not valid_remote_object(unit_component) then
                        error(
                            "Could not find the controlled player's Blam unit."
                        )
                    end
                    local owner = unit_component:GetOwner()
                    local synchronization_class = StaticFindObject(
                        "/Script/BlamSynchronization.BlamObjectSynchronizationComponent"
                    )
                    local synchronization = valid_remote_object(owner)
                        and valid_remote_object(synchronization_class)
                        and owner:GetComponentByClass(synchronization_class)
                        or nil
                    if not valid_remote_object(synchronization) then
                        error(
                            "The controlled player's Blam synchronization "
                                .. "component is unavailable."
                        )
                    end
                    local tag_index =
                        synchronization.BlamTagDefinitionIndex
                    if type(tag_index) ~= "number" then
                        local got, unwrapped = pcall(function()
                            return tag_index:get()
                        end)
                        if got then tag_index = unwrapped end
                    end
                    tag_index = tonumber(tag_index)
                    if not tag_index or tag_index % 1 ~= 0 then
                        error(
                            "The controlled player's unit tag index is unavailable."
                        )
                    end
                    -- This reflected IntProperty may contain either the raw
                    -- 16-bit tag-table index or the complete signed 32-bit tag
                    -- datum. Runtime tag datums store the table index in their
                    -- low word; the high word is the salt used to reject stale
                    -- references. Normalize signed Lua values before extracting
                    -- that index. PlayerCameraService validates that the result
                    -- is a currently loaded [bipd] before it writes anything.
                    local tag_datum = tag_index % 0x100000000
                    if tag_datum == 0xffffffff then
                        error(
                            "The controlled player's unit tag index is unavailable."
                        )
                    end
                    tag_index = tag_datum % 0x10000
                    return string.format("%d", tag_index)
                end, debug.traceback)

                if ok then
                    write_result(request_id, "ok", "Return value: " .. value)
                else
                    write_result(request_id, "error", value)
                end
            end)
        end

        -- Native Blam machinima owns the camera. This Lua side only reports the
        -- active view, enumerates authored camera nodes, and owns HUD visibility.
        local machinima_enabled = false
        local machinima_camera = nil
        local machinima_original_pawn = nil
        local machinima_original_view_target = nil
        local machinima_original_hud_visible = true
        local machinima_location = { X = 0.0, Y = 0.0, Z = 0.0 }
        local machinima_rotation = { Pitch = 0.0, Yaw = 0.0, Roll = 0.0 }
        local machinima_speed = 1800.0
        local machinima_loop_ready = false
        local machinima_update_pending = false
        local machinima_last_update_time = nil
        local machinima_move_input_ignored = false
        local machinima_look_input_ignored = false
        -- Unreal expects an FKey struct here, not UE4SS's numeric hotkey
        -- constants and not a bare FName. Passing a bare FName can cause a
        -- native access violation before Lua's pcall can recover.
        local machinima_keys = {
            W = { KeyName = FName("W") },
            A = { KeyName = FName("A") },
            S = { KeyName = FName("S") },
            D = { KeyName = FName("D") },
            SPACE = { KeyName = FName("SpaceBar") },
            SHIFT = { KeyName = FName("LeftShift") },
            CONTROL = { KeyName = FName("LeftControl") },
        }

        local function player_controller()
            local controller = UEHelpers.GetPlayerController
                and UEHelpers.GetPlayerController()
                or nil
            if not valid_remote_object(controller) then
                error(
                    "The local player controller is unavailable. "
                        .. "Load an offline campaign mission first."
                )
            end
            return controller
        end

        local function active_world(controller)
            local world = controller:GetWorld()
            if not valid_remote_object(world) then
                error("The active campaign World is unavailable.")
            end
            return world
        end

        local function finite_number(value)
            return type(value) == "number"
                and value == value
                and math.abs(value) < math.huge
        end

        local function safe_field(value)
            return tostring(value or ""):gsub("[\r\n\t]", " ")
        end

        local function controlled_player_actor()
            local unit_component = find_controlled_unit_component()
            if valid_remote_object(unit_component) then
                local owner = unit_component:GetOwner()
                if valid_remote_object(owner) then
                    return owner
                end
            end
            error("Could not find the controlled player's Unreal actor.")
        end

        local function execute_player_weapon_normalize(request_id)
            ExecuteInGameThread(function()
                local ok, value_or_error = xpcall(function()
                    local owner = controlled_player_actor()
                    local inventory_class = StaticFindObject(
                        "/Script/BlamSynchronization.BlamUnitInventoryComponent")
                    local inventory = valid_remote_object(owner)
                        and valid_remote_object(inventory_class)
                        and owner:GetComponentByClass(inventory_class)
                        or nil
                    if not valid_remote_object(inventory) then
                        error("The controlled player's weapon inventory is unavailable.")
                    end
                    local restored = 0
                    for index = 0, 7 do
                        local got, weapon = pcall(function()
                            return inventory:GetWeapon(index)
                        end)
                        if got and valid_remote_object(weapon) then
                            weapon:SetActorScale3D({ X = 1, Y = 1, Z = 1 })
                            restored = restored + 1
                        end
                    end
                    return string.format("Restored %d player weapon actor(s) to 1x.", restored)
                end, debug.traceback)
                if ok then
                    write_result(request_id, "ok", value_or_error)
                else
                    write_result(request_id, "error", value_or_error)
                end
            end)
        end

        local function machinima_state(controller)
            local world = active_world(controller)
            local manager = controller.PlayerCameraManager
            if not valid_remote_object(manager) then
                error("The local player camera manager is unavailable.")
            end
            local location = manager:GetCameraLocation()
            local rotation = manager:GetCameraRotation()
            if not location or not rotation then
                error("Could not read the active camera transform.")
            end
            local player_location =
                controlled_player_actor():K2_GetActorLocation()
            if not player_location then
                error("Could not read the controlled player's Unreal position.")
            end
            return string.format(
                "enabled=%d\nworld=%s\ncamera_ue_x=%.9g\n"
                    .. "camera_ue_y=%.9g\ncamera_ue_z=%.9g\n"
                    .. "player_ue_x=%.9g\nplayer_ue_y=%.9g\n"
                    .. "player_ue_z=%.9g\npitch=%.9g\nyaw=%.9g\nroll=%.9g",
                machinima_enabled and 1 or 0,
                safe_field(world:GetFullName()),
                location.X,
                location.Y,
                location.Z,
                player_location.X,
                player_location.Y,
                player_location.Z,
                rotation.Pitch,
                rotation.Yaw,
                rotation.Roll
            )
        end

        local function set_hud_visible(controller, visible)
            if valid_remote_object(controller.MyHUD) then
                controller.MyHUD.bShowHUD = visible
            end
        end

        local function update_machinima_camera()
            if not machinima_enabled
                or not valid_remote_object(machinima_camera) then
                return
            end
            machinima_camera:K2_SetActorLocationAndRotation(
                {
                    X = machinima_location.X,
                    Y = machinima_location.Y,
                    Z = machinima_location.Z,
                },
                {
                    Pitch = machinima_rotation.Pitch,
                    Yaw = machinima_rotation.Yaw,
                    Roll = machinima_rotation.Roll,
                },
                false,
                {},
                false
            )
        end

        local function enable_machinima(controller)
            if machinima_enabled then
                return
            end
            machinima_original_hud_visible = true
            if valid_remote_object(controller.MyHUD) then
                local hud_visible = controller.MyHUD.bShowHUD
                if type(hud_visible) ~= "boolean" then
                    local got, unwrapped = pcall(function()
                        return hud_visible:get()
                    end)
                    if got then hud_visible = unwrapped end
                end
                if type(hud_visible) == "boolean" then
                    machinima_original_hud_visible = hud_visible
                end
            end

            machinima_enabled = true
            local configured, configure_error = pcall(function()
                set_hud_visible(controller, false)
            end)
            if not configured then
                machinima_enabled = false
                pcall(function()
                    set_hud_visible(
                        controller,
                        machinima_original_hud_visible
                    )
                end)
                error(configure_error)
            end
        end

        local function disable_machinima(controller)
            machinima_enabled = false
            local restored, restore_error = pcall(function()
                set_hud_visible(controller, machinima_original_hud_visible)
            end)
            machinima_camera = nil
            machinima_original_pawn = nil
            machinima_original_view_target = nil
            if not restored then
                error("Advanced Machinima HUD cleanup reported: "
                    .. tostring(restore_error))
            end
        end

        local function live_camera_nodes(controller)
            local world = active_world(controller)
            local world_name = world:GetFullName()
            local nodes = {}
            local seen = {}

            local function collect(class_name)
                for _, candidate in ipairs(FindAllOf(class_name) or {}) do
                    if valid_remote_object(candidate) then
                        local ok, full_name, candidate_world_name, location, rotation =
                            pcall(function()
                                local candidate_world = candidate:GetWorld()
                                if not valid_remote_object(candidate_world) then
                                    return nil
                                end
                                return candidate:GetFullName(),
                                    candidate_world:GetFullName(),
                                    candidate:K2_GetActorLocation(),
                                    candidate:K2_GetActorRotation()
                            end)
                        if ok and full_name and candidate_world_name == world_name
                            and not seen[full_name]
                            and location and rotation then
                            seen[full_name] = true
                            table.insert(nodes, {
                                name = safe_field(full_name),
                                location = location,
                                rotation = rotation,
                            })
                        end
                    end
                end
            end

            collect("CameraActor")
            collect("CineCameraActor")
            table.sort(nodes, function(left, right)
                return left.name < right.name
            end)
            return nodes
        end

        local function execute_machinima(request_id, operation, payload)
            ExecuteInGameThread(function()
                local ok, value = xpcall(function()
                    local controller = player_controller()
                    if operation == "state" then
                        return machinima_state(controller)
                    elseif operation == "nodes" then
                        local lines = {}
                        for _, node in ipairs(live_camera_nodes(controller)) do
                            table.insert(lines, string.format(
                                "%.9g\t%.9g\t%.9g\t%.9g\t%.9g\t%.9g\t%s",
                                node.location.X,
                                node.location.Y,
                                node.location.Z,
                                node.rotation.Pitch,
                                node.rotation.Yaw,
                                node.rotation.Roll,
                                node.name
                            ))
                        end
                        return table.concat(lines, "\n")
                    elseif operation == "enable" then
                        enable_machinima(controller)
                        return machinima_state(controller)
                    elseif operation == "disable" then
                        disable_machinima(controller)
                        return machinima_state(controller)
                    elseif operation ~= "teleport" then
                        error("Unsupported machinima operation.")
                    end

                    error(
                        "Native machinima camera movement must use the "
                            .. "verified Blam camera route."
                    )
                end, debug.traceback)

                if ok then
                    write_result(request_id, "ok", value)
                else
                    write_result(request_id, "error", value)
                end
            end)
        end

        local function key_down(controller, key)
            local ok, down = pcall(function()
                return controller:IsInputKeyDown(key)
            end)
            return ok and down == true
        end

        local function machinima_forward(rotation)
            local pitch = rotation.Pitch * math.pi / 180.0
            local yaw = rotation.Yaw * math.pi / 180.0
            return {
                X = math.cos(pitch) * math.cos(yaw),
                Y = math.cos(pitch) * math.sin(yaw),
                Z = math.sin(pitch),
            }
        end

        local function update_machinima()
            if not machinima_enabled
                or not valid_remote_object(machinima_camera) then
                return
            end
            local controller = player_controller()
            local gameplay = StaticFindObject(
                "/Script/Engine.Default__GameplayStatics"
            )
            local world = active_world(controller)
            if not valid_remote_object(gameplay) then
                error("GameplayStatics is unavailable for camera timing.")
            end
            local now = tonumber(gameplay:GetRealTimeSeconds(world))
            if not now then
                error("The Unreal real-time clock is unavailable.")
            end
            local delta = machinima_last_update_time
                and now - machinima_last_update_time
                or 0.016
            machinima_last_update_time = now
            if delta < 0.0005 then delta = 0.0005 end
            if delta > 0.05 then delta = 0.05 end

            local ok_mouse, mouse_x, mouse_y = pcall(function()
                return controller:GetInputMouseDelta()
            end)
            if ok_mouse and type(mouse_x) == "number"
                and type(mouse_y) == "number" then
                machinima_rotation.Yaw =
                    machinima_rotation.Yaw + mouse_x * 1.8
                machinima_rotation.Pitch = math.max(
                    -89.0,
                    math.min(89.0, machinima_rotation.Pitch - mouse_y * 1.8)
                )
            end

            local boost = key_down(controller, machinima_keys.SHIFT)
            local distance = machinima_speed * (boost and 3.0 or 1.0) * delta
            local forward = machinima_forward(machinima_rotation)
            local yaw = (machinima_rotation.Yaw + 90.0) * math.pi / 180.0
            local right = { X = math.cos(yaw), Y = math.sin(yaw) }
            local move_x, move_y, move_z = 0.0, 0.0, 0.0

            if key_down(controller, machinima_keys.W) then
                move_x = move_x + forward.X
                move_y = move_y + forward.Y
                move_z = move_z + forward.Z
            end
            if key_down(controller, machinima_keys.S) then
                move_x = move_x - forward.X
                move_y = move_y - forward.Y
                move_z = move_z - forward.Z
            end
            if key_down(controller, machinima_keys.D) then
                move_x = move_x + right.X
                move_y = move_y + right.Y
            end
            if key_down(controller, machinima_keys.A) then
                move_x = move_x - right.X
                move_y = move_y - right.Y
            end
            if key_down(controller, machinima_keys.SPACE) then
                move_z = move_z + 1.0
            end
            if key_down(controller, machinima_keys.CONTROL) then
                move_z = move_z - 1.0
            end

            machinima_location.X =
                machinima_location.X + move_x * distance
            machinima_location.Y =
                machinima_location.Y + move_y * distance
            machinima_location.Z =
                machinima_location.Z + move_z * distance
            update_machinima_camera()
        end

        local function register_machinima_loop()
            local ok, loop_error = pcall(function()
                LoopAsync(8, function()
                    if machinima_enabled
                        and not machinima_update_pending then
                        machinima_update_pending = true
                        local queued, queue_error = pcall(function()
                            ExecuteInGameThread(function()
                                local updated, update_error = xpcall(
                                    update_machinima,
                                    debug.traceback
                                )
                                machinima_update_pending = false
                                if not updated then
                                    print(string.format(
                                        "[HaloMeister] Advanced Machinima "
                                            .. "update failed: %s\n",
                                        tostring(update_error)
                                    ))
                                end
                            end)
                        end)
                        if not queued then
                            machinima_update_pending = false
                            print(string.format(
                                "[HaloMeister] Advanced Machinima update "
                                    .. "could not be queued: %s\n",
                                tostring(queue_error)
                            ))
                        end
                    end
                    return false
                end)
            end)
            machinima_loop_ready = ok
            if not ok then
                print(string.format(
                    "[HaloMeister] Advanced Machinima loop unavailable: %s\n",
                    tostring(loop_error)
                ))
            end
        end

        local function execute_blam_tag_asset_load(request_id, asset_path)
            if not asset_path:match("^/Game/Tags/[A-Za-z0-9_/%-]+$") then
                write_result(
                    request_id,
                    "error",
                    "Tag assets must be a safe path below /Game/Tags."
                )
                return
            end

            ExecuteInGameThread(function()
                local ok, value = xpcall(function()
                    -- Campaign Evolved ships this exact LoadAsset route in its
                    -- unloaded-asset summon handler. UE4SS requires the game thread.
                    LoadAsset(asset_path)

                    local asset_name = asset_path:match("([^/]+)$")
                    local object_path = asset_path .. "." .. asset_name
                    local asset = StaticFindObject(object_path)
                    if not valid_remote_object(asset) then
                        error("Unreal did not load " .. object_path)
                    end

                    local class_name = asset:GetClass():GetFullName()
                    if not class_name:find("BlamWeaponTagDataAsset", 1, true) then
                        error(
                            "Loaded object is not a Blam weapon tag data asset: "
                                .. tostring(class_name)
                        )
                    end

                    loaded_tag_assets[object_path] = asset
                    local blob_size = tonumber(asset.BinaryBlobSize) or 0
                    return string.format(
                        "Loaded and rooted %s (binary blob: %u bytes).",
                        asset:GetFullName(),
                        blob_size
                    )
                end, debug.traceback)

                if ok then
                    write_result(request_id, "ok", value)
                else
                    write_result(request_id, "error", value)
                end
            end)
        end

        local function execute_blam_spawn(request_id, operation, payload)
            local formation_offset_x, formation_offset_y = 0.0, 0.0
            local ai_right_x, ai_right_y = 1.0, 0.0
            local friendly_companion = false
            if operation == "ai" or operation == "ai_team" then
                local base_payload, offset_x, offset_y, friendly =
                    payload:match("^(.*);([^;]+);([^;]+);([01])$")
                if not base_payload then
                    base_payload, offset_x, offset_y =
                        payload:match("^(.*);([^;]+);([^;]+)$")
                else
                    friendly_companion = friendly == "1"
                end
                if base_payload then
                    formation_offset_x = tonumber(offset_x)
                    formation_offset_y = tonumber(offset_y)
                    if not formation_offset_x or not formation_offset_y
                        or formation_offset_x ~= formation_offset_x
                        or formation_offset_y ~= formation_offset_y
                        or math.abs(formation_offset_x) == math.huge
                        or math.abs(formation_offset_y) == math.huge then
                        write_result(
                            request_id,
                            "error",
                            "The AI formation offset is invalid."
                        )
                        return
                    end
                    payload = base_payload
                end
            end
            local valid_object = operation == "object"
                and payload:match("^%x%x%x%x%x%x%x%x$")
            local valid_weapon = operation == "weapon"
                and payload:match("^%x%x%x%x%x%x%x%x$")
            local valid_variant = operation == "variant"
                and payload:match("^%x%x%x%x%x%x%x%x$")
            local valid_colors = operation == "colors"
                and payload:match(
                    "^%x%x,%x%x%x%x%x%x,%x%x%x%x%x%x,"
                        .. "%x%x%x%x%x%x,%x%x%x%x%x%x$")
            local weapon_segment, weapon_variant = payload:match(
                "^([A-Za-z0-9_]+),(%x%x%x%x%x%x%x%x)$")
            local valid_weapon_variant = operation == "weapon_variant"
                and weapon_segment ~= nil
            local valid_biped = operation == "biped"
                and payload:match("^%x%x%x%x%x%x%x%x$")
            local valid_biped_body = operation == "biped_body"
                and payload:match("^%x%x%x%x%x%x%x%x$")
            local valid_biped_variant_body =
                operation == "biped_variant_body"
                and payload:match(
                    "^%x%x%x%x%x%x%x%x,%x%x%x%x%x%x%x%x$")
            local valid_bump_off = operation == "bump_off" and payload == "off"
            local valid_cheat_read = operation == "cheat_read" and payload == "read"
            local valid_cheat_write = operation == "cheat_write"
                and payload:match("^cheat_[a-z0-9_]+=[01]$")
            local valid_skull_read = operation == "skull_read" and payload == "read"
            local valid_skull_write = operation == "skull_write"
                and payload:match("^skull_[a-z0-9_]+=[01]$")
            local valid_soft_ceiling_read =
                operation == "soft_ceiling_read" and payload == "read"
            local valid_soft_ceiling_write =
                operation == "soft_ceiling_write" and payload:match("^[01]$")
            local valid_boundary_read =
                operation == "boundary_read" and payload == "read"
            local valid_boundary_disable =
                operation == "boundary_disable" and payload == "disable"
            local valid_boundary_restore =
                operation == "boundary_restore" and payload == "restore"
            local valid_player_position =
                operation == "player_position" and payload == "read"
            local teleport_x, teleport_y, teleport_z = payload:match(
                "^([%+%-]?[%d%.eE]+),([%+%-]?[%d%.eE]+),([%+%-]?[%d%.eE]+)$"
            )
            teleport_x = tonumber(teleport_x)
            teleport_y = tonumber(teleport_y)
            teleport_z = tonumber(teleport_z)
            local valid_teleport = operation == "player_teleport"
                and teleport_x ~= nil and teleport_y ~= nil and teleport_z ~= nil
                and teleport_x == teleport_x and teleport_y == teleport_y
                and teleport_z == teleport_z
                and math.abs(teleport_x) < math.huge
                and math.abs(teleport_y) < math.huge
                and math.abs(teleport_z) < math.huge
            local object_target, object_tx, object_ty, object_tz =
                payload:match(
                    "^(last),([%+%-]?[%d%.eE]+),([%+%-]?[%d%.eE]+),([%+%-]?[%d%.eE]+)$"
                )
            if not object_target then
                object_target, object_tx, object_ty, object_tz =
                    payload:match(
                        "^([aAuU]%x%x%x%x%x%x%x%x),([%+%-]?[%d%.eE]+),([%+%-]?[%d%.eE]+),([%+%-]?[%d%.eE]+)$"
                    )
            end
            object_tx = tonumber(object_tx)
            object_ty = tonumber(object_ty)
            object_tz = tonumber(object_tz)
            local valid_object_teleport = operation == "object_teleport"
                and object_target ~= nil
                and object_tx ~= nil and object_ty ~= nil and object_tz ~= nil
                and object_tx == object_tx and object_ty == object_ty
                and object_tz == object_tz
                and math.abs(object_tx) < math.huge
                and math.abs(object_ty) < math.huge
                and math.abs(object_tz) < math.huge
            local valid_object_position = operation == "object_position"
                and (payload == "last"
                    or payload:match("^[aAuU]%x%x%x%x%x%x%x%x$"))
            local valid_noclip = operation == "player_noclip"
                and payload:match("^[01]$")
            local valid_player_team = operation == "player_team"
                and (payload == "read"
                    or payload == "restore"
                    or payload:match("^%d%d?$"))
            local valid_object_team = operation == "object_team"
                and (payload:match("^last,%d%d?$")
                    or payload:match("^[aAuU]%x%x%x%x%x%x%x%x,%d%d?$"))
            local valid_player_input = operation == "player_input"
                and (payload == "suppress" or payload == "restore")
            local valid_native_machinima = operation == "machinima"
                and (payload == "read" or payload == "enable"
                    or payload == "disable" or payload == "restore")
            local valid_research_call = operation == "research_call"
                and payload:match(
                    "^" .. string.rep("%x", 8)
                        .. "," .. string.rep("%x", 32)
                        .. ",[0-4]"
                        .. string.rep("," .. string.rep("%x", 16), 4)
                        .. "$"
                )
            local function is_hex_width(value, width)
                return #value == width
                    and value:match("^" .. string.rep("%x", width) .. "$")
            end
            local function split_fields(value)
                local fields = {}
                for field in value:gmatch("[^,]+") do
                    fields[#fields + 1] = field
                end
                return fields
            end
            local function validate_ai(value)
                local fields = split_fields(value)
                if #fields ~= 6 and #fields ~= 7 then
                    return false
                end
                return is_hex_width(fields[1], 4)
                    and is_hex_width(fields[2], 16)
                    and is_hex_width(fields[3], 16)
                    and is_hex_width(fields[4], 16)
                    and is_hex_width(fields[5], 32)
                    and is_hex_width(fields[6], 8)
                    and (#fields == 6 or is_hex_width(fields[7], 8))
            end
            local valid_ai = operation == "ai"
                and validate_ai(payload)
            local function validate_ai_team(value)
                local fields = split_fields(value)
                if #fields < 8 or #fields > 21 then
                    return false
                end
                local has_weapon = false
                local count
                if (#fields - 5) % 3 == 0 then
                    count = (#fields - 5) / 3
                elseif (#fields - 6) % 3 == 0 then
                    count = (#fields - 6) / 3
                    has_weapon = true
                else
                    return false
                end
                if count < 1 or count > 5
                    or not is_hex_width(fields[1], 4)
                    or not is_hex_width(fields[2], 16)
                    or not is_hex_width(fields[3], 4) then
                    return false
                end
                for index = 4, 3 + count * 3 do
                    if not is_hex_width(fields[index], 16) then
                        return false
                    end
                end
                local reference_index = 4 + count * 3
                local variant_index = 5 + count * 3
                return is_hex_width(fields[reference_index], 32)
                    and is_hex_width(fields[variant_index], 8)
                    and (not has_weapon
                        or is_hex_width(fields[variant_index + 1], 8))
            end
            local valid_ai_team = operation == "ai_team"
                and validate_ai_team(payload)
            if not valid_object and not valid_weapon and not valid_variant
                and not valid_colors
                and not valid_weapon_variant
                and not valid_biped and not valid_biped_body
                and not valid_biped_variant_body
                and not valid_bump_off and not valid_cheat_read
                and not valid_cheat_write and not valid_skull_read
                and not valid_skull_write and not valid_soft_ceiling_read
                and not valid_soft_ceiling_write and not valid_boundary_read
                and not valid_boundary_disable and not valid_boundary_restore
                and not valid_player_position
                and not valid_teleport and not valid_noclip
                and not valid_player_team
                and not valid_object_team
                and not valid_object_position
                and not valid_object_teleport
                and not valid_player_input and not valid_native_machinima
                and not valid_research_call
                and not valid_ai and not valid_ai_team then
                write_result(
                    request_id,
                    "error",
                    "The Blam spawn request payload is invalid."
                )
                return
            end

            ExecuteInGameThread(function()
                local ok, dispatch_error = xpcall(function()
                    local x, y, z = 0.0, 0.0, 0.0
                    if operation ~= "bump_off"
                        and operation ~= "cheat_read"
                        and operation ~= "cheat_write"
                        and operation ~= "skull_read"
                        and operation ~= "skull_write"
                        and operation ~= "soft_ceiling_read"
                        and operation ~= "soft_ceiling_write"
                        and operation ~= "boundary_read"
                        and operation ~= "boundary_disable"
                        and operation ~= "boundary_restore"
                        and operation ~= "player_input"
                        and operation ~= "machinima"
                        and operation ~= "object_position"
                        and operation ~= "object_teleport" then
                        -- UEHelpers v3 calls this GetPlayer; newer revisions may expose
                        -- GetPlayerPawn. Support both because HCE packages v3.
                        local get_player = UEHelpers.GetPlayerPawn or UEHelpers.GetPlayer
                        if not get_player then
                            error("The installed UEHelpers has no local-player accessor.")
                        end
                        local pawn = get_player()
                        if not valid_remote_object(pawn) then
                            error("The local player pawn is unavailable. Load a campaign mission first.")
                        end

                        local location = pawn:K2_GetActorLocation()
                        local forward = pawn:GetActorForwardVector()
                        if not location or not forward then
                            error("Could not resolve the player's position and facing direction.")
                        end

                        -- Biped switching is collision-driven. Spawn at the controlled
                        -- player's exact location so the engine sees overlapping unit
                        -- capsules on the next simulation update. AI companions spawn
                        -- beside the player (left/right), not metres ahead.
                        local distance = (operation == "biped"
                            or operation == "ai"
                            or operation == "ai_team") and 0.0 or 150.0
                        x = location.X + forward.X * distance
                        y = location.Y + forward.Y * distance
                        z = location.Z + forward.Z * distance
                        if operation == "ai" or operation == "ai_team" then
                            local right = pawn.GetActorRightVector
                                and pawn:GetActorRightVector()
                                or nil
                            if not right then
                                right = { X = -forward.Y, Y = forward.X, Z = 0 }
                            end
                            local right_hx, right_hy = right.X, -right.Y
                            local length = math.sqrt(
                                right_hx * right_hx + right_hy * right_hy)
                            if length > 0.001 then
                                ai_right_x = right_hx / length
                                ai_right_y = right_hy / length
                            end
                        end
                    end
                    if operation == "object" or operation == "weapon"
                        or operation == "variant"
                        or operation == "colors"
                        or operation == "weapon_variant"
                        or operation == "biped"
                        or operation == "biped_body"
                        or operation == "biped_variant_body"
                        or operation == "player_position"
                        or operation == "player_teleport"
                        or operation == "player_noclip"
                        or operation == "player_team"
                        or operation == "object_team"
                        or ((operation == "ai" or operation == "ai_team")
                            and friendly_companion) then
                        local unit_component = find_controlled_unit_component()
                        if not unit_component then
                            error("Could not find the controlled player's Blam unit.")
                        end
                        local owner = unit_component:GetOwner()
                        if operation == "weapon_variant" then
                            local inventory_class = StaticFindObject(
                                "/Script/BlamSynchronization.BlamUnitInventoryComponent")
                            local inventory = valid_remote_object(owner)
                                and valid_remote_object(inventory_class)
                                and owner:GetComponentByClass(inventory_class)
                                or nil
                            if not valid_remote_object(inventory) then
                                error("The controlled player's weapon inventory is unavailable.")
                            end

                            local keywords = {
                                AssaultRifle = {"assaultrifle", "assault_rifle"},
                                assaultrifle = {"assaultrifle", "assault_rifle"},
                                BattleRifle = {"battlerifle", "battle_rifle"},
                                battlerifle = {"battlerifle", "battle_rifle"},
                                EnergySword = {"energysword", "energy_sword"},
                                energysword = {"energysword", "energy_sword"},
                                FuelRod = {"fuelrod", "fuel_rod", "flak_cannon"},
                                flakcannon = {"fuelrod", "fuel_rod", "flak_cannon"},
                                fuelrodcannon = {"fuelrod", "fuel_rod", "flak_cannon"},
                                Magnum = {"magnum"},
                                magnum = {"magnum"},
                                Needler = {"needler"},
                                needler = {"needler"},
                                SniperRifle = {"sniperrifle", "sniper_rifle", "stanchion"},
                                sniperrifle = {"sniperrifle", "sniper_rifle"},
                                stanchion = {"stanchion"},
                                Spnkr = {"spnkr", "rocketlauncher", "rocket_launcher"},
                                rocketlauncher = {
                                    "spnkr", "rocketlauncher", "rocket_launcher"
                                },
                                spnkr = {"spnkr", "rocketlauncher", "rocket_launcher"},
                            }
                            local expected = keywords[weapon_segment]
                                or {weapon_segment}
                            local function normalize_weapon_identity(value)
                                return string.lower(tostring(value or "")):gsub(
                                    "[^a-z0-9]", "")
                            end

                            local weapon = nil
                            for inventory_index = 0, 7 do
                                local got, candidate = pcall(function()
                                    return inventory:GetWeapon(inventory_index)
                                end)
                                if got and valid_remote_object(candidate) then
                                    local candidate_component = candidate:GetComponentByClass(
                                        StaticFindObject(
                                            "/Script/BlamSynchronization.BlamWeaponComponent"))
                                    local asset = valid_remote_object(candidate_component)
                                        and candidate_component.WeaponDataAsset
                                        or nil
                                    local identity = normalize_weapon_identity(
                                        candidate:GetFullName() .. " "
                                            .. (valid_remote_object(asset)
                                                and asset:GetFullName() or ""))
                                    for _, keyword in ipairs(expected) do
                                        local normalized_keyword =
                                            normalize_weapon_identity(keyword)
                                        if normalized_keyword ~= ""
                                            and string.find(
                                                identity,
                                                normalized_keyword,
                                                1,
                                                true) then
                                            weapon = candidate
                                            break
                                        end
                                    end
                                end
                                if weapon then break end
                            end
                            if not valid_remote_object(weapon) then
                                error(
                                    "The selected weapon type is not currently in the player's inventory.")
                            end
                            owner = weapon
                        end

                        local synchronization_class = StaticFindObject(
                            "/Script/BlamSynchronization.BlamObjectSynchronizationComponent"
                        )
                        local synchronization = valid_remote_object(owner)
                            and valid_remote_object(synchronization_class)
                            and owner:GetComponentByClass(synchronization_class)
                            or nil
                        if not valid_remote_object(synchronization) then
                            error("The player's Blam synchronization component is unavailable.")
                        end

                        local unit_datum = synchronization.BlamObjectIndex
                        if type(unit_datum) ~= "number" then
                            local got, value = pcall(function()
                                return unit_datum:get()
                            end)
                            if got then unit_datum = value end
                        end
                        unit_datum = tonumber(unit_datum)
                        if not unit_datum or unit_datum == -1 then
                            error("The player's native Blam object datum is unavailable.")
                        end
                        unit_datum = unit_datum % 0x100000000
                        if operation == "player_position" then
                            payload = string.format("%08x", unit_datum)
                        elseif operation == "player_teleport" then
                            x, y, z = teleport_x, teleport_y, teleport_z
                            payload = string.format("%08x", unit_datum)
                        elseif operation == "ai" or operation == "ai_team" then
                            payload = payload
                                .. ",p" .. string.format("%08x", unit_datum)
                        elseif operation == "object_team" then
                            -- target,team[,playerUnit] — player clears combat aim.
                            payload = payload
                                .. "," .. string.format("%08x", unit_datum)
                        else
                            payload = (operation == "weapon_variant"
                                    and weapon_variant or payload)
                                .. "," .. string.format("%08x", unit_datum)
                        end
                        if operation == "weapon_variant" then
                            operation = "variant"
                        end
                    end
                    if operation == "object_teleport" then
                        x, y, z = object_tx, object_ty, object_tz
                        payload = object_target
                    elseif operation == "object_position" then
                        -- payload already holds last|aXXXXXXXX|uXXXXXXXX
                        x, y, z = 0.0, 0.0, 0.0
                    end
                    if operation == "ai" or operation == "ai_team" then
                        -- Campaign Evolved's UE scene uses centimetres while the
                        -- simulation uses 10-foot world units, with the Y axis
                        -- mirrored. Offset along the player's right so native AI
                        -- is created beside the controlled player.
                        x = x / 304.8
                        y = -y / 304.8
                        z = z / 304.8
                        x = x + ai_right_x * formation_offset_x
                        y = y + ai_right_y * formation_offset_x
                    elseif operation == "weapon"
                        or operation == "biped"
                        or operation == "biped_body"
                        or operation == "biped_variant_body" then
                        -- Unreal exposes centimeters; Blam scenario points use
                        -- simulation world units. Writing the centimeter values
                        -- placed AI roughly 100x outside the BSP and crashed
                        -- during deferred actor/pathfinding initialization.
                        x = x / 100.0
                        y = y / 100.0
                        z = z / 100.0
                    end
                    local coord_lines = (operation == "ai" or operation == "ai_team")
                        and string.format(
                            "%.9g\n%.9g\n%.9g\n%.9g\n%.9g\n",
                            x, y, z, ai_right_x, ai_right_y)
                        or string.format("%.9g\n%.9g\n%.9g\n", x, y, z)
                    os.remove(native_result_path)
                    local wrote, write_error = write_atomic(
                        native_request_path,
                        "HMBLAM2\n" .. request_id .. "\n" .. operation .. "\n"
                            .. payload .. "\n"
                            .. coord_lines
                    )
                    if not wrote then
                        error("Could not write the native spawn request: " .. tostring(write_error))
                    end

                    get_blam_invoke()()
                end, debug.traceback)

                if not ok then
                    os.remove(native_request_path)
                    os.remove(native_result_path)
                    write_result(request_id, "error", dispatch_error)
                    return
                end

                local started = os.time()
                LoopAsync(25, function()
                    local file = io.open(native_result_path, "rb")
                    if not file then
                        if os.difftime(os.time(), started) < 10 then
                            return false
                        end
                        os.remove(native_request_path)
                        write_result(
                            request_id,
                            "error",
                            "No Blam simulation thread claimed the creation request within 10 seconds."
                        )
                        return true
                    end

                    local magic = file:read("*l")
                    local result_id = file:read("*l")
                    local status = file:read("*l")
                    local native_message = file:read("*a")
                    file:close()
                    os.remove(native_request_path)
                    os.remove(native_result_path)
                    if magic ~= "HMBRES1" or result_id ~= request_id then
                        write_result(
                            request_id,
                            "error",
                            "The native Blam bridge returned an invalid result."
                        )
                    elseif status == "ok" then
                        write_result(request_id, "ok", native_message)
                    elseif status == "submitted" then
                        write_result(request_id, "submitted", native_message)
                    else
                        write_result(
                            request_id,
                            "error",
                            native_message or "Native Blam creation failed."
                        )
                    end
                    return true
                end)
            end)
        end

        local function execute_cheat_hook(request_id, payload)
            local name, value
            if payload == "read" then
                name, value = "read", "0"
            else
                name, value = payload:match("^([a-z_]+)=([01])$")
            end
            if not name or (name ~= "read"
                and name ~= "infinite_health"
                and name ~= "infinite_ammo"
                and name ~= "jetpack") then
                write_result(
                    request_id,
                    "error",
                    "The separate gameplay-cheat request is invalid."
                )
                return
            end

            ExecuteInGameThread(function()
                local ok, dispatch_error = xpcall(function()
                    os.remove(cheat_result_path)
                    local wrote, write_error = write_atomic(
                        cheat_request_path,
                        "HMCHEAT1\n" .. request_id .. "\n"
                            .. name .. "\n" .. value
                    )
                    if not wrote then
                        error("Could not write the gameplay-cheat request: "
                            .. tostring(write_error))
                    end
                    get_cheat_invoke()()
                end, debug.traceback)

                if not ok then
                    os.remove(cheat_request_path)
                    os.remove(cheat_result_path)
                    write_result(request_id, "error", dispatch_error)
                    return
                end

                local started = os.time()
                LoopAsync(25, function()
                    local file = io.open(cheat_result_path, "rb")
                    if not file then
                        if os.difftime(os.time(), started) < 10 then
                            return false
                        end
                        os.remove(cheat_request_path)
                        write_result(
                            request_id,
                            "error",
                            "No eligible campaign simulation thread applied the gameplay cheat within 10 seconds."
                        )
                        return true
                    end

                    local magic = file:read("*l")
                    local result_id = file:read("*l")
                    local status = file:read("*l")
                    local native_message = file:read("*a")
                    file:close()
                    os.remove(cheat_request_path)
                    os.remove(cheat_result_path)
                    if magic ~= "HMBRES1" or result_id ~= request_id then
                        write_result(
                            request_id,
                            "error",
                            "The gameplay-cheat hook returned an invalid result."
                        )
                    elseif status == "ok" then
                        write_result(request_id, "ok", native_message)
                    else
                        write_result(
                            request_id,
                            "error",
                            native_message or "The gameplay-cheat hook failed."
                        )
                    end
                    return true
                end)
            end)
        end

        local function read_request()
            if not os.rename(request_path, processing_path) then return nil end
            local file, open_error = io.open(processing_path, "rb")
            if not file then
                os.remove(processing_path)
                print(string.format(
                    "[HaloMeister] Could not open scripting request: %s\n",
                    tostring(open_error)
                ))
                return nil
            end

            local magic = file:read("*l")
            local request_id = file:read("*l")
            local kind = file:read("*l")
            local code = file:read("*a")
            file:close()
            os.remove(processing_path)

            if magic ~= request_magic then
                write_result(request_id or "invalid", "error", "Invalid request header.")
                return nil
            end
            if not request_id or #request_id ~= 32
                or not request_id:match("^[0-9a-f]+$") then
                write_result("invalid", "error", "Invalid request identifier.")
                return nil
            end
            if not code or #code == 0 or #code > maximum_code_bytes then
                write_result(request_id, "error", "Script is empty or exceeds the 64 KiB limit.")
                return nil
            end
            if code:find("\0", 1, true) then
                write_result(request_id, "error", "Scripts may not contain NUL bytes.")
                return nil
            end
            return { id = request_id, kind = kind, code = code }
        end

        local function process_request(request)
            if request.kind == "lua" then
                execute_lua(request.id, request.code)
            elseif request.kind == "haloscript" then
                execute_haloscript(request.id, request.code)
            elseif request.kind == "console" then
                execute_console(request.id, request.code, "Console command")
            elseif request.kind == "blam_spawn" then
                execute_blam_spawn(request.id, "object", request.code)
            elseif request.kind == "blam_ai_spawn" then
                execute_blam_spawn(request.id, "ai", request.code)
            elseif request.kind == "blam_ai_team_spawn" then
                execute_blam_spawn(request.id, "ai_team", request.code)
            elseif request.kind == "blam_weapon_load" then
                execute_blam_spawn(request.id, "weapon", request.code)
            elseif request.kind == "blam_object_variant" then
                execute_blam_spawn(request.id, "variant", request.code)
            elseif request.kind == "blam_object_colors" then
                execute_blam_spawn(request.id, "colors", request.code)
            elseif request.kind == "blam_weapon_variant" then
                execute_blam_spawn(request.id, "weapon_variant", request.code)
            elseif request.kind == "blam_biped_possess" then
                execute_blam_spawn(request.id, "biped", request.code)
            elseif request.kind == "blam_biped_spawn" then
                execute_blam_spawn(request.id, "biped_body", request.code)
            elseif request.kind == "blam_biped_variant_spawn" then
                execute_blam_spawn(request.id, "biped_variant_body", request.code)
            elseif request.kind == "blam_bump_possession_off" then
                execute_blam_spawn(request.id, "bump_off", request.code)
            elseif request.kind == "blam_cheat_globals_read" then
                execute_cheat_hook(request.id, "read")
            elseif request.kind == "blam_cheat_global_write" then
                execute_cheat_hook(request.id, request.code)
            elseif request.kind == "blam_skulls_read" then
                execute_blam_spawn(request.id, "skull_read", request.code)
            elseif request.kind == "blam_skull_write" then
                execute_blam_spawn(request.id, "skull_write", request.code)
            elseif request.kind == "blam_soft_ceiling_read" then
                execute_blam_spawn(request.id, "soft_ceiling_read", request.code)
            elseif request.kind == "blam_soft_ceiling_write" then
                execute_blam_spawn(request.id, "soft_ceiling_write", request.code)
            elseif request.kind == "blam_boundaries_read" then
                execute_blam_spawn(request.id, "boundary_read", request.code)
            elseif request.kind == "blam_boundaries_disable" then
                execute_blam_spawn(request.id, "boundary_disable", request.code)
            elseif request.kind == "blam_boundaries_restore" then
                execute_blam_spawn(request.id, "boundary_restore", request.code)
            elseif request.kind == "player_teleport" then
                execute_blam_spawn(request.id, "player_teleport", request.code)
            elseif request.kind == "player_noclip" then
                execute_blam_spawn(request.id, "player_noclip", request.code)
            elseif request.kind == "player_team" then
                execute_blam_spawn(request.id, "player_team", request.code)
            elseif request.kind == "object_team" then
                execute_blam_spawn(request.id, "object_team", request.code)
            elseif request.kind == "object_position" then
                execute_blam_spawn(request.id, "object_position", request.code)
            elseif request.kind == "object_teleport" then
                execute_blam_spawn(request.id, "object_teleport", request.code)
            elseif request.kind == "player_input" then
                execute_blam_spawn(request.id, "player_input", request.code)
            elseif request.kind == "player_weapon_normalize" then
                execute_player_weapon_normalize(request.id)
            elseif request.kind == "blam_machinima" then
                execute_blam_spawn(request.id, "machinima", request.code)
            elseif request.kind == "blam_tag_asset_load" then
                execute_blam_tag_asset_load(request.id, request.code)
            elseif request.kind == "player_position" then
                execute_blam_spawn(request.id, "player_position", "read")
            elseif request.kind == "blam_research_call" then
                execute_blam_spawn(request.id, "research_call", request.code)
            elseif request.kind == "player_unit_tag_read" then
                execute_player_unit_tag_read(request.id)
            elseif request.kind == "machinima_state" then
                execute_machinima(request.id, "state", request.code)
            elseif request.kind == "machinima_nodes" then
                execute_machinima(request.id, "nodes", request.code)
            elseif request.kind == "machinima_enable" then
                execute_machinima(request.id, "enable", request.code)
            elseif request.kind == "machinima_disable" then
                execute_machinima(request.id, "disable", request.code)
            elseif request.kind == "machinima_camera_teleport" then
                execute_machinima(request.id, "teleport", request.code)
            else
                write_result(
                    request.id,
                    "error",
                    "Unsupported script kind: " .. tostring(request.kind)
                )
            end
        end

        local function poll()
            local now = os.time()
            if now ~= last_heartbeat then
                local wrote, write_error = write_atomic(
                    status_path,
                    status_magic .. "\n" .. now .. "\nready\n" .. bridge_version
                )
                if wrote then
                    last_heartbeat = now
                elseif write_error then
                    print(string.format(
                        "[HaloMeister] Could not write scripting heartbeat: %s\n",
                        tostring(write_error)
                    ))
                end
            end
            if not ensure_ue_helpers() then return end

            local ok, request_or_error = xpcall(read_request, debug.traceback)
            if ok and request_or_error then
                local dispatched, dispatch_error = xpcall(
                    function() process_request(request_or_error) end,
                    debug.traceback
                )
                if not dispatched then
                    write_result(request_or_error.id, "error", dispatch_error)
                end
            elseif not ok then
                print(string.format(
                    "[HaloMeister] Scripting bridge poll failed: %s\n",
                    tostring(request_or_error)
                ))
            end

            -- AI capture is optional. Do not let an unavailable native DLL or a
            -- transient bootstrap failure block the base heartbeat or mailbox.
            if ai_capture_bootstrap == nil and now >= next_ai_capture_attempt then
                next_ai_capture_attempt = now + 5
                local loaded, bootstrap_or_error = pcall(
                    package.loadlib,
                    native_module_path,
                    "HaloMeisterAiCaptureBootstrap"
                )
                if loaded and bootstrap_or_error then
                    ai_capture_bootstrap = bootstrap_or_error
                    print("[HaloMeister] Native AI capture bootstrap loaded\n")
                else
                    print(string.format(
                        "[HaloMeister] Native AI capture bootstrap will retry: %s\n",
                        tostring(bootstrap_or_error)
                    ))
                end
            end
            if ai_capture_bootstrap ~= nil then
                local bootstrapped, bootstrap_error = pcall(ai_capture_bootstrap)
                if not bootstrapped then
                    print(string.format(
                        "[HaloMeister] Native AI capture bootstrap will retry: %s\n",
                        tostring(bootstrap_error)
                    ))
                    ai_capture_bootstrap = nil
                    next_ai_capture_attempt = now + 5
                end
            end
        end

        print(string.format(
            "[HaloMeister] Scripting bridge HMREQ1 v%d loaded\n",
            bridge_version
        ))
        poll()
        LoopAsync(poll_delay_ms, function()
            poll()
            return false
        end)
    end)

    if not hm_ok then
        print(string.format(
            "[HaloMeister] Scripting bridge failed to load: %s\n",
            tostring(hm_error)
        ))
    end
end
-- HALOMEISTER SCRIPTING BRIDGE:END
