#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <TlHelp32.h>
#include <intrin.h>

#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
constexpr char kRequestMagic[] = "HMBLAM2";
constexpr char kCheatRequestMagic[] = "HMCHEAT1";
constexpr char kResultMagic[] = "HMBRES1";
constexpr wchar_t kSimulationModule[] = L"HaloSimulation_tag_release.dll";

constexpr std::size_t kThreadGameStateOffset = 0x60;
constexpr std::size_t kThreadObjectAllegianceStateOffset = 0x130;
constexpr std::size_t kThreadKillVolumeStateOffset = 0x340;
constexpr std::size_t kThreadMachinimaCameraStateOffset = 0xB8;
constexpr std::size_t kMachinimaEnabledOffset = 0x9C8;
constexpr std::size_t kMachinimaResetOffset = 0x9C9;
constexpr std::size_t kMachinimaMirrorOffset = 0x9CA;
constexpr std::size_t kGameStateSkullMaskOffset = 0x1EBE0;
constexpr std::size_t kObjectAllegianceEntriesOffset = 0x1C4;
constexpr std::size_t kObjectAllegianceEntryCount = 16;
constexpr std::size_t kUnitTeamOffset = 0x1BA;
constexpr std::uint32_t kUnitObjectTypeMask = 0x1003;
constexpr std::size_t kScenarioTriggerVolumesOffset = 0x278;
constexpr std::size_t kScenarioKillTriggersOffset = 0x45C;
constexpr std::size_t kScenarioTriggerVolumeSize = 0x7C;
constexpr std::size_t kTriggerVolumeKillIndexOffset = 0x78;
constexpr std::size_t kPlacementSize = 0x330;
constexpr std::size_t kPositionOffset = 0x1C;
constexpr std::size_t kHookLength = 15;
constexpr std::size_t kCommandPumpHookLength = 16;
constexpr std::size_t kObjectNewHookLength = 16;
constexpr std::size_t kActorNewHookLength = 15;
constexpr std::size_t kActorStartingLocationSize = 0x70;
constexpr std::size_t kActorCharacterDatumOffset = 0x24;
constexpr std::size_t kActorVariantOffset = 0x48;
constexpr std::size_t kActorRecordSize = 0xD10;
constexpr std::size_t kActorUnitDatumOffset = 0x1C;
constexpr std::size_t kThreadActorDataOffset = 0x28;
constexpr std::size_t kHsHookLength = 20;
constexpr std::uintptr_t kHsArgumentsEvaluateRva = 0x001FEC90;
constexpr std::uintptr_t kHsReturnRva = 0x001FE0F0;
constexpr std::uintptr_t kAiPlayerAddFireteamSquadRva = 0x001CDFE0;
constexpr std::uintptr_t kAiPlayerAddFireteamSquadArgumentsReturnRva =
    0x001CE010;
constexpr std::uintptr_t kAiPlayerAddFireteamSquadHsReturnRva =
    0x001CE42C;
constexpr std::int16_t kAiPlayerAddFireteamSquadOpcode = 572;
constexpr std::array<std::uint8_t, kHsHookLength>
    kHsArgumentsEvaluatePrologue{
        0x48, 0x8B, 0xC4, 0x44, 0x88, 0x48, 0x20, 0x4C,
        0x89, 0x40, 0x18, 0x66, 0x89, 0x50, 0x10, 0x53,
        0x56, 0x57, 0x41, 0x56,
    };
constexpr std::array<std::uint8_t, kHsHookLength> kHsReturnPrologue{
    0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x56, 0x48,
    0x83, 0xEC, 0x20, 0x65, 0x48, 0x8B, 0x04, 0x25,
    0x58, 0x00, 0x00, 0x00,
};
constexpr std::array<std::uint8_t, 16>
    kAiPlayerAddFireteamSquadPrologue{
        0x89, 0x54, 0x24, 0x10, 0x48, 0x83, 0xEC, 0x78,
        0x8B, 0xC2, 0x45, 0x0F, 0xB6, 0xC8, 0x48, 0x0F,
    };
constexpr std::uintptr_t kAiObjectStateResolveRva = 0x000FCEC0;
constexpr std::uintptr_t kAiObjectSetTeamRva = 0x00087540;
constexpr std::array<std::uint8_t, 16> kAiObjectStateResolvePrologue{
    0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41,
    0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x8B,
};
constexpr std::array<std::uint8_t, 16> kAiObjectSetTeamPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x10, 0x89, 0x4C, 0x24,
    0x08, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55,
};
constexpr std::uintptr_t kAiPlacePreObjectReturnRva = 0x0004A875;
constexpr std::size_t kMaxAiPlacements = 5;

#include "generated_game_build.h"
#include "generated_research_hooks.h"

enum class SpawnKind
{
    object,
    weapon,
    variant,
    colors,
    biped,
    biped_body,
    biped_variant_body,
    bump_off,
    cheat_read,
    cheat_write,
    skull_read,
    skull_write,
    soft_ceiling_read,
    soft_ceiling_write,
    boundary_read,
    boundary_disable,
    boundary_restore,
    player_position,
    player_teleport,
    player_noclip,
    player_team,
    object_team,
    object_position,
    object_teleport,
    player_input,
    machinima,
    ai,
    research_call,
    saved_film,
};

struct SkullDefinition
{
    const char* name;
    std::uint8_t index;
};

// Exact order of game_skull_enum_definition in the supported simulation build.
constexpr std::array<SkullDefinition, 56> kSkulls{{
    {"skull_iron", 0},
    {"skull_black_eye", 1},
    {"skull_tough_luck", 2},
    {"skull_catch", 3},
    {"skull_fog", 4},
    {"skull_famine", 5},
    {"skull_thunderstorm", 6},
    {"skull_tilt", 7},
    {"skull_mythic", 8},
    {"skull_assassin", 9},
    {"skull_blind", 10},
    {"skull_superman", 11},
    {"skull_birthday_party", 12},
    {"skull_daddy", 13},
    {"skull_red", 14},
    {"skull_yellow", 15},
    {"skull_blue", 16},
    {"skull_angry", 17},
    {"skull_bandanna", 18},
    {"skull_bonded_pair", 19},
    {"skull_boom", 20},
    {"skull_envy", 21},
    {"skull_eye_patch", 22},
    {"skull_foreign", 23},
    {"skull_ghost", 24},
    {"skull_grunt_funeral", 25},
    {"skull_jacked", 26},
    {"skull_malfunction", 27},
    {"skull_masterblaster", 28},
    {"skull_pinata", 29},
    {"skull_recession", 30},
    {"skull_scarab", 31},
    {"skull_so_angry", 32},
    {"skull_swarm", 33},
    {"skull_thats_just_wrong", 34},
    {"skull_they_come_back", 35},
    {"skull_boots_off_the_ground", 36},
    {"skull_adaptation", 37},
    {"skull_reload", 38},
    {"skull_spore_visibility", 39},
    {"skull_night_vision", 40},
    {"skull_lights_out", 41},
    {"skull_riskrun", 42},
    {"skull_pop", 43},
    {"skull_armistice", 44},
    {"skull_fragile", 45},
    {"skull_give_and_take", 46},
    {"skull_stow_and_grow", 47},
    {"skull_hip_fire", 48},
    {"skull_temperamental", 49},
    {"skull_floor_is_lava", 50},
    {"skull_magnified", 51},
    {"skull_johnny_ammo_tree", 52},
    {"skull_leadhead", 53},
    {"skull_efficient", 54},
    {"skull_third_person", 55},
}};

struct SpawnRequest
{
    std::string id;
    SpawnKind kind{SpawnKind::object};
    std::uint32_t tag_datum{};
    std::uint32_t variant_string_id{};
    std::int32_t unit_datum{-1};
    std::uint8_t color_mask{};
    std::array<float, 12> colors{};
    std::uint16_t squad_index{};
    std::uint16_t ai_placement_count{1};
    std::uintptr_t ai_team_address{};
    std::uint16_t ai_team_value{};
    std::array<std::uintptr_t, kMaxAiPlacements>
        character_reference_addresses{};
    std::array<std::uintptr_t, kMaxAiPlacements> spawn_position_addresses{};
    std::array<std::uintptr_t, kMaxAiPlacements> actor_variant_addresses{};
    std::string saved_film_path;
    std::string cheat_name;
    bool cheat_value{};
    std::int16_t player_team{-1};
    std::int32_t ally_player_unit{-1};
    std::array<std::uint8_t, 16> character_reference{};
    std::array<std::uint8_t, 4> actor_variant{};
    std::uint32_t ai_weapon_datum{UINT32_MAX};
    bool ai_follow_player{};
    float ai_right_x{1.0f};
    float ai_right_y{0.0f};
    std::uint32_t research_rva{};
    std::array<std::uint8_t, 16> research_prologue{};
    std::uint8_t research_argument_count{};
    std::array<std::uint64_t, 4> research_arguments{};
    float x{};
    float y{};
    float z{};
};

struct CheatHookRequest
{
    std::string id;
    std::string name;
    bool is_read{};
    bool enabled{};
};

using SimulationContext = void* (*)();
using CommandPump = void (*)(void*);
using ObjectNew = std::int32_t (*)(void*);
using ActorNew = std::int32_t (*)(std::int16_t, const void*);
using ActorStartingLocationsBuild = void (*)(
    std::int32_t,
    std::int32_t,
    const void*,
    void*,
    const void*);
using HsArgumentsEvaluate = void* (*)(
    std::int32_t,
    std::int16_t,
    const void*,
    bool);
using HsReturn = void (*)(std::int32_t, std::int32_t);
using HsEvaluator = void (*)(std::int16_t, std::int32_t, bool);
SimulationContext g_original_simulation_context = nullptr;
CommandPump g_original_command_pump = nullptr;
ObjectNew g_original_object_new_for_ai = nullptr;
ActorNew g_original_actor_new = nullptr;
ActorStartingLocationsBuild g_actor_starting_locations_build = nullptr;
HsArgumentsEvaluate g_original_hs_arguments_evaluate = nullptr;
HsReturn g_original_hs_return = nullptr;
struct CapturedActorTemplate
{
    std::array<std::uint8_t, kActorStartingLocationSize> location{};
    std::int16_t encounter_index = -1;
    std::uint32_t character_datum = 0xFFFFFFFFu;
};
constexpr std::size_t kMaxCapturedActorTemplates = 64;
std::array<CapturedActorTemplate, kMaxCapturedActorTemplates>
    g_captured_actor_templates{};
std::size_t g_captured_actor_template_count = 0;
SRWLOCK g_captured_actor_template_lock = SRWLOCK_INIT;
SpawnRequest g_pending_request;
std::filesystem::path g_pending_result_path;
volatile LONG g_pending_state = 0;
ULONGLONG g_pending_request_due = 0;
ULONGLONG g_deferred_result_due = 0;
std::string g_deferred_result_message;
std::array<std::array<std::uint8_t, 16>, kMaxAiPlacements>
    g_deferred_ai_references{};
std::array<std::array<std::uint8_t, 12>, kMaxAiPlacements>
    g_deferred_ai_positions{};
std::array<std::array<std::uint8_t, 4>, kMaxAiPlacements>
    g_deferred_ai_variants{};
std::array<std::uint8_t, 2> g_deferred_ai_team{};
bool g_deferred_ai_patch_active = false;
// actor_new path keeps the borrowed squad team patched until deferred
// finalize finishes applying the live unit team; restoring earlier lets
// campaign sync re-stamp the original hostile team onto the new actor.
bool g_deferred_ai_squad_team_active = false;
std::array<std::int32_t, kMaxAiPlacements> g_deferred_ai_actors{};
std::int32_t g_last_ai_actor_datum = -1;
std::array<bool, kMaxAiPlacements> g_deferred_ai_weapon_done{};
std::array<bool, kMaxAiPlacements> g_deferred_ai_companion_done{};
ULONGLONG g_deferred_ai_finalize_deadline = 0;
bool g_deferred_ai_fireteam_done = false;
std::int32_t g_deferred_variant_object_datum = -1;
thread_local bool g_processing_spawn = false;
thread_local std::string g_ai_creation_diagnostic;
thread_local const SpawnRequest* g_active_ai_override = nullptr;
thread_local std::uint8_t* g_active_ai_module = nullptr;
thread_local bool g_hs_fireteam_override_active = false;
thread_local std::uint8_t* g_hs_fireteam_module = nullptr;
thread_local std::array<std::int32_t, 2> g_hs_fireteam_arguments{};
thread_local std::size_t g_active_ai_actor_index = 0;
std::vector<std::uint32_t> g_boundary_snapshot;
std::uintptr_t g_boundary_scenario_root = 0;
std::size_t g_boundary_kill_count = 0;
bool g_boundary_snapshot_valid = false;
bool g_noclip_active = false;
bool g_noclip_jetpack_was_enabled = false;
struct PlayerTeamSnapshot
{
    bool valid{};
    bool had_override{};
    std::int32_t unit_datum{-1};
    std::size_t slot{};
    std::int8_t original_override_team{-1};
    std::int8_t original_unit_team{-1};
    std::int8_t desired_team{-1};
};
PlayerTeamSnapshot g_player_team_snapshot;
struct MachinimaSnapshot
{
    bool valid{};
    std::array<std::uint8_t, 3> values{};
};
MachinimaSnapshot g_machinima_snapshot;
CheatHookRequest g_cheat_hook_request;
std::filesystem::path g_cheat_hook_result_path;
volatile LONG g_cheat_hook_state = 0;
ULONGLONG g_cheat_hook_due = 0;

void* hooked_simulation_context();
void hooked_command_pump(void* context);
std::int32_t hooked_object_new_for_ai(void* placement);
std::int32_t hooked_actor_new(
    std::int16_t encounter_index,
    const void* starting_location);
void install_ai_spawn_hooks(std::uint8_t* module);
void install_hs_fireteam_hooks(std::uint8_t* module);
bool writable_range(std::uintptr_t address, std::size_t size);
void process_cheat_hook_request(std::uint8_t* module);
DWORD invoke_object_set_variant(
    std::uint8_t* module,
    std::int32_t unit_datum,
    std::uint32_t variant_string_id,
    std::uintptr_t* exception_address);
void delete_object_noexcept(
    std::uint8_t* module,
    std::int32_t object_datum);

