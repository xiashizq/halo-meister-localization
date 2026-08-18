# Game update playbook

Halo Meister treats every `HaloSimulation_tag_release.dll` update as an unknown
build until its fingerprint and memory anchors have been verified. Unknown builds
are read/write blocked; do not bypass that guard by changing only the timestamp.

## Current supported build

The native bridge currently targets the 2026-08-17 Steam update,
catalogued as `2026-08-17-steam` (Steam build `24670874`):

- SHA-256: `C8C144404ADF61A9DE821C996682A7E66ABADD7E530397D3BBDE31C123203BF7`
- host SHA-256: `EB1DACA659207F2B5C8A6FD922917195AE9C8AE19E771E396E08906282A4B152`
- PE timestamp: `0x6A7A740A`
- image size: `0x02CE1000`
- runtime tag-table pointer: `0x0182D1E8`
- segmented tag arena table: `0x02C2CC90`
- TLS index: `0x00D72730`
- scenario root pointer: `0x010C3558`

The older `2026-07-29-wingdk` and `2026-07-25-steam-post-cu2` fingerprints remain
in `Assets/GameBuildProfiles.json` so unmanaged tools can still identify those
DLLs. They no longer share this native layout: many Blam functions moved `+0x10`
while the tag/arena/TLS/scenario/string-ID data globals did not.

`objectSetPhysics` is not a unique 16-byte signature. The selected RVA
`0x005A60A0` is the object-module match whose RIP-relative load points at the PE
TLS index and sits next to the other relocated `object_*` functions. Other
prefix clones were rejected.

The supplied community mappings for the pulse hook, pool/heap globals, tag and
segment tables, and string-ID globals are retained in
`Assets/GameBuildProfiles.json`. Halo Meister currently consumes the tag-table and
arena-table values; the other mappings are research anchors for later features.
The complete multiplayer, Survival, Sandbox, Megalo, cinematic, object, tag, and
allocator old-to-new migration table—including provisional entries and their
confidence labels—is preserved in
`docs/address-migrations/2026-07-29-wingdk.md` and is parsed by the analyzer.
Generation also emits `generated_research_hooks.h`, a namespaced C++ catalog
which future native features can consume after validating the relevant ABI.

- Independent static analysis relocated every native function used by the bridge.
  Most were unique wildcard-signature matches. Common prologues were selected by
  proximity to the previous verified RVA, and their complete current prologues are
  emitted into the generated native header. HaloScript / fireteam / AI-team helpers
  also live in the profile `native` table and are aggregated as `NativeAddressTable`
  (`native_address_table.h` + `kNativeAddresses`). Absolute return sites that are
  not function entries remain profile-maintained (see analyzer
  `MANUAL_NATIVE_ASSUMPTIONS.aiHooks`). Cheat globals are not delta-adjusted:
the update reordered their table, so the analyzer finds each ASCII name, locates
the sole pointer to it in a type-5 registration record, and derives the writable
value at record `+0x10`.

`scenarioRootPointer` is `0x10C3558` in the additional exact migration table and
remains protected by runtime scenario-layout checks. It must still be exercised
in a loaded mission before boundary overrides are considered live validated.

## After the next game update

1. Keep the game closed and retain the previous profile/catalog entry.
2. Run the read-only analyzer:

   ```powershell
   python tools\game_build_analyzer.py `
     --dll "...\HaloSimulation_tag_release.dll" `
     --base current `
     --report ".analysis\game-build-report.json"
   ```

   Its report includes the prior migration catalog as named relocation seeds, so
   future updates can compare each multiplayer and Survival hook without relying
   on chat history.

3. Review the report:

   - record the new SHA-256, PE timestamp, and image size;
   - require a unique match for narrow function signatures;
   - inspect every ambiguous match and never accept proximity alone;
   - recover cheat registrations semantically, not by applying a section delta;
   - independently recover or obtain the tag-table, arena-table, TLS, scenario,
     and other data anchors because data subsections can move by different amounts.

4. Add a new entry to
   `src/HaloMeister.App/Assets/GameBuildProfiles.json`. Never overwrite an older
   entry: profiles are useful for users who have not received the update yet.
5. Once the installed DLL exactly matches the new catalog entry, regenerate the
   native constants and prologue guards:

   ```powershell
   python tools\game_build_analyzer.py --generate current
   ```

6. Build the native bridge, then the solution:

   ```powershell
   cmd /c native\HaloMeister.BlamBridge\build.cmd
   dotnet build HaloMeister.sln -c Release -p:Platform=x64
   ```

7. Perform live validation in an offline campaign mission, from least invasive to
   most invasive:

   - attach and enumerate runtime tags;
   - read tag fields and resolve segmented references;
   - read cheats, skulls, soft ceilings, and player position;
   - apply and restore one reversible tag-reference edit;
   - spawn and delete a harmless object;
   - test weapon pickup, biped possession, AI placement, boundaries, and saved
     films separately.

8. Record the exact build fingerprint, analyzer report, build results, and live
   outcomes. A successful compile is static validation, not proof that every ABI
   and structure layout is unchanged.

## Design rules

- The JSON catalog is the source of truth for managed runtime-tag addresses,
  research anchors, and the native RVA table (`NativeAddressTable`).
- `native_address_table.h` defines the address schema; `generated_game_build.h`
  is generated input that fills `kNativeAddresses` and prologue guards.
- The managed app requires an exact SHA-256, timestamp, and image-size match.
- The native bridge additionally validates operation-specific machine-code
  prologues before calling or hooking a function.
- No unknown build may write game memory.
- No global address is accepted solely because a neighboring global moved by the
  same delta.
