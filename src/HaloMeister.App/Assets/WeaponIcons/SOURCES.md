# Weapon icon sources

These PNG files are decoded from the user's installed copy of Halo: Campaign
Evolved. Their cooked source textures are under:

- `Meteorite/Content/ui/Hud/WeaponCradle/Textures`
- `Meteorite/Content/ui/Hud/GrenadeCradle/Textures`

The assets were converted from the game's IoStore containers with `retoc
to-legacy`, then their inline BC7 / DXT1 mip data was decoded to PNG without
changing the artwork. They are used only as local UI previews for loaded
runtime weapon tags.

## Coverage notes

`WeaponCradle/Textures` was re-scanned against `pakchunk0-Windows` (2026-08).
The game ships one cradle icon that was previously missing from Halo Meister:

- `T_UI_SeraphMissiles_Icons` (DXT1) — added for Seraph missile hardpoints

Every other `T_UI_*WeaponIcon*` / turret cradle texture was already present.
Close cousins (gravity hammer → energy sword, concussion → fuel rod, etc.)
still reuse nearby cradle art via `ProjectileSwapperService.WeaponIconUri`.

Wiki fallbacks (no dedicated cradle icon in the game pak):

| File | Source |
|------|--------|
| `wiki_brute_shot.png` | Halopedia [H2A - Brute Shot model.jpg](https://www.halopedia.org/File:H2A_-_Brute_Shot_model.jpg) |
| `wiki_mauler.png` | Halopedia [HO Mauler HiPoly Render 1.jpg](https://www.halopedia.org/File:HO_Mauler_HiPoly_Render_1.jpg) |

Anything with no confident mapping falls back to `missing.png` rather than
the assault rifle.
