# Halo Meister native Blam bridge

This small Lua-loadable native module queues Campaign Evolved Blam object creation
from the Unreal game thread. A guarded trampoline on the simulation-context getter
claims the request from a thread with a valid Blam world, then invokes object, weapon
pickup, or AI placement. It intentionally does not link against Lua or UE4SS:
`package.loadlib` invokes the exported `HaloMeisterBlamInvoke` function, which reads
and writes the existing authenticated Halo Meister mailbox.

Support is locked to the current Steam (`2026-08-17-steam`)
`HaloSimulation_tag_release.dll` build recorded in the game-build profile catalog.
The module validates the PE timestamp, image size, and operation-specific function
prologues before calling anything.

Verified static call chain:

- placement initializer: module RVA `0x5EE570`, 0x330-byte placement structure;
- placement position: three floats at structure offset `0x1C`;
- object allocator/constructor: module RVA `0x5A0FC0`, returns a 32-bit object datum.
- player weapon pickup: module RVA `0x609BC0`, called with the reflected controlled
  unit datum, the newly created weapon datum, and pickup method `4` (the method used
  by the game's normal pickup flow).
- rejected-weapon cleanup: module RVA `0x5A2C30`; a temporary weapon object is
  deleted if the engine will not accept it into the inventory.
- live object model variant: the registered `object_set_variant` script handler at
  RVA `0x1B7D00` forwards the controlled object datum and model-variant `string_id`
  to RVA `0x5A7D10`. Halo Meister calls the latter on the simulation callback after
  validating its prologue, so Customization changes the existing player immediately.
- live per-object colors: RVA `0x5B32D0` receives the controlled object datum, a
  four-bit active-channel mask, and twelve normalized RGB floats. Halo Meister calls
  it on the same simulation callback, then enqueues the same object-change event at
  RVA `0x4586E0` that `object_set_variant` uses to publish the renderer update. The
  bridge repeats the color write and event after a short synchronization delay so an
  intervening engine tick cannot restore the previous appearance. Both
  prologues are validated before either call.
- simulation-context getter: module RVA `0x180E70`; a null context on the Unreal
  game thread is why direct bridge v6 calls were rejected.
- AI placement evaluator: module RVA `0x0FD810`; for `[char]` tags Halo Meister
  temporarily substitutes the character reference and player-relative position
  into loaded scenario squad spawn points, places either one actor or a five-member
  team, then restores the original scenario bytes. Team placement requires five
  usable points in one squad, temporarily overrides that squad to Covenant team `3`,
  and restores the original team plus shared palette references in reverse order.
- built-in `cheat_bump_possession` boolean: writable module RVA `0x9A92F0`.
  The biped operation enables it before creating a selected `[bipd]`; the separate
  disable operation clears it after the player collides with the new unit. If object
  creation fails, the previous flag value is restored.
- registered cheat-global table: 15 semantically recovered records between RVAs
  `0x9A91D8` and `0x9A9340`. Their `+0x10` fields are null backing-value pointers in retail, not
  inline booleans, so they are never used for gameplay changes.
- separate gameplay-cheat hook: the `HaloMeisterCheatInvoke` export reads its own
  `HMCHEAT1` mailbox and owns request state independent of object creation. Infinite
  Health / Invulnerability changes bit 11 (`skull_superman`, experimental); Infinite
  Ammo changes bit 18 (Bandanna); Jetpack / Flight changes bit 36 (Acrophobia /
  `skull_boots_off_the_ground`). The hook applies the full mask through RVA `0x209FC0`
  on an eligible simulation callback and verifies it before completing the request.
- player allegiance: resolves the controlled unit with the build-verified
  `object_get` routine, changes the unit's actual campaign-team byte used by AI
  targeting, and mirrors that value into the retail `object_set_allegiance`
  table. Because campaign synchronization republishes the authored team, the
  simulation hook maintains both selected values while the same unit datum is
  alive. The bridge snapshots both originals and never rewrites the
  scenario-wide AI allegiance matrix.
- physical soft-ceiling override: registered boolean `soft_ceilings_disable` at
  registration RVA `0x9A65E0`, name RVA `0x7E7CB8`, and value RVA `0x9A65F0`.
  Read/write requests validate the registration and run on the simulation thread.
- runtime kill/out-of-bounds override: reads the active scenario through RVA
  `0x10C3558`, maps trigger volumes to kill-trigger indices using the verified
  `0x7C` trigger-volume layout and `+0x78` kill index, then changes the
  simulation-thread disable bitset reached through TLS offset `0x340`.
  Disable snapshots the original words and restore requires the same active
  scenario. This is the runtime path used by countdown boundaries; changing
  loaded scenario block counts after mission initialization is ineffective.
- saved-film open wrapper: module RVA `0x205500`; it accepts the same mode-1/full-path
  request used by the game's native command queue, reads the CE `0x1F9B8`-byte film
  header through RVA `0x2057B0`, populates native session options, and advances the
  saved-film state machine. The bridge only accepts finalized `.film` files inside
  Meteorite's autosave directory. Saved-film requests are claimed between ticks by a
  guarded trampoline on the retail Blam command pump at RVA `0x0E670`, the same native
  thread that normally calls the wrapper. They must not run from UE4SS's game thread
  or re-entrantly from the simulation-context hook.

Run `build.cmd` from this directory to produce
`src/HaloMeister.App/Assets/UE4SS/halomeister_blam_v45.dll`.

Build-specific addresses live in `NativeAddressTable` (`native_address_table.h`)
and are filled by `generated_game_build.h`. See
`docs/game-update-playbook.md` and run `tools/game_build_analyzer.py` after an
update instead of adjusting RVAs in `blam_bridge.cpp`.
