// Generated from src/HaloMeister.App/Assets/GameBuildProfiles.json.
// Regenerate with: python tools/game_build_analyzer.py --dll <path-to-dll> --base 2026-08-17-steam --generate 2026-08-17-steam
// Do not hand-edit addresses here.

#include "native_address_table.h"

struct SupportedBuildIdentity
{
    const char* id;
    const char* sha256;
    DWORD timestamp;
    DWORD image_size;
};

constexpr std::array<SupportedBuildIdentity, 1> kSupportedBuilds{{
    {"2026-08-17-steam", "C8C144404ADF61A9DE821C996682A7E66ABADD7E530397D3BBDE31C123203BF7", 0x6A7A740A, 0x02CE1000},
}};

constexpr NativeAddressTable kNativeAddresses{
    0x00205500, // savedFilmOpen
    0x0000E670, // commandPump
    0x005EE570, // placementInitialize
    0x005A0FC0, // objectNew
    0x005A2C30, // objectDelete
    0x005A7D10, // objectSetVariant
    0x005B32D0, // objectSetColors
    0x004586E0, // objectChanged
    0x005A96A0, // objectGet
    0x005A6780, // objectGetPosition
    0x005A6AE0, // objectGetOrientation
    0x00351610, // objectTeleport
    0x005A60A0, // objectSetPhysics
    0x00609BC0, // unitAddWeapon
    0x00180E70, // simulationContext
    0x00188400, // playerEnableInput
    0x001F4A00, // machinimaCameraToggle
    0x000FD810, // aiPlace
    0x00057D50, // actorNew
    0x0004B780, // actorStartingLocationsBuild
    0x009A92F0, // cheatBumpPossession
    0x00209FC0, // skullMaskApply
    0x00D72730, // tlsIndex
    0x010C3558, // scenarioRootPointer
    0x02C2CC90, // tagArenaTable
    0x001FECA0, // hsArgumentsEvaluate
    0x001FE100, // hsReturn
    0x001CDFF0, // aiPlayerAddFireteamSquad
    0x001CE020, // aiPlayerAddFireteamSquadArgumentsReturn
    0x001CE43C, // aiPlayerAddFireteamSquadHsReturn
    0x000FCEC0, // aiObjectStateResolve
    0x00087540, // aiObjectSetTeam
    0x0004A875, // aiPlacePreObjectReturn
};

constexpr std::uintptr_t kSavedFilmOpenRva = kNativeAddresses.savedFilmOpen;
constexpr std::uintptr_t kCommandPumpRva = kNativeAddresses.commandPump;
constexpr std::uintptr_t kPlacementInitializeRva = kNativeAddresses.placementInitialize;
constexpr std::uintptr_t kObjectNewRva = kNativeAddresses.objectNew;
constexpr std::uintptr_t kObjectDeleteRva = kNativeAddresses.objectDelete;
constexpr std::uintptr_t kObjectSetVariantRva = kNativeAddresses.objectSetVariant;
constexpr std::uintptr_t kObjectSetColorsRva = kNativeAddresses.objectSetColors;
constexpr std::uintptr_t kObjectChangedRva = kNativeAddresses.objectChanged;
constexpr std::uintptr_t kObjectGetRva = kNativeAddresses.objectGet;
constexpr std::uintptr_t kObjectGetPositionRva = kNativeAddresses.objectGetPosition;
constexpr std::uintptr_t kObjectGetOrientationRva = kNativeAddresses.objectGetOrientation;
constexpr std::uintptr_t kObjectTeleportRva = kNativeAddresses.objectTeleport;
constexpr std::uintptr_t kObjectSetPhysicsRva = kNativeAddresses.objectSetPhysics;
constexpr std::uintptr_t kUnitAddWeaponRva = kNativeAddresses.unitAddWeapon;
constexpr std::uintptr_t kSimulationContextRva = kNativeAddresses.simulationContext;
constexpr std::uintptr_t kPlayerEnableInputRva = kNativeAddresses.playerEnableInput;
constexpr std::uintptr_t kMachinimaCameraToggleRva = kNativeAddresses.machinimaCameraToggle;
constexpr std::uintptr_t kAiPlaceRva = kNativeAddresses.aiPlace;
constexpr std::uintptr_t kActorNewRva = kNativeAddresses.actorNew;
constexpr std::uintptr_t kActorStartingLocationsBuildRva = kNativeAddresses.actorStartingLocationsBuild;
constexpr std::uintptr_t kCheatBumpPossessionRva = kNativeAddresses.cheatBumpPossession;
constexpr std::uintptr_t kSkullMaskApplyRva = kNativeAddresses.skullMaskApply;
constexpr std::uintptr_t kTlsIndexRva = kNativeAddresses.tlsIndex;
constexpr std::uintptr_t kScenarioRootPointerRva = kNativeAddresses.scenarioRootPointer;
constexpr std::uintptr_t kTagArenaTableRva = kNativeAddresses.tagArenaTable;
constexpr std::uintptr_t kHsArgumentsEvaluateRva = kNativeAddresses.hsArgumentsEvaluate;
constexpr std::uintptr_t kHsReturnRva = kNativeAddresses.hsReturn;
constexpr std::uintptr_t kAiPlayerAddFireteamSquadRva = kNativeAddresses.aiPlayerAddFireteamSquad;
constexpr std::uintptr_t kAiPlayerAddFireteamSquadArgumentsReturnRva = kNativeAddresses.aiPlayerAddFireteamSquadArgumentsReturn;
constexpr std::uintptr_t kAiPlayerAddFireteamSquadHsReturnRva = kNativeAddresses.aiPlayerAddFireteamSquadHsReturn;
constexpr std::uintptr_t kAiObjectStateResolveRva = kNativeAddresses.aiObjectStateResolve;
constexpr std::uintptr_t kAiObjectSetTeamRva = kNativeAddresses.aiObjectSetTeam;
constexpr std::uintptr_t kAiPlacePreObjectReturnRva = kNativeAddresses.aiPlacePreObjectReturn;

