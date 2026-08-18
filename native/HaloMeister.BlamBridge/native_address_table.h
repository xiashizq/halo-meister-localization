// Unified native RVA schema for HaloMeister.BlamBridge.
// Values are emitted into generated_game_build.h as kNativeAddresses.
// Do not hardcode simulation RVAs in blam_bridge.cpp.

#pragma once

#include <cstdint>

struct NativeAddressTable
{
    // Core object / spawn path
    std::uintptr_t savedFilmOpen;
    std::uintptr_t commandPump;
    std::uintptr_t placementInitialize;
    std::uintptr_t objectNew;
    std::uintptr_t objectDelete;
    std::uintptr_t objectSetVariant;
    std::uintptr_t objectSetColors;
    std::uintptr_t objectChanged;
    std::uintptr_t objectGet;
    std::uintptr_t objectGetPosition;
    std::uintptr_t objectGetOrientation;
    std::uintptr_t objectTeleport;
    std::uintptr_t objectSetPhysics;
    std::uintptr_t unitAddWeapon;
    std::uintptr_t simulationContext;
    std::uintptr_t playerEnableInput;
    std::uintptr_t machinimaCameraToggle;
    std::uintptr_t aiPlace;
    std::uintptr_t actorNew;
    std::uintptr_t actorStartingLocationsBuild;
    std::uintptr_t cheatBumpPossession;
    std::uintptr_t skullMaskApply;
    std::uintptr_t tlsIndex;
    std::uintptr_t scenarioRootPointer;
    std::uintptr_t tagArenaTable;

    // AI / fireteam / HaloScript hooks (previously hardcoded in blam_bridge.cpp)
    std::uintptr_t hsArgumentsEvaluate;
    std::uintptr_t hsReturn;
    std::uintptr_t aiPlayerAddFireteamSquad;
    std::uintptr_t aiPlayerAddFireteamSquadArgumentsReturn;
    std::uintptr_t aiPlayerAddFireteamSquadHsReturn;
    std::uintptr_t aiObjectStateResolve;
    std::uintptr_t aiObjectSetTeam;
    std::uintptr_t aiPlacePreObjectReturn;
};
