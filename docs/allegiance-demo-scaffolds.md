# Allegiance Demo scaffolds

Friendly/hostile AI spawn borrows a scenario squad scaffold. Location variance
happens when a mission area has no usable `player`/`human` spawn points, so the
spawner falls back to a hostile encounter squad.

## Runtime diagnosis

1. Open **Live tools → Allegiance Demo**, connect, then **Scan**.
2. Status shows ally / idle / hostile / dedicated scaffold counts.
3. Every spawn appends a line to  
   `%LOCALAPPDATA%\Meteorite\Saved\HaloMeister\AllegianceDemo\scaffold-diagnosis.log`
4. If the spawn message contains `Borrowed hostile scaffold`, that location has
   no usable ally scaffold.

## Bridge requirement

Allegiance Demo needs bridge **v107** (AI spawn beside the player). Install/repair the bridge and restart the game.

## Built-in mod (MMYJ_FULL_VEHI_WAP_P)

`hm_ally` / `hm_hostile` are baked into the same bundled overlay as Full
Palettes vehicle/weapon expansions, a curated AI character-palette fill
(schema max 64; representatives first, then secondary variants to fill;
drivers/pilots and broken helpers excluded), plus Superior Marines (`trooper.character`, retuned to press/melee and hide less)
and Superior Covenant (elite/grunt/jackal/brute/hunter)
AI overlays: `MMYJ_FULL_VEHI_WAP_P.{utoc,ucas,pak}`
under `src/HaloMeister.App/Assets/Overlays/`.

In the app: **Game files → Built-in mod** (also listed under Live tools → Spawn).

- Install copies the three files into `Meteorite/Content/Paks`.
- **Restart the game** after install/remove.
- Allegiance Demo friend/foe spawn is enabled only when all three files are
  detected.
- Vehicle Workshop / Weapon Loader work without the mod; catalogs may be
  incomplete per mission.

Rebuild the bundled assets after exporter changes:

```powershell
cd native\HaloMeister.TagModExporter
.\expand-palettes.ps1 -DryRun
.\expand-palettes.ps1 -Install -UpdateBundledAssets
```

Validate every mission's dedicated scaffolds (BuildPlan-usable spawn points):

```powershell
$exe = ".\target\release\halomeister-tagmod-exporter.exe"
$paks = "<game>\Meteorite\Content\Paks"
& $exe --paks $paks --dump-demo-squads
# Expect: VALIDATE scenarios=13 dedicated_failures=0
```

Donors must have at least one spawn point with a direct `character type` index
or a resolvable `cell` (including cells under `designer` / `templated`). Spawn
points that only have a position (`character type=-1`, `cell=-1`) are rejected —
that is what made d40 report `dedicatedAlly=1` while still borrowing
`sq_grunt_ee`. Existing dedicated squads with mixed usable/unusable points are
repaired by stamping a resolved palette index onto every unusable point.

For all-hostile missions (no player/human squads), `hm_ally` is cloned from a
combat-preferring **usable** hostile donor, then rewritten to team player while
**keeping** the donor combat objective/task. Runtime spawn prefers those names
and does not clear their objective.

After install, restart the game and Scan: expect usable `hm_ally≥1`. Friendly
spawn should report `scaffold=dedicated:hm_ally` and `follow=hm_ally`. Only that
dedicated scaffold joins the player fireteam. Borrowed mission squads stay on
the player team but do not follow. Hostile spawn does not join the fireteam.