std::filesystem::path bridge_root()
{
    std::array<wchar_t, 32768> buffer{};
    DWORD length = GetEnvironmentVariableW(
        L"LOCALAPPDATA", buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
    {
        return {};
    }
    return std::filesystem::path(buffer.data()) /
           L"Meteorite" / L"Saved" / L"HaloMeister" / L"Scripting";
}

void write_result(
    const std::filesystem::path& path,
    const std::string& request_id,
    const char* status,
    const std::string& message)
{
    std::filesystem::create_directories(path.parent_path());
    std::filesystem::path temporary = path;
    temporary += L".tmp";
    {
        std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
        output << kResultMagic << '\n'
               << request_id << '\n'
               << status << '\n'
               << message;
        output.flush();
        if (!output)
        {
            return;
        }
    }
    MoveFileExW(
        temporary.c_str(),
        path.c_str(),
        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

bool parse_request(
    const std::filesystem::path& path,
    SpawnRequest& request,
    std::string& error)
{
    std::ifstream input(path, std::ios::binary);
    std::string magic;
    std::string operation;
    std::string payload;
    std::string x;
    std::string y;
    std::string z;
    std::string right_x;
    std::string right_y;
    if (!std::getline(input, magic) ||
        !std::getline(input, request.id) ||
        !std::getline(input, operation) ||
        !std::getline(input, payload) ||
        !std::getline(input, x) ||
        !std::getline(input, y) ||
        !std::getline(input, z))
    {
        error = "The native spawn request is incomplete.";
        return false;
    }
    if (std::getline(input, right_x) && std::getline(input, right_y))
    {
        char* right_end = nullptr;
        request.ai_right_x = std::strtof(right_x.c_str(), &right_end);
        if (!right_end || *right_end != '\0' || !std::isfinite(request.ai_right_x))
            request.ai_right_x = 1.0f;
        request.ai_right_y = std::strtof(right_y.c_str(), &right_end);
        if (!right_end || *right_end != '\0' || !std::isfinite(request.ai_right_y))
            request.ai_right_y = 0.0f;
    }
    if (magic != kRequestMagic ||
        request.id.size() != 32 ||
        request.id.find_first_not_of("0123456789abcdef") != std::string::npos)
    {
        error = "The native spawn request header is invalid.";
        return false;
    }

    if (operation == "object")
    {
        request.kind = SpawnKind::object;
        if (payload.size() != 17 ||
            payload[8] != ',' ||
            payload.find_first_not_of(
                "0123456789abcdefABCDEF,",
                0) != std::string::npos)
        {
            error =
                "The object payload must contain tag and controlled-player "
                "datums as XXXXXXXX,XXXXXXXX.";
            return false;
        }
        auto result = std::from_chars(
            payload.data(), payload.data() + 8, request.tag_datum, 16);
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data() + 9, payload.data() + 17, unit_datum, 16);
        if (result.ec != std::errc{} ||
            result.ptr != payload.data() + 8 ||
            unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + 17 ||
            unit_datum == UINT32_MAX)
        {
            error = "The object tag or controlled-player datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "biped" || operation == "biped_body")
    {
        request.kind = operation == "biped"
            ? SpawnKind::biped
            : SpawnKind::biped_body;
        if (payload.size() != 17 ||
            payload[8] != ',' ||
            payload.find_first_not_of(
                "0123456789abcdefABCDEF,",
                0) != std::string::npos)
        {
            error =
                "The biped payload must contain tag and controlled-player datums "
                "as XXXXXXXX,XXXXXXXX.";
            return false;
        }
        auto result = std::from_chars(
            payload.data(), payload.data() + 8, request.tag_datum, 16);
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data() + 9, payload.data() + 17, unit_datum, 16);
        if (result.ec != std::errc{} ||
            result.ptr != payload.data() + 8 ||
            unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + 17 ||
            unit_datum == UINT32_MAX)
        {
            error = "The biped tag or controlled-player datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "biped_variant_body")
    {
        request.kind = SpawnKind::biped_variant_body;
        if (payload.size() != 26 ||
            payload[8] != ',' ||
            payload[17] != ',' ||
            payload.find_first_not_of(
                "0123456789abcdefABCDEF,",
                0) != std::string::npos)
        {
            error =
                "The variant biped payload must use "
                "TTTTTTTT,VVVVVVVV,PPPPPPPP.";
            return false;
        }
        std::uint32_t unit_datum = 0;
        auto tag_result = std::from_chars(
            payload.data(), payload.data() + 8, request.tag_datum, 16);
        auto variant_result = std::from_chars(
            payload.data() + 9,
            payload.data() + 17,
            request.variant_string_id,
            16);
        auto unit_result = std::from_chars(
            payload.data() + 18,
            payload.data() + 26,
            unit_datum,
            16);
        if (tag_result.ec != std::errc{} ||
            tag_result.ptr != payload.data() + 8 ||
            variant_result.ec != std::errc{} ||
            variant_result.ptr != payload.data() + 17 ||
            unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + 26 ||
            unit_datum == UINT32_MAX)
        {
            error = "The variant biped payload contains an invalid datum.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "bump_off")
    {
        request.kind = SpawnKind::bump_off;
        if (payload != "off")
        {
            error = "The bump-possession disable request is invalid.";
            return false;
        }
    }
    else if (operation == "cheat_read")
    {
        request.kind = SpawnKind::cheat_read;
        if (payload != "read")
        {
            error = "The cheat-global read request is invalid.";
            return false;
        }
    }
    else if (operation == "cheat_write")
    {
        request.kind = SpawnKind::cheat_write;
        std::size_t separator = payload.find('=');
        if (separator == std::string::npos ||
            separator == 0 ||
            separator + 2 != payload.size() ||
            (payload.back() != '0' && payload.back() != '1'))
        {
            error = "The cheat-global write must use name=0 or name=1.";
            return false;
        }
        request.cheat_name = payload.substr(0, separator);
        request.cheat_value = payload.back() == '1';
        bool known = std::any_of(
            kCheatGlobals.begin(),
            kCheatGlobals.end(),
            [&](const CheatGlobal& global)
            {
                return request.cheat_name == global.name;
            });
        if (!known)
        {
            error = "The requested cheat global is not in the verified catalog.";
            return false;
        }
    }
    else if (operation == "skull_read")
    {
        request.kind = SpawnKind::skull_read;
        if (payload != "read")
        {
            error = "The live-skull read request is invalid.";
            return false;
        }
    }
    else if (operation == "skull_write")
    {
        request.kind = SpawnKind::skull_write;
        std::size_t separator = payload.find('=');
        if (separator == std::string::npos ||
            separator == 0 ||
            separator + 2 != payload.size() ||
            (payload.back() != '0' && payload.back() != '1'))
        {
            error = "The live-skull write must use name=0 or name=1.";
            return false;
        }
        request.cheat_name = payload.substr(0, separator);
        request.cheat_value = payload.back() == '1';
        bool known = std::any_of(
            kSkulls.begin(),
            kSkulls.end(),
            [&](const SkullDefinition& skull)
            {
                return request.cheat_name == skull.name;
            });
        if (!known)
        {
            error = "The requested skull is not in the verified runtime catalog.";
            return false;
        }
    }
    else if (operation == "soft_ceiling_read")
    {
        request.kind = SpawnKind::soft_ceiling_read;
        if (payload != "read")
        {
            error = "The soft-ceiling read request is invalid.";
            return false;
        }
    }
    else if (operation == "soft_ceiling_write")
    {
        request.kind = SpawnKind::soft_ceiling_write;
        if (payload != "0" && payload != "1")
        {
            error = "The soft-ceiling write request must be 0 or 1.";
            return false;
        }
        request.cheat_value = payload == "1";
    }
    else if (operation == "boundary_read")
    {
        request.kind = SpawnKind::boundary_read;
        if (payload != "read")
        {
            error = "The runtime-boundary read request is invalid.";
            return false;
        }
    }
    else if (operation == "boundary_disable")
    {
        request.kind = SpawnKind::boundary_disable;
        if (payload != "disable")
        {
            error = "The runtime-boundary disable request is invalid.";
            return false;
        }
    }
    else if (operation == "boundary_restore")
    {
        request.kind = SpawnKind::boundary_restore;
        if (payload != "restore")
        {
            error = "The runtime-boundary restore request is invalid.";
            return false;
        }
    }
    else if (operation == "player_position" ||
             operation == "player_teleport")
    {
        request.kind = operation == "player_position"
            ? SpawnKind::player_position
            : SpawnKind::player_teleport;
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data(), payload.data() + payload.size(), unit_datum, 16);
        if (payload.size() != 8 ||
            unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + payload.size() ||
            unit_datum == UINT32_MAX)
        {
            error = "The player-position datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "player_noclip")
    {
        request.kind = SpawnKind::player_noclip;
        std::size_t separator = payload.find(',');
        if (separator != 1 ||
            separator + 9 != payload.size() ||
            (payload[0] != '0' && payload[0] != '1'))
        {
            error =
                "The no-clip payload must use enabled,unit.";
            return false;
        }
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data() + separator + 1,
            payload.data() + payload.size(),
            unit_datum,
            16);
        if (unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + payload.size() ||
            unit_datum == UINT32_MAX)
        {
            error = "The no-clip player datum is invalid.";
            return false;
        }
        request.cheat_value = payload[0] == '1';
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "player_team")
    {
        request.kind = SpawnKind::player_team;
        std::size_t separator = payload.find(',');
        if (separator == std::string::npos ||
            separator + 9 != payload.size())
        {
            error = "The player-team payload must use action,unit.";
            return false;
        }
        std::string action = payload.substr(0, separator);
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data() + separator + 1,
            payload.data() + payload.size(),
            unit_datum,
            16);
        if (unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + payload.size() ||
            unit_datum == UINT32_MAX)
        {
            error = "The player-team unit datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
        request.cheat_name = action;
        if (action != "read" && action != "restore")
        {
            unsigned team = 0;
            auto team_result = std::from_chars(
                action.data(),
                action.data() + action.size(),
                team,
                10);
            if (team_result.ec != std::errc{} ||
                team_result.ptr != action.data() + action.size() ||
                team > 13)
            {
                error =
                    "The requested campaign team must be between 0 and 13.";
                return false;
            }
            request.player_team = static_cast<std::int16_t>(team);
        }
    }
    else if (operation == "object_position" ||
             operation == "object_teleport")
    {
        // target — last | aXXXXXXXX | uXXXXXXXX
        request.kind = operation == "object_position"
            ? SpawnKind::object_position
            : SpawnKind::object_teleport;
        std::string target = payload;
        if (target == "last")
        {
            request.cheat_name = "last";
            request.unit_datum = -1;
        }
        else if ((target[0] == 'a' || target[0] == 'A' ||
                  target[0] == 'u' || target[0] == 'U') &&
                 target.size() == 9)
        {
            request.cheat_name =
                (target[0] == 'u' || target[0] == 'U') ? "unit" : "actor";
            std::uint32_t datum = 0;
            auto datum_result = std::from_chars(
                target.data() + 1,
                target.data() + target.size(),
                datum,
                16);
            if (datum_result.ec != std::errc{} ||
                datum_result.ptr != target.data() + target.size() ||
                datum == UINT32_MAX)
            {
                error = "The object target datum is invalid.";
                return false;
            }
            request.unit_datum = static_cast<std::int32_t>(datum);
        }
        else
        {
            error =
                "The object target must be last, a<actor>, or u<unit>.";
            return false;
        }
    }
    else if (operation == "object_team")
    {
        // target,team[,playerUnit]
        request.kind = SpawnKind::object_team;
        std::array<std::string, 3> parts{};
        std::size_t start = 0;
        std::size_t part_count = 0;
        while (part_count < parts.size())
        {
            std::size_t comma = payload.find(',', start);
            parts[part_count++] = payload.substr(
                start,
                comma == std::string::npos ? std::string::npos : comma - start);
            if (comma == std::string::npos)
                break;
            start = comma + 1;
        }
        if (part_count != 2 && part_count != 3)
        {
            error = "The object-team payload must use target,team[,player].";
            return false;
        }
        std::string target = parts[0];
        unsigned team = 0;
        auto team_result = std::from_chars(
            parts[1].data(),
            parts[1].data() + parts[1].size(),
            team,
            10);
        if (team_result.ec != std::errc{} ||
            team_result.ptr != parts[1].data() + parts[1].size() ||
            team > 13)
        {
            error = "The requested campaign team must be between 0 and 13.";
            return false;
        }
        request.player_team = static_cast<std::int16_t>(team);
        if (part_count == 3)
        {
            if (parts[2].size() != 8)
            {
                error = "The object-team player datum is invalid.";
                return false;
            }
            std::uint32_t player_unit = 0;
            auto player_result = std::from_chars(
                parts[2].data(),
                parts[2].data() + parts[2].size(),
                player_unit,
                16);
            if (player_result.ec != std::errc{} ||
                player_result.ptr != parts[2].data() + parts[2].size() ||
                player_unit == UINT32_MAX)
            {
                error = "The object-team player datum is invalid.";
                return false;
            }
            request.ally_player_unit = static_cast<std::int32_t>(player_unit);
        }
        if (target == "last")
        {
            request.cheat_name = "last";
            request.unit_datum = -1;
        }
        else if ((target[0] == 'a' || target[0] == 'A' ||
                  target[0] == 'u' || target[0] == 'U') &&
                 target.size() == 9)
        {
            request.cheat_name =
                (target[0] == 'u' || target[0] == 'U') ? "unit" : "actor";
            std::uint32_t datum = 0;
            auto datum_result = std::from_chars(
                target.data() + 1,
                target.data() + target.size(),
                datum,
                16);
            if (datum_result.ec != std::errc{} ||
                datum_result.ptr != target.data() + target.size() ||
                datum == UINT32_MAX)
            {
                error = "The object-team datum is invalid.";
                return false;
            }
            request.unit_datum = static_cast<std::int32_t>(datum);
        }
        else
        {
            error =
                "The object-team payload must use last|aXXXXXXXX|uXXXXXXXX,...";
            return false;
        }
    }
    else if (operation == "player_input")
    {
        request.kind = SpawnKind::player_input;
        if (payload != "suppress" && payload != "restore")
        {
            error = "The player-input request must be suppress or restore.";
            return false;
        }
        request.cheat_value = payload == "restore";
    }
    else if (operation == "machinima")
    {
        request.kind = SpawnKind::machinima;
        if (payload != "read" && payload != "enable" &&
            payload != "disable" && payload != "restore")
        {
            error =
                "The native machinima request must be read, enable, disable, or restore.";
            return false;
        }
        request.cheat_name = payload;
    }
    else if (operation == "weapon")
    {
        request.kind = SpawnKind::weapon;
        if (payload.size() != 17 ||
            payload[8] != ',' ||
            payload.find_first_not_of("0123456789abcdefABCDEF,", 0) !=
                std::string::npos)
        {
            error =
                "The weapon payload must contain tag and player datums as "
                "XXXXXXXX,XXXXXXXX.";
            return false;
        }
        auto tag_result = std::from_chars(
            payload.data(), payload.data() + 8, request.tag_datum, 16);
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data() + 9, payload.data() + 17, unit_datum, 16);
        if (tag_result.ec != std::errc{} ||
            tag_result.ptr != payload.data() + 8 ||
            unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + 17 ||
            unit_datum == UINT32_MAX)
        {
            error = "The weapon or player datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "variant")
    {
        request.kind = SpawnKind::variant;
        if (payload.size() != 17 ||
            payload[8] != ',' ||
            payload.find_first_not_of("0123456789abcdefABCDEF,", 0) !=
                std::string::npos)
        {
            error =
                "The variant payload must contain string-id and player datums as "
                "XXXXXXXX,XXXXXXXX.";
            return false;
        }
        auto variant_result = std::from_chars(
            payload.data(), payload.data() + 8, request.variant_string_id, 16);
        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            payload.data() + 9, payload.data() + 17, unit_datum, 16);
        if (variant_result.ec != std::errc{} ||
            variant_result.ptr != payload.data() + 8 ||
            unit_result.ec != std::errc{} ||
            unit_result.ptr != payload.data() + 17 ||
            unit_datum == UINT32_MAX)
        {
            error = "The model variant or player datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "colors")
    {
        request.kind = SpawnKind::colors;
        std::array<std::string, 6> parts{};
        std::size_t start = 0;
        for (std::size_t index = 0; index < parts.size(); ++index)
        {
            std::size_t comma = payload.find(',', start);
            if ((index + 1 < parts.size() && comma == std::string::npos) ||
                (index + 1 == parts.size() && comma != std::string::npos))
            {
                error = "The color payload is incomplete.";
                return false;
            }
            parts[index] = payload.substr(
                start,
                comma == std::string::npos ? std::string::npos : comma - start);
            start = comma == std::string::npos ? payload.size() : comma + 1;
        }
        if (parts[0].size() != 2 ||
            parts[1].size() != 6 ||
            parts[2].size() != 6 ||
            parts[3].size() != 6 ||
            parts[4].size() != 6 ||
            parts[5].size() != 8)
        {
            error =
                "Colors must use MM,RRGGBB,RRGGBB,RRGGBB,RRGGBB,PPPPPPPP.";
            return false;
        }

        unsigned mask = 0;
        auto mask_result = std::from_chars(
            parts[0].data(), parts[0].data() + parts[0].size(), mask, 16);
        if (mask_result.ec != std::errc{} ||
            mask_result.ptr != parts[0].data() + parts[0].size() ||
            mask == 0 ||
            (mask & ~0x0Fu) != 0)
        {
            error = "The object-color channel mask is invalid.";
            return false;
        }
        request.color_mask = static_cast<std::uint8_t>(mask);

        for (std::size_t channel = 0; channel < 4; ++channel)
        {
            unsigned rgb = 0;
            auto rgb_result = std::from_chars(
                parts[channel + 1].data(),
                parts[channel + 1].data() + parts[channel + 1].size(),
                rgb,
                16);
            if (rgb_result.ec != std::errc{} ||
                rgb_result.ptr !=
                    parts[channel + 1].data() + parts[channel + 1].size())
            {
                error = "An object color is invalid.";
                return false;
            }
            request.colors[channel * 3] =
                static_cast<float>((rgb >> 16) & 0xFFu) / 255.0f;
            request.colors[channel * 3 + 1] =
                static_cast<float>((rgb >> 8) & 0xFFu) / 255.0f;
            request.colors[channel * 3 + 2] =
                static_cast<float>(rgb & 0xFFu) / 255.0f;
        }

        std::uint32_t unit_datum = 0;
        auto unit_result = std::from_chars(
            parts[5].data(),
            parts[5].data() + parts[5].size(),
            unit_datum,
            16);
        if (unit_result.ec != std::errc{} ||
            unit_result.ptr != parts[5].data() + parts[5].size() ||
            unit_datum == UINT32_MAX)
        {
            error = "The object-color player datum is invalid.";
            return false;
        }
        request.unit_datum = static_cast<std::int32_t>(unit_datum);
    }
    else if (operation == "ai" || operation == "ai_team")
    {
        request.kind = SpawnKind::ai;
        // A five-actor friendly team with a weapon override contains exactly
        // 22 fields (including the controlled-player datum). Keep one spare
        // slot so filling the last valid field is not mistaken for overflow.
        std::array<std::string, 23> parts{};
        std::size_t start = 0;
        std::size_t part_count = 0;
        while (part_count < parts.size())
        {
            std::size_t comma = payload.find(',', start);
            parts[part_count++] = payload.substr(
                start,
                comma == std::string::npos ? std::string::npos : comma - start);
            if (comma == std::string::npos)
            {
                break;
            }
            start = comma + 1;
        }
        if (start < payload.size() && part_count == parts.size())
        {
            error = "The AI spawn payload has too many fields.";
            return false;
        }
        if (part_count > 0 &&
            parts[part_count - 1].size() == 9 &&
            parts[part_count - 1][0] == 'p')
        {
            std::uint32_t player_datum = 0;
            auto player_result = std::from_chars(
                parts[part_count - 1].data() + 1,
                parts[part_count - 1].data() + 9,
                player_datum,
                16);
            if (player_result.ec != std::errc{} ||
                player_result.ptr != parts[part_count - 1].data() + 9 ||
                player_datum == UINT32_MAX)
            {
                error = "The AI companion player datum is invalid.";
                return false;
            }
            request.unit_datum = static_cast<std::int32_t>(player_datum);
            request.ai_follow_player = true;
            --part_count;
        }
        bool has_weapon_override = false;
        if (operation == "ai")
        {
            request.ai_placement_count = 1;
            if (part_count != 6 && part_count != 7)
            {
                error = "The AI spawn payload is incomplete.";
                return false;
            }
            has_weapon_override = part_count == 7;
        }
        else
        {
            // One placement is valid (allegiance demo / single companion).
            // Layout: squad,teamAddr,teamVal,(ref,pos,var)*N,charRef,variant[,weapon]
            if (part_count >= 8 && (part_count - 5) % 3 == 0)
            {
                request.ai_placement_count = static_cast<std::uint16_t>(
                    (part_count - 5) / 3);
            }
            else if (part_count >= 9 && (part_count - 6) % 3 == 0)
            {
                request.ai_placement_count = static_cast<std::uint16_t>(
                    (part_count - 6) / 3);
                has_weapon_override = true;
            }
            else
            {
                error =
                    "The AI team payload must contain between one and five placements.";
                return false;
            }
            if (request.ai_placement_count < 1 ||
                request.ai_placement_count > kMaxAiPlacements)
            {
                error =
                    "The AI team payload must contain between one and five placements.";
                return false;
            }
        }
        const std::size_t expected_parts =
            1 + (operation == "ai_team" ? 2 : 0) +
            request.ai_placement_count * 3 + 2 +
            (has_weapon_override ? 1 : 0);
        if (part_count != expected_parts)
        {
            error = "The AI spawn payload is incomplete.";
            return false;
        }
        const std::size_t placement_part = operation == "ai_team" ? 3 : 1;
        const std::size_t reference_part =
            placement_part + request.ai_placement_count * 3;
        const std::size_t variant_part = reference_part + 1;
        const std::size_t weapon_part = variant_part + 1;
        if (parts[0].size() != 4 ||
            parts[reference_part].size() != 32 ||
            parts[variant_part].size() != 8 ||
            (has_weapon_override && parts[weapon_part].size() != 8))
        {
            error = "The AI spawn payload has an invalid field width.";
            return false;
        }

        auto squad_result = std::from_chars(
            parts[0].data(), parts[0].data() + parts[0].size(),
            request.squad_index, 16);
        if (squad_result.ec != std::errc{})
        {
            error = "The AI spawn payload contains an invalid hexadecimal number.";
            return false;
        }
        if (operation == "ai_team")
        {
            if (parts[1].size() != 16 || parts[2].size() != 4)
            {
                error = "The AI team override has an invalid field width.";
                return false;
            }
            std::uint64_t team_address = 0;
            auto team_address_result = std::from_chars(
                parts[1].data(),
                parts[1].data() + parts[1].size(),
                team_address,
                16);
            auto team_value_result = std::from_chars(
                parts[2].data(),
                parts[2].data() + parts[2].size(),
                request.ai_team_value,
                16);
            if (team_address_result.ec != std::errc{} ||
                team_value_result.ec != std::errc{} ||
                request.ai_team_value > 15)
            {
                error = "The AI team override is invalid.";
                return false;
            }
            request.ai_team_address =
                static_cast<std::uintptr_t>(team_address);
        }
        for (std::size_t index = 0; index < request.ai_placement_count; ++index)
        {
            const std::size_t base = placement_part + index * 3;
            if (parts[base].size() != 16 ||
                parts[base + 1].size() != 16 ||
                parts[base + 2].size() != 16)
            {
                error = "The AI placement payload has an invalid address width.";
                return false;
            }
            std::uint64_t reference_address = 0;
            std::uint64_t position_address = 0;
            std::uint64_t variant_address = 0;
            auto reference_result = std::from_chars(
                parts[base].data(), parts[base].data() + parts[base].size(),
                reference_address, 16);
            auto position_result = std::from_chars(
                parts[base + 1].data(),
                parts[base + 1].data() + parts[base + 1].size(),
                position_address, 16);
            auto variant_result = std::from_chars(
                parts[base + 2].data(),
                parts[base + 2].data() + parts[base + 2].size(),
                variant_address, 16);
            if (reference_result.ec != std::errc{} ||
                position_result.ec != std::errc{} ||
                variant_result.ec != std::errc{})
            {
                error =
                    "The AI spawn payload contains an invalid hexadecimal address.";
                return false;
            }
            request.character_reference_addresses[index] =
                static_cast<std::uintptr_t>(reference_address);
            request.spawn_position_addresses[index] =
                static_cast<std::uintptr_t>(position_address);
            request.actor_variant_addresses[index] =
                static_cast<std::uintptr_t>(variant_address);
        }
        for (std::size_t index = 0; index < request.character_reference.size(); ++index)
        {
            unsigned value = 0;
            auto byte_result = std::from_chars(
                parts[reference_part].data() + index * 2,
                parts[reference_part].data() + index * 2 + 2,
                value,
                16);
            if (byte_result.ec != std::errc{})
            {
                error = "The character tag reference is invalid.";
                return false;
            }
            request.character_reference[index] = static_cast<std::uint8_t>(value);
        }
        for (std::size_t index = 0; index < request.actor_variant.size(); ++index)
        {
            unsigned value = 0;
            auto byte_result = std::from_chars(
                parts[variant_part].data() + index * 2,
                parts[variant_part].data() + index * 2 + 2,
                value,
                16);
            if (byte_result.ec != std::errc{})
            {
                error = "The AI spawn payload contains an invalid actor variant.";
                return false;
            }
            request.actor_variant[index] = static_cast<std::uint8_t>(value);
        }
        if (has_weapon_override)
        {
            auto weapon_result = std::from_chars(
                parts[weapon_part].data(),
                parts[weapon_part].data() + parts[weapon_part].size(),
                request.ai_weapon_datum,
                16);
            if (weapon_result.ec != std::errc{} ||
                weapon_result.ptr !=
                    parts[weapon_part].data() +
                        parts[weapon_part].size() ||
                request.ai_weapon_datum == UINT32_MAX)
            {
                error = "The AI weapon datum is invalid.";
                return false;
            }
        }
        constexpr std::array<std::uint8_t, 4> reversed_character_group{
            'r', 'a', 'h', 'c',
        };
        if (!std::equal(
                reversed_character_group.begin(),
                reversed_character_group.end(),
                request.character_reference.begin()))
        {
            error = "AI spawning requires a [char] tag reference.";
            return false;
        }
    }
    else if (operation == "research_call")
    {
        request.kind = SpawnKind::research_call;
        std::array<std::string, 7> parts{};
        std::size_t start = 0;
        for (std::size_t index = 0; index < parts.size(); ++index)
        {
            const std::size_t comma = payload.find(',', start);
            if ((index + 1 < parts.size() && comma == std::string::npos) ||
                (index + 1 == parts.size() && comma != std::string::npos))
            {
                error = "The native research-call payload is incomplete.";
                return false;
            }
            parts[index] = payload.substr(
                start,
                comma == std::string::npos ? std::string::npos : comma - start);
            start = comma == std::string::npos ? payload.size() : comma + 1;
        }
        if (parts[0].size() != 8 ||
            parts[1].size() != request.research_prologue.size() * 2 ||
            parts[2].size() != 1 ||
            std::any_of(
                parts.begin() + 3,
                parts.end(),
                [](const std::string& part) { return part.size() != 16; }))
        {
            error = "The native research-call payload has an invalid field width.";
            return false;
        }
        auto rva_result = std::from_chars(
            parts[0].data(),
            parts[0].data() + parts[0].size(),
            request.research_rva,
            16);
        unsigned argument_count = 0;
        auto count_result = std::from_chars(
            parts[2].data(),
            parts[2].data() + parts[2].size(),
            argument_count,
            16);
        if (rva_result.ec != std::errc{} ||
            rva_result.ptr != parts[0].data() + parts[0].size() ||
            count_result.ec != std::errc{} ||
            count_result.ptr != parts[2].data() + parts[2].size() ||
            argument_count > request.research_arguments.size())
        {
            error = "The native research-call RVA or argument count is invalid.";
            return false;
        }
        request.research_argument_count =
            static_cast<std::uint8_t>(argument_count);
        for (std::size_t index = 0;
             index < request.research_prologue.size();
             ++index)
        {
            unsigned value = 0;
            auto byte_result = std::from_chars(
                parts[1].data() + index * 2,
                parts[1].data() + index * 2 + 2,
                value,
                16);
            if (byte_result.ec != std::errc{} ||
                byte_result.ptr != parts[1].data() + index * 2 + 2)
            {
                error = "The native research-call prologue is invalid.";
                return false;
            }
            request.research_prologue[index] =
                static_cast<std::uint8_t>(value);
        }
        for (std::size_t index = 0;
             index < request.research_arguments.size();
             ++index)
        {
            auto argument_result = std::from_chars(
                parts[index + 3].data(),
                parts[index + 3].data() + parts[index + 3].size(),
                request.research_arguments[index],
                16);
            if (argument_result.ec != std::errc{} ||
                argument_result.ptr !=
                    parts[index + 3].data() + parts[index + 3].size())
            {
                error = "A native research-call argument is invalid.";
                return false;
            }
        }
    }
    else if (operation == "saved_film")
    {
        request.kind = SpawnKind::saved_film;
        if (payload.empty() ||
            payload.size() >= 0x200 ||
            payload.find_first_of("\r\n") != std::string::npos)
        {
            error =
                "The saved-film path is empty, too long, or contains a line break.";
            return false;
        }
        request.saved_film_path = payload;
    }
    else
    {
        error = "The native spawn operation is unsupported.";
        return false;
    }

    char* end = nullptr;
    request.x = std::strtof(x.c_str(), &end);
    if (!end || *end != '\0')
    {
        error = "The spawn X coordinate is invalid.";
        return false;
    }
    request.y = std::strtof(y.c_str(), &end);
    if (!end || *end != '\0')
    {
        error = "The spawn Y coordinate is invalid.";
        return false;
    }
    request.z = std::strtof(z.c_str(), &end);
    if (!end || *end != '\0')
    {
        error = "The spawn Z coordinate is invalid.";
        return false;
    }
    if (!std::isfinite(request.x) ||
        !std::isfinite(request.y) ||
        !std::isfinite(request.z))
    {
        error = "The native spawn request contains an invalid number.";
        return false;
    }
    return true;
}

bool validate_module(
    std::uint8_t* module,
    std::string& error,
    SpawnKind kind = SpawnKind::object)
{
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(module);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        error = "The loaded simulation module has an invalid DOS header.";
        return false;
    }
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(module + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        error = "This HaloSimulation_tag_release.dll build is not supported.";
        return false;
    }
    bool supported_build = false;
    for (const SupportedBuildIdentity& identity : kSupportedBuilds)
    {
        if (nt->FileHeader.TimeDateStamp == identity.timestamp &&
            nt->OptionalHeader.SizeOfImage == identity.image_size)
        {
            supported_build = true;
            break;
        }
    }
    if (!supported_build)
    {
        error = "This HaloSimulation_tag_release.dll build is not supported.";
        return false;
    }
    if (kind == SpawnKind::research_call)
    {
        if (g_original_simulation_context == nullptr &&
            std::memcmp(
                module + kSimulationContextRva,
                kSimulationContextPrologue.data(),
                kSimulationContextPrologue.size()) != 0)
        {
            error =
                "The simulation-thread signature does not match this game build.";
            return false;
        }
        return true;
    }
    if (std::memcmp(
            module + kPlacementInitializeRva,
            kPlacementInitializePrologue.data(),
            kPlacementInitializePrologue.size()) != 0 ||
        (g_original_object_new_for_ai == nullptr &&
         std::memcmp(
             module + kObjectNewRva,
             kObjectNewPrologue.data(),
             kObjectNewPrologue.size()) != 0) ||
        (kind == SpawnKind::weapon &&
         (std::memcmp(
              module + kObjectDeleteRva,
              kObjectDeletePrologue.data(),
              kObjectDeletePrologue.size()) != 0 ||
          std::memcmp(
              module + kUnitAddWeaponRva,
              kUnitAddWeaponPrologue.data(),
              kUnitAddWeaponPrologue.size()) != 0)) ||
        ((kind == SpawnKind::variant ||
          kind == SpawnKind::biped_variant_body) &&
         std::memcmp(
             module + kObjectSetVariantRva,
             kObjectSetVariantPrologue.data(),
             kObjectSetVariantPrologue.size()) != 0) ||
        (kind == SpawnKind::colors &&
         (std::memcmp(
              module + kObjectSetColorsRva,
              kObjectSetColorsPrologue.data(),
              kObjectSetColorsPrologue.size()) != 0 ||
          std::memcmp(
              module + kObjectChangedRva,
              kObjectChangedPrologue.data(),
              kObjectChangedPrologue.size()) != 0)) ||
        ((kind == SpawnKind::object ||
          kind == SpawnKind::biped ||
          kind == SpawnKind::biped_body ||
          kind == SpawnKind::biped_variant_body) &&
         (std::memcmp(
              module + kObjectGetPositionRva,
              kObjectGetPositionPrologue.data(),
              kObjectGetPositionPrologue.size()) != 0 ||
          std::memcmp(
              module + kObjectGetOrientationRva,
              kObjectGetOrientationPrologue.data(),
              kObjectGetOrientationPrologue.size()) != 0)) ||
        ((kind == SpawnKind::player_position ||
          kind == SpawnKind::player_teleport ||
          kind == SpawnKind::player_noclip ||
          kind == SpawnKind::object_position ||
          kind == SpawnKind::object_teleport) &&
         (std::memcmp(
              module + kObjectGetPositionRva,
              kObjectGetPositionPrologue.data(),
              kObjectGetPositionPrologue.size()) != 0 ||
          std::memcmp(
              module + kObjectGetOrientationRva,
              kObjectGetOrientationPrologue.data(),
              kObjectGetOrientationPrologue.size()) != 0)) ||
        ((kind == SpawnKind::player_teleport ||
          kind == SpawnKind::player_noclip ||
          kind == SpawnKind::object_teleport) &&
         std::memcmp(
             module + kObjectTeleportRva,
             kObjectTeleportPrologue.data(),
             kObjectTeleportPrologue.size()) != 0) ||
        (kind == SpawnKind::player_team &&
         std::memcmp(
             module + kObjectGetRva,
             kObjectGetPrologue.data(),
             kObjectGetPrologue.size()) != 0) ||
        (kind == SpawnKind::object_team &&
         (std::memcmp(
              module + kObjectGetRva,
              kObjectGetPrologue.data(),
              kObjectGetPrologue.size()) != 0 ||
          std::memcmp(
              module + kAiObjectSetTeamRva,
              kAiObjectSetTeamPrologue.data(),
              kAiObjectSetTeamPrologue.size()) != 0 ||
          std::memcmp(
              module + kAiObjectStateResolveRva,
              kAiObjectStateResolvePrologue.data(),
              kAiObjectStateResolvePrologue.size()) != 0)) ||
        (kind == SpawnKind::player_noclip &&
         std::memcmp(
             module + kObjectSetPhysicsRva,
             kObjectSetPhysicsPrologue.data(),
              kObjectSetPhysicsPrologue.size()) != 0) ||
        (kind == SpawnKind::player_input &&
         std::memcmp(
             module + kPlayerEnableInputRva,
             kPlayerEnableInputPrologue.data(),
             kPlayerEnableInputPrologue.size()) != 0) ||
        (kind == SpawnKind::machinima &&
         std::memcmp(
             module + kMachinimaCameraToggleRva,
             kMachinimaCameraTogglePrologue.data(),
             kMachinimaCameraTogglePrologue.size()) != 0) ||
        (kind == SpawnKind::ai &&
         std::memcmp(
             module + kAiPlaceRva,
             kAiPlacePrologue.data(),
             kAiPlacePrologue.size()) != 0) ||
        (kind == SpawnKind::saved_film &&
         std::memcmp(
             module + kSavedFilmOpenRva,
             kSavedFilmOpenPrologue.data(),
             kSavedFilmOpenPrologue.size()) != 0) ||
        (kind == SpawnKind::saved_film &&
         g_original_command_pump == nullptr &&
         std::memcmp(
             module + kCommandPumpRva,
             kCommandPumpPrologue.data(),
             kCommandPumpPrologue.size()) != 0) ||
        ((kind == SpawnKind::skull_read || kind == SpawnKind::skull_write) &&
         std::memcmp(
             module + kSkullMaskApplyRva,
             kSkullMaskApplyPrologue.data(),
             kSkullMaskApplyPrologue.size()) != 0) ||
        (g_original_simulation_context == nullptr &&
         std::memcmp(
             module + kSimulationContextRva,
             kSimulationContextPrologue.data(),
             kSimulationContextPrologue.size()) != 0))
    {
        error = "The native object-creation signatures do not match this game build.";
        return false;
    }
    return true;
}

DWORD invoke_saved_film_open(
    std::uint8_t* module,
    const char* film_path,
    std::uintptr_t* exception_address)
{
    struct NativeSavedFilmRequest
    {
        std::int32_t mode;
        char path[0x200];
    };

    NativeSavedFilmRequest native_request{};
    native_request.mode = 1;
    std::memcpy(
        native_request.path,
        film_path,
        std::strlen(film_path) + 1);

    DWORD exception_code = 0;
    __try
    {
        using SavedFilmOpen = void (*)(NativeSavedFilmRequest*);
        auto open = reinterpret_cast<SavedFilmOpen>(
            module + kSavedFilmOpenRva);
        open(&native_request);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

bool path_is_within(
    const std::filesystem::path& path,
    const std::filesystem::path& root)
{
    auto path_part = path.begin();
    auto root_part = root.begin();
    for (; root_part != root.end(); ++root_part, ++path_part)
    {
        if (path_part == path.end() ||
            _wcsicmp(path_part->c_str(), root_part->c_str()) != 0)
        {
            return false;
        }
    }
    return true;
}

std::string launch_saved_film(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (module == nullptr)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded.");
    }

    std::string validation_error;
    if (!validate_module(module, validation_error, SpawnKind::saved_film))
    {
        throw std::runtime_error(validation_error);
    }

    std::array<wchar_t, 32768> local_app_data{};
    DWORD local_app_data_length = GetEnvironmentVariableW(
        L"LOCALAPPDATA",
        local_app_data.data(),
        static_cast<DWORD>(local_app_data.size()));
    if (local_app_data_length == 0 ||
        local_app_data_length >= local_app_data.size())
    {
        throw std::runtime_error(
            "LOCALAPPDATA is unavailable for saved-film path validation.");
    }

    std::error_code path_error;
    std::filesystem::path film_path =
        std::filesystem::weakly_canonical(
            std::filesystem::u8path(request.saved_film_path),
            path_error);
    if (path_error)
    {
        throw std::runtime_error(
            "The selected saved-film path could not be resolved.");
    }
    std::filesystem::path film_root =
        std::filesystem::weakly_canonical(
            std::filesystem::path(local_app_data.data()) /
                L"Meteorite" / L"Saved" / L"BlamData" / L"autosave",
            path_error);
    if (path_error ||
        !path_is_within(film_path, film_root) ||
        _wcsicmp(film_path.extension().c_str(), L".film") != 0 ||
        !std::filesystem::is_regular_file(film_path, path_error) ||
        path_error)
    {
        throw std::runtime_error(
            "The selected file is not a finalized .film inside Meteorite's autosave directory.");
    }

    std::string native_path = film_path.u8string();
    if (native_path.size() >= 0x200)
    {
        throw std::runtime_error(
            "The selected saved-film path is too long for the Blam request.");
    }

    std::uintptr_t exception_address = 0;
    DWORD exception_code = invoke_saved_film_open(
        module,
        native_path.c_str(),
        &exception_address);
    if (exception_code != 0)
    {
        char message[192]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native saved-film open raised Windows exception 0x%08X at "
            "simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    return
        "Submitted the native Blam saved-film command for '" +
        film_path.filename().u8string() + "'.";
}

std::int32_t invoke_object_new(
    std::uint8_t* module,
    const SpawnRequest* request,
    DWORD* fault_code,
    std::uintptr_t* fault_address,
    std::uintptr_t* fault_target,
    ULONG_PTR* fault_operation,
    int* stage)
{
    using PlacementInitialize = void (*)(
        void* placement,
        std::int32_t tag_datum,
        std::int32_t owner_object_datum,
        const float* initial_velocity);
    using ObjectNew = std::int32_t (*)(void* placement);

    auto placement_initialize = reinterpret_cast<PlacementInitialize>(
        module + kPlacementInitializeRva);
    auto object_new = reinterpret_cast<ObjectNew>(module + kObjectNewRva);

    __try
    {
        alignas(16) std::uint8_t placement[kPlacementSize]{};
        *stage = 1;
        placement_initialize(
            placement,
            static_cast<std::int32_t>(request->tag_datum),
            request->kind == SpawnKind::weapon ? request->unit_datum : -1,
            nullptr);
        *stage = 2;
        float position[3]{request->x, request->y, request->z};
        std::memcpy(placement + kPositionOffset, position, sizeof(position));
        *stage = 3;
        *stage = 4;
        return object_new(placement);
    }
    __except ((
        *fault_code = GetExceptionCode(),
        *fault_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        *fault_target = GetExceptionInformation()->ExceptionRecord->NumberParameters >= 2
            ? static_cast<std::uintptr_t>(
                GetExceptionInformation()->ExceptionRecord->ExceptionInformation[1])
            : 0,
        *fault_operation = GetExceptionInformation()->ExceptionRecord->NumberParameters >= 1
            ? GetExceptionInformation()->ExceptionRecord->ExceptionInformation[0]
            : 0,
        EXCEPTION_EXECUTE_HANDLER))
    {
        return -2;
    }
}

DWORD read_object_transform(
    std::uint8_t* module,
    std::int32_t object_datum,
    float* position,
    float* forward,
    std::uintptr_t* exception_address,
    float* output_up = nullptr)
{
    using ObjectGetPosition = float* (*)(
        std::int32_t object_datum,
        float* position);
    using ObjectGetOrientation = void (*)(
        std::int32_t object_datum,
        float* forward,
        float* up);
    auto get_position = reinterpret_cast<ObjectGetPosition>(
        module + kObjectGetPositionRva);
    auto get_orientation = reinterpret_cast<ObjectGetOrientation>(
        module + kObjectGetOrientationRva);

    DWORD exception_code = 0;
    __try
    {
        float up[3]{};
        if (get_position(object_datum, position) == nullptr)
        {
            exception_code = ERROR_NOT_FOUND;
        }
        else
        {
            get_orientation(object_datum, forward, up);
            if (output_up != nullptr)
            {
                std::memcpy(output_up, up, sizeof(up));
            }
        }
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

std::string spawn(
    const SpawnRequest& request,
    std::int32_t* created_object_datum = nullptr)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }

    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    SpawnRequest placement_request = request;
    if (request.kind == SpawnKind::object ||
        request.kind == SpawnKind::biped ||
        request.kind == SpawnKind::biped_body ||
        request.kind == SpawnKind::biped_variant_body)
    {
        float position[3]{};
        float forward[3]{};
        std::uintptr_t transform_exception = 0;
        DWORD transform_error = read_object_transform(
            module,
            request.unit_datum,
            position,
            forward,
            &transform_exception);
        if (transform_error != 0)
        {
            char message[224]{};
            std::snprintf(
                message,
                sizeof(message),
                "Could not resolve controlled player 0x%08X through the native "
                "object transform path (error 0x%08X, simulation RVA 0x%llX).",
                static_cast<std::uint32_t>(request.unit_datum),
                static_cast<unsigned>(transform_error),
                transform_exception >= reinterpret_cast<std::uintptr_t>(module)
                    ? static_cast<unsigned long long>(
                        transform_exception -
                        reinterpret_cast<std::uintptr_t>(module))
                    : 0ULL);
            throw std::runtime_error(message);
        }
        // A zero-distance spawn can remain inside an existing collision pair,
        // which does not raise a new bump event on subsequent switch attempts.
        // Keep the target capsule overlapping the player, but enter it from a
        // short distance in front to force a fresh collision transition.
        float distance = request.kind == SpawnKind::biped ? 0.35f : 2.5f;
        placement_request.x = position[0] + forward[0] * distance;
        placement_request.y = position[1] + forward[1] * distance;
        placement_request.z =
            position[2] + forward[2] * distance +
            (request.kind == SpawnKind::biped ? 0.10f : 0.75f);
    }

    DWORD exception_code = 0;
    std::uintptr_t exception_address = 0;
    std::uintptr_t exception_target = 0;
    ULONG_PTR exception_operation = 0;
    int stage = 0;
    std::int32_t object_datum =
        invoke_object_new(
            module,
            &placement_request,
            &exception_code,
            &exception_address,
            &exception_target,
            &exception_operation,
            &stage);
    if (object_datum == -2)
    {
        const char* stage_name = stage == 1
            ? "placement_initialize"
            : stage == 4 ? "object_new" : "placement preparation";
        const char* operation_name = exception_operation == 0
            ? "read"
            : exception_operation == 1 ? "write" : "execute";
        char message[256]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native Blam creation raised Windows exception 0x%08X during %s at "
            "instruction %p (simulation RVA 0x%llX), attempting to %s %p.",
            static_cast<unsigned>(exception_code),
            stage_name,
            reinterpret_cast<void*>(exception_address),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL,
            operation_name,
            reinterpret_cast<void*>(exception_target));
        throw std::runtime_error(message);
    }
    // Blam datums are opaque 32-bit handles. A valid salted datum may have its
    // high bit set and therefore appear negative when carried in int32_t.
    if (object_datum == -1)
    {
        throw std::runtime_error(
            "Blam object_new returned an invalid object datum for the selected "
            "tag or placement.");
    }
    if (created_object_datum != nullptr)
    {
        *created_object_datum = object_datum;
    }

    if (request.kind == SpawnKind::biped_variant_body &&
        request.variant_string_id != 0)
    {
        std::uintptr_t variant_exception_address = 0;
        DWORD variant_error = invoke_object_set_variant(
            module,
            object_datum,
            request.variant_string_id,
            &variant_exception_address);
        if (variant_error != 0)
        {
            delete_object_noexcept(module, object_datum);
            char variant_message[224]{};
            std::snprintf(
                variant_message,
                sizeof(variant_message),
                "The biped was removed because object_set_variant raised "
                "Windows exception 0x%08X at simulation RVA 0x%llX.",
                static_cast<unsigned>(variant_error),
                variant_exception_address >= reinterpret_cast<std::uintptr_t>(module)
                    ? static_cast<unsigned long long>(
                        variant_exception_address -
                        reinterpret_cast<std::uintptr_t>(module))
                    : 0ULL);
            throw std::runtime_error(variant_message);
        }
    }

    char message[192]{};
    std::snprintf(
        message,
        sizeof(message),
        request.kind == SpawnKind::biped_variant_body
            ? "Created Blam object datum 0x%08X at %.2f, %.2f, %.2f "
              "with model variant 0x%08X."
            : "Created Blam object datum 0x%08X at %.2f, %.2f, %.2f.",
        static_cast<std::uint32_t>(object_datum),
        placement_request.x,
        placement_request.y,
        placement_request.z,
        request.variant_string_id);
    return message;
}

DWORD invoke_unit_add_weapon(
    std::uint8_t* module,
    std::int32_t unit_datum,
    std::int32_t weapon_datum,
    bool* attached,
    std::uintptr_t* exception_address)
{
    using UnitAddWeapon = bool (*)(
        std::int32_t unit_datum,
        std::int32_t weapon_datum,
        std::int32_t pickup_method);
    auto unit_add_weapon = reinterpret_cast<UnitAddWeapon>(
        module + kUnitAddWeaponRva);
    DWORD fault = 0;
    __try
    {
        // Method 4 is the path used by the game's normal player pickup flow.
        // It lets the engine choose a slot and perform any required switch/drop.
        *attached = unit_add_weapon(unit_datum, weapon_datum, 4);
    }
    __except ((
        fault = GetExceptionCode(),
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return fault;
}

void delete_object_noexcept(
    std::uint8_t* module,
    std::int32_t object_datum)
{
    using ObjectDelete = void (*)(std::int32_t object_datum);
    auto object_delete = reinterpret_cast<ObjectDelete>(
        module + kObjectDeleteRva);
    __try
    {
        object_delete(object_datum);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
    }
}

std::string load_weapon(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }

    std::string validation_error;
    if (!validate_module(module, validation_error, SpawnKind::weapon))
    {
        throw std::runtime_error(validation_error);
    }

    DWORD exception_code = 0;
    std::uintptr_t exception_address = 0;
    std::uintptr_t exception_target = 0;
    ULONG_PTR exception_operation = 0;
    int stage = 0;
    std::int32_t weapon_datum = invoke_object_new(
        module,
        &request,
        &exception_code,
        &exception_address,
        &exception_target,
        &exception_operation,
        &stage);
    if (weapon_datum == -2)
    {
        char message[224]{};
        std::snprintf(
            message,
            sizeof(message),
            "The engine raised Windows exception 0x%08X while creating the "
            "selected weapon (simulation RVA 0x%llX).",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }
    if (weapon_datum == -1)
    {
        throw std::runtime_error(
            "The engine rejected the selected weapon tag.");
    }

    bool attached = false;
    exception_code = 0;
    exception_address = 0;
    exception_code = invoke_unit_add_weapon(
        module,
        request.unit_datum,
        weapon_datum,
        &attached,
        &exception_address);

    if (!attached)
    {
        delete_object_noexcept(module, weapon_datum);
        if (exception_code != 0)
        {
            char message[224]{};
            std::snprintf(
                message,
                sizeof(message),
                "The engine raised Windows exception 0x%08X while adding the "
                "weapon to the player (simulation RVA 0x%llX).",
                static_cast<unsigned>(exception_code),
                exception_address >= reinterpret_cast<std::uintptr_t>(module)
                    ? static_cast<unsigned long long>(
                        exception_address - reinterpret_cast<std::uintptr_t>(module))
                    : 0ULL);
            throw std::runtime_error(message);
        }
        throw std::runtime_error(
            "The engine would not add that weapon to the player's inventory. "
            "The temporary weapon object was deleted safely.");
    }

    char message[160]{};
    std::snprintf(
        message,
        sizeof(message),
        "The engine created weapon 0x%08X and equipped it through the normal "
        "player pickup path.",
        static_cast<std::uint32_t>(weapon_datum));
    return message;
}

DWORD invoke_object_set_variant(
    std::uint8_t* module,
    std::int32_t unit_datum,
    std::uint32_t variant_string_id,
    std::uintptr_t* exception_address)
{
    using ObjectSetVariant = void (*)(
        std::int32_t object_datum,
        std::uint32_t variant_string_id);
    auto set_variant = reinterpret_cast<ObjectSetVariant>(
        module + kObjectSetVariantRva);
    DWORD exception_code = 0;
    __try
    {
        set_variant(unit_datum, variant_string_id);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

std::string set_object_variant(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }

    std::string validation_error;
    if (!validate_module(module, validation_error, SpawnKind::variant))
    {
        throw std::runtime_error(validation_error);
    }

    std::uintptr_t exception_address = 0;
    DWORD exception_code = invoke_object_set_variant(
        module,
        request.unit_datum,
        request.variant_string_id,
        &exception_address);
    if (exception_code != 0)
    {
        char message[224]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native object_set_variant raised Windows exception 0x%08X at "
            "simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    char message[160]{};
    std::snprintf(
        message,
        sizeof(message),
        "Applied model variant string-id 0x%08X to player object 0x%08X.",
        request.variant_string_id,
        static_cast<std::uint32_t>(request.unit_datum));
    return message;
}

DWORD invoke_object_set_colors(
    std::uint8_t* module,
    std::int32_t unit_datum,
    std::uint8_t color_mask,
    const float* colors,
    std::uintptr_t* exception_address)
{
    using ObjectSetColors = void (*)(
        std::int32_t object_datum,
        std::uint8_t active_channel_mask,
        const float* colors);
    auto set_colors = reinterpret_cast<ObjectSetColors>(
        module + kObjectSetColorsRva);
    using ObjectChanged = void (*)(
        std::int32_t object_datum,
        bool force_update);
    auto object_changed = reinterpret_cast<ObjectChanged>(
        module + kObjectChangedRva);
    DWORD exception_code = 0;
    __try
    {
        set_colors(unit_datum, color_mask, colors);
        object_changed(unit_datum, true);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

std::string set_object_colors(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }

    std::string validation_error;
    if (!validate_module(module, validation_error, SpawnKind::colors))
    {
        throw std::runtime_error(validation_error);
    }

    std::uintptr_t exception_address = 0;
    DWORD exception_code = invoke_object_set_colors(
        module,
        request.unit_datum,
        request.color_mask,
        request.colors.data(),
        &exception_address);
    if (exception_code != 0)
    {
        char message[224]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native object_set_colors raised Windows exception 0x%08X at "
            "simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    char message[160]{};
    std::snprintf(
        message,
        sizeof(message),
        "Applied object color mask 0x%02X to player object 0x%08X.",
        request.color_mask,
        static_cast<std::uint32_t>(request.unit_datum));
    return message;
}

std::string set_bump_possession(
    std::uint8_t* module,
    bool enabled)
{
    auto* flag = module + kCheatBumpPossessionRva;
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(flag),
            sizeof(std::uint8_t)))
    {
        throw std::runtime_error(
            "The cheat_bump_possession global is not writable in this build.");
    }
    *flag = enabled ? 1 : 0;
    if (*flag != (enabled ? 1 : 0))
    {
        throw std::runtime_error(
            "The game did not retain the bump-possession setting.");
    }
    return enabled
        ? "The engine's cheat_bump_possession path is enabled."
        : "The engine's cheat_bump_possession path is disabled.";
}

void validate_cheat_global(
    std::uint8_t* module,
    const CheatGlobal& global)
{
    auto* registration = module + global.registration_rva;
    std::uint64_t registered_name = 0;
    std::uint64_t registered_type = 0;
    std::memcpy(&registered_name, registration, sizeof(registered_name));
    std::memcpy(
        &registered_type,
        registration + sizeof(registered_name),
        sizeof(registered_type));
    if (registered_name !=
            reinterpret_cast<std::uintptr_t>(module + global.name_rva) ||
        registered_type != 5)
    {
        throw std::runtime_error(
            std::string("The registration for ") + global.name +
            " does not match the verified boolean layout.");
    }
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(module + global.value_rva),
            sizeof(std::uint64_t)))
    {
        throw std::runtime_error(
            std::string("The value for ") + global.name + " is not writable.");
    }
}

std::string process_cheat_globals(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    for (const CheatGlobal& global : kCheatGlobals)
    {
        validate_cheat_global(module, global);
    }

    if (request.kind == SpawnKind::cheat_write)
    {
        auto match = std::find_if(
            kCheatGlobals.begin(),
            kCheatGlobals.end(),
            [&](const CheatGlobal& global)
            {
                return request.cheat_name == global.name;
            });
        auto* value = module + match->value_rva;
        std::uint64_t native_value = request.cheat_value ? 1 : 0;
        std::memcpy(value, &native_value, sizeof(native_value));
        std::uint64_t verified_value = 0;
        std::memcpy(&verified_value, value, sizeof(verified_value));
        if (verified_value != native_value)
        {
            throw std::runtime_error(
                "The game did not retain the cheat-global setting.");
        }
        return request.cheat_name + "=" +
            (request.cheat_value ? "1" : "0");
    }

    std::ostringstream output;
    for (std::size_t index = 0; index < kCheatGlobals.size(); ++index)
    {
        const CheatGlobal& global = kCheatGlobals[index];
        std::uint64_t value = 0;
        std::memcpy(
            &value,
            module + global.value_rva,
            sizeof(value));
        if (value > 1)
        {
            throw std::runtime_error(
                std::string("The value for ") + global.name +
                " is not a valid boolean.");
        }
        if (index != 0)
        {
            output << '\n';
        }
        output << global.name << '=' << static_cast<unsigned>(value);
    }
    return output.str();
}

void* try_resolve_game_thread_globals(std::uint8_t* module)
{
    DWORD tls_index = 0;
    std::memcpy(&tls_index, module + kTlsIndexRva, sizeof(tls_index));
    if (tls_index >= 4096)
    {
        return nullptr;
    }

    // Blam uses the loader's per-module TLS vector, emitted by the compiler as
    // gs:[0x58][tls_index]. This is not the Win32 TlsAlloc/TlsGetValue store.
    auto* tls_slots = reinterpret_cast<void**>(__readgsqword(0x58));
    if (!tls_slots ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(tls_slots + tls_index),
            sizeof(void*)))
    {
        return nullptr;
    }

    void* thread_globals = nullptr;
    std::memcpy(
        &thread_globals,
        tls_slots + tls_index,
        sizeof(thread_globals));
    return thread_globals;
}

std::uint8_t* try_resolve_machinima_camera_state(std::uint8_t* module)
{
    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        return nullptr;
    }

    std::uint8_t* state = nullptr;
    std::memcpy(
        &state,
        static_cast<std::uint8_t*>(thread_globals) +
            kThreadMachinimaCameraStateOffset,
        sizeof(state));
    if (!state ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(
                state + kMachinimaEnabledOffset),
            3))
    {
        return nullptr;
    }
    return state;
}

std::uint64_t* try_resolve_live_skull_mask(std::uint8_t* module)
{
    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        return nullptr;
    }

    std::uint8_t* game_state = nullptr;
    std::memcpy(
        &game_state,
        static_cast<std::uint8_t*>(thread_globals) + kThreadGameStateOffset,
        sizeof(game_state));
    if (!game_state)
    {
        return nullptr;
    }
    auto* mask = reinterpret_cast<std::uint64_t*>(
        game_state + kGameStateSkullMaskOffset);
    if (!writable_range(reinterpret_cast<std::uintptr_t>(mask), sizeof(*mask)))
    {
        return nullptr;
    }
    return mask;
}

std::uint32_t* try_resolve_boundary_disable_words(std::uint8_t* module)
{
    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        return nullptr;
    }

    std::uint32_t* disabled_words = nullptr;
    std::memcpy(
        &disabled_words,
        static_cast<std::uint8_t*>(thread_globals) +
            kThreadKillVolumeStateOffset,
        sizeof(disabled_words));
    if (!disabled_words ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(disabled_words),
            sizeof(*disabled_words)))
    {
        return nullptr;
    }
    return disabled_words;
}

std::uint64_t* resolve_live_skull_mask(std::uint8_t* module)
{
    std::uint64_t* mask = try_resolve_live_skull_mask(module);
    if (!mask)
    {
        throw std::runtime_error(
            "The active campaign skull mask is unavailable on this simulation thread.");
    }
    return mask;
}

DWORD invoke_skull_mask_apply(
    std::uint8_t* module,
    const std::uint64_t* mask,
    std::uintptr_t* exception_address)
{
    using ApplySkullMask = void (*)(const std::uint64_t* mask);
    auto apply = reinterpret_cast<ApplySkullMask>(
        module + kSkullMaskApplyRva);
    DWORD caught_code = 0;
    __try
    {
        apply(mask);
    }
    __except ((
        caught_code = GetExceptionCode(),
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return caught_code;
}

std::string process_live_skulls(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    std::uint64_t* live_mask = resolve_live_skull_mask(module);
    if (request.kind == SpawnKind::skull_write)
    {
        auto match = std::find_if(
            kSkulls.begin(),
            kSkulls.end(),
            [&](const SkullDefinition& skull)
            {
                return request.cheat_name == skull.name;
            });
        std::uint64_t updated = *live_mask;
        std::uint64_t bit = 1ULL << match->index;
        updated = request.cheat_value ? updated | bit : updated & ~bit;

        std::uintptr_t exception_address = 0;
        DWORD exception_code = invoke_skull_mask_apply(
            module,
            &updated,
            &exception_address);
        if (exception_code != 0)
        {
            char message[192]{};
            std::snprintf(
                message,
                sizeof(message),
                "The engine's skull-mask apply routine raised native exception "
                "0x%08lX at RVA 0x%llX.",
                static_cast<unsigned long>(exception_code),
                static_cast<unsigned long long>(
                    exception_address -
                    reinterpret_cast<std::uintptr_t>(module)));
            throw std::runtime_error(message);
        }

        std::uint64_t verified = *resolve_live_skull_mask(module);
        if (((verified & bit) != 0) != request.cheat_value)
        {
            throw std::runtime_error(
                "The game did not retain the requested skull state.");
        }
    }

    std::uint64_t mask = *resolve_live_skull_mask(module);
    std::ostringstream output;
    for (std::size_t index = 0; index < kSkulls.size(); ++index)
    {
        if (index != 0)
        {
            output << '\n';
        }
        const SkullDefinition& skull = kSkulls[index];
        output << skull.name << '='
               << static_cast<unsigned>((mask >> skull.index) & 1ULL);
    }
    return output.str();
}

void process_cheat_hook_request(std::uint8_t* module)
{
    std::uint64_t* live_mask = try_resolve_live_skull_mask(module);
    if (live_mask == nullptr ||
        InterlockedCompareExchange(&g_cheat_hook_state, 2, 1) != 1)
    {
        return;
    }

    try
    {
        constexpr std::uint64_t infinite_health_bit = 1ULL << 11;
        constexpr std::uint64_t infinite_ammo_bit = 1ULL << 18;
        constexpr std::uint64_t jetpack_bit = 1ULL << 36;
        std::uint64_t updated = *live_mask;
        if (!g_cheat_hook_request.is_read)
        {
            std::uint64_t bit = g_cheat_hook_request.name == "infinite_health"
                ? infinite_health_bit
                : g_cheat_hook_request.name == "infinite_ammo"
                    ? infinite_ammo_bit
                    : jetpack_bit;
            updated = g_cheat_hook_request.enabled
                ? updated | bit
                : updated & ~bit;

            std::uintptr_t exception_address = 0;
            DWORD exception_code = invoke_skull_mask_apply(
                module,
                &updated,
                &exception_address);
            if (exception_code != 0)
            {
                char message[192]{};
                std::snprintf(
                    message,
                    sizeof(message),
                    "The gameplay-modifier apply routine raised native exception "
                    "0x%08lX at RVA 0x%llX.",
                    static_cast<unsigned long>(exception_code),
                    static_cast<unsigned long long>(
                        exception_address -
                        reinterpret_cast<std::uintptr_t>(module)));
                throw std::runtime_error(message);
            }
        }

        std::uint64_t verified = *resolve_live_skull_mask(module);
        std::ostringstream output;
        output << "infinite_health="
               << static_cast<unsigned>((verified & infinite_health_bit) != 0)
               << '\n'
               << "infinite_ammo="
               << static_cast<unsigned>((verified & infinite_ammo_bit) != 0)
               << '\n'
               << "jetpack="
               << static_cast<unsigned>((verified & jetpack_bit) != 0);
        write_result(
            g_cheat_hook_result_path,
            g_cheat_hook_request.id,
            "ok",
            output.str());
    }
    catch (const std::exception& exception)
    {
        write_result(
            g_cheat_hook_result_path,
            g_cheat_hook_request.id,
            "error",
            exception.what());
    }
    InterlockedExchange(&g_cheat_hook_state, 0);
}

DWORD invoke_object_teleport(
    std::uint8_t* module,
    std::int32_t unit_datum,
    const float* position,
    const float* forward,
    const float* up,
    std::uintptr_t* exception_address)
{
    using ObjectTeleport = void (*)(
        std::int32_t object_datum,
        const float* position,
        const float* forward,
        const float* up,
        bool disconnect_from_parent);
    auto teleport = reinterpret_cast<ObjectTeleport>(
        module + kObjectTeleportRva);
    DWORD exception_code = 0;
    __try
    {
        // The retail HaloScript caller always supplies the complete orientation
        // basis. Omitting either vector can make the later transform application
        // fail, after which object_teleport deletes the object.
        teleport(unit_datum, position, forward, up, true);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

DWORD invoke_object_set_physics(
    std::uint8_t* module,
    std::int32_t unit_datum,
    bool disabled,
    std::uintptr_t* exception_address)
{
    using ObjectSetPhysics = void (*)(
        std::int32_t object_datum,
        bool disabled);
    auto set_physics = reinterpret_cast<ObjectSetPhysics>(
        module + kObjectSetPhysicsRva);
    DWORD exception_code = 0;
    __try
    {
        set_physics(unit_datum, disabled);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

std::string teleport_player(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    float current_position[3]{};
    float current_forward[3]{};
    float current_up[3]{};
    std::uintptr_t transform_exception = 0;
    DWORD transform_error = read_object_transform(
        module,
        request.unit_datum,
        current_position,
        current_forward,
        &transform_exception,
        current_up);
    if (transform_error != 0)
    {
        throw std::runtime_error(
            "Could not resolve the player's live orientation before teleporting.");
    }

    float position[3]{request.x, request.y, request.z};
    std::uintptr_t exception_address = 0;
    DWORD exception_code = invoke_object_teleport(
        module,
        request.unit_datum,
        position,
        current_forward,
        current_up,
        &exception_address);
    if (exception_code != 0)
    {
        char message[224]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native player teleport raised Windows exception 0x%08X at "
            "simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    float verified[3]{};
    float forward[3]{};
    transform_exception = 0;
    transform_error = read_object_transform(
        module,
        request.unit_datum,
        verified,
        forward,
        &transform_exception);
    if (transform_error != 0 ||
        std::fabs(verified[0] - request.x) > 0.05f ||
        std::fabs(verified[1] - request.y) > 0.05f ||
        std::fabs(verified[2] - request.z) > 0.05f)
    {
        throw std::runtime_error(
            "The engine did not retain the requested player position.");
    }

    char message[192]{};
    std::snprintf(
        message,
        sizeof(message),
        "Teleported player 0x%08X to %.3f, %.3f, %.3f world units.",
        static_cast<std::uint32_t>(request.unit_datum),
        verified[0],
        verified[1],
        verified[2]);
    return message;
}

std::string read_player_position(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    float position[3]{};
    float forward[3]{};
    std::uintptr_t exception_address = 0;
    DWORD exception_code = read_object_transform(
        module,
        request.unit_datum,
        position,
        forward,
        &exception_address);
    if (exception_code != 0 ||
        !std::isfinite(position[0]) ||
        !std::isfinite(position[1]) ||
        !std::isfinite(position[2]))
    {
        throw std::runtime_error(
            "Could not read the controlled player's native Blam position.");
    }

    char message[160]{};
    std::snprintf(
        message,
        sizeof(message),
        "Return value: %.9g,%.9g,%.9g",
        position[0],
        position[1],
        position[2]);
    return message;
}

std::string set_player_noclip(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    constexpr std::uint64_t jetpack_bit = 1ULL << 36;
    std::uint64_t* live_mask = resolve_live_skull_mask(module);
    std::uint64_t updated_mask = *live_mask;
    bool enabled = request.cheat_value;
    if (enabled && !g_noclip_active)
    {
        g_noclip_jetpack_was_enabled =
            (updated_mask & jetpack_bit) != 0;
    }
    if (enabled)
    {
        updated_mask |= jetpack_bit;
    }
    else if (g_noclip_active && g_noclip_jetpack_was_enabled)
    {
        updated_mask |= jetpack_bit;
    }
    else if (g_noclip_active)
    {
        updated_mask &= ~jetpack_bit;
    }

    std::uintptr_t exception_address = 0;
    // object_set_physics receives the inverse of HaloScript's boolean:
    // true disables physics and all collision.
    DWORD exception_code = invoke_object_set_physics(
        module,
        request.unit_datum,
        enabled,
        &exception_address);
    if (exception_code != 0)
    {
        char message[224]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native object_set_physics raised Windows exception 0x%08X at "
            "simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    exception_address = 0;
    exception_code = invoke_skull_mask_apply(
        module,
        &updated_mask,
        &exception_address);
    if (exception_code != 0)
    {
        if (enabled)
        {
            std::uintptr_t ignored_address = 0;
            invoke_object_set_physics(
                module,
                request.unit_datum,
                false,
                &ignored_address);
        }
        throw std::runtime_error(
            "The engine could not apply the flight modifier for no-clip.");
    }

    g_noclip_active = enabled;
    if (!enabled)
    {
        g_noclip_jetpack_was_enabled = false;
    }
    if (!enabled)
    {
        return "No-clip is off: player physics/collision are restored.";
    }
    return "No-clip is on: collision is disabled and campaign flight is enabled.";
}

struct ObjectAllegianceEntry
{
    std::int32_t object_datum;
    std::int8_t team;
    std::uint8_t reserved[3];
};
static_assert(sizeof(ObjectAllegianceEntry) == 8);

ObjectAllegianceEntry* resolve_object_allegiances(std::uint8_t* module)
{
    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        throw std::runtime_error(
            "The simulation thread's object-allegiance state is unavailable.");
    }

    std::uint8_t* state = nullptr;
    std::memcpy(
        &state,
        static_cast<std::uint8_t*>(thread_globals) +
            kThreadObjectAllegianceStateOffset,
        sizeof(state));
    if (!state)
    {
        throw std::runtime_error(
            "The active object-allegiance table is unavailable.");
    }

    auto* entries = reinterpret_cast<ObjectAllegianceEntry*>(
        state + kObjectAllegianceEntriesOffset);
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(entries),
            sizeof(ObjectAllegianceEntry) * kObjectAllegianceEntryCount))
    {
        throw std::runtime_error(
            "The active object-allegiance table is not writable.");
    }
    return entries;
}

DWORD invoke_object_get(
    std::uint8_t* module,
    std::int32_t object_datum,
    void** object,
    std::uintptr_t* exception_address)
{
    using ObjectGet = void* (*)(
        std::int32_t object_datum,
        std::uint32_t object_type_mask);
    auto object_get = reinterpret_cast<ObjectGet>(
        module + kObjectGetRva);
    DWORD exception_code = 0;
    __try
    {
        *object = object_get(object_datum, kUnitObjectTypeMask);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

std::int8_t* resolve_unit_team(
    std::uint8_t* module,
    std::int32_t unit_datum)
{
    void* object = nullptr;
    std::uintptr_t exception_address = 0;
    const DWORD exception_code = invoke_object_get(
        module,
        unit_datum,
        &object,
        &exception_address);
    if (exception_code != 0)
    {
        char diagnostic[192]{};
        std::snprintf(
            diagnostic,
            sizeof(diagnostic),
            "object_get failed with SEH 0x%08lX at 0x%llX.",
            static_cast<unsigned long>(exception_code),
            static_cast<unsigned long long>(exception_address));
        throw std::runtime_error(diagnostic);
    }
    if (!object)
    {
        throw std::runtime_error(
            "The controlled player datum no longer resolves to a live unit.");
    }

    auto* team = reinterpret_cast<std::int8_t*>(
        static_cast<std::uint8_t*>(object) + kUnitTeamOffset);
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(team),
            sizeof(*team)))
    {
        throw std::runtime_error(
            "The controlled player's unit-team field is not writable.");
    }
    return team;
}

void maintain_player_team(std::uint8_t* module)
{
    if (!g_player_team_snapshot.valid)
    {
        return;
    }

    // Campaign synchronization republishes the controlled player's authored
    // team after a one-shot write. Keep the live unit field and the retail
    // object-specific override aligned for as long as this exact datum remains
    // active. Each resolver is independently guarded because simulation_context
    // is queried from several native threads with different TLS state.
    try
    {
        std::int8_t* unit_team = resolve_unit_team(
            module,
            g_player_team_snapshot.unit_datum);
        if (*unit_team != g_player_team_snapshot.desired_team)
        {
            *unit_team = g_player_team_snapshot.desired_team;
        }
    }
    catch (const std::exception&)
    {
    }

    try
    {
        ObjectAllegianceEntry* entries =
            resolve_object_allegiances(module);
        ObjectAllegianceEntry& entry =
            entries[g_player_team_snapshot.slot];
        if (entry.object_datum == g_player_team_snapshot.unit_datum &&
            entry.team != g_player_team_snapshot.desired_team)
        {
            entry.team = g_player_team_snapshot.desired_team;
        }
    }
    catch (const std::exception&)
    {
    }
}

std::string process_player_team(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    ObjectAllegianceEntry* entries = resolve_object_allegiances(module);
    std::int8_t* unit_team = resolve_unit_team(
        module,
        request.unit_datum);
    auto find_player = [&]()
    {
        return std::find_if(
            entries,
            entries + kObjectAllegianceEntryCount,
            [&](const ObjectAllegianceEntry& entry)
            {
                return entry.object_datum == request.unit_datum;
            });
    };
    ObjectAllegianceEntry* matching = find_player();

    if (request.cheat_name == "restore")
    {
        if (!g_player_team_snapshot.valid)
        {
            throw std::runtime_error(
                "There is no player-team override snapshot to restore.");
        }
        if (g_player_team_snapshot.unit_datum != request.unit_datum)
        {
            throw std::runtime_error(
                "The controlled player changed after the team override. Reload the checkpoint to clear the old object.");
        }

        ObjectAllegianceEntry& entry =
            entries[g_player_team_snapshot.slot];
        if (entry.object_datum != request.unit_datum)
        {
            throw std::runtime_error(
                "The player-team override moved or was cleared by the game.");
        }
        if (g_player_team_snapshot.had_override)
        {
            entry.team =
                g_player_team_snapshot.original_override_team;
        }
        else
        {
            entry.object_datum = -1;
            entry.team = -1;
        }
        *unit_team = g_player_team_snapshot.original_unit_team;
        if (*unit_team != g_player_team_snapshot.original_unit_team)
        {
            throw std::runtime_error(
                "The game did not retain the restored player team.");
        }
        g_player_team_snapshot = {};
        matching = find_player();
    }
    else if (request.cheat_name != "read")
    {
        ObjectAllegianceEntry* target = matching;
        if (target == entries + kObjectAllegianceEntryCount)
        {
            target = std::find_if(
                entries,
                entries + kObjectAllegianceEntryCount,
                [](const ObjectAllegianceEntry& entry)
                {
                    return entry.object_datum == -1;
                });
        }
        if (target == entries + kObjectAllegianceEntryCount)
        {
            throw std::runtime_error(
                "All 16 object-specific allegiance override slots are in use.");
        }

        std::size_t slot = static_cast<std::size_t>(target - entries);
        if (!g_player_team_snapshot.valid ||
            g_player_team_snapshot.unit_datum != request.unit_datum ||
            g_player_team_snapshot.slot != slot)
        {
            g_player_team_snapshot = {
                true,
                target->object_datum == request.unit_datum,
                request.unit_datum,
                slot,
                target->team,
                *unit_team,
                static_cast<std::int8_t>(request.player_team),
            };
        }
        else
        {
            g_player_team_snapshot.desired_team =
                static_cast<std::int8_t>(request.player_team);
        }
        target->object_datum = request.unit_datum;
        target->team = static_cast<std::int8_t>(request.player_team);
        *unit_team = static_cast<std::int8_t>(request.player_team);
        if (target->object_datum != request.unit_datum ||
            target->team != request.player_team ||
            *unit_team != request.player_team)
        {
            throw std::runtime_error(
                "The game did not retain the requested player team.");
        }
        matching = target;
    }

    std::ostringstream output;
    output << "team="
           << static_cast<int>(*unit_team)
           << '\n';
    if (matching == entries + kObjectAllegianceEntryCount)
    {
        output << "override=-1\n";
    }
    else
    {
        output << "override="
               << static_cast<int>(matching->team)
               << '\n';
    }
    output << "snapshot="
           << static_cast<unsigned>(
                  g_player_team_snapshot.valid &&
                  g_player_team_snapshot.unit_datum == request.unit_datum);
    return output.str();
}

std::string process_player_input(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    using PlayerEnableInput = void (*)(bool enabled);
    auto enable_input = reinterpret_cast<PlayerEnableInput>(
        module + kPlayerEnableInputRva);
    enable_input(request.cheat_value);
    return request.cheat_value
        ? "Player input is restored."
        : "Player simulation input is suppressed; Unreal camera input remains available.";
}

std::string process_machinima_camera(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    std::uint8_t* state = try_resolve_machinima_camera_state(module);
    if (!state)
    {
        throw std::runtime_error(
            "The simulation thread's native machinima-camera state is unavailable.");
    }
    auto* values = state + kMachinimaEnabledOffset;

    if (request.cheat_name == "enable")
    {
        if (!g_machinima_snapshot.valid)
        {
            std::copy(values, values + 3, g_machinima_snapshot.values.begin());
            g_machinima_snapshot.valid = true;
        }
        values[0] = 1;
        values[kMachinimaResetOffset - kMachinimaEnabledOffset] = 0;
        values[kMachinimaMirrorOffset - kMachinimaEnabledOffset] = 1;
    }
    else if (request.cheat_name == "disable")
    {
        values[0] = 0;
        values[kMachinimaResetOffset - kMachinimaEnabledOffset] = 0;
        values[kMachinimaMirrorOffset - kMachinimaEnabledOffset] = 0;
    }
    else if (request.cheat_name == "restore")
    {
        if (!g_machinima_snapshot.valid)
        {
            throw std::runtime_error(
                "There is no native machinima-camera snapshot to restore.");
        }
        std::copy(
            g_machinima_snapshot.values.begin(),
            g_machinima_snapshot.values.end(),
            values);
        g_machinima_snapshot = {};
    }

    std::ostringstream output;
    output << "enabled=" << static_cast<unsigned>(values[0] != 0) << '\n'
           << "snapshot="
           << static_cast<unsigned>(g_machinima_snapshot.valid) << '\n'
           << "state=0x" << std::hex << std::uppercase
           << reinterpret_cast<std::uintptr_t>(state);
    return output.str();
}

std::string process_soft_ceiling_global(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }
    validate_cheat_global(module, kSoftCeilingsDisable);

    auto* value = module + kSoftCeilingsDisable.value_rva;
    if (request.kind == SpawnKind::soft_ceiling_write)
    {
        std::uint64_t native_value = request.cheat_value ? 1 : 0;
        std::memcpy(value, &native_value, sizeof(native_value));
    }

    std::uint64_t verified_value = 0;
    std::memcpy(&verified_value, value, sizeof(verified_value));
    if (verified_value > 1 ||
        (request.kind == SpawnKind::soft_ceiling_write &&
         verified_value != (request.cheat_value ? 1u : 0u)))
    {
        throw std::runtime_error(
            "The game did not retain a valid soft-ceiling setting.");
    }
    return std::string(kSoftCeilingsDisable.name) + "=" +
        (verified_value ? "1" : "0");
}

struct RuntimeBoundaryState
{
    std::uintptr_t scenario_root{};
    std::uint32_t* disabled_words{};
    std::size_t kill_count{};
    std::size_t word_count{};
};

std::uintptr_t resolve_tag_offset(
    std::uint8_t* module,
    std::uint32_t encoded_offset)
{
    if (encoded_offset == 0 || encoded_offset == 0xFFFFFFFF)
    {
        return 0;
    }
    std::uint32_t arena = encoded_offset >> 28;
    std::uint32_t word_offset = encoded_offset & 0x0FFFFFFF;
    std::uintptr_t arena_base = 0;
    std::memcpy(
        &arena_base,
        module + kTagArenaTableRva + arena * sizeof(std::uintptr_t),
        sizeof(arena_base));
    if (arena_base == 0 ||
        word_offset >
            ((std::numeric_limits<std::uintptr_t>::max)() - arena_base) / 4)
    {
        return 0;
    }
    return arena_base + static_cast<std::uintptr_t>(word_offset) * 4;
}

RuntimeBoundaryState resolve_runtime_boundaries(std::uint8_t* module)
{
    std::uintptr_t scenario_root = 0;
    std::memcpy(
        &scenario_root,
        module + kScenarioRootPointerRva,
        sizeof(scenario_root));
    if (scenario_root == 0)
    {
        throw std::runtime_error(
            "The active scenario root is unavailable. Load an offline campaign mission first.");
    }

    std::int32_t declared_kill_count = 0;
    std::memcpy(
        &declared_kill_count,
        reinterpret_cast<const void*>(
            scenario_root + kScenarioKillTriggersOffset),
        sizeof(declared_kill_count));
    if (declared_kill_count < 0 || declared_kill_count > 1024)
    {
        throw std::runtime_error(
            "The active scenario has an invalid kill-trigger count.");
    }

    std::int32_t trigger_volume_count = 0;
    std::uint32_t trigger_volume_data_offset = 0;
    std::memcpy(
        &trigger_volume_count,
        reinterpret_cast<const void*>(
            scenario_root + kScenarioTriggerVolumesOffset),
        sizeof(trigger_volume_count));
    std::memcpy(
        &trigger_volume_data_offset,
        reinterpret_cast<const void*>(
            scenario_root + kScenarioTriggerVolumesOffset + 4),
        sizeof(trigger_volume_data_offset));
    if (trigger_volume_count < 0 || trigger_volume_count > 4096)
    {
        throw std::runtime_error(
            "The active scenario has an invalid trigger-volume count.");
    }

    std::size_t inferred_kill_count =
        static_cast<std::size_t>(declared_kill_count);
    if (trigger_volume_count > 0)
    {
        std::uintptr_t trigger_volumes =
            resolve_tag_offset(module, trigger_volume_data_offset);
        if (trigger_volumes == 0)
        {
            throw std::runtime_error(
                "The active scenario trigger-volume data could not be resolved.");
        }
        for (std::int32_t index = 0; index < trigger_volume_count; ++index)
        {
            std::int16_t kill_index = -1;
            std::memcpy(
                &kill_index,
                reinterpret_cast<const void*>(
                    trigger_volumes +
                    static_cast<std::size_t>(index) *
                        kScenarioTriggerVolumeSize +
                    kTriggerVolumeKillIndexOffset),
                sizeof(kill_index));
            if (kill_index >= 0)
            {
                inferred_kill_count = (std::max)(
                    inferred_kill_count,
                    static_cast<std::size_t>(kill_index) + 1);
            }
        }
    }
    if (inferred_kill_count == 0 || inferred_kill_count > 1024)
    {
        throw std::runtime_error(
            "No runtime kill/out-of-bounds triggers were found in the active scenario.");
    }

    std::uint32_t* disabled_words =
        try_resolve_boundary_disable_words(module);
    std::size_t word_count = (inferred_kill_count + 31) / 32;
    if (!disabled_words ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(disabled_words),
            word_count * sizeof(std::uint32_t)))
    {
        throw std::runtime_error(
            "The runtime boundary-disable bitset is unavailable.");
    }

    return {
        scenario_root,
        disabled_words,
        inferred_kill_count,
        word_count,
    };
}

std::size_t count_disabled_boundaries(const RuntimeBoundaryState& state)
{
    std::size_t disabled = 0;
    for (std::size_t index = 0; index < state.kill_count; ++index)
    {
        if ((state.disabled_words[index / 32] &
             (1u << (index % 32))) != 0)
        {
            ++disabled;
        }
    }
    return disabled;
}

std::string process_runtime_boundaries(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    RuntimeBoundaryState state = resolve_runtime_boundaries(module);
    if (request.kind == SpawnKind::boundary_disable)
    {
        if (!g_boundary_snapshot_valid ||
            g_boundary_scenario_root != state.scenario_root ||
            g_boundary_kill_count != state.kill_count)
        {
            g_boundary_snapshot.assign(
                state.disabled_words,
                state.disabled_words + state.word_count);
            g_boundary_scenario_root = state.scenario_root;
            g_boundary_kill_count = state.kill_count;
            g_boundary_snapshot_valid = true;
        }
        for (std::size_t index = 0; index < state.kill_count; ++index)
        {
            state.disabled_words[index / 32] |= 1u << (index % 32);
        }
    }
    else if (request.kind == SpawnKind::boundary_restore)
    {
        if (!g_boundary_snapshot_valid)
        {
            throw std::runtime_error(
                "There is no runtime boundary snapshot to restore.");
        }
        if (g_boundary_scenario_root != state.scenario_root ||
            g_boundary_kill_count != state.kill_count ||
            g_boundary_snapshot.size() != state.word_count)
        {
            throw std::runtime_error(
                "The active scenario changed after the boundary snapshot; reload the mission instead.");
        }
        std::copy(
            g_boundary_snapshot.begin(),
            g_boundary_snapshot.end(),
            state.disabled_words);
        g_boundary_snapshot.clear();
        g_boundary_scenario_root = 0;
        g_boundary_kill_count = 0;
        g_boundary_snapshot_valid = false;
    }

    std::size_t disabled = count_disabled_boundaries(state);
    std::ostringstream output;
    output << "total=" << state.kill_count << '\n'
           << "disabled=" << disabled << '\n'
           << "snapshot=" << static_cast<unsigned>(
                  g_boundary_snapshot_valid &&
                  g_boundary_scenario_root == state.scenario_root &&
                  g_boundary_kill_count == state.kill_count);
    return output.str();
}

std::string spawn_biped_with_bump(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }

    auto* flag = module + kCheatBumpPossessionRva;
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(flag),
            sizeof(std::uint8_t)))
    {
        throw std::runtime_error(
            "The cheat_bump_possession global is not writable in this build.");
    }
    std::uint8_t previous = *flag;
    set_bump_possession(module, true);
    try
    {
        std::string result = spawn(request);
        char message[256]{};
        std::snprintf(
            message,
            sizeof(message),
            "%s Spawned overlapping controlled player 0x%08X with "
            "cheat_bump_possession enabled.",
            result.c_str(),
            static_cast<std::uint32_t>(request.unit_datum));
        return message;
    }
    catch (...)
    {
        *flag = previous;
        throw;
    }
}

bool writable_range(std::uintptr_t address, std::size_t size)
{
    if (address == 0 || size == 0 || address > UINTPTR_MAX - size)
    {
        return false;
    }
    MEMORY_BASIC_INFORMATION information{};
    if (VirtualQuery(
            reinterpret_cast<const void*>(address),
            &information,
            sizeof(information)) != sizeof(information) ||
        information.State != MEM_COMMIT ||
        (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
    {
        return false;
    }
    DWORD protection = information.Protect & 0xFF;
    bool writable = protection == PAGE_READWRITE ||
                    protection == PAGE_WRITECOPY ||
                    protection == PAGE_EXECUTE_READWRITE ||
                    protection == PAGE_EXECUTE_WRITECOPY;
    std::uintptr_t region_end =
        reinterpret_cast<std::uintptr_t>(information.BaseAddress) +
        information.RegionSize;
    return writable && address + size <= region_end;
}

bool executable_range(std::uintptr_t address, std::size_t size)
{
    if (address == 0 || size == 0 || address > UINTPTR_MAX - size)
    {
        return false;
    }
    MEMORY_BASIC_INFORMATION information{};
    if (VirtualQuery(
            reinterpret_cast<const void*>(address),
            &information,
            sizeof(information)) != sizeof(information) ||
        information.State != MEM_COMMIT ||
        (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
    {
        return false;
    }
    const DWORD protection = information.Protect & 0xFF;
    const bool executable =
        protection == PAGE_EXECUTE ||
        protection == PAGE_EXECUTE_READ ||
        protection == PAGE_EXECUTE_READWRITE ||
        protection == PAGE_EXECUTE_WRITECOPY;
    const std::uintptr_t region_end =
        reinterpret_cast<std::uintptr_t>(information.BaseAddress) +
        information.RegionSize;
    return executable && address + size <= region_end;
}

DWORD invoke_research_target(
    const SpawnRequest* request,
    std::uint8_t* module,
    std::uint64_t* return_value,
    std::uintptr_t* exception_address)
{
    DWORD exception_code = 0;
    __try
    {
        using ResearchTarget = std::uint64_t (*)(
            std::uint64_t,
            std::uint64_t,
            std::uint64_t,
            std::uint64_t);
        auto target = reinterpret_cast<ResearchTarget>(
            module + request->research_rva);
        *return_value = target(
            request->research_arguments[0],
            request->research_arguments[1],
            request->research_arguments[2],
            request->research_arguments[3]);
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

std::string execute_research_call(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load an offline campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(module);
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(module + dos->e_lfanew);
    if (request.research_rva >
            nt->OptionalHeader.SizeOfImage -
                request.research_prologue.size())
    {
        throw std::runtime_error(
            "The research-call RVA lies outside the simulation module.");
    }
    const std::uintptr_t target_address =
        reinterpret_cast<std::uintptr_t>(module) + request.research_rva;
    if (!executable_range(
            target_address,
            request.research_prologue.size()))
    {
        throw std::runtime_error(
            "The research-call target is not committed executable memory.");
    }
    if (std::memcmp(
            reinterpret_cast<const void*>(target_address),
            request.research_prologue.data(),
            request.research_prologue.size()) != 0)
    {
        throw std::runtime_error(
            "The research-call prologue no longer matches live memory.");
    }

    std::uint64_t return_value = 0;
    std::uintptr_t exception_address = 0;
    const DWORD exception_code = invoke_research_target(
        &request,
        module,
        &return_value,
        &exception_address);
    if (exception_code != 0)
    {
        char message[192]{};
        std::snprintf(
            message,
            sizeof(message),
            "Research call raised Windows exception 0x%08X at simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address -
                    reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    char message[128]{};
    std::snprintf(
        message,
        sizeof(message),
        "rva=0x%08X\nargument_count=%u\nrax=0x%016llX",
        request.research_rva,
        static_cast<unsigned>(request.research_argument_count),
        static_cast<unsigned long long>(return_value));
    return message;
}

void fill_ai_side_position(
    const SpawnRequest& request,
    std::size_t index,
    float position[3])
{
    static constexpr std::array<float, kMaxAiPlacements> kSideOffsets{
        -0.55f, 0.55f, -1.05f, 1.05f, 0.0f};
    if (index >= kSideOffsets.size())
        index = kSideOffsets.size() - 1;
    float right_x = request.ai_right_x;
    float right_y = request.ai_right_y;
    const float length = std::sqrt(right_x * right_x + right_y * right_y);
    if (!(length > 0.001f) || !std::isfinite(length))
    {
        right_x = 1.0f;
        right_y = 0.0f;
    }
    else
    {
        right_x /= length;
        right_y /= length;
    }
    const float along = kSideOffsets[index];
    position[0] = request.x + right_x * along;
    position[1] = request.y + right_y * along;
    position[2] = request.z;
}

DWORD invoke_ai_place(
    std::uint8_t* module,
    const SpawnRequest* request,
    std::array<std::array<std::uint8_t, 16>, kMaxAiPlacements>*
        old_references,
    std::array<std::array<std::uint8_t, 12>, kMaxAiPlacements>*
        old_positions,
    std::array<std::array<std::uint8_t, 4>, kMaxAiPlacements>*
        old_variants,
    std::array<std::uint8_t, 2>* old_team,
    DWORD* out_exception_code,
    std::uintptr_t* exception_address)
{
    using AiPlace = void (*)(std::int32_t ai_index, std::int16_t count, bool in_limbo);
    auto ai_place = reinterpret_cast<AiPlace>(module + kAiPlaceRva);
    std::size_t captured = 0;
    __try
    {
        if (request->ai_team_address != 0)
        {
            std::memcpy(
                old_team->data(),
                reinterpret_cast<const void*>(request->ai_team_address),
                old_team->size());
            std::memcpy(
                reinterpret_cast<void*>(request->ai_team_address),
                &request->ai_team_value,
                sizeof(request->ai_team_value));
        }
        for (std::size_t index = 0;
             index < request->ai_placement_count;
             ++index)
        {
            std::memcpy(
                (*old_references)[index].data(),
                reinterpret_cast<const void*>(
                    request->character_reference_addresses[index]),
                (*old_references)[index].size());
            std::memcpy(
                (*old_positions)[index].data(),
                reinterpret_cast<const void*>(
                    request->spawn_position_addresses[index]),
                (*old_positions)[index].size());
            std::memcpy(
                (*old_variants)[index].data(),
                reinterpret_cast<const void*>(
                    request->actor_variant_addresses[index]),
                (*old_variants)[index].size());
            ++captured;
            std::memcpy(
                reinterpret_cast<void*>(
                    request->character_reference_addresses[index]),
                request->character_reference.data(),
                request->character_reference.size());
            float position[3]{};
            fill_ai_side_position(*request, index, position);
            std::memcpy(
                reinterpret_cast<void*>(
                    request->spawn_position_addresses[index]),
                position,
                sizeof(position));
            std::memcpy(
                reinterpret_cast<void*>(
                    request->actor_variant_addresses[index]),
                request->actor_variant.data(),
                request->actor_variant.size());
        }
        std::int32_t ai_index =
            static_cast<std::int32_t>(0x20000000u | request->squad_index);
        g_active_ai_override = request;
        g_active_ai_module = module;
        g_active_ai_actor_index = 0;
        ai_place(
            ai_index,
            static_cast<std::int16_t>(request->ai_placement_count),
            false);
        g_active_ai_override = nullptr;
        g_active_ai_module = nullptr;
    }
    __except ((
        *out_exception_code = GetExceptionCode(),
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
        g_active_ai_override = nullptr;
        g_active_ai_module = nullptr;
        while (captured > 0)
        {
            --captured;
            std::memcpy(
                reinterpret_cast<void*>(
                    request->character_reference_addresses[captured]),
                (*old_references)[captured].data(),
                (*old_references)[captured].size());
            std::memcpy(
                reinterpret_cast<void*>(
                    request->spawn_position_addresses[captured]),
                (*old_positions)[captured].data(),
                (*old_positions)[captured].size());
            std::memcpy(
                reinterpret_cast<void*>(
                    request->actor_variant_addresses[captured]),
                (*old_variants)[captured].data(),
                (*old_variants)[captured].size());
        }
        if (request->ai_team_address != 0)
        {
            std::memcpy(
                reinterpret_cast<void*>(request->ai_team_address),
                old_team->data(),
                old_team->size());
        }
        return *out_exception_code;
    }
    return 0;
}

bool configure_actor_as_player_companion(
    std::uint8_t* module,
    std::int32_t actor_datum,
    std::int32_t player_unit_datum,
    const char** error);
bool finalize_deferred_ai(
    std::uint8_t* module,
    const SpawnRequest& request,
    std::string& error);
bool restamp_deferred_ai_teams(
    std::uint8_t* module,
    const SpawnRequest& request,
    std::string& error);

DWORD invoke_actor_new_direct(
    const SpawnRequest* request,
    std::array<std::int32_t, kMaxAiPlacements>* created_actors,
    std::uintptr_t* exception_address)
{
    DWORD exception_code = 0;
    __try
    {
        std::uint32_t character_datum = 0;
        std::uint32_t actor_variant = 0;
        std::memcpy(
            &character_datum,
            request->character_reference.data() + 12,
            sizeof(character_datum));
        std::memcpy(
            &actor_variant,
            request->actor_variant.data(),
            sizeof(actor_variant));

        alignas(16) std::array<std::uint8_t, 0x18> selection{};
        std::fill(
            selection.begin(),
            selection.begin() + 0x0C,
            static_cast<std::uint8_t>(0xFF));
        const std::uint32_t include_authored_locations = 0x100;
        std::memcpy(
            selection.data() + 0x0C,
            &include_authored_locations,
            sizeof(include_authored_locations));

        struct StartingLocationOutput
        {
            std::int32_t count{};
            std::int32_t reserved{};
            std::array<
                std::array<std::uint8_t, kActorStartingLocationSize>,
                192> locations{};
        };
        alignas(32) StartingLocationOutput built_locations;
        g_actor_starting_locations_build(
            request->squad_index,
            0,
            selection.data(),
            &built_locations,
            nullptr);
        if (built_locations.count <= 0 ||
            built_locations.count >
                static_cast<std::int32_t>(built_locations.locations.size()))
        {
            return ERROR_NOT_FOUND;
        }

        for (std::size_t index = 0;
             index < request->ai_placement_count;
             ++index)
        {
            alignas(16)
                std::array<std::uint8_t, kActorStartingLocationSize> location =
                    built_locations.locations[0];
            float position[3]{};
            fill_ai_side_position(*request, index, position);
            std::memcpy(location.data(), position, sizeof(position));

            std::memcpy(location.data() + kActorCharacterDatumOffset,
                        &character_datum, 4);
            const std::int32_t use_character_defaults = -1;
            // actor_new consumes the three consecutive scenario starting-
            // location fields as primary weapon, secondary weapon, and
            // equipment. The selected [weap] runtime datum belongs in the
            // primary slot; writing it to +0x30 creates a loose equipment
            // object while the actor keeps the character's default pistol.
            const std::uint32_t weapon_datum = request->ai_weapon_datum;
            std::memcpy(
                location.data() + 0x28,
                &weapon_datum,
                sizeof(weapon_datum));
            std::memcpy(
                location.data() + 0x2C,
                &use_character_defaults,
                sizeof(use_character_defaults));
            std::memcpy(
                location.data() + 0x30,
                &use_character_defaults,
                sizeof(use_character_defaults));
            std::memcpy(location.data() + kActorVariantOffset,
                        &actor_variant, 4);

            std::uint32_t source_character_datum = 0;
            std::memcpy(
                &source_character_datum,
                built_locations.locations[0].data() +
                    kActorCharacterDatumOffset,
                sizeof(source_character_datum));
            char diagnostic[256]{};
            std::snprintf(
                diagnostic,
                sizeof(diagnostic),
                "builder_count=%d squad=%u source_character=0x%08X "
                "requested_character=0x%08X position=%.3f,%.3f,%.3f",
                built_locations.count,
                static_cast<unsigned>(request->squad_index),
                source_character_datum,
                character_datum,
                position[0],
                position[1],
                position[2]);
            g_ai_creation_diagnostic = diagnostic;

            (*created_actors)[index] = g_original_actor_new(
                static_cast<std::int16_t>(request->squad_index),
                location.data());
        }
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    return exception_code;
}

bool configure_actor_as_player_companion(
    std::uint8_t* module,
    std::int32_t actor_datum,
    std::int32_t player_unit_datum,
    const char** error)
{
    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        *error = "The simulation thread actor table is unavailable.";
        return false;
    }

    std::uint8_t* actor_table = nullptr;
    std::memcpy(
        &actor_table,
        static_cast<std::uint8_t*>(thread_globals) +
            kThreadActorDataOffset,
        sizeof(actor_table));
    if (!actor_table ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(actor_table + 0x50),
            sizeof(void*)))
    {
        *error = "The live actor data array is unavailable.";
        return false;
    }
    std::uint8_t* actor_records = nullptr;
    std::memcpy(
        &actor_records,
        actor_table + 0x50,
        sizeof(actor_records));
    const std::uint16_t actor_index =
        static_cast<std::uint16_t>(actor_datum);
    if (!actor_records ||
        actor_index >
            ((std::numeric_limits<std::uintptr_t>::max)() -
                reinterpret_cast<std::uintptr_t>(actor_records)) /
                kActorRecordSize)
    {
        *error = "The created actor datum does not resolve into the actor array.";
        return false;
    }
    std::uint8_t* actor_record =
        actor_records +
        static_cast<std::size_t>(actor_index) * kActorRecordSize;
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(
                actor_record + kActorUnitDatumOffset),
            sizeof(std::int32_t)))
    {
        *error = "The created actor record is not readable.";
        return false;
    }
    std::int32_t unit_datum = -1;
    std::memcpy(
        &unit_datum,
        actor_record + kActorUnitDatumOffset,
        sizeof(unit_datum));
    if (unit_datum == -1)
    {
        *error = "The created actor has no live unit object.";
        return false;
    }

    void* player_object = nullptr;
    void* companion_object = nullptr;
    std::uintptr_t exception_address = 0;
    if (invoke_object_get(
            module,
            player_unit_datum,
            &player_object,
            &exception_address) != 0 ||
        invoke_object_get(
            module,
            unit_datum,
            &companion_object,
            &exception_address) != 0 ||
        !player_object ||
        !companion_object)
    {
        *error = "The player or companion unit no longer resolves.";
        return false;
    }
    auto* player_team = reinterpret_cast<std::int8_t*>(
        static_cast<std::uint8_t*>(player_object) + kUnitTeamOffset);
    auto* companion_team = reinterpret_cast<std::int8_t*>(
        static_cast<std::uint8_t*>(companion_object) + kUnitTeamOffset);
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(player_team),
            sizeof(*player_team)) ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(companion_team),
            sizeof(*companion_team)))
    {
        *error = "The player or companion team field is unavailable.";
        return false;
    }
    const std::int8_t team = *player_team;
    if (team < 0 || team > 13)
    {
        *error = "The player's campaign team is invalid.";
        return false;
    }

    using AiObjectStateResolve = std::uint8_t* (*)(std::int32_t);
    using AiObjectSetTeam = void (*)(std::int32_t, std::int32_t);
    auto resolve_state = reinterpret_cast<AiObjectStateResolve>(
        module + kAiObjectStateResolveRva);
    auto set_team = reinterpret_cast<AiObjectSetTeam>(
        module + kAiObjectSetTeamRva);
    std::uint8_t* state = resolve_state(unit_datum);
    if (!state ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(state),
            0x18))
    {
        *error = "The companion AI object state is unavailable.";
        return false;
    }

    std::int32_t state_object = -1;
    std::memcpy(&state_object, state, sizeof(state_object));
    if (state_object != unit_datum)
    {
        std::memcpy(state, &unit_datum, sizeof(unit_datum));
        const std::uint32_t zero = 0;
        const std::uint32_t default_flags = 0xFF7FFFFF;
        const std::uint16_t zero_short = 0;
        std::memcpy(state + 8, &zero, sizeof(zero));
        std::memcpy(state + 0x14, &default_flags, sizeof(default_flags));
        std::memcpy(state + 0x0C, &zero_short, sizeof(zero_short));
    }
    state[4] = static_cast<std::uint8_t>(team);
    *companion_team = team;
    set_team(unit_datum, team);
    if (*companion_team != team || state[4] != static_cast<std::uint8_t>(team))
    {
        *error = "The game did not retain the companion team.";
        return false;
    }

    // Targeting consults the per-object allegiance table; unit-team alone is
    // not enough when the actor was created from a hostile borrowed squad.
    try
    {
        ObjectAllegianceEntry* entries = resolve_object_allegiances(module);
        ObjectAllegianceEntry* matching = std::find_if(
            entries,
            entries + kObjectAllegianceEntryCount,
            [&](const ObjectAllegianceEntry& entry)
            {
                return entry.object_datum == unit_datum;
            });
        ObjectAllegianceEntry* target = matching;
        if (target == entries + kObjectAllegianceEntryCount)
        {
            target = std::find_if(
                entries,
                entries + kObjectAllegianceEntryCount,
                [](const ObjectAllegianceEntry& entry)
                {
                    return entry.object_datum == -1;
                });
        }
        if (target == entries + kObjectAllegianceEntryCount)
        {
            *error =
                "All 16 object-specific allegiance override slots are in use.";
            return false;
        }
        target->object_datum = unit_datum;
        target->team = team;
        if (target->object_datum != unit_datum || target->team != team)
        {
            *error = "The game did not retain the companion allegiance override.";
            return false;
        }
    }
    catch (const std::exception& exception)
    {
        *error = exception.what();
        return false;
    }
    return true;
}

bool resolve_actor_unit_datum(
    std::uint8_t* module,
    std::int32_t actor_datum,
    std::int32_t* unit_datum,
    const char** error)
{
    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        *error = "The simulation thread actor table is unavailable.";
        return false;
    }
    std::uint8_t* actor_table = nullptr;
    std::memcpy(
        &actor_table,
        static_cast<std::uint8_t*>(thread_globals) +
            kThreadActorDataOffset,
        sizeof(actor_table));
    if (!actor_table ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(actor_table + 0x50),
            sizeof(void*)))
    {
        *error = "The live actor data array is unavailable.";
        return false;
    }
    std::uint8_t* actor_records = nullptr;
    std::memcpy(
        &actor_records,
        actor_table + 0x50,
        sizeof(actor_records));
    const std::uint16_t actor_index =
        static_cast<std::uint16_t>(actor_datum);
    if (!actor_records ||
        actor_index >
            ((std::numeric_limits<std::uintptr_t>::max)() -
                reinterpret_cast<std::uintptr_t>(actor_records)) /
                kActorRecordSize)
    {
        *error = "The created actor datum does not resolve into the actor array.";
        return false;
    }
    std::uint8_t* actor_record =
        actor_records +
        static_cast<std::size_t>(actor_index) * kActorRecordSize;
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(
                actor_record + kActorUnitDatumOffset),
            sizeof(*unit_datum)))
    {
        *error = "The created actor record is not readable.";
        return false;
    }
    std::memcpy(
        unit_datum,
        actor_record + kActorUnitDatumOffset,
        sizeof(*unit_datum));
    if (*unit_datum == -1)
    {
        *error = "The created actor has no live unit object yet.";
        return false;
    }
    return true;
}


bool apply_unit_campaign_team(
    std::uint8_t* module,
    std::int32_t unit_datum,
    std::int8_t team,
    const char** error)
{
    if (team < 0 || team > 13)
    {
        *error = "The requested campaign team is invalid.";
        return false;
    }
    void* unit_object = nullptr;
    std::uintptr_t exception_address = 0;
    if (invoke_object_get(
            module,
            unit_datum,
            &unit_object,
            &exception_address) != 0 ||
        !unit_object)
    {
        *error = "The target unit no longer resolves.";
        return false;
    }
    auto* unit_team = reinterpret_cast<std::int8_t*>(
        static_cast<std::uint8_t*>(unit_object) + kUnitTeamOffset);
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(unit_team),
            sizeof(*unit_team)))
    {
        *error = "The unit team field is unavailable.";
        return false;
    }

    using AiObjectStateResolve = std::uint8_t* (*)(std::int32_t);
    using AiObjectSetTeam = void (*)(std::int32_t, std::int32_t);
    auto resolve_state = reinterpret_cast<AiObjectStateResolve>(
        module + kAiObjectStateResolveRva);
    auto set_team = reinterpret_cast<AiObjectSetTeam>(
        module + kAiObjectSetTeamRva);
    std::uint8_t* state = resolve_state(unit_datum);
    if (!state ||
        !writable_range(reinterpret_cast<std::uintptr_t>(state), 0x18))
    {
        *error = "The AI object state is unavailable.";
        return false;
    }
    std::int32_t state_object = -1;
    std::memcpy(&state_object, state, sizeof(state_object));
    if (state_object != unit_datum)
    {
        std::memcpy(state, &unit_datum, sizeof(unit_datum));
        const std::uint32_t zero = 0;
        const std::uint32_t default_flags = 0xFF7FFFFF;
        const std::uint16_t zero_short = 0;
        std::memcpy(state + 8, &zero, sizeof(zero));
        std::memcpy(state + 0x14, &default_flags, sizeof(default_flags));
        std::memcpy(state + 0x0C, &zero_short, sizeof(zero_short));
    }
    state[4] = static_cast<std::uint8_t>(team);
    *unit_team = team;
    set_team(unit_datum, team);

    ObjectAllegianceEntry* entries = resolve_object_allegiances(module);
    ObjectAllegianceEntry* matching = std::find_if(
        entries,
        entries + kObjectAllegianceEntryCount,
        [&](const ObjectAllegianceEntry& entry)
        {
            return entry.object_datum == unit_datum;
        });
    ObjectAllegianceEntry* target = matching;
    if (target == entries + kObjectAllegianceEntryCount)
    {
        target = std::find_if(
            entries,
            entries + kObjectAllegianceEntryCount,
            [](const ObjectAllegianceEntry& entry)
            {
                return entry.object_datum == -1;
            });
    }
    if (target == entries + kObjectAllegianceEntryCount)
    {
        *error = "All 16 object-specific allegiance override slots are in use.";
        return false;
    }
    target->object_datum = unit_datum;
    target->team = team;
    if (*unit_team != team ||
        state[4] != static_cast<std::uint8_t>(team) ||
        target->team != team)
    {
        *error = "The game did not retain the requested campaign team.";
        return false;
    }
    return true;
}

bool clear_actor_player_combat_targets(
    std::uint8_t* module,
    std::int32_t actor_datum,
    std::int32_t player_unit_datum,
    int* cleared_targets,
    const char** error)
{
    *cleared_targets = 0;
    if (player_unit_datum == -1)
        return true;

    void* thread_globals = try_resolve_game_thread_globals(module);
    if (!thread_globals)
    {
        *error = "The simulation thread actor table is unavailable.";
        return false;
    }
    std::uint8_t* actor_table = nullptr;
    std::memcpy(
        &actor_table,
        static_cast<std::uint8_t*>(thread_globals) +
            kThreadActorDataOffset,
        sizeof(actor_table));
    if (!actor_table ||
        !writable_range(
            reinterpret_cast<std::uintptr_t>(actor_table + 0x50),
            sizeof(void*)))
    {
        *error = "The live actor data array is unavailable.";
        return false;
    }
    std::uint8_t* actor_records = nullptr;
    std::memcpy(&actor_records, actor_table + 0x50, sizeof(actor_records));
    const std::uint16_t actor_index =
        static_cast<std::uint16_t>(actor_datum);
    if (!actor_records ||
        actor_index >
            ((std::numeric_limits<std::uintptr_t>::max)() -
                reinterpret_cast<std::uintptr_t>(actor_records)) /
                kActorRecordSize)
    {
        *error = "The actor datum does not resolve into the actor array.";
        return false;
    }
    std::uint8_t* actor_record =
        actor_records +
        static_cast<std::size_t>(actor_index) * kActorRecordSize;
    if (!writable_range(
            reinterpret_cast<std::uintptr_t>(actor_record),
            kActorRecordSize))
    {
        *error = "The actor record is not writable.";
        return false;
    }

    // Only exact matches of the player unit datum. Never rewrite status /
    // objective bytes; those blind heuristics previously froze the game.
    for (std::size_t offset = 0;
         offset + sizeof(std::int32_t) <= kActorRecordSize;
         offset += sizeof(std::int32_t))
    {
        if (offset == kActorUnitDatumOffset)
            continue;
        std::int32_t value = -1;
        std::memcpy(&value, actor_record + offset, sizeof(value));
        if (value != player_unit_datum)
            continue;
        const std::int32_t cleared = -1;
        std::memcpy(actor_record + offset, &cleared, sizeof(cleared));
        ++(*cleared_targets);
    }
    return true;
}

bool resolve_object_target_unit(
    std::uint8_t* module,
    const SpawnRequest& request,
    std::int32_t* unit_datum,
    std::int32_t* actor_datum,
    std::string& error)
{
    *unit_datum = request.unit_datum;
    *actor_datum = -1;
    if (request.cheat_name == "last")
    {
        *actor_datum = g_last_ai_actor_datum;
        if (*actor_datum == -1)
        {
            error =
                "No AI actor has been created yet in this session. Spawn one first.";
            return false;
        }
        const char* resolve_error = nullptr;
        if (!resolve_actor_unit_datum(
                module,
                *actor_datum,
                unit_datum,
                &resolve_error))
        {
            error = resolve_error != nullptr
                ? resolve_error
                : "Could not resolve the last AI actor to a unit.";
            return false;
        }
        return true;
    }
    if (request.cheat_name == "actor")
    {
        *actor_datum = request.unit_datum;
        const char* resolve_error = nullptr;
        if (!resolve_actor_unit_datum(
                module,
                *actor_datum,
                unit_datum,
                &resolve_error))
        {
            error = resolve_error != nullptr
                ? resolve_error
                : "Could not resolve the AI actor to a unit.";
            return false;
        }
        return true;
    }
    if (request.cheat_name == "unit")
        return true;
    error = "The object target must be last, actor, or unit.";
    return false;
}

std::string read_object_position(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    std::int32_t unit_datum = -1;
    std::int32_t actor_datum = -1;
    std::string resolve_error;
    if (!resolve_object_target_unit(
            module,
            request,
            &unit_datum,
            &actor_datum,
            resolve_error))
    {
        throw std::runtime_error(resolve_error);
    }

    float position[3]{};
    float forward[3]{};
    std::uintptr_t exception_address = 0;
    DWORD exception_code = read_object_transform(
        module,
        unit_datum,
        position,
        forward,
        &exception_address);
    if (exception_code != 0 ||
        !std::isfinite(position[0]) ||
        !std::isfinite(position[1]) ||
        !std::isfinite(position[2]))
    {
        throw std::runtime_error(
            "Could not read the target object's native Blam position.");
    }

    char message[192]{};
    std::snprintf(
        message,
        sizeof(message),
        "Return value: %.6g, %.6g, %.6g (unit 0x%08X).",
        position[0],
        position[1],
        position[2],
        static_cast<std::uint32_t>(unit_datum));
    return message;
}

std::string teleport_object(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    std::int32_t unit_datum = -1;
    std::int32_t actor_datum = -1;
    std::string resolve_error;
    if (!resolve_object_target_unit(
            module,
            request,
            &unit_datum,
            &actor_datum,
            resolve_error))
    {
        throw std::runtime_error(resolve_error);
    }

    float current_position[3]{};
    float current_forward[3]{};
    float current_up[3]{};
    std::uintptr_t transform_exception = 0;
    DWORD transform_error = read_object_transform(
        module,
        unit_datum,
        current_position,
        current_forward,
        &transform_exception,
        current_up);
    if (transform_error != 0)
    {
        throw std::runtime_error(
            "Could not resolve the target object's orientation before teleporting.");
    }

    float position[3]{request.x, request.y, request.z};
    std::uintptr_t exception_address = 0;
    DWORD exception_code = invoke_object_teleport(
        module,
        unit_datum,
        position,
        current_forward,
        current_up,
        &exception_address);
    if (exception_code != 0)
    {
        char message[224]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native object teleport raised Windows exception 0x%08X at "
            "simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }

    float verified[3]{};
    float forward[3]{};
    transform_exception = 0;
    transform_error = read_object_transform(
        module,
        unit_datum,
        verified,
        forward,
        &transform_exception);
    if (transform_error != 0 ||
        std::fabs(verified[0] - request.x) > 0.35f ||
        std::fabs(verified[1] - request.y) > 0.35f ||
        std::fabs(verified[2] - request.z) > 0.35f)
    {
        throw std::runtime_error(
            "The engine did not retain the requested object position.");
    }

    char message[192]{};
    std::snprintf(
        message,
        sizeof(message),
        "Teleported object 0x%08X to %.3f, %.3f, %.3f world units.",
        static_cast<std::uint32_t>(unit_datum),
        verified[0],
        verified[1],
        verified[2]);
    return message;
}

std::string process_object_team(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }

    std::int32_t actor_datum = -1;
    std::int32_t unit_datum = request.unit_datum;
    if (request.cheat_name == "last")
    {
        actor_datum = g_last_ai_actor_datum;
        if (actor_datum == -1)
        {
            throw std::runtime_error(
                "No AI actor has been created yet in this session. Spawn one first.");
        }
        const char* resolve_error = nullptr;
        if (!resolve_actor_unit_datum(
                module,
                actor_datum,
                &unit_datum,
                &resolve_error))
        {
            throw std::runtime_error(
                resolve_error != nullptr
                    ? resolve_error
                    : "Could not resolve the last AI actor to a unit.");
        }
    }
    else if (request.cheat_name == "actor")
    {
        actor_datum = request.unit_datum;
        const char* resolve_error = nullptr;
        if (!resolve_actor_unit_datum(
                module,
                actor_datum,
                &unit_datum,
                &resolve_error))
        {
            throw std::runtime_error(
                resolve_error != nullptr
                    ? resolve_error
                    : "Could not resolve the AI actor to a unit.");
        }
    }
    else if (request.cheat_name != "unit")
    {
        throw std::runtime_error(
            "The object-team target must be last, actor, or unit.");
    }

    if (request.player_team < 0 || request.player_team > 13)
    {
        throw std::runtime_error(
            "The requested campaign team must be between 0 and 13.");
    }

    const char* apply_error = nullptr;
    if (!apply_unit_campaign_team(
            module,
            unit_datum,
            static_cast<std::int8_t>(request.player_team),
            &apply_error))
    {
        throw std::runtime_error(
            apply_error != nullptr
                ? apply_error
                : "Could not apply the campaign team to the unit.");
    }

    // Clear only exact player-unit combat aim slots. Do not scrub objective /
    // task / status bytes; those heuristics previously froze the game.
    int cleared_targets = 0;
    if (actor_datum != -1 && request.ally_player_unit != -1)
    {
        const char* clear_error = nullptr;
        if (!clear_actor_player_combat_targets(
                module,
                actor_datum,
                request.ally_player_unit,
                &cleared_targets,
                &clear_error))
        {
            throw std::runtime_error(
                clear_error != nullptr
                    ? clear_error
                    : "Could not clear combat aim at the player.");
        }
    }

    char message[220]{};
    std::snprintf(
        message,
        sizeof(message),
        "unit=0x%08X\nactor=0x%08X\nteam=%d\n"
        "cleared_combat=%d\n"
        "method=ai_object_set_team+allegiance_table",
        static_cast<std::uint32_t>(unit_datum),
        static_cast<std::uint32_t>(actor_datum),
        static_cast<int>(request.player_team),
        cleared_targets);
    return message;
}

DWORD invoke_add_player_fireteam_squad(
    std::uint8_t* module,
    std::int32_t player_unit_datum,
    std::uint16_t squad_index,
    std::uintptr_t* exception_address)
{
    DWORD exception_code = 0;
    __try
    {
        g_hs_fireteam_arguments[0] = player_unit_datum;
        g_hs_fireteam_arguments[1] =
            static_cast<std::int32_t>(
                0x20000000u |
                static_cast<std::uint32_t>(squad_index));
        g_hs_fireteam_module = module;
        g_hs_fireteam_override_active = true;
        auto evaluator = reinterpret_cast<HsEvaluator>(
            module + kAiPlayerAddFireteamSquadRva);
        evaluator(kAiPlayerAddFireteamSquadOpcode, 0, false);
        g_hs_fireteam_override_active = false;
        g_hs_fireteam_module = nullptr;
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        *exception_address = reinterpret_cast<std::uintptr_t>(
            GetExceptionInformation()->ExceptionRecord->ExceptionAddress),
        EXCEPTION_EXECUTE_HANDLER))
    {
        g_hs_fireteam_override_active = false;
        g_hs_fireteam_module = nullptr;
    }
    return exception_code;
}

