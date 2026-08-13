use crate::ensure_demo_squads;
use crate::tune_marine_ai;
use anyhow::{Context, Result, anyhow, bail};
use blam_tags::fields::{TagFieldData, TagReferenceData};
use blam_tags::iostore::IoStoreArchive;
use blam_tags::TagFile;
use std::collections::{BTreeSet, HashSet};
use std::path::Path;

const MAX_OBJECT_PALETTE_ENTRIES: usize = 256;
const MAX_CHARACTER_PALETTE_ENTRIES: usize = 64;

struct BakedAiOverride {
    file_suffix: &'static str,
    bytes: &'static [u8],
    label: &'static str,
}

/// Eridnus Superior Marines v1.3 + Superior Covenant v1.2 character AI.
const SUPERIOR_AI_TAGS: &[BakedAiOverride] = &[
    BakedAiOverride {
        file_suffix: "objects/characters/marine/ai/trooper-character.ubulk",
        bytes: include_bytes!("../assets/superior-marines/trooper-character.ubulk"),
        label: "Superior Marines trooper",
    },
    BakedAiOverride {
        file_suffix: "objects/characters/elite/ai/elite-character.ubulk",
        bytes: include_bytes!("../assets/superior-covenant/elite-character.ubulk"),
        label: "Superior Covenant elite",
    },
    BakedAiOverride {
        file_suffix: "objects/characters/grunt/ai/grunt-character.ubulk",
        bytes: include_bytes!("../assets/superior-covenant/grunt-character.ubulk"),
        label: "Superior Covenant grunt",
    },
    BakedAiOverride {
        file_suffix: "objects/characters/jackal/ai/jackal-character.ubulk",
        bytes: include_bytes!("../assets/superior-covenant/jackal-character.ubulk"),
        label: "Superior Covenant jackal",
    },
    BakedAiOverride {
        file_suffix: "objects/characters/brute/ai/brute-character.ubulk",
        bytes: include_bytes!("../assets/superior-covenant/brute-character.ubulk"),
        label: "Superior Covenant brute",
    },
    BakedAiOverride {
        file_suffix: "objects/characters/hunter/ai/hunter-character.ubulk",
        bytes: include_bytes!("../assets/superior-covenant/hunter-character.ubulk"),
        label: "Superior Covenant hunter",
    },
];
const BIPED_GROUP: u32 = u32::from_be_bytes(*b"bipd");
const VEHICLE_GROUP: u32 = u32::from_be_bytes(*b"vehi");
const WEAPON_GROUP: u32 = u32::from_be_bytes(*b"weap");
const CHARACTER_GROUP: u32 = u32::from_be_bytes(*b"char");

pub struct ExpandReport {
    pub scenarios_seen: usize,
    pub scenarios_changed: usize,
    pub biped_catalog: usize,
    pub vehicle_catalog: usize,
    pub weapon_catalog: usize,
    pub character_catalog: usize,
    pub biped_added_total: usize,
    pub vehicle_added_total: usize,
    pub weapon_added_total: usize,
    pub character_added_total: usize,
    pub character_skipped_cap: usize,
    pub ally_added: usize,
    pub ally_from_hostile_fallback: usize,
    pub hostile_added: usize,
    pub lines: Vec<String>,
}