constexpr std::array<std::uint8_t, 16> kPlacementInitializePrologue{
    0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x55, 0x41,
    0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x41,
};
constexpr std::array<std::uint8_t, 16> kObjectNewPrologue{
    0x48, 0x89, 0x4C, 0x24, 0x08, 0x41, 0x54, 0x41,
    0x55, 0x48, 0x81, 0xEC, 0x98, 0x04, 0x00, 0x00,
};
constexpr std::array<std::uint8_t, 16> kObjectDeletePrologue{
    0x48, 0x8B, 0xC4, 0x53, 0x48, 0x83, 0xEC, 0x70,
    0x8B, 0x15, 0xF2, 0xFA, 0x7C, 0x00, 0x48, 0x89,
};
constexpr std::array<std::uint8_t, 16> kUnitAddWeaponPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57,
    0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
};
constexpr std::array<std::uint8_t, 16> kObjectSetVariantPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83,
    0xEC, 0x20, 0x44, 0x8B, 0x05, 0x0F, 0xAA, 0x7C,
};
constexpr std::array<std::uint8_t, 16> kObjectSetColorsPrologue{
    0x4C, 0x89, 0x44, 0x24, 0x18, 0x53, 0x55, 0x56,
    0x57, 0x41, 0x54, 0x41, 0x56, 0x41, 0x57, 0x48,
};
constexpr std::array<std::uint8_t, 16> kObjectChangedPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74,
    0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x65,
};
constexpr std::array<std::uint8_t, 16> kObjectGetPrologue{
    0x44, 0x8B, 0xC9, 0x45, 0x33, 0xC0, 0x65, 0x48,
    0x8B, 0x0C, 0x25, 0x58, 0x00, 0x00, 0x00, 0x44,
};
constexpr std::array<std::uint8_t, 16> kObjectGetPositionPrologue{
    0x44, 0x8B, 0x05, 0xA9, 0xBF, 0x7C, 0x00, 0x4C,
    0x8B, 0xCA, 0x65, 0x48, 0x8B, 0x04, 0x25, 0x58,
};
constexpr std::array<std::uint8_t, 16> kObjectGetOrientationPrologue{
    0x40, 0x53, 0x48, 0x81, 0xEC, 0xA0, 0x00, 0x00,
    0x00, 0x44, 0x8B, 0x0D, 0x40, 0xBC, 0x7C, 0x00,
};
constexpr std::array<std::uint8_t, 16> kObjectTeleportPrologue{
    0x83, 0xF9, 0xFF, 0x0F, 0x84, 0x0D, 0x04, 0x00,
    0x00, 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x50, 0x10,
};
constexpr std::array<std::uint8_t, 16> kObjectSetPhysicsPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83,
    0xEC, 0x20, 0x44, 0x8B, 0x05, 0x7F, 0xC6, 0x7C,
};
constexpr std::array<std::uint8_t, kHookLength> kSimulationContextPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74,
    0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x20,
};
constexpr std::array<std::uint8_t, 16> kPlayerEnableInputPrologue{
    0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00, 0x00,
    0x00, 0x80, 0xF1, 0x01, 0x8B, 0x15, 0x1E, 0xA3,
};
constexpr std::array<std::uint8_t, 16> kMachinimaCameraTogglePrologue{
    0x8B, 0x0D, 0x2A, 0xDD, 0xB7, 0x00, 0x44, 0x8B,
    0xCA, 0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00,
};
constexpr std::array<std::uint8_t, 16> kAiPlacePrologue{
    0x48, 0x89, 0x5C, 0x24, 0x10, 0x56, 0x48, 0x83,
    0xEC, 0x70, 0x65, 0x48, 0x8B, 0x04, 0x25, 0x58,
};
constexpr std::array<std::uint8_t, 15> kActorNewPrologue{
    0x48, 0x8B, 0xC4, 0x48, 0x89, 0x50, 0x10, 0x66,
    0x89, 0x48, 0x08, 0x55, 0x53, 0x56, 0x57,
};
constexpr std::array<std::uint8_t, 16> kActorStartingLocationsBuildPrologue{
    0x4C, 0x89, 0x4C, 0x24, 0x20, 0x4C, 0x89, 0x44,
    0x24, 0x18, 0x89, 0x54, 0x24, 0x10, 0x55, 0x53,
};
constexpr std::array<std::uint8_t, 16> kSavedFilmOpenPrologue{
    0x48, 0x83, 0xEC, 0x28, 0x4C, 0x8D, 0x05, 0xBD,
    0x0B, 0x17, 0x01, 0xC7, 0x05, 0xEF, 0x1F, 0x15,
};
constexpr std::array<std::uint8_t, kCommandPumpHookLength> kCommandPumpPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x08, 0x55, 0x56, 0x57,
    0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
};
constexpr std::array<std::uint8_t, 16> kSkullMaskApplyPrologue{
    0x4C, 0x8B, 0xDC, 0x56, 0x57, 0x41, 0x56, 0x48,
    0x83, 0xEC, 0x50, 0x8B, 0x15, 0x5F, 0x87, 0xB6,
};
constexpr std::array<std::uint8_t, kHsHookLength> kHsArgumentsEvaluatePrologue{
    0x48, 0x8B, 0xC4, 0x44, 0x88, 0x48, 0x20, 0x4C,
    0x89, 0x40, 0x18, 0x66, 0x89, 0x50, 0x10, 0x53,
    0x56, 0x57, 0x41, 0x56,
};
constexpr std::array<std::uint8_t, kHsHookLength> kHsReturnPrologue{
    0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x56, 0x48,
    0x83, 0xEC, 0x20, 0x65, 0x48, 0x8B, 0x04, 0x25,
    0x58, 0x00, 0x00, 0x00,
};
constexpr std::array<std::uint8_t, 16> kAiPlayerAddFireteamSquadPrologue{
    0x89, 0x54, 0x24, 0x10, 0x48, 0x83, 0xEC, 0x78,
    0x8B, 0xC2, 0x45, 0x0F, 0xB6, 0xC8, 0x48, 0x0F,
};
constexpr std::array<std::uint8_t, 16> kAiObjectStateResolvePrologue{
    0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41,
    0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x8B,
};
constexpr std::array<std::uint8_t, 16> kAiObjectSetTeamPrologue{
    0x48, 0x89, 0x5C, 0x24, 0x10, 0x89, 0x4C, 0x24,
    0x08, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55,
};