bool finalize_deferred_ai(
    std::uint8_t* module,
    const SpawnRequest& request,
    std::string& error)
{
    for (std::size_t index = 0;
         index < request.ai_placement_count;
         ++index)
    {
        const std::int32_t actor_datum = g_deferred_ai_actors[index];
        std::int32_t unit_datum = -1;
        const char* actor_error =
            "The created actor unit is not published yet.";
        if (!resolve_actor_unit_datum(
                module,
                actor_datum,
                &unit_datum,
                &actor_error))
        {
            error = actor_error;
            return false;
        }

        // The selected weapon is supplied through starting-location +0x28 and
        // is created/equipped by actor_new itself. Do not create a second
        // world pickup and attempt to attach it after the actor is alive.
        g_deferred_ai_weapon_done[index] = true;

        // Birth-time squad team patch is necessary but not sufficient: also
        // mirror the intended campaign team onto the live unit + allegiance
        // table once the unit object exists. Skipping this left actors with a
        // correct squad birth team that later object_team edits could not
        // fully reverse for combat disposition.
        if (!g_deferred_ai_companion_done[index])
        {
            if (request.ai_follow_player)
            {
                if (!configure_actor_as_player_companion(
                        module,
                        actor_datum,
                        request.unit_datum,
                        &actor_error))
                {
                    error = actor_error;
                    return false;
                }
            }
            else if (request.ai_team_address != 0)
            {
                if (!apply_unit_campaign_team(
                        module,
                        unit_datum,
                        static_cast<std::int8_t>(request.ai_team_value),
                        &actor_error))
                {
                    error = actor_error;
                    return false;
                }
            }
            g_deferred_ai_companion_done[index] = true;
        }
    }

    if (request.ai_follow_player && !g_deferred_ai_fireteam_done)
    {
        install_hs_fireteam_hooks(module);
        if (std::memcmp(
                module + kAiPlayerAddFireteamSquadRva,
                kAiPlayerAddFireteamSquadPrologue.data(),
                kAiPlayerAddFireteamSquadPrologue.size()) != 0)
        {
            error =
                "The player-fireteam evaluator does not match this game build.";
            return false;
        }

        std::uintptr_t exception_address = 0;
        DWORD exception_code = invoke_add_player_fireteam_squad(
            module,
            request.unit_datum,
            request.squad_index,
            &exception_address);
        if (exception_code != 0)
        {
            char message[192]{};
            std::snprintf(
                message,
                sizeof(message),
                "Player-fireteam registration raised Windows exception "
                "0x%08X at simulation RVA 0x%llX.",
                static_cast<unsigned>(exception_code),
                exception_address >= reinterpret_cast<std::uintptr_t>(module)
                    ? static_cast<unsigned long long>(
                          exception_address -
                          reinterpret_cast<std::uintptr_t>(module))
                    : 0ULL);
            error = message;
            return false;
        }
        g_deferred_ai_fireteam_done = true;
    }
    return true;
}