pub fn expand_all_mission_palettes(
    archives: &[IoStoreArchive],
    output: &Path,
    dry_run: bool,
) -> Result<ExpandReport> {
    let bipeds = collect_tag_paths(archives, "biped")?;
    let vehicles = collect_tag_paths(archives, "vehicle")?;
    let weapons = collect_tag_paths(archives, "weapon")?;
    let characters = collect_ai_character_paths(archives)?;
    let scenarios = collect_scenario_entries(archives)?;
    if bipeds.is_empty() {
        bail!("no biped tags found under Meteorite/Content/Tags");
    }
    if vehicles.is_empty() {
        bail!("no vehicle tags found under Meteorite/Content/Tags");
    }
    if weapons.is_empty() {
        bail!("no weapon tags found under Meteorite/Content/Tags");
    }
    if characters.is_empty() {
        bail!("no AI character tags found under Meteorite/Content/Tags");
    }
    if scenarios.is_empty() {
        bail!("no scenario tags found under Meteorite/Content/Tags");
    }

    let mut report = ExpandReport {
        scenarios_seen: scenarios.len(),
        scenarios_changed: 0,
        biped_catalog: bipeds.len(),
        vehicle_catalog: vehicles.len(),
        weapon_catalog: weapons.len(),
        character_catalog: characters.len(),
        biped_added_total: 0,
        vehicle_added_total: 0,
        weapon_added_total: 0,
        character_added_total: 0,
        character_skipped_cap: 0,
        ally_added: 0,
        ally_from_hostile_fallback: 0,
        hostile_added: 0,
        lines: Vec::new(),
    };
    report.lines.push(format!(
        "Catalog: {} biped(s), {} vehicle(s), {} weapon(s), {} safe AI character(s) (fill to {}); {} scenario(s) (palettes + hm_ally/hm_hostile)",
        bipeds.len(),
        vehicles.len(),
        weapons.len(),
        characters.len(),
        MAX_CHARACTER_PALETTE_ENTRIES,
        scenarios.len()
    ));

    let mut edited: Vec<(usize, String, Vec<u8>)> = Vec::new();
    for (archive_index, rel_path, tag_path) in &scenarios {
        let bytes = archives[*archive_index]
            .read(rel_path)
            .with_context(|| format!("could not read {rel_path}"))?;
        let mut tag = TagFile::read_from_bytes(&bytes)
            .with_context(|| format!("could not parse {rel_path}"))?;
        let biped_added = ensure_palette(
            &mut tag,
            "biped palette",
            BIPED_GROUP,
            &bipeds,
            "name",
            MAX_OBJECT_PALETTE_ENTRIES,
        )?;
        let vehicle_added = ensure_palette(
            &mut tag,
            "vehicle palette",
            VEHICLE_GROUP,
            &vehicles,
            "name",
            MAX_OBJECT_PALETTE_ENTRIES,
        )?;
        let weapon_added = ensure_palette(
            &mut tag,
            "weapon palette",
            WEAPON_GROUP,
            &weapons,
            "name",
            MAX_OBJECT_PALETTE_ENTRIES,
        )?;
        let character_fill = ensure_character_palette(&mut tag, &characters)?;
        let squads = ensure_demo_squads::ensure_demo_squads_on_tag(&mut tag, tag_path)?;
        if biped_added == 0
            && vehicle_added == 0
            && weapon_added == 0
            && character_fill.added == 0
            && !squads.changed
        {
            continue;
        }
        report.scenarios_changed += 1;
        report.biped_added_total += biped_added;
        report.vehicle_added_total += vehicle_added;
        report.weapon_added_total += weapon_added;
        report.character_added_total += character_fill.added;
        report.character_skipped_cap += character_fill.skipped_cap;
        report.ally_added += squads.ally_added;
        report.ally_from_hostile_fallback += squads.ally_from_hostile_fallback;
        report.hostile_added += squads.hostile_added;
        report.lines.push(format!(
            "{tag_path}: +{biped_added} biped(s), +{vehicle_added} vehicle(s), +{weapon_added} weapon(s), +{} character(s) (cap-skip {})",
            character_fill.added,
            character_fill.skipped_cap,
        ));
        report.lines.extend(squads.lines);
        if dry_run {
            continue;
        }
        let serialized = tag
            .write_to_bytes()
            .with_context(|| format!("could not serialize {rel_path}"))?;
        TagFile::read_from_bytes(&serialized)
            .with_context(|| format!("serialized verification failed for {rel_path}"))?;
        edited.push((*archive_index, rel_path.clone(), serialized));
    }

    let mut ai_overrides = Vec::new();
    for baked in SUPERIOR_AI_TAGS {
        let bytes = if baked.file_suffix.contains("trooper-character") {
            let (tuned, lines) = tune_marine_ai::apply_aggressive_trooper(baked.bytes)?;
            report.lines.extend(lines);
            tuned
        } else {
            TagFile::read_from_bytes(baked.bytes).with_context(|| {
                format!(
                    "bundled {} ({}) is not a readable tag",
                    baked.label, baked.file_suffix
                )
            })?;
            baked.bytes.to_vec()
        };
        let (archive_index, rel_path) = find_first_ubulk(archives, baked.file_suffix)
            .with_context(|| format!("could not find vanilla {}", baked.file_suffix))?;
        report.lines.push(format!(
            "{}: overlay {rel_path} ({} bytes)",
            baked.label,
            bytes.len()
        ));
        ai_overrides.push((archive_index, rel_path, bytes));
    }

    if dry_run {
        report.lines.push("Dry run: no overlay files written.".to_owned());
        return Ok(report);
    }
    if edited.is_empty() {
        report.lines.push(
            "Every scenario already contained the full catalogs and demo squads.".to_owned(),
        );
    }

    let mut overrides: Vec<(&IoStoreArchive, &str, &[u8])> = edited
        .iter()
        .map(|(archive, path, bytes)| (&archives[*archive], path.as_str(), bytes.as_slice()))
        .collect();
    for (archive_index, rel_path, bytes) in &ai_overrides {
        overrides.push((&archives[*archive_index], rel_path.as_str(), bytes.as_slice()));
    }
    blam_tags::iostore::writer::write_mod_container_ex(&overrides, &[], output)
        .with_context(|| format!("could not write {}", output.display()))?;
    report.lines.push(format!(
        "Wrote {} edited scenario(s) + {} superior AI character(s) to {}",
        edited.len(),
        ai_overrides.len(),
        output.display()
    ));
    Ok(report)
}

