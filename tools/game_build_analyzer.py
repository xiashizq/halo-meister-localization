#!/usr/bin/env python3
"""Fingerprint and relocate Halo Meister's game-build profile.

The analyzer is read-only unless --generate is supplied. It finds code anchors
with wildcarded byte signatures, recovers cheat globals from their semantic
name registrations, and compares the result with the canonical JSON catalog.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
from pathlib import Path
from typing import Any

import pefile


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CATALOG = (
    ROOT / "src/HaloMeister.App/Assets/GameBuildProfiles.json"
)
GENERATED_HEADER = (
    ROOT / "native/HaloMeister.BlamBridge/generated_game_build.h"
)
GENERATED_RESEARCH_HEADER = (
    ROOT / "native/HaloMeister.BlamBridge/generated_research_hooks.h"
)
DEFAULT_MIGRATION_TABLE = (
    ROOT / "docs/address-migrations/2026-07-29-wingdk.md"
)

# Wildcards cover RIP-relative displacements, which normally change when a
# section moves. The nearest known RVA disambiguates common function prologues.
FUNCTION_PATTERNS = {
    "savedFilmOpen": "48 83 EC 28 4C 8D 05 ?? ?? ?? ?? C7 05",
    "commandPump": (
        "48 89 5C 24 08 55 56 57 41 54 41 55 41 56 41 57 "
        "48 8D AC 24 80 FD FF FF"
    ),
    "placementInitialize": (
        "40 53 55 56 57 41 55 41 56 41 57 48 83 EC 20 41"
    ),
    "objectNew": (
        "48 89 4C 24 08 41 54 41 55 48 81 EC 98 04 00 00"
    ),
    "objectDelete": "48 8B C4 53 48 83 EC 70 8B 15 ?? ?? ?? ?? 48 89",
    "objectSetVariant": (
        "48 89 5C 24 08 57 48 83 EC 20 44 8B 05 ?? ?? ?? ?? "
        "65 48 8B 04 25 58 00 00 00 8B F9 41 B9 20"
    ),
    "objectSetColors": (
        "4C 89 44 24 18 53 55 56 57 41 54 41 56 41 57 48 "
        "81 EC 20 01 00 00 65 48 8B 04 25 58 00 00 00 4C "
        "8D 35 ?? ?? ?? ?? 0F B6 DA 49 8B E8"
    ),
    "objectChanged": (
        "48 89 5C 24 10 48 89 74 24 18 57 48 83 EC 20 65 "
        "48 8B 04 25 58 00 00 00 44 8B 05 ?? ?? ?? ?? 0F B6 F2"
    ),
    "objectGet": (
        "44 8B C9 45 33 C0 65 48 8B 0C 25 58 00 00 00 44"
    ),
    "objectGetPosition": (
        "44 8B 05 ?? ?? ?? ?? 4C 8B CA 65 48 8B 04 25 58 "
        "00 00 00 BA 20 00 00 00 4A 8B 04 C0 4C 8B 04 10 "
        "0F B7 C1 49 8B 50 50"
    ),
    "objectGetOrientation": (
        "40 53 48 81 EC A0 00 00 00 44 8B 0D ?? ?? ?? ??"
    ),
    "objectTeleport": (
        "83 F9 FF 0F 84 ?? ?? ?? ?? 48 8B C4 48 89 50 10"
    ),
    "objectSetPhysics": (
        "48 89 5C 24 08 57 48 83 EC 20 44 8B 05 ?? ?? ?? ??"
    ),
    "unitAddWeapon": (
        "48 89 5C 24 10 55 56 57 41 54 41 55 41 56 41 57 "
        "48 8D 6C 24 D9 48 81 EC B0 00 00 00 44 8B 0D "
        "?? ?? ?? ?? 65 48 8B 04 25 58 00 00 00 41 BC 14"
    ),
    "simulationContext": (
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 "
        "8B 0D ?? ?? ?? ?? 65 48 8B 04 25 58 00 00 00 "
        "BF 14 00 00 00 48 8B 1C C8 80 3C 3B 00 75 05 E8 "
        "?? ?? ?? ?? BE 38 00 00 00"
    ),
    "playerEnableInput": (
        "65 48 8B 04 25 58 00 00 00 80 F1 01 8B 15 ?? ?? ?? ?? "
        "4C 8B 04 D0 B8 F8 00 00 00"
    ),
    "machinimaCameraToggle": (
        "8B 0D ?? ?? ?? ?? 44 8B CA 65 48 8B 04 25 58 00 00 00 "
        "45 32 C0 BA B8 00 00 00"
    ),
    "aiPlace": (
        "48 89 5C 24 10 56 48 83 EC 70 65 48 8B 04 25 58"
    ),
    "actorNew": (
        "48 8B C4 48 89 50 10 66 89 48 08 55 53 56 57"
    ),
    "actorStartingLocationsBuild": (
        "4C 89 4C 24 20 4C 89 44 24 18 89 54 24 10 55 53"
    ),
    "skullMaskApply": (
        "4C 8B DC 56 57 41 56 48 83 EC 50 8B 15 ?? ?? ??"
    ),
    "hsArgumentsEvaluate": (
        "48 8B C4 44 88 48 20 4C 89 40 18 66 89 50 10 53 56 57 41 56"
    ),
    "hsReturn": (
        "40 53 55 56 57 41 56 48 83 EC 20 65 48 8B 04 25 58 00 00 00"
    ),
    "aiPlayerAddFireteamSquad": (
        "89 54 24 10 48 83 EC 78 8B C2 45 0F B6 C8 48 0F"
    ),
    "aiObjectStateResolve": (
        "40 53 55 56 57 41 54 41 56 41 57 48 83 EC 20 8B"
    ),
    "aiObjectSetTeam": (
        "48 89 5C 24 10 89 4C 24 08 55 56 57 41 54 41 55"
    ),
}

# A capability is never enabled merely because its build fingerprint matched.
# This table makes the static evidence emitted by the analyzer reviewable by
# feature, while final promotion remains a separate live-validation decision.
CAPABILITY_REQUIREMENTS = {
    "ObjectSpawn": ["placementInitialize", "objectNew", "objectGetPosition", "objectGetOrientation"],
    "WeaponLoad": ["placementInitialize", "objectNew", "objectDelete", "unitAddWeapon"],
    "ObjectAppearance": ["objectSetVariant", "objectSetColors", "objectChanged"],
    "AiPlacement": [
        "aiPlace",
        "actorNew",
        "actorStartingLocationsBuild",
        "hsArgumentsEvaluate",
        "hsReturn",
        "aiPlayerAddFireteamSquad",
    ],
    "GameplayCheats": ["skullMaskApply"],
    "PlayerTools": [
        "objectGetPosition",
        "objectGetOrientation",
        "objectTeleport",
        "objectSetPhysics",
        "playerEnableInput",
    ],
    "PlayerAllegiance": [
        "objectGet",
        "aiObjectStateResolve",
        "aiObjectSetTeam",
    ],
    "Machinima": ["machinimaCameraToggle"],
    "SavedFilm": ["savedFilmOpen", "commandPump"],
}

# These are native ABI assumptions which cannot yet be recovered from a
# byte-pattern alone. They remain visible in every report so a new build cannot
# be promoted without an explicit review of the remaining manual evidence.
MANUAL_NATIVE_ASSUMPTIONS = {
    "aiHooks": [
        # Absolute return sites inside relocated functions. Kept in the profile
        # native table (not signature-scanned) because they are call-return
        # addresses rather than function entry points.
        "aiPlayerAddFireteamSquadArgumentsReturn",
        "aiPlayerAddFireteamSquadHsReturn",
        "aiPlacePreObjectReturn",
    ],
    "layouts": [
        "thread TLS offsets",
        "placement data size and position offset",
        "actor record and starting-location layout",
        "scenario trigger-volume and kill-trigger layout",
    ],
}

# Ordered fields for NativeAddressTable in native_address_table.h.
NATIVE_ADDRESS_TABLE_FIELDS = [
    "savedFilmOpen",
    "commandPump",
    "placementInitialize",
    "objectNew",
    "objectDelete",
    "objectSetVariant",
    "objectSetColors",
    "objectChanged",
    "objectGet",
    "objectGetPosition",
    "objectGetOrientation",
    "objectTeleport",
    "objectSetPhysics",
    "unitAddWeapon",
    "simulationContext",
    "playerEnableInput",
    "machinimaCameraToggle",
    "aiPlace",
    "actorNew",
    "actorStartingLocationsBuild",
    "cheatBumpPossession",
    "skullMaskApply",
    "tlsIndex",
    "scenarioRootPointer",
    "tagArenaTable",
    "hsArgumentsEvaluate",
    "hsReturn",
    "aiPlayerAddFireteamSquad",
    "aiPlayerAddFireteamSquadArgumentsReturn",
    "aiPlayerAddFireteamSquadHsReturn",
    "aiObjectStateResolve",
    "aiObjectSetTeam",
    "aiPlacePreObjectReturn",
]

CPP_NAMES = {
    "savedFilmOpen": "kSavedFilmOpenRva",
    "commandPump": "kCommandPumpRva",
    "placementInitialize": "kPlacementInitializeRva",
    "objectNew": "kObjectNewRva",
    "objectDelete": "kObjectDeleteRva",
    "objectSetVariant": "kObjectSetVariantRva",
    "objectSetColors": "kObjectSetColorsRva",
    "objectChanged": "kObjectChangedRva",
    "objectGet": "kObjectGetRva",
    "objectGetPosition": "kObjectGetPositionRva",
    "objectGetOrientation": "kObjectGetOrientationRva",
    "objectTeleport": "kObjectTeleportRva",
    "objectSetPhysics": "kObjectSetPhysicsRva",
    "unitAddWeapon": "kUnitAddWeaponRva",
    "simulationContext": "kSimulationContextRva",
    "playerEnableInput": "kPlayerEnableInputRva",
    "machinimaCameraToggle": "kMachinimaCameraToggleRva",
    "aiPlace": "kAiPlaceRva",
    "actorNew": "kActorNewRva",
    "actorStartingLocationsBuild": "kActorStartingLocationsBuildRva",
    "cheatBumpPossession": "kCheatBumpPossessionRva",
    "skullMaskApply": "kSkullMaskApplyRva",
    "tlsIndex": "kTlsIndexRva",
    "scenarioRootPointer": "kScenarioRootPointerRva",
    "tagArenaTable": "kTagArenaTableRva",
    "hsArgumentsEvaluate": "kHsArgumentsEvaluateRva",
    "hsReturn": "kHsReturnRva",
    "aiPlayerAddFireteamSquad": "kAiPlayerAddFireteamSquadRva",
    "aiPlayerAddFireteamSquadArgumentsReturn": (
        "kAiPlayerAddFireteamSquadArgumentsReturnRva"
    ),
    "aiPlayerAddFireteamSquadHsReturn": (
        "kAiPlayerAddFireteamSquadHsReturnRva"
    ),
    "aiObjectStateResolve": "kAiObjectStateResolveRva",
    "aiObjectSetTeam": "kAiObjectSetTeamRva",
    "aiPlacePreObjectReturn": "kAiPlacePreObjectReturnRva",
}

PROLOGUE_NAMES = {
    "placementInitialize": ("kPlacementInitializePrologue", 16),
    "objectNew": ("kObjectNewPrologue", 16),
    "objectDelete": ("kObjectDeletePrologue", 16),
    "unitAddWeapon": ("kUnitAddWeaponPrologue", 16),
    "objectSetVariant": ("kObjectSetVariantPrologue", 16),
    "objectSetColors": ("kObjectSetColorsPrologue", 16),
    "objectChanged": ("kObjectChangedPrologue", 16),
    "objectGet": ("kObjectGetPrologue", 16),
    "objectGetPosition": ("kObjectGetPositionPrologue", 16),
    "objectGetOrientation": ("kObjectGetOrientationPrologue", 16),
    "objectTeleport": ("kObjectTeleportPrologue", 16),
    "objectSetPhysics": ("kObjectSetPhysicsPrologue", 16),
    "simulationContext": ("kSimulationContextPrologue", 15),
    "playerEnableInput": ("kPlayerEnableInputPrologue", 16),
    "machinimaCameraToggle": ("kMachinimaCameraTogglePrologue", 16),
    "aiPlace": ("kAiPlacePrologue", 16),
    "actorNew": ("kActorNewPrologue", 15),
    "actorStartingLocationsBuild": (
        "kActorStartingLocationsBuildPrologue",
        16,
    ),
    "savedFilmOpen": ("kSavedFilmOpenPrologue", 16),
    "commandPump": ("kCommandPumpPrologue", 16),
    "skullMaskApply": ("kSkullMaskApplyPrologue", 16),
    "hsArgumentsEvaluate": ("kHsArgumentsEvaluatePrologue", 20),
    "hsReturn": ("kHsReturnPrologue", 20),
    "aiPlayerAddFireteamSquad": ("kAiPlayerAddFireteamSquadPrologue", 16),
    "aiObjectStateResolve": ("kAiObjectStateResolvePrologue", 16),
    "aiObjectSetTeam": ("kAiObjectSetTeamPrologue", 16),
}

CHEAT_NAMES = [
    "cheat_inhibit_input_only_when_activating",
    "cheat_infinite_equipment_energy",
    "cheat_controller",
    "cheat_omnipotent",
    "cheat_porcupine",
    "cheat_chevy",
    "cheat_super_jump",
    "cheat_bump_possession",
    "cheat_medusa",
    "cheat_reflexive_damage_effects",
    "cheat_jetpack",
    "cheat_valhalla",
    "cheat_bottomless_clip",
    "cheat_infinite_ammo",
    "cheat_deathless_player",
]

MIGRATION_ROW = re.compile(
    r"^\|\s*`(?P<name>[^`]+)`\s*"
    r"\|\s*`(?P<old>0x[0-9A-Fa-f]+)`\s*"
    r"\|\s*`(?P<new>0x[0-9A-Fa-f]+)`\s*"
    r"\|\s*`(?P<delta>[+-]0x[0-9A-Fa-f]+)`\s*"
    r"\|\s*(?P<confidence>[^|]+?)\s*\|$"
)


def parse_hex(value: str) -> int:
    return int(value, 16)


def hex_rva(value: int) -> str:
    return f"0x{value:08X}"


def load_migrations(path: Path) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []
    for line_number, line in enumerate(
        path.read_text(encoding="utf-8").splitlines(),
        start=1,
    ):
        match = MIGRATION_ROW.match(line)
        if not match:
            continue
        old_rva = int(match["old"], 16)
        new_rva = int(match["new"], 16)
        delta_text = match["delta"]
        declared_delta = int(delta_text[1:], 16)
        if delta_text.startswith("-"):
            declared_delta = -declared_delta
        actual_delta = new_rva - old_rva
        if declared_delta != actual_delta:
            raise RuntimeError(
                f"{path}:{line_number}: {match['name']} declares delta "
                f"{delta_text}, calculated {actual_delta:+#x}"
            )
        entries.append(
            {
                "name": match["name"],
                "oldRva": hex_rva(old_rva),
                "newRva": hex_rva(new_rva),
                "delta": f"{actual_delta:+#x}",
                "confidence": match["confidence"].strip(),
            }
        )
    if not entries:
        raise RuntimeError(f"No address migrations were parsed from {path}")
    return entries


def compile_pattern(text: str) -> tuple[bytes, bytes]:
    values = bytearray()
    mask = bytearray()
    for token in text.split():
        if token == "??":
            values.append(0)
            mask.append(0)
        else:
            values.append(int(token, 16))
            mask.append(0xFF)
    return bytes(values), bytes(mask)


def pattern_matches(data: bytes, offset: int, values: bytes, mask: bytes) -> bool:
    return all(
        not expected_mask or data[offset + index] == expected
        for index, (expected, expected_mask) in enumerate(zip(values, mask))
    )


def scan_pattern(pe: pefile.PE, text: str) -> list[int]:
    values, mask = compile_pattern(text)
    matches: list[int] = []
    fixed_runs: list[tuple[int, int]] = []
    run_start: int | None = None
    for index, expected_mask in enumerate(mask + b"\0"):
        if expected_mask and run_start is None:
            run_start = index
        elif not expected_mask and run_start is not None:
            fixed_runs.append((run_start, index))
            run_start = None
    anchor_start, anchor_end = max(
        fixed_runs,
        key=lambda run: run[1] - run[0],
    )
    anchor = values[anchor_start:anchor_end]
    for section in pe.sections:
        if not section.IMAGE_SCN_MEM_EXECUTE:
            continue
        raw = section.get_data()
        search_from = 0
        while True:
            anchor_offset = raw.find(anchor, search_from)
            if anchor_offset < 0:
                break
            search_from = anchor_offset + 1
            offset = anchor_offset - anchor_start
            if (
                offset >= 0
                and offset + len(values) <= len(raw)
                and pattern_matches(raw, offset, values, mask)
            ):
                matches.append(section.VirtualAddress + offset)
    return matches


def read_rva(pe: pefile.PE, rva: int, count: int) -> bytes:
    return pe.get_data(rva, count)


def recover_cheat(
    pe: pefile.PE,
    file_data: bytes,
    name: str,
) -> dict[str, str]:
    name_offset = file_data.find(name.encode("ascii") + b"\0")
    if name_offset < 0:
        raise RuntimeError(f"String not found: {name}")
    name_rva = pe.get_rva_from_offset(name_offset)
    pointer = struct.pack("<Q", pe.OPTIONAL_HEADER.ImageBase + name_rva)
    candidates: list[int] = []
    start = 0
    while True:
        occurrence = file_data.find(pointer, start)
        if occurrence < 0:
            break
        start = occurrence + 1
        try:
            registration_rva = pe.get_rva_from_offset(occurrence)
        except pefile.PEFormatError:
            continue
        if read_rva(pe, registration_rva + 8, 8) == struct.pack("<Q", 5):
            candidates.append(registration_rva)
    if len(candidates) != 1:
        raise RuntimeError(
            f"{name}: expected one type-5 registration, found {len(candidates)}"
        )
    registration = candidates[0]
    return {
        "name": name,
        "registration": hex_rva(registration),
        "nameRva": hex_rva(name_rva),
        "value": hex_rva(registration + 0x10),
    }


def choose_profile(
    catalog: dict[str, Any],
    profile_id: str | None,
) -> dict[str, Any]:
    profiles = catalog["profiles"]
    if profile_id in (None, "current"):
        return profiles[-1]
    return next(
        profile for profile in profiles if profile["id"] == profile_id
    )


def analyze(
    dll: Path,
    catalog: dict[str, Any],
    base_profile: dict[str, Any],
    migration_table: Path,
) -> tuple[dict[str, Any], pefile.PE]:
    file_data = dll.read_bytes()
    pe = pefile.PE(data=file_data, fast_load=False)
    digest = hashlib.sha256(file_data).hexdigest().upper()
    native_seed = base_profile.get("native", {})

    functions: dict[str, Any] = {}
    for name, pattern in FUNCTION_PATTERNS.items():
        candidates = scan_pattern(pe, pattern)
        seed = parse_hex(native_seed[name]) if name in native_seed else None
        selected = (
            min(candidates, key=lambda candidate: abs(candidate - seed))
            if candidates and seed is not None
            else candidates[0] if len(candidates) == 1 else None
        )
        functions[name] = {
            "selected": hex_rva(selected) if selected is not None else None,
            "candidates": [hex_rva(value) for value in candidates],
            "distanceFromSeed": (
                selected - seed
                if selected is not None and seed is not None
                else None
            ),
        }

    cheats = [
        recover_cheat(pe, file_data, name)
        for name in CHEAT_NAMES + ["soft_ceilings_disable"]
    ]
    capability_evidence = {}
    for capability, required in CAPABILITY_REQUIREMENTS.items():
        unresolved = [
            name for name in required if functions[name]["selected"] is None
        ]
        ambiguous = [
            name for name in required if len(functions[name]["candidates"]) != 1
        ]
        capability_evidence[capability] = {
            "requiredFunctions": required,
            "staticVerified": not unresolved and not ambiguous,
            "unresolvedFunctions": unresolved,
            "ambiguousFunctions": ambiguous,
        }
    report = {
        "dll": str(dll),
        "sha256": digest,
        "peTimestamp": hex_rva(pe.FILE_HEADER.TimeDateStamp),
        "imageSize": hex_rva(pe.OPTIONAL_HEADER.SizeOfImage),
        "baseProfile": base_profile["id"],
        "exactCatalogMatch": next(
            (
                profile["id"]
                for profile in catalog["profiles"]
                if profile["sha256"].upper() == digest
                and parse_hex(profile["peTimestamp"])
                == pe.FILE_HEADER.TimeDateStamp
                and parse_hex(profile["imageSize"])
                == pe.OPTIONAL_HEADER.SizeOfImage
            ),
            None,
        ),
        "functions": functions,
        "capabilities": capability_evidence,
        "manualReviewRequired": MANUAL_NATIVE_ASSUMPTIONS,
        "cheatGlobals": cheats,
        "profileAnchors": {
            "runtimeTags": base_profile.get("runtimeTags", {}),
            "researchAnchors": base_profile.get("researchAnchors", {}),
            "nativeData": {
                key: value
                for key, value in native_seed.items()
                if key not in FUNCTION_PATTERNS
            },
        },
        "migrationCatalog": {
            "path": str(migration_table),
            "entries": load_migrations(migration_table),
        },
    }
    return report, pe


def byte_array(name: str, data: bytes, declared_size: str = "16") -> str:
    lines = []
    for start in range(0, len(data), 8):
        chunk = data[start : start + 8]
        lines.append("    " + ", ".join(f"0x{value:02X}" for value in chunk) + ",")
    return (
        f"constexpr std::array<std::uint8_t, {declared_size}> {name}{{\n"
        + "\n".join(lines)
        + "\n};"
    )


def generate_header(
    profile: dict[str, Any],
    report: dict[str, Any],
    pe: pefile.PE,
    catalog: dict[str, Any],
) -> str:
    if report["exactCatalogMatch"] != profile["id"]:
        raise RuntimeError(
            "--generate requires the DLL fingerprint to exactly match the selected profile"
        )
    selected = {
        name: parse_hex(details["selected"])
        for name, details in report["functions"].items()
        if details["selected"] is not None
    }
    if len(selected) != len(FUNCTION_PATTERNS):
        raise RuntimeError("Not every native function signature resolved")

    native_layout = profile.get("nativeLayout", profile["id"])
    layout_profile = choose_profile(catalog, native_layout)
    native = dict(layout_profile["native"])
    native.update({name: hex_rva(rva) for name, rva in selected.items()})
    cheats = report["cheatGlobals"]
    bump = next(item for item in cheats if item["name"] == "cheat_bump_possession")
    native["cheatBumpPossession"] = bump["value"]

    missing = [key for key in NATIVE_ADDRESS_TABLE_FIELDS if key not in native]
    if missing:
        raise RuntimeError(
            "native address table is incomplete; missing: "
            + ", ".join(missing)
        )

    supported_builds = [
        candidate
        for candidate in catalog["profiles"]
        if candidate["id"] == native_layout
        or candidate.get("nativeLayout") == native_layout
    ]
    output = [
        "// Generated from src/HaloMeister.App/Assets/GameBuildProfiles.json.",
        f'// Regenerate with: python tools/game_build_analyzer.py --dll <path-to-dll> --base {native_layout} --generate {profile["id"]}',
        "// Do not hand-edit addresses here.",
        "",
        '#include "native_address_table.h"',
        "",
        "struct SupportedBuildIdentity",
        "{",
        "    const char* id;",
        "    const char* sha256;",
        "    DWORD timestamp;",
        "    DWORD image_size;",
        "};",
        "",
        "constexpr std::array<SupportedBuildIdentity, "
        f"{len(supported_builds)}> kSupportedBuilds{{{{",
    ]
    for candidate in supported_builds:
        output.append(
            f'    {{"{candidate["id"]}", "{candidate["sha256"]}", '
            f'{candidate["peTimestamp"]}, {candidate["imageSize"]}}},'
        )
    output.extend([
        "}};",
        "",
        "constexpr NativeAddressTable kNativeAddresses{",
    ])
    for key in NATIVE_ADDRESS_TABLE_FIELDS:
        output.append(f"    {native[key]}, // {key}")
    output.extend([
        "};",
        "",
    ])
    for key in NATIVE_ADDRESS_TABLE_FIELDS:
        cpp_name = CPP_NAMES[key]
        output.append(
            f"constexpr std::uintptr_t {cpp_name} = kNativeAddresses.{key};"
        )
    output.append("")
    for key, (cpp_name, length) in PROLOGUE_NAMES.items():
        declared = (
            "kHookLength"
            if key == "simulationContext"
            else "kCommandPumpHookLength"
            if key == "commandPump"
            else "kHsHookLength"
            if key in ("hsArgumentsEvaluate", "hsReturn")
            else str(length)
        )
        source_rva = selected.get(key)
        if source_rva is None:
            source_rva = parse_hex(native[key])
        output.append(
            byte_array(cpp_name, read_rva(pe, source_rva, length), declared)
        )
    output.extend(
        [
            "",
            "struct CheatGlobal",
            "{",
            "    const char* name;",
            "    std::uintptr_t registration_rva;",
            "    std::uintptr_t name_rva;",
            "    std::uintptr_t value_rva;",
            "};",
            "",
            "constexpr std::array<CheatGlobal, 15> kCheatGlobals{{",
        ]
    )
    for item in cheats[:-1]:
        output.append(
            f'    {{"{item["name"]}", {item["registration"]}, '
            f'{item["nameRva"]}, {item["value"]}}},'
        )
    soft = cheats[-1]
    output.extend(
        [
            "}};",
            "",
            "constexpr CheatGlobal kSoftCeilingsDisable{",
            f'    "{soft["name"]}", {soft["registration"]}, '
            f'{soft["nameRva"]}, {soft["value"]},',
            "};",
            "",
        ]
    )
    return "\n".join(output)


def generate_research_header(entries: list[dict[str, Any]]) -> str:
    output = [
        "// Generated from docs/address-migrations/2026-07-29-wingdk.md.",
        "// Research seeds only: inclusion does not make a hook safe to invoke.",
        "#pragma once",
        "",
        "#include <array>",
        "#include <cstdint>",
        "",
        "namespace halo_meister_research",
        "{",
        "enum class Confidence",
        "{",
        "    exact,",
        "    content_match,",
        "    pointer_slot,",
        "    interpolated,",
        "};",
        "",
        "struct AddressMigration",
        "{",
        "    const char* name;",
        "    std::uintptr_t old_rva;",
        "    std::uintptr_t new_rva;",
        "    Confidence confidence;",
        "};",
        "",
        f"inline constexpr std::array<AddressMigration, {len(entries)}> "
        "kAddressMigrations{{",
    ]
    confidence_names = {
        "exact": "exact",
        "content match": "content_match",
        "pointer slot": "pointer_slot",
        "interpolated": "interpolated",
    }
    for entry in entries:
        confidence = confidence_names[entry["confidence"]]
        output.append(
            f'    {{"{entry["name"]}", {entry["oldRva"]}, '
            f'{entry["newRva"]}, Confidence::{confidence}}},'
        )
    output.extend(["}};", "} // namespace halo_meister_research", ""])
    return "\n".join(output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--dll",
        type=Path,
        required=True,
        help="Path to HaloSimulation_tag_release.dll for the build being analyzed.",
    )
    parser.add_argument("--catalog", type=Path, default=DEFAULT_CATALOG)
    parser.add_argument(
        "--migration-table",
        type=Path,
        default=DEFAULT_MIGRATION_TABLE,
    )
    parser.add_argument(
        "--base",
        default="current",
        help="Known profile whose RVAs disambiguate signature matches.",
    )
    parser.add_argument("--report", type=Path)
    parser.add_argument(
        "--generate",
        metavar="PROFILE",
        help="Regenerate the native header after an exact profile match.",
    )
    args = parser.parse_args()

    catalog = json.loads(args.catalog.read_text(encoding="utf-8"))
    base_profile = choose_profile(catalog, args.base)
    report, pe = analyze(
        args.dll,
        catalog,
        base_profile,
        args.migration_table,
    )
    rendered = json.dumps(report, indent=2)
    print(rendered)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(rendered + "\n", encoding="utf-8")
    if args.generate:
        profile = choose_profile(catalog, args.generate)
        GENERATED_HEADER.write_text(
            generate_header(profile, report, pe, catalog),
            encoding="utf-8",
        )
        print(f"Generated {GENERATED_HEADER}")
        GENERATED_RESEARCH_HEADER.write_text(
            generate_research_header(
                report["migrationCatalog"]["entries"]
            ),
            encoding="utf-8",
        )
        print(f"Generated {GENERATED_RESEARCH_HEADER}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