bool restamp_deferred_ai_teams(
    std::uint8_t* module,
    const SpawnRequest& request,
    std::string& error)
{
    // Ordinary single-actor "ai" spawns have no team override to preserve.
    if (request.ai_team_address == 0 && !request.ai_follow_player)
        return true;

    for (std::size_t index = 0;
         index < request.ai_placement_count;
         ++index)
    {
        const std::int32_t actor_datum = g_deferred_ai_actors[index];
        if (actor_datum == -1)
            continue;

        const char* actor_error = "Could not re-stamp the actor campaign team.";
        if (request.ai_follow_player)
        {
            if (!configure_actor_as_player_companion(
                    module,
                    actor_datum,
                    request.unit_datum,
                    &actor_error))
            {
                error = actor_error;
                return false;
            }
            continue;
        }

        std::int32_t unit_datum = -1;
        if (!resolve_actor_unit_datum(
                module,
                actor_datum,
                &unit_datum,
                &actor_error))
        {
            error = actor_error;
            return false;
        }
        if (!apply_unit_campaign_team(
                module,
                unit_datum,
                static_cast<std::int8_t>(request.ai_team_value),
                &actor_error))
        {
            error = actor_error;
            return false;
        }
        if (request.ally_player_unit != -1)
        {
            int cleared_targets = 0;
            if (!clear_actor_player_combat_targets(
                    module,
                    actor_datum,
                    request.ally_player_unit,
                    &cleared_targets,
                    &actor_error))
            {
                error = actor_error;
                return false;
            }
        }
    }
    return true;
}