struct CharacterFill {
    added: usize,
    skipped_cap: usize,
}

fn ensure_character_palette(
    tag: &mut TagFile,
    catalog: &BTreeSet<String>,
) -> Result<CharacterFill> {
    let existing = read_palette_paths(tag, "character palette", "reference")?;
    let prioritized = prioritize_characters(catalog);
    let missing: Vec<&String> = prioritized
        .iter()
        .filter(|path| !existing.contains(path.as_str()))
        .collect();
    let room = MAX_CHARACTER_PALETTE_ENTRIES.saturating_sub(existing.len());
    let (take, skipped_cap) = if missing.len() > room {
        (&missing[..room], missing.len() - room)
    } else {
        (missing.as_slice(), 0)
    };
    let added = append_palette_entries(
        tag,
        "character palette",
        CHARACTER_GROUP,
        take,
        "reference",
    )?;
    Ok(CharacterFill { added, skipped_cap })
}

fn ensure_palette(
    tag: &mut TagFile,
    block_name: &str,
    group_tag: u32,
    catalog: &BTreeSet<String>,
    reference_field: &str,
    max_entries: usize,
) -> Result<usize> {
    let existing = read_palette_paths(tag, block_name, reference_field)?;
    let missing: Vec<&String> = catalog
        .iter()
        .filter(|path| !existing.contains(path.as_str()))
        .collect();
    if missing.is_empty() {
        return Ok(0);
    }
    if existing.len() + missing.len() > max_entries {
        bail!(
            "{block_name} would exceed {max_entries} entries (have {}, need {})",
            existing.len(),
            missing.len()
        );
    }
    append_palette_entries(tag, block_name, group_tag, &missing, reference_field)
}

fn append_palette_entries(
    tag: &mut TagFile,
    block_name: &str,
    group_tag: u32,
    paths: &[&String],
    reference_field: &str,
) -> Result<usize> {
    let mut added = 0usize;
    for path in paths {
        let index = {
            let mut root = tag.root_mut();
            let mut field = root
                .field_path_mut(block_name)
                .ok_or_else(|| anyhow!("{block_name} block was not found"))?;
            let mut block = field
                .as_block_mut()
                .ok_or_else(|| anyhow!("{block_name} is not a block"))?;
            block.add_element()
        };
        let reference_path = format!("{block_name}[{index}]/{reference_field}");
        let mut root = tag.root_mut();
        let mut field = root
            .field_path_mut(&reference_path)
            .ok_or_else(|| anyhow!("{reference_path} was not found after add_element"))?;
        field
            .set(TagFieldData::TagReference(TagReferenceData {
                group_tag_and_name: Some((group_tag, (*path).clone())),
            }))
            .map_err(|error| anyhow!("failed to set {reference_path}: {error:?}"))?;
        added += 1;
    }
    Ok(added)
}

fn read_palette_paths(
    tag: &TagFile,
    block_name: &str,
    reference_field: &str,
) -> Result<HashSet<String>> {
    let root = tag.root();
    let field = root
        .field_path(block_name)
        .ok_or_else(|| anyhow!("{block_name} block was not found"))?;
    let block = field
        .as_block()
        .ok_or_else(|| anyhow!("{block_name} is not a block"))?;
    let mut paths = HashSet::new();
    for index in 0..block.len() {
        let Some(element) = block.element(index) else {
            continue;
        };
        let Some(name_field) = element
            .field(reference_field)
            .or_else(|| element.field("name"))
            .or_else(|| element.field("reference"))
        else {
            continue;
        };
        let Some(TagFieldData::TagReference(reference)) = name_field.value() else {
            continue;
        };
        let Some((_, path)) = reference.group_tag_and_name else {
            continue;
        };
        paths.insert(normalize_tag_path(&path));
    }
    Ok(paths)
}