struct CheatGlobal
{
    const char* name;
    std::uintptr_t registration_rva;
    std::uintptr_t name_rva;
    std::uintptr_t value_rva;
};

constexpr std::array<CheatGlobal, 15> kCheatGlobals{{
    {"cheat_inhibit_input_only_when_activating", 0x009A9208, 0x007E4340, 0x009A9218},
    {"cheat_infinite_equipment_energy", 0x009A9220, 0x007E4320, 0x009A9230},
    {"cheat_controller", 0x009A9268, 0x007E42D8, 0x009A9278},
    {"cheat_omnipotent", 0x009A9280, 0x007E42C0, 0x009A9290},
    {"cheat_porcupine", 0x009A91D8, 0x007E4380, 0x009A91E8},
    {"cheat_chevy", 0x009A91F0, 0x007E4370, 0x009A9200},
    {"cheat_super_jump", 0x009A92C8, 0x007E4278, 0x009A92D8},
    {"cheat_bump_possession", 0x009A92E0, 0x007E4260, 0x009A92F0},
    {"cheat_medusa", 0x009A9238, 0x007E4310, 0x009A9248},
    {"cheat_reflexive_damage_effects", 0x009A9250, 0x007E42F0, 0x009A9260},
    {"cheat_jetpack", 0x009A9328, 0x007E41F8, 0x009A9338},
    {"cheat_valhalla", 0x009A9340, 0x007E41E8, 0x009A9350},
    {"cheat_bottomless_clip", 0x009A9298, 0x007E42A8, 0x009A92A8},
    {"cheat_infinite_ammo", 0x009A92B0, 0x007E4290, 0x009A92C0},
    {"cheat_deathless_player", 0x009A92F8, 0x007E4248, 0x009A9308},
}};

constexpr CheatGlobal kSoftCeilingsDisable{
    "soft_ceilings_disable", 0x009A65E0, 0x007E7CB8, 0x009A65F0,
};