bool restore_deferred_ai_squad_team(std::string& error)
{
    if (!g_deferred_ai_squad_team_active)
        return true;
    if (g_pending_request.ai_team_address == 0)
    {
        g_deferred_ai_squad_team_active = false;
        return true;
    }
    if (!writable_range(g_pending_request.ai_team_address, 2))
    {
        error =
            "AI placement was submitted, but the borrowed scenario team moved "
            "before it could be restored.";
        return false;
    }
    DWORD exception_code = 0;
    __try
    {
        std::memcpy(
            reinterpret_cast<void*>(g_pending_request.ai_team_address),
            g_deferred_ai_team.data(),
            g_deferred_ai_team.size());
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    if (exception_code != 0)
    {
        char message[160]{};
        std::snprintf(
            message,
            sizeof(message),
            "AI placement was submitted, but restoring its scenario team "
            "raised Windows exception 0x%08X.",
            static_cast<unsigned>(exception_code));
        error = message;
        return false;
    }
    g_deferred_ai_squad_team_active = false;
    return true;
}

bool restore_deferred_ai_patch(std::string& error)
{
    if (!g_deferred_ai_patch_active)
    {
        // actor_new path may still be holding the squad-team patch.
        return restore_deferred_ai_squad_team(error);
    }
    for (std::size_t index = 0;
         index < g_pending_request.ai_placement_count;
         ++index)
    {
        if (!writable_range(
                g_pending_request.character_reference_addresses[index], 16) ||
            !writable_range(
                g_pending_request.spawn_position_addresses[index], 12) ||
            !writable_range(
                g_pending_request.actor_variant_addresses[index], 4))
        {
            error =
                "AI placement was submitted, but the borrowed scenario fields moved "
                "before they could be restored.";
            return false;
        }
    }
    if (g_pending_request.ai_team_address != 0 &&
        !writable_range(g_pending_request.ai_team_address, 2))
    {
        error =
            "AI placement was submitted, but the borrowed scenario team moved "
            "before it could be restored.";
        return false;
    }

    DWORD exception_code = 0;
    __try
    {
        for (std::size_t index = g_pending_request.ai_placement_count;
             index > 0;
             --index)
        {
            const std::size_t placement = index - 1;
            std::memcpy(
                reinterpret_cast<void*>(
                    g_pending_request.character_reference_addresses[placement]),
                g_deferred_ai_references[placement].data(),
                g_deferred_ai_references[placement].size());
            std::memcpy(
                reinterpret_cast<void*>(
                    g_pending_request.spawn_position_addresses[placement]),
                g_deferred_ai_positions[placement].data(),
                g_deferred_ai_positions[placement].size());
            std::memcpy(
                reinterpret_cast<void*>(
                    g_pending_request.actor_variant_addresses[placement]),
                g_deferred_ai_variants[placement].data(),
                g_deferred_ai_variants[placement].size());
        }
        if (g_pending_request.ai_team_address != 0)
        {
            std::memcpy(
                reinterpret_cast<void*>(g_pending_request.ai_team_address),
                g_deferred_ai_team.data(),
                g_deferred_ai_team.size());
        }
    }
    __except ((
        exception_code =
            GetExceptionInformation()->ExceptionRecord->ExceptionCode,
        EXCEPTION_EXECUTE_HANDLER))
    {
    }
    if (exception_code != 0)
    {
        char message[160]{};
        std::snprintf(
            message,
            sizeof(message),
            "AI placement was submitted, but restoring its scenario template "
            "raised Windows exception 0x%08X.",
            static_cast<unsigned>(exception_code));
        error = message;
        return false;
    }
    g_deferred_ai_patch_active = false;
    g_deferred_ai_squad_team_active = false;
    return true;
}

std::string spawn_ai(const SpawnRequest& request)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (!module)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }
    std::string validation_error;
    if (!validate_module(module, validation_error, SpawnKind::ai))
    {
        throw std::runtime_error(validation_error);
    }
    install_ai_spawn_hooks(module);

    // actor_new inherits the live scenario squad team at birth. Keep that
    // override active until deferred finalize mirrors it onto the live unit +
    // allegiance table; restoring immediately after actor_new lets campaign
    // sync re-apply the original hostile squad team (spawn-then-change).
    if (request.ai_team_address != 0)
    {
        if (!writable_range(request.ai_team_address, g_deferred_ai_team.size()))
        {
            throw std::runtime_error(
                "The borrowed scenario squad team is not writable.");
        }
        std::memcpy(
            g_deferred_ai_team.data(),
            reinterpret_cast<const void*>(request.ai_team_address),
            g_deferred_ai_team.size());
        std::memcpy(
            reinterpret_cast<void*>(request.ai_team_address),
            &request.ai_team_value,
            sizeof(request.ai_team_value));
        g_deferred_ai_squad_team_active = true;
    }

    std::array<std::int32_t, kMaxAiPlacements> created_actors{};
    created_actors.fill(-1);
    std::uintptr_t exception_address = 0;
    DWORD exception_code = invoke_actor_new_direct(
        &request,
        &created_actors,
        &exception_address);
    auto restore_team_on_failure = [&]()
    {
        if (!g_deferred_ai_squad_team_active)
            return;
        std::memcpy(
            reinterpret_cast<void*>(request.ai_team_address),
            g_deferred_ai_team.data(),
            g_deferred_ai_team.size());
        g_deferred_ai_squad_team_active = false;
    };
    if (exception_code == ERROR_NOT_FOUND)
    {
        restore_team_on_failure();
        throw std::runtime_error(
            "The engine did not build an authored AI starting location for the "
            "selected hostile squad.");
    }
    if (exception_code == ERROR_INVALID_STATE)
    {
        restore_team_on_failure();
        throw std::runtime_error(
            "The actor was created, but the game rejected its friendly "
            "companion state. " + g_ai_creation_diagnostic);
    }
    if (exception_code != 0)
    {
        restore_team_on_failure();
        char message[192]{};
        std::snprintf(
            message,
            sizeof(message),
            "Native actor_new raised Windows exception 0x%08X at simulation RVA 0x%llX.",
            static_cast<unsigned>(exception_code),
            exception_address >= reinterpret_cast<std::uintptr_t>(module)
                ? static_cast<unsigned long long>(
                    exception_address - reinterpret_cast<std::uintptr_t>(module))
                : 0ULL);
        throw std::runtime_error(message);
    }
    for (std::size_t index = 0;
         index < request.ai_placement_count;
         ++index)
    {
        if (created_actors[index] == -1)
        {
            restore_team_on_failure();
            throw std::runtime_error(
                "Native actor_new rejected the selected character or starting "
                "location. " + g_ai_creation_diagnostic);
        }
    }
    g_deferred_ai_actors = created_actors;
    g_last_ai_actor_datum = created_actors[0];
    g_deferred_ai_weapon_done.fill(false);
    g_deferred_ai_companion_done.fill(false);
    g_deferred_ai_fireteam_done = false;
    g_deferred_ai_finalize_deadline = GetTickCount64() + 5000;

    char message[320]{};
    int written = std::snprintf(
        message,
        sizeof(message),
        "Created %u native AI actor(s) at %.2f, %.2f, %.2f "
        "(first actor datum 0x%08X; actor datums",
        static_cast<unsigned>(request.ai_placement_count),
        request.x,
        request.y,
        request.z,
        static_cast<std::uint32_t>(created_actors[0]));
    for (std::size_t index = 0;
         index < request.ai_placement_count && written > 0 &&
         static_cast<std::size_t>(written) + 12 < sizeof(message);
         ++index)
    {
        written += std::snprintf(
            message + written,
            sizeof(message) - static_cast<std::size_t>(written),
            "%s0x%08X",
            index == 0 ? " " : ",",
            static_cast<std::uint32_t>(created_actors[index]));
    }
    if (written > 0 && static_cast<std::size_t>(written) + 3 < sizeof(message))
        std::snprintf(
            message + written,
            sizeof(message) - static_cast<std::size_t>(written),
            ").");
    return message;
}