/// Preferred combat AI — filled first. Remaining slots up to the schema max
/// (64) are padded with secondary variants that are still spawnable.
const REPRESENTATIVE_CHARACTER_LEAVES: &[&str] = &[
    // UNSC
    "trooper",
    "trooper_female",
    "trooper_ragtag",
    "johnson",
    "keyes",
    // Elites — base ranks + distinct playstyles; one sacristan hero
    "elite",
    "elite_officer",
    "elite_ultra",
    "elite_general",
    "elite_specops",
    "elite_stealth",
    "elite_jetpack",
    "elite_sacristan_zealot",
    // Grunts
    "grunt",
    "grunt_major",
    "grunt_heavy",
    "grunt_ultra",
    "grunt_specops",
    // Jackals / skirmishers
    "jackal",
    "jackal_major",
    "jackal_sniper",
    "skirmisher",
    "skirmisher_major",
    "skirmisher_champion",
    // Brutes
    "brute",
    "brute_captain",
    "brute_chieftain_weapon",
    // Hunter
    "hunter",
    // Flood
    "flood_infection",
    "flood_carrier",
    "floodcombat_base",
    "floodcombat_elite",
    "floodcombat_elite_camo",
    "flood_combat_human",
    "flood_pureform",
    "flood_pureform_tank",
    // Support / Forerunner
    "engineer",
    "sentinel_aggressor",
    "sentinel_aggressor_major",
    "monitor",
];

pub fn list_ai_character_catalog(archives: &[IoStoreArchive]) -> Result<Vec<String>> {
    let all = collect_tag_paths(archives, "character")?;
    let mut paths: Vec<String> = all
        .into_iter()
        .filter(|path| is_candidate_ai_character(path))
        .collect();
    paths.sort_by(|left, right| {
        character_priority(right)
            .cmp(&character_priority(left))
            .then_with(|| left.cmp(right))
    });
    Ok(paths)
}

pub fn list_all_ai_character_paths(archives: &[IoStoreArchive]) -> Result<Vec<String>> {
    let all = collect_tag_paths(archives, "character")?;
    let mut paths: Vec<String> = all
        .into_iter()
        .filter(|path| {
            let lower = path.to_ascii_lowercase();
            lower.contains("\\ai\\")
                && !lower.contains("\\stimuli\\")
                && !lower.contains("\\null\\")
        })
        .collect();
    paths.sort();
    Ok(paths)
}

fn collect_ai_character_paths(archives: &[IoStoreArchive]) -> Result<BTreeSet<String>> {
    Ok(list_ai_character_catalog(archives)?.into_iter().collect())
}

fn prioritize_characters(catalog: &BTreeSet<String>) -> Vec<String> {
    let mut scored: Vec<(i32, String)> = catalog
        .iter()
        .cloned()
        .map(|path| (character_priority(&path), path))
        .collect();
    scored.sort_by(|left, right| right.0.cmp(&left.0).then_with(|| left.1.cmp(&right.1)));
    scored.into_iter().map(|(_, path)| path).collect()
}

fn is_candidate_ai_character(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.contains("\\ai\\")
        && !lower.contains("\\stimuli\\")
        && !lower.contains("\\null\\")
        && !is_hard_rejected_character(&lower)
}

fn is_hard_rejected_character(lower_path: &str) -> bool {
    // Never spend palette slots on these — drivers, crippled helpers, vehicle AI.
    const REJECT_SUBSTRINGS: &[&str] = &[
        "pilot",
        "flying",
        "crewman",
        "\\cryo",
        "thirsty",
        "low_perception",
        "no_melee",
        "no_kill",
        "unique_crazy",
        "eye_of_prophet",
        "\\vehicles\\",
        "monitor_engine",
        "johnson_orion",
    ];
    REJECT_SUBSTRINGS
        .iter()
        .any(|needle| lower_path.contains(needle))
}

fn is_representative_character(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    let leaf = tag_leaf_name(&lower);
    REPRESENTATIVE_CHARACTER_LEAVES
        .iter()
        .any(|name| *name == leaf)
}

fn tag_leaf_name(path: &str) -> &str {
    path.rsplit(['\\', '/']).next().unwrap_or(path)
}