void write_absolute_jump(std::uint8_t* destination, const void* target)
{
    destination[0] = 0xFF;
    destination[1] = 0x25;
    std::memset(destination + 2, 0, 4);
    std::memcpy(destination + 6, &target, sizeof(target));
}

class SuspendedProcessThreads
{
public:
    SuspendedProcessThreads(
        const std::uint8_t* patched_code,
        const std::uint8_t* trampoline,
        std::size_t patched_length)
    {
        // Reserve before suspending anything so vector growth cannot wait on a
        // heap lock owned by a thread we just paused.
        threads_.reserve(512);
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            throw std::runtime_error(
                "Could not enumerate game threads before installing the simulation hook.");
        }

        THREADENTRY32 entry{};
        entry.dwSize = sizeof(entry);
        DWORD process_id = GetCurrentProcessId();
        DWORD current_thread_id = GetCurrentThreadId();
        if (Thread32First(snapshot, &entry))
        {
            do
            {
                if (entry.th32OwnerProcessID != process_id ||
                    entry.th32ThreadID == current_thread_id)
                {
                    continue;
                }

                HANDLE thread = OpenThread(
                    THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT,
                    FALSE,
                    entry.th32ThreadID);
                if (thread == nullptr)
                {
                    safe_to_patch_ = false;
                    continue;
                }
                if (SuspendThread(thread) == static_cast<DWORD>(-1))
                {
                    safe_to_patch_ = false;
                    CloseHandle(thread);
                    continue;
                }

                CONTEXT context{};
                context.ContextFlags = CONTEXT_CONTROL;
                if (GetThreadContext(thread, &context))
                {
                    auto instruction = reinterpret_cast<const std::uint8_t*>(
                        context.Rip);
                    if (instruction >= patched_code &&
                        instruction < patched_code + patched_length)
                    {
                        context.Rip = reinterpret_cast<DWORD64>(
                            trampoline + (instruction - patched_code));
                        if (!SetThreadContext(thread, &context))
                        {
                            safe_to_patch_ = false;
                        }
                    }
                }
                else
                {
                    safe_to_patch_ = false;
                }
                threads_.push_back(thread);
            }
            while (Thread32Next(snapshot, &entry));
        }
        CloseHandle(snapshot);
    }

    ~SuspendedProcessThreads()
    {
        for (auto iterator = threads_.rbegin(); iterator != threads_.rend(); ++iterator)
        {
            ResumeThread(*iterator);
            CloseHandle(*iterator);
        }
    }

    SuspendedProcessThreads(const SuspendedProcessThreads&) = delete;
    SuspendedProcessThreads& operator=(const SuspendedProcessThreads&) = delete;
    bool safe_to_patch() const { return safe_to_patch_; }

private:
    std::vector<HANDLE> threads_;
    bool safe_to_patch_ = true;
};

void* hooked_hs_arguments_evaluate(
    std::int32_t script_thread,
    std::int16_t argument_count,
    const void* argument_types,
    bool initialize)
{
    if (g_hs_fireteam_override_active &&
        g_hs_fireteam_module != nullptr &&
        _ReturnAddress() ==
            g_hs_fireteam_module +
                kAiPlayerAddFireteamSquadArgumentsReturnRva)
    {
        return g_hs_fireteam_arguments.data();
    }
    return g_original_hs_arguments_evaluate(
        script_thread,
        argument_count,
        argument_types,
        initialize);
}

void hooked_hs_return(std::int32_t script_thread, std::int32_t value)
{
    if (g_hs_fireteam_override_active &&
        g_hs_fireteam_module != nullptr &&
        _ReturnAddress() ==
            g_hs_fireteam_module +
                kAiPlayerAddFireteamSquadHsReturnRva)
    {
        return;
    }
    g_original_hs_return(script_thread, value);
}

template<typename Function>
void install_relocatable_hook(
    std::uint8_t* target,
    const std::array<std::uint8_t, kHsHookLength>& expected,
    const void* replacement,
    Function* original,
    const char* label)
{
    if (*original != nullptr)
        return;
    if (std::memcmp(target, expected.data(), expected.size()) != 0)
        throw std::runtime_error(
            std::string("The ") + label +
            " signature does not match this game build.");

    auto* trampoline = static_cast<std::uint8_t*>(VirtualAlloc(
        nullptr,
        64,
        MEM_COMMIT | MEM_RESERVE,
        PAGE_EXECUTE_READWRITE));
    if (!trampoline)
        throw std::runtime_error(
            std::string("Could not allocate the ") + label +
            " trampoline.");
    std::memcpy(trampoline, target, kHsHookLength);
    write_absolute_jump(
        trampoline + kHsHookLength,
        target + kHsHookLength);
    *original = reinterpret_cast<Function>(trampoline);

    SuspendedProcessThreads suspended_threads(
        target,
        trampoline,
        kHsHookLength);
    if (!suspended_threads.safe_to_patch())
    {
        *original = nullptr;
        VirtualFree(trampoline, 0, MEM_RELEASE);
        throw std::runtime_error(
            std::string("Could not safely install the ") + label +
            " hook.");
    }
    DWORD previous_protection = 0;
    if (!VirtualProtect(
            target,
            kHsHookLength,
            PAGE_EXECUTE_READWRITE,
            &previous_protection))
    {
        *original = nullptr;
        VirtualFree(trampoline, 0, MEM_RELEASE);
        throw std::runtime_error(
            std::string("Could not unlock the ") + label +
            " routine for hooking.");
    }
    write_absolute_jump(target, replacement);
    std::memset(target + 14, 0x90, kHsHookLength - 14);
    FlushInstructionCache(
        GetCurrentProcess(),
        target,
        kHsHookLength);
    DWORD ignored = 0;
    VirtualProtect(
        target,
        kHsHookLength,
        previous_protection,
        &ignored);
}

void install_hs_fireteam_hooks(std::uint8_t* module)
{
    install_relocatable_hook(
        module + kHsArgumentsEvaluateRva,
        kHsArgumentsEvaluatePrologue,
        reinterpret_cast<const void*>(&hooked_hs_arguments_evaluate),
        &g_original_hs_arguments_evaluate,
        "HaloScript argument evaluator");
    install_relocatable_hook(
        module + kHsReturnRva,
        kHsReturnPrologue,
        reinterpret_cast<const void*>(&hooked_hs_return),
        &g_original_hs_return,
        "HaloScript return");
}

std::int32_t hooked_object_new_for_ai(void* placement)
{
    if (g_active_ai_override != nullptr &&
        g_active_ai_module != nullptr &&
        _ReturnAddress() ==
            g_active_ai_module + kAiPlacePreObjectReturnRva)
    {
        // ai_place may create an authored vehicle/body before it creates the
        // actor. That object belongs to the borrowed scenario point, not the
        // requested character, so make this optional pre-object fail cleanly.
        return -1;
    }
    return g_original_object_new_for_ai(placement);
}