fn character_priority(path: &str) -> i32 {
    let lower = path.to_ascii_lowercase();
    let leaf = tag_leaf_name(&lower);
    // Higher = filled earlier. Representatives first, then secondary padding.
    let base = match leaf {
        "trooper" | "elite" | "grunt" | "jackal" | "brute" | "hunter" => 100,
        "trooper_female" | "johnson" | "keyes" => 95,
        "elite_officer" | "elite_ultra" | "grunt_major" | "jackal_major" | "brute_captain" => 90,
        "elite_specops" | "elite_stealth" | "grunt_specops" | "jackal_sniper" => 85,
        "elite_general" | "elite_jetpack" | "grunt_heavy" | "grunt_ultra" => 80,
        "skirmisher" | "skirmisher_major" | "skirmisher_champion" => 75,
        "brute_chieftain_weapon" | "elite_sacristan_zealot" => 70,
        "floodcombat_elite" | "flood_combat_human" | "floodcombat_base" => 65,
        "flood_infection" | "flood_carrier" | "flood_pureform" | "flood_pureform_tank" => 60,
        "floodcombat_elite_camo" => 55,
        "sentinel_aggressor" | "sentinel_aggressor_major" | "engineer" | "monitor" => 50,
        "trooper_ragtag" => 45,
        // Secondary padding (still combat-capable; used to reach 64).
        "elite_sacristan_major" | "elite_sacristan_specops" | "elite_sacristan_stealth" => 35,
        "elite_sacristan_minor" | "grunt_sacristan_major" | "grunt_sacristan_specops" => 34,
        "grunt_sacristan_minor" | "jackal_sacristan_major" | "jackal_sacristan_sniper" => 33,
        "jackal_sacristan_minor" | "hunter_sacristan" | "brute_chieftain_armor" => 32,
        "skirmisher_commando" | "skirmisher_murmillone" => 31,
        "floodcombat_elite_shielded" => 30,
        "trooper_unique" | "trooper_female_unique" | "trooper_badlands" => 28,
        "trooper_ragtag_unique" | "trooper_female_ragtag" | "trooper_female_ragtag_unique" => 27,
        "keyes_marine" => 26,
        "trooper_c10" | "trooper_female_c10" | "floodcombat_elite_c10_vig" => 20,
        _ => {
            if is_representative_character(path) {
                40
            } else {
                15
            }
        }
    };
    base
}

fn find_first_ubulk(archives: &[IoStoreArchive], file_suffix: &str) -> Option<(usize, String)> {
    let suffix = file_suffix.replace('\\', "/").to_ascii_lowercase();
    for (index, archive) in archives.iter().enumerate() {
        for entry in archive.ublock_entries() {
            let normalized = entry.path.replace('\\', "/").to_ascii_lowercase();
            if normalized.ends_with(&suffix) {
                return Some((index, entry.path.clone()));
            }
        }
    }
    None
}

fn collect_tag_paths(archives: &[IoStoreArchive], group_name: &str) -> Result<BTreeSet<String>> {
    let suffix = format!("-{group_name}.ubulk");
    let mut paths = BTreeSet::new();
    for archive in archives {
        for entry in archive.ublock_entries() {
            let normalized = entry.path.replace('\\', "/").to_ascii_lowercase();
            let Some(tag_path) = tag_path_from_ubulk(&normalized, &suffix) else {
                continue;
            };
            paths.insert(tag_path);
        }
    }
    Ok(paths)
}

fn collect_scenario_entries(archives: &[IoStoreArchive]) -> Result<Vec<(usize, String, String)>> {
    let suffix = "-scenario.ubulk";
    let mut found = Vec::new();
    for (archive_index, archive) in archives.iter().enumerate() {
        for entry in archive.ublock_entries() {
            let normalized = entry.path.replace('\\', "/").to_ascii_lowercase();
            let Some(tag_path) = tag_path_from_ubulk(&normalized, suffix) else {
                continue;
            };
            // Prefer later containers (higher pakchunk / _P overlays) by
            // replacing earlier hits for the same scenario identity.
            if let Some(existing) = found
                .iter()
                .position(|(_, _, path): &(usize, String, String)| path == &tag_path)
            {
                found[existing] = (archive_index, entry.path.clone(), tag_path);
            } else {
                found.push((archive_index, entry.path.clone(), tag_path));
            }
        }
    }
    found.sort_by(|left, right| left.2.cmp(&right.2));
    Ok(found)
}

fn tag_path_from_ubulk(normalized_path: &str, suffix: &str) -> Option<String> {
    const PREFIX: &str = "meteorite/content/tags/";
    let rest = normalized_path.strip_prefix(PREFIX)?;
    let stem = rest.strip_suffix(suffix)?;
    if stem.is_empty() {
        return None;
    }
    Some(normalize_tag_path(stem))
}

fn normalize_tag_path(path: &str) -> String {
    path.trim_matches(['\\', '/', '.'])
        .replace('/', "\\")
        .to_ascii_lowercase()
}