std::int32_t hooked_actor_new(
    std::int16_t encounter_index,
    const void* starting_location)
{
    if (starting_location != nullptr)
    {
        CapturedActorTemplate captured;
        std::memcpy(
            captured.location.data(),
            starting_location,
            captured.location.size());
        captured.encounter_index = encounter_index;
        std::memcpy(
            &captured.character_datum,
            captured.location.data() + kActorCharacterDatumOffset,
            sizeof(captured.character_datum));

        AcquireSRWLockExclusive(&g_captured_actor_template_lock);
        std::size_t destination = g_captured_actor_template_count;
        for (std::size_t index = 0;
             index < g_captured_actor_template_count;
             ++index)
        {
            if (g_captured_actor_templates[index].character_datum ==
                captured.character_datum)
            {
                destination = index;
                break;
            }
        }
        if (destination == g_captured_actor_template_count)
        {
            if (g_captured_actor_template_count <
                g_captured_actor_templates.size())
            {
                ++g_captured_actor_template_count;
            }
            else
            {
                destination = g_captured_actor_templates.size() - 1;
            }
        }
        g_captured_actor_templates[destination] = captured;
        ReleaseSRWLockExclusive(&g_captured_actor_template_lock);
    }

    const SpawnRequest* request = g_active_ai_override;
    if (request == nullptr || starting_location == nullptr)
    {
        return g_original_actor_new(encounter_index, starting_location);
    }

    alignas(16)
        std::array<std::uint8_t, kActorStartingLocationSize> overridden{};
    std::memcpy(
        overridden.data(),
        starting_location,
        overridden.size());

    std::uint32_t character_datum = 0;
    std::uint32_t actor_variant = 0;
    std::memcpy(
        &character_datum,
        request->character_reference.data() + 12,
        sizeof(character_datum));
    std::memcpy(
        &actor_variant,
        request->actor_variant.data(),
        sizeof(actor_variant));

    const std::size_t actor_index = (std::min)(
        g_active_ai_actor_index++,
        kMaxAiPlacements - 1);
    float position[3]{};
    fill_ai_side_position(*request, actor_index, position);
    std::memcpy(overridden.data(), position, sizeof(position));
    std::memcpy(
        overridden.data() + kActorCharacterDatumOffset,
        &character_datum,
        sizeof(character_datum));
    std::memcpy(
        overridden.data() + kActorVariantOffset,
        &actor_variant,
        sizeof(actor_variant));

    return g_original_actor_new(encounter_index, overridden.data());
}

void install_ai_spawn_hooks(std::uint8_t* module)
{
    if (std::memcmp(
            module + kActorNewRva,
            kActorNewPrologue.data(),
            kActorNewPrologue.size()) != 0 ||
        std::memcmp(
            module + kActorStartingLocationsBuildRva,
            kActorStartingLocationsBuildPrologue.data(),
            kActorStartingLocationsBuildPrologue.size()) != 0 ||
        std::memcmp(
            module + kObjectDeleteRva,
            kObjectDeletePrologue.data(),
            kObjectDeletePrologue.size()) != 0 ||
        std::memcmp(
            module + kUnitAddWeaponRva,
            kUnitAddWeaponPrologue.data(),
            kUnitAddWeaponPrologue.size()) != 0)
    {
        throw std::runtime_error(
            "The native AI construction signatures do not match this game build.");
    }
    g_original_actor_new =
        reinterpret_cast<ActorNew>(module + kActorNewRva);
    g_actor_starting_locations_build =
        reinterpret_cast<ActorStartingLocationsBuild>(
            module + kActorStartingLocationsBuildRva);
}

void install_simulation_context_hook(std::uint8_t* module)
{
    if (g_original_simulation_context != nullptr)
    {
        return;
    }

    auto* target = module + kSimulationContextRva;
    if (std::memcmp(
            target,
            kSimulationContextPrologue.data(),
            kSimulationContextPrologue.size()) != 0)
    {
        throw std::runtime_error(
            "The simulation-context signature does not match this game build.");
    }

    auto* trampoline = static_cast<std::uint8_t*>(VirtualAlloc(
        nullptr,
        64,
        MEM_COMMIT | MEM_RESERVE,
        PAGE_EXECUTE_READWRITE));
    if (trampoline == nullptr)
    {
        throw std::runtime_error(
            "Could not allocate the simulation-thread trampoline.");
    }
    std::memcpy(trampoline, target, kHookLength);
    write_absolute_jump(trampoline + kHookLength, target + kHookLength);

    HMODULE pinned_module = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&hooked_simulation_context),
            &pinned_module))
    {
        VirtualFree(trampoline, 0, MEM_RELEASE);
        throw std::runtime_error(
            "Could not pin the native bridge before installing its simulation hook.");
    }

    g_original_simulation_context =
        reinterpret_cast<SimulationContext>(trampoline);
    {
        SuspendedProcessThreads suspended_threads(
            target,
            trampoline,
            kHookLength);
        if (!suspended_threads.safe_to_patch())
        {
            g_original_simulation_context = nullptr;
            VirtualFree(trampoline, 0, MEM_RELEASE);
            throw std::runtime_error(
                "Could not safely relocate every suspended game thread before hooking.");
        }
        DWORD previous_protection = 0;
        if (!VirtualProtect(
                target,
                kHookLength,
                PAGE_EXECUTE_READWRITE,
                &previous_protection))
        {
            g_original_simulation_context = nullptr;
            VirtualFree(trampoline, 0, MEM_RELEASE);
            throw std::runtime_error(
                "Could not unlock the simulation-context routine for hooking.");
        }

        write_absolute_jump(target, reinterpret_cast<const void*>(
            &hooked_simulation_context));
        target[14] = 0x90;
        FlushInstructionCache(GetCurrentProcess(), target, kHookLength);

        DWORD ignored = 0;
        VirtualProtect(target, kHookLength, previous_protection, &ignored);
    }
}

void install_command_pump_hook(std::uint8_t* module)
{
    if (g_original_command_pump != nullptr)
    {
        return;
    }

    auto* target = module + kCommandPumpRva;
    if (std::memcmp(
            target,
            kCommandPumpPrologue.data(),
            kCommandPumpPrologue.size()) != 0)
    {
        throw std::runtime_error(
            "The Blam command-pump signature does not match this game build.");
    }

    auto* trampoline = static_cast<std::uint8_t*>(VirtualAlloc(
        nullptr,
        64,
        MEM_COMMIT | MEM_RESERVE,
        PAGE_EXECUTE_READWRITE));
    if (trampoline == nullptr)
    {
        throw std::runtime_error(
            "Could not allocate the Blam command-pump trampoline.");
    }
    std::memcpy(trampoline, target, kCommandPumpHookLength);
    write_absolute_jump(
        trampoline + kCommandPumpHookLength,
        target + kCommandPumpHookLength);

    HMODULE pinned_module = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&hooked_command_pump),
            &pinned_module))
    {
        VirtualFree(trampoline, 0, MEM_RELEASE);
        throw std::runtime_error(
            "Could not pin the native bridge before installing the command-pump hook.");
    }

    g_original_command_pump =
        reinterpret_cast<CommandPump>(trampoline);
    {
        SuspendedProcessThreads suspended_threads(
            target,
            trampoline,
            kCommandPumpHookLength);
        if (!suspended_threads.safe_to_patch())
        {
            g_original_command_pump = nullptr;
            VirtualFree(trampoline, 0, MEM_RELEASE);
            throw std::runtime_error(
                "Could not safely relocate every thread before hooking the command pump.");
        }
        DWORD previous_protection = 0;
        if (!VirtualProtect(
                target,
                kCommandPumpHookLength,
                PAGE_EXECUTE_READWRITE,
                &previous_protection))
        {
            g_original_command_pump = nullptr;
            VirtualFree(trampoline, 0, MEM_RELEASE);
            throw std::runtime_error(
                "Could not unlock the Blam command pump for hooking.");
        }

        write_absolute_jump(
            target,
            reinterpret_cast<const void*>(&hooked_command_pump));
        std::memset(
            target + 14,
            0x90,
            kCommandPumpHookLength - 14);
        FlushInstructionCache(
            GetCurrentProcess(),
            target,
            kCommandPumpHookLength);

        DWORD ignored = 0;
        VirtualProtect(
            target,
            kCommandPumpHookLength,
            previous_protection,
            &ignored);
    }
}

void queue_spawn(
    const SpawnRequest& request,
    const std::filesystem::path& result_path)
{
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (module == nullptr)
    {
        throw std::runtime_error(
            "HaloSimulation_tag_release.dll is not loaded. Load a campaign mission first.");
    }

    std::string validation_error;
    if (!validate_module(module, validation_error, request.kind))
    {
        throw std::runtime_error(validation_error);
    }
    if (request.kind == SpawnKind::saved_film ||
        request.kind == SpawnKind::machinima)
    {
        install_command_pump_hook(module);
    }
    else
    {
        install_simulation_context_hook(module);
    }

    LONG pending_state =
        InterlockedCompareExchange(&g_pending_state, 0, 0);
    if (pending_state == 1 &&
        GetTickCount64() >= g_pending_request_due)
    {
        InterlockedCompareExchange(&g_pending_state, 0, 1);
        pending_state =
            InterlockedCompareExchange(&g_pending_state, 0, 0);
    }
    if (pending_state != 0)
    {
        throw std::runtime_error(
            "Another native Blam creation request is still pending.");
    }
    g_pending_request = request;
    g_pending_result_path = result_path;
    g_pending_request_due = GetTickCount64() + 12000;
    InterlockedExchange(&g_pending_state, 1);
}

void hooked_command_pump(void* context)
{
    g_original_command_pump(context);
    if (g_processing_spawn ||
        (g_pending_request.kind != SpawnKind::saved_film &&
         g_pending_request.kind != SpawnKind::machinima) ||
        InterlockedCompareExchange(&g_pending_state, 2, 1) != 1)
    {
        return;
    }

    g_processing_spawn = true;
    try
    {
        if (g_pending_request.kind == SpawnKind::machinima)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_machinima_camera(g_pending_request));
        }
        else
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "submitted",
                launch_saved_film(g_pending_request));
        }
    }
    catch (const std::exception& exception)
    {
        write_result(
            g_pending_result_path,
            g_pending_request.id,
            "error",
            exception.what());
    }
    g_processing_spawn = false;
    InterlockedExchange(&g_pending_state, 0);
}

void* hooked_simulation_context()
{
    void* context = g_original_simulation_context();
    auto* module = reinterpret_cast<std::uint8_t*>(
        GetModuleHandleW(kSimulationModule));
    if (context != nullptr && module != nullptr &&
        InterlockedCompareExchange(&g_cheat_hook_state, 0, 0) == 1)
    {
        process_cheat_hook_request(module);
    }
    if (context != nullptr && module != nullptr)
    {
        // Player-team overrides still need a tick maintain (campaign sync
        // republishes the controlled body). AI born on a matching scaffold
        // does not  per-object maintain was removed once friendly-squad
        // borrowing made birth team stick without it.
        maintain_player_team(module);
    }

    if (context == nullptr || g_processing_spawn)
    {
        return context;
    }
    if (InterlockedCompareExchange(&g_pending_state, 0, 0) == 1 &&
        (g_pending_request.kind == SpawnKind::saved_film ||
         g_pending_request.kind == SpawnKind::machinima))
    {
        // These operations belong to the command-pump thread. A simulation hook
        // installed by another tool must not consume the shared one-shot request.
        return context;
    }

    if (InterlockedCompareExchange(&g_pending_state, 0, 0) == 1 &&
        GetTickCount64() >= g_pending_request_due &&
        InterlockedCompareExchange(&g_pending_state, 2, 1) == 1)
    {
        write_result(
            g_pending_result_path,
            g_pending_request.id,
            "error",
            "Timed out waiting for the operation-specific campaign TLS state.");
        InterlockedExchange(&g_pending_state, 0);
        return context;
    }

    if (InterlockedCompareExchange(&g_pending_state, 0, 0) == 3)
    {
        if (GetTickCount64() >= g_deferred_result_due &&
            InterlockedCompareExchange(&g_pending_state, 4, 3) == 3)
        {
            if (g_pending_request.kind == SpawnKind::biped_variant_body)
            {
                try
                {
                    SpawnRequest variant_request = g_pending_request;
                    variant_request.unit_datum =
                        g_deferred_variant_object_datum;
                    std::string variant_message =
                        set_object_variant(variant_request);
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "ok",
                        g_deferred_result_message + " " + variant_message);
                }
                catch (const std::exception& exception)
                {
                    if (module != nullptr &&
                        g_deferred_variant_object_datum != -1)
                    {
                        delete_object_noexcept(
                            module,
                            g_deferred_variant_object_datum);
                    }
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "error",
                        exception.what());
                }
                g_deferred_variant_object_datum = -1;
            }
            else if (g_pending_request.kind == SpawnKind::colors)
            {
                try
                {
                    std::string color_message =
                        set_object_colors(g_pending_request);
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "ok",
                        g_deferred_result_message + " " + color_message);
                }
                catch (const std::exception& exception)
                {
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "error",
                        exception.what());
                }
            }
            else
            {
                std::string finalization_error;
                if (!finalize_deferred_ai(
                        module,
                        g_pending_request,
                        finalization_error) &&
                    GetTickCount64() < g_deferred_ai_finalize_deadline)
                {
                    g_deferred_result_due = GetTickCount64() + 250;
                    InterlockedExchange(&g_pending_state, 3);
                    return context;
                }
                // Always restore borrowed scenario fields (including squad
                // team held for actor_new) after finalize succeeds or fails.
                std::string restore_error;
                const bool restored =
                    restore_deferred_ai_patch(restore_error);
                // Restoring the scaffold squad team lets active encounters
                // re-sync live actors to the original hostile team. That is
                // why friendlies worked in quiet zones but turned enemy in
                // combat zones. Re-stamp unit team + allegiance after restore.
                std::string restamp_error;
                const bool restamped =
                    finalization_error.empty() &&
                    restored &&
                    restamp_deferred_ai_teams(
                        module,
                        g_pending_request,
                        restamp_error);
                if (!finalization_error.empty())
                {
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "error",
                        "Created the requested actors, but their deferred "
                        "companion setup failed: " + finalization_error);
                }
                else if (!restored)
                {
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "error",
                        restore_error);
                }
                else if (!restamped)
                {
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "error",
                        "Created the requested actors, but re-stamping their "
                        "campaign team after scaffold restore failed: " +
                            restamp_error);
                }
                else
                {
                    write_result(
                        g_pending_result_path,
                        g_pending_request.id,
                        "submitted",
                        g_deferred_result_message);
                }
            }
            InterlockedExchange(&g_pending_state, 0);
        }
        return context;
    }
    if (InterlockedCompareExchange(&g_pending_state, 0, 0) == 1)
    {
        bool needs_skull_tls =
            g_pending_request.kind == SpawnKind::skull_read ||
            g_pending_request.kind == SpawnKind::skull_write ||
            g_pending_request.kind == SpawnKind::player_noclip;
        bool needs_ai_tls = g_pending_request.kind == SpawnKind::ai;
        bool needs_research_tls =
            g_pending_request.kind == SpawnKind::research_call;
        bool needs_boundary_tls =
            g_pending_request.kind == SpawnKind::boundary_read ||
            g_pending_request.kind == SpawnKind::boundary_disable ||
            g_pending_request.kind == SpawnKind::boundary_restore;
        if ((needs_skull_tls || needs_ai_tls || needs_research_tls ||
             needs_boundary_tls) &&
            (!module ||
             ((needs_skull_tls || needs_ai_tls || needs_research_tls) &&
              !try_resolve_live_skull_mask(module)) ||
              (needs_boundary_tls &&
               !try_resolve_boundary_disable_words(module))))
        {
            // simulation_context is queried from more than one native thread.
            // Do not let an ineligible callback consume the one-shot request;
            // the bridge will leave it pending for a callback with the
            // operation-specific campaign TLS state.
            return context;
        }
    }
    if (InterlockedCompareExchange(&g_pending_state, 2, 1) != 1)
    {
        return context;
    }

    g_processing_spawn = true;
    bool deferred_result = false;
    try
    {
        if (g_pending_request.kind == SpawnKind::ai)
        {
            g_deferred_result_message = spawn_ai(g_pending_request);
            g_deferred_result_due = GetTickCount64() + 1500;
            InterlockedExchange(&g_pending_state, 3);
            deferred_result = true;
        }
        else if (g_pending_request.kind == SpawnKind::research_call)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                execute_research_call(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::weapon)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                load_weapon(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::variant)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                set_object_variant(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::colors)
        {
            g_deferred_result_message =
                set_object_colors(g_pending_request);
            // The Unreal/Blam synchronization layer can republish the authored
            // object colors on the next few ticks. Reapply after that window and
            // only then confirm the request.
            g_deferred_result_due = GetTickCount64() + 500;
            InterlockedExchange(&g_pending_state, 3);
            deferred_result = true;
        }
        else if (g_pending_request.kind == SpawnKind::biped)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                spawn_biped_with_bump(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::biped_body)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                spawn(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::biped_variant_body)
        {
            g_deferred_result_message = spawn(
                g_pending_request,
                &g_deferred_variant_object_datum);
            // object_new publishes the datum before the biped finishes its
            // model initialization. The engine can overwrite a variant set in
            // this callback with the biped's default on a subsequent tick.
            // Reapply after initialization and only then confirm the request.
            g_deferred_result_due = GetTickCount64() + 500;
            InterlockedExchange(&g_pending_state, 3);
            deferred_result = true;
        }
        else if (g_pending_request.kind == SpawnKind::bump_off)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                set_bump_possession(module, false));
        }
        else if (g_pending_request.kind == SpawnKind::cheat_read ||
                 g_pending_request.kind == SpawnKind::cheat_write)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_cheat_globals(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::skull_read ||
                 g_pending_request.kind == SpawnKind::skull_write)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_live_skulls(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::soft_ceiling_read ||
                 g_pending_request.kind == SpawnKind::soft_ceiling_write)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_soft_ceiling_global(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::boundary_read ||
                 g_pending_request.kind == SpawnKind::boundary_disable ||
                 g_pending_request.kind == SpawnKind::boundary_restore)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_runtime_boundaries(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::player_position)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                read_player_position(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::player_teleport)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                teleport_player(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::player_noclip)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                set_player_noclip(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::player_team)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_player_team(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::object_team)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_object_team(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::object_position)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                read_object_position(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::object_teleport)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                teleport_object(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::player_input)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                process_player_input(g_pending_request));
        }
        else if (g_pending_request.kind == SpawnKind::saved_film)
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "submitted",
                launch_saved_film(g_pending_request));
        }
        else
        {
            write_result(
                g_pending_result_path,
                g_pending_request.id,
                "ok",
                spawn(g_pending_request));
        }
    }
    catch (const std::exception& exception)
    {
        write_result(
            g_pending_result_path,
            g_pending_request.id,
            "error",
            exception.what());
    }
    g_processing_spawn = false;
    if (!deferred_result)
    {
        InterlockedExchange(&g_pending_state, 0);
    }
    return context;
}
} // namespace

// package.loadlib invokes this export with lua_State*. The module intentionally
// ignores that opaque pointer and communicates through the authenticated mailbox,
// so it has no link-time dependency on UE4SS's private Lua build.
extern "C" __declspec(dllexport) int HaloMeisterBlamInvoke(void*)
{
    std::filesystem::path root = bridge_root();
    std::filesystem::path request_path = root / L"blam_spawn_request.hm";
    std::filesystem::path result_path = root / L"blam_spawn_result.hm";
    SpawnRequest request;
    std::string error;

    if (root.empty())
    {
        return 0;
    }
    if (!parse_request(request_path, request, error))
    {
        write_result(result_path, request.id, "error", error);
        return 0;
    }

    try
    {
        queue_spawn(request, result_path);
    }
    catch (const std::exception& exception)
    {
        write_result(result_path, request.id, "error", exception.what());
    }
    return 0;
}

extern "C" __declspec(dllexport) int HaloMeisterAiCaptureBootstrap(void*)
{
    try
    {
        auto* module = reinterpret_cast<std::uint8_t*>(
            GetModuleHandleW(kSimulationModule));
        if (module != nullptr)
        {
            std::string validation_error;
            if (validate_module(module, validation_error, SpawnKind::ai))
            {
                install_ai_spawn_hooks(module);
            }
        }
    }
    catch (...)
    {
        // Lua retries this harmless bootstrap while the game changes states.
        // Spawn requests still surface a detailed validation/hook error.
    }
    return 0;
}

extern "C" __declspec(dllexport) int HaloMeisterCheatInvoke(void*)
{
    std::filesystem::path root = bridge_root();
    std::filesystem::path request_path = root / L"cheat_hook_request.hm";
    std::filesystem::path result_path = root / L"cheat_hook_result.hm";
    CheatHookRequest request;

    if (root.empty())
    {
        return 0;
    }

    try
    {
        std::ifstream input(request_path, std::ios::binary);
        std::string magic;
        std::string value;
        if (!input ||
            !std::getline(input, magic) ||
            !std::getline(input, request.id) ||
            !std::getline(input, request.name) ||
            !std::getline(input, value) ||
            magic != kCheatRequestMagic ||
            request.id.size() != 32)
        {
            throw std::runtime_error(
                "The separate gameplay-cheat request is invalid.");
        }
        request.is_read = request.name == "read";
        if (!request.is_read &&
            request.name != "infinite_health" &&
            request.name != "infinite_ammo" &&
            request.name != "jetpack")
        {
            throw std::runtime_error(
                "The requested gameplay cheat is not supported by this hook.");
        }
        if (!request.is_read && value != "0" && value != "1")
        {
            throw std::runtime_error(
                "The gameplay-cheat value must be 0 or 1.");
        }
        request.enabled = value == "1";

        auto* module = reinterpret_cast<std::uint8_t*>(
            GetModuleHandleW(kSimulationModule));
        if (module == nullptr)
        {
            throw std::runtime_error(
                "HaloSimulation_tag_release.dll is not loaded. Load an offline campaign mission first.");
        }
        std::string validation_error;
        if (!validate_module(module, validation_error, SpawnKind::skull_write))
        {
            throw std::runtime_error(validation_error);
        }
        install_simulation_context_hook(module);

        LONG state = InterlockedCompareExchange(&g_cheat_hook_state, 0, 0);
        if (state == 1 && GetTickCount64() >= g_cheat_hook_due)
        {
            InterlockedCompareExchange(&g_cheat_hook_state, 0, 1);
            state = InterlockedCompareExchange(&g_cheat_hook_state, 0, 0);
        }
        if (state != 0)
        {
            throw std::runtime_error(
                "Another gameplay-cheat request is still being applied.");
        }

        g_cheat_hook_request = request;
        g_cheat_hook_result_path = result_path;
        g_cheat_hook_due = GetTickCount64() + 12000;
        InterlockedExchange(&g_cheat_hook_state, 1);
    }
    catch (const std::exception& exception)
    {
        write_result(
            result_path,
            request.id.empty() ? "invalid" : request.id,
            "error",
            exception.what());
    }
    return 0;
}
