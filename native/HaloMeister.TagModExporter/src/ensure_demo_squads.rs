use anyhow::{Context, Result, anyhow, bail};
use blam_tags::fields::TagFieldData;
use blam_tags::iostore::IoStoreArchive;
use blam_tags::TagFile;
use std::path::Path;

const ALLY_NAME: &str = "hm_ally";
const HOSTILE_NAME: &str = "hm_hostile";
const ALLY_TEAM: i16 = 1; // player
const HOSTILE_TEAM: i16 = 3; // covenant

pub struct EnsureReport {
    pub scenarios_seen: usize,
    pub scenarios_changed: usize,
    pub ally_added: usize,
    pub ally_from_hostile_fallback: usize,
    pub hostile_added: usize,
    pub skipped_missing_donor: usize,
    pub lines: Vec<String>,
}

/// Diagnostic: dump dedicated squad spawn-point usability using the same
/// character-type / cell / palette checks runtime BuildPlan needs.
pub fn dump_dedicated_squad_usability(
    archives: &[IoStoreArchive],
    filter: Option<&str>,
) -> Result<Vec<String>> {
    let scenarios = collect_scenario_entries(archives)?;
    let mut lines = Vec::new();
    let mut failures = Vec::new();
    let mut checked = 0usize;
    let filter = filter.map(|value| value.replace('/', "\\").to_ascii_lowercase());
    for (archive_index, rel_path, tag_path) in &scenarios {
        if let Some(filter) = &filter {
            if !tag_path.contains(filter) {
                continue;
            }
        }
        let bytes = archives[*archive_index]
            .read(rel_path)
            .with_context(|| format!("could not read {rel_path}"))?;
        let tag = TagFile::read_from_bytes(&bytes)
            .with_context(|| format!("could not parse {rel_path}"))?;
        let (tag_lines, tag_failures) = dump_dedicated_for_tag(&tag, tag_path)?;
        lines.extend(tag_lines);
        failures.extend(tag_failures);
        checked += 1;
    }
    if checked == 0 {
        bail!(
            "no matching scenario tags found{}",
            filter
                .as_ref()
                .map(|value| format!(" for filter '{value}'"))
                .unwrap_or_default()
        );
    }
    lines.push(format!(
        "VALIDATE scenarios={checked} dedicated_failures={}",
        failures.len()
    ));
    for failure in &failures {
        lines.push(format!("FAIL {failure}"));
    }
    if !failures.is_empty() {
        bail!(
            "{} dedicated squad(s) have zero BuildPlan-usable spawn points",
            failures.len()
        );
    }
    Ok(lines)
}

fn dump_dedicated_for_tag(
    tag: &TagFile,
    tag_path: &str,
) -> Result<(Vec<String>, Vec<String>)> {
    let root = tag.root();
    let squads = root
        .field_path("squads")
        .or_else(|| root.field_path("squads!"))
        .and_then(|field| field.as_block())
        .ok_or_else(|| anyhow!("{tag_path}: squads block was not found"))?;
    let palette = root
        .field_path("character palette")
        .or_else(|| root.field_path("character palette!"))
        .and_then(|field| field.as_block());
    let palette_len = palette.as_ref().map(|block| block.len()).unwrap_or(0);
    let mut lines = vec![format!(
        "{tag_path}: character_palette_len={palette_len}"
    )];
    let mut failures = Vec::new();
    let mut saw_ally = false;
    let mut saw_hostile = false;
    for index in 0..squads.len() {
        let Some(element) = squads.element(index) else {
            continue;
        };
        let name = read_string_field(&element, "name").unwrap_or_default();
        if !name.eq_ignore_ascii_case(ALLY_NAME) && !name.eq_ignore_ascii_case(HOSTILE_NAME) {
            continue;
        }
        if name.eq_ignore_ascii_case(ALLY_NAME) {
            saw_ally = true;
        }
        if name.eq_ignore_ascii_case(HOSTILE_NAME) {
            saw_hostile = true;
        }
        let team = read_short_enum(&element, "team").unwrap_or(-1);
        let objective = read_short_block_index(&element, "initial objective").unwrap_or(-1);
        let spawn_points = element
            .field("spawn points!")
            .or_else(|| element.field("spawn points"))
            .and_then(|field| field.as_block());
        let spawn_count = spawn_points.as_ref().map(|block| block.len()).unwrap_or(0);
        lines.push(format!(
            "  squad[{index}] name={name} team={team} objective={objective} spawn_points={spawn_count}"
        ));
        let Some(spawn_points) = spawn_points else {
            lines.push("    NO_SPAWN_POINTS_BLOCK".to_owned());
            failures.push(format!("{tag_path} {name}: no spawn points block"));
            continue;
        };
        for nested_name in ["designer", "templated"] {
            if let Some(nested) = element.field(nested_name) {
                lines.push(format!(
                    "    {nested_name}_fields=[{}]",
                    nested_field_summary(&nested).join(", ")
                ));
            }
        }
        let mut usable = 0usize;
        for point_index in 0..spawn_points.len() {
            let Some(point) = spawn_points.element(point_index) else {
                continue;
            };
            let character_type = read_short_block_index(&point, "character type").unwrap_or(-1);
            let cell = read_short_block_index(&point, "cell")
                .or_else(|| read_short_block_index(&point, "cell!"))
                .unwrap_or(-1);
            let has_variant = point
                .field("actor variant name")
                .map(|field| matches!(field.value(), Some(TagFieldData::StringId(_))))
                .unwrap_or(false);
            let has_position = point.field("position").is_some()
                || point.field("position!").is_some();
            let mut resolved = character_type;
            let mut via = "direct";
            if resolved < 0 {
                via = "cell";
                resolved = resolve_cell_palette_index(&element, cell);
            }
            let palette_ok = resolved >= 0 && (resolved as usize) < palette_len;
            let ref_path = if palette_ok {
                palette
                    .as_ref()
                    .and_then(|block| block.element(resolved as usize))
                    .and_then(|entry| entry.field("reference").or_else(|| entry.field("name")))
                    .and_then(|field| match field.value() {
                        Some(TagFieldData::TagReference(reference)) => reference
                            .group_tag_and_name
                            .as_ref()
                            .map(|(_, path)| path.clone()),
                        _ => None,
                    })
                    .unwrap_or_else(|| "<no-ref>".to_owned())
            } else {
                "<unresolved>".to_owned()
            };
            let null_char = ref_path.to_ascii_lowercase().contains("\\null\\");
            let point_usable =
                has_position && has_variant && palette_ok && !null_char && resolved >= 0;
            if point_usable {
                usable += 1;
            } else {
                lines.push(format!(
                    "    point[{point_index}] UNUSABLE char={character_type} cell={cell} \
resolved={resolved} via={via} palette_ok={palette_ok} variant={has_variant} \
position={has_position} null={null_char} ref={ref_path}"
                ));
            }
        }
        lines.push(format!(
            "  => {name} usable_spawn_points={usable}/{spawn_count}"
        ));
        if usable == 0 {
            failures.push(format!(
                "{tag_path} {name}: usable_spawn_points=0/{spawn_count}"
            ));
        }
    }
    if !saw_ally {
        failures.push(format!("{tag_path}: missing {ALLY_NAME}"));
    }
    if !saw_hostile {
        failures.push(format!("{tag_path}: missing {HOSTILE_NAME}"));
    }
    Ok((lines, failures))
}

fn nested_field_summary(field: &blam_tags::TagField<'_>) -> Vec<String> {
    // Struct fields expose nested members through as_struct when available.
    if let Some(structure) = field.as_struct() {
        return structure
            .fields()
            .map(|nested| {
                if let Some(block) = nested.as_block() {
                    format!("{}:block({})", nested.clean_name(), block.len())
                } else {
                    format!("{}:{}", nested.clean_name(), nested.type_name())
                }
            })
            .collect();
    }
    if let Some(block) = field.as_block() {
        return vec![format!("block({})", block.len())];
    }
    vec![field.type_name().to_owned()]
}

fn resolve_cell_palette_index(squad: &blam_tags::TagStruct<'_>, cell_index: i16) -> i16 {
    if cell_index < 0 {
        return -1;
    }
    for cells in find_cell_blocks(squad) {
        if (cell_index as usize) >= cells.len() {
            continue;
        }
        let Some(cell) = cells.element(cell_index as usize) else {
            continue;
        };
        let choices = cell
            .field("character palette choices!")
            .or_else(|| cell.field("character palette choices"))
            .or_else(|| cell.field("character type"))
            .and_then(|field| field.as_block());
        if let Some(choices) = choices {
            for choice_index in 0..choices.len() {
                let Some(choice) = choices.element(choice_index) else {
                    continue;
                };
                let palette_index = read_short_block_index(&choice, "character type").unwrap_or(-1);
                if palette_index >= 0 {
                    return palette_index;
                }
            }
        } else if let Some(palette_index) = read_short_block_index(&cell, "character type") {
            if palette_index >= 0 {
                return palette_index;
            }
        }
    }
    -1
}

fn find_cell_blocks<'a>(
    squad: &'a blam_tags::TagStruct<'a>,
) -> Vec<blam_tags::TagBlock<'a>> {
    let mut blocks = Vec::new();
    for field in squad.fields() {
        let name = field.clean_name().to_ascii_lowercase();
        if (name.contains("cell") || name == "cells" || name == "cells!")
            && let Some(block) = field.as_block()
        {
            blocks.push(block);
        }
        if let Some(structure) = field.as_struct() {
            for nested in structure.fields() {
                let nested_name = nested.clean_name().to_ascii_lowercase();
                if (nested_name.contains("cell")
                    || nested_name == "cells"
                    || nested_name == "cells!")
                    && let Some(block) = nested.as_block()
                {
                    blocks.push(block);
                }
            }
        }
    }
    blocks
}

pub fn ensure_all_mission_demo_squads(
    archives: &[IoStoreArchive],
    output: &Path,
    dry_run: bool,
) -> Result<EnsureReport> {
    let scenarios = collect_scenario_entries(archives)?;
    if scenarios.is_empty() {
        bail!("no scenario tags found under Meteorite/Content/Tags");
    }

    let mut report = EnsureReport {
        scenarios_seen: scenarios.len(),
        scenarios_changed: 0,
        ally_added: 0,
        ally_from_hostile_fallback: 0,
        hostile_added: 0,
        skipped_missing_donor: 0,
        lines: Vec::new(),
    };
    report.lines.push(format!(
        "Ensuring {ALLY_NAME}/{HOSTILE_NAME} squads across {} scenario(s)",
        scenarios.len()
    ));

    let mut edited: Vec<(usize, String, Vec<u8>)> = Vec::new();
    for (archive_index, rel_path, tag_path) in &scenarios {
        let bytes = archives[*archive_index]
            .read(rel_path)
            .with_context(|| format!("could not read {rel_path}"))?;
        let mut tag = TagFile::read_from_bytes(&bytes)
            .with_context(|| format!("could not parse {rel_path}"))?;
        let delta = ensure_demo_squads_on_tag(&mut tag, tag_path)?;
        report.ally_added += delta.ally_added;
        report.ally_from_hostile_fallback += delta.ally_from_hostile_fallback;
        report.hostile_added += delta.hostile_added;
        report.skipped_missing_donor += delta.skipped_missing_donor;
        report.lines.extend(delta.lines);
        if !delta.changed {
            continue;
        }
        report.scenarios_changed += 1;
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

    if dry_run {
        report.lines.push("Dry run: no overlay files written.".to_owned());
        return Ok(report);
    }
    if edited.is_empty() {
        report
            .lines
            .push("Every scenario already had dedicated demo squads (or lacked donors).".to_owned());
        return Ok(report);
    }

    let overrides: Vec<(&IoStoreArchive, &str, &[u8])> = edited
        .iter()
        .map(|(archive, path, bytes)| (&archives[*archive], path.as_str(), bytes.as_slice()))
        .collect();
    blam_tags::iostore::writer::write_mod_container_ex(&overrides, &[], output)
        .with_context(|| format!("could not write {}", output.display()))?;
    report.lines.push(format!(
        "Wrote {} edited scenario(s) to {}",
        edited.len(),
        output.display()
    ));
    Ok(report)
}

#[derive(Default)]
pub struct SquadEnsureDelta {
    pub changed: bool,
    pub ally_added: usize,
    pub ally_from_hostile_fallback: usize,
    pub hostile_added: usize,
    pub skipped_missing_donor: usize,
    pub lines: Vec<String>,
}

/// Mutate one scenario tag in place. Used by the standalone squads tool and by
/// the merged Full Palettes + demo-squads overlay builder.
pub fn ensure_demo_squads_on_tag(tag: &mut TagFile, tag_path: &str) -> Result<SquadEnsureDelta> {
    let mut delta = SquadEnsureDelta::default();
    let inventory = inspect_squads(tag)?;

    if !inventory.has_ally_name {
        // Prefer a real player/human donor. All-hostile missions (Flood, etc.)
        // have none, so fall back to a combat-preferring hostile donor and
        // rewrite team to player while keeping the donor combat objective.
        if let Some(donor) = inventory.ally_donor {
            clone_squad(tag, donor.index, ALLY_NAME, ALLY_TEAM)?;
            delta.ally_added += 1;
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: +{ALLY_NAME} (ally donor squad {} idle={} objective kept)",
                donor.index, donor.idle
            ));
        } else if let Some(donor) = inventory.hostile_donor {
            clone_squad(tag, donor.index, ALLY_NAME, ALLY_TEAM)?;
            delta.ally_added += 1;
            delta.ally_from_hostile_fallback += 1;
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: +{ALLY_NAME} (hostile fallback donor squad {} idle={} -> team player, combat objective kept)",
                donor.index, donor.idle
            ));
        } else {
            delta.skipped_missing_donor += 1;
            delta.lines.push(format!(
                "{tag_path}: skipped {ALLY_NAME} (no squad with usable spawn points)"
            ));
        }
    }

    // Re-inspect after a possible ally insert so hostile donor indices stay valid.
    let inventory = inspect_squads(tag)?;
    if !inventory.has_hostile_name {
        match inventory.hostile_donor {
            Some(donor) => {
                clone_squad(tag, donor.index, HOSTILE_NAME, HOSTILE_TEAM)?;
                delta.hostile_added += 1;
                delta.changed = true;
                delta.lines.push(format!(
                    "{tag_path}: +{HOSTILE_NAME} (hostile donor squad {} idle={} objective kept)",
                    donor.index, donor.idle
                ));
            }
            None => {
                delta.skipped_missing_donor += 1;
                delta.lines.push(format!(
                    "{tag_path}: skipped {HOSTILE_NAME} (no hostile donor with spawn points)"
                ));
            }
        }
    }

    // Older overlays cleared objective to -1, which leaves actors standing with
    // no combat desire. Repair existing dedicated squads when possible.
    let inventory = inspect_squads(tag)?;
    if let Some(ally_index) = inventory.ally_index {
        if repair_dedicated_combat(tag, ally_index, inventory.combat_donor)? {
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: repaired {ALLY_NAME} combat objective from donor"
            ));
        }
    }
    if let Some(hostile_index) = inventory.hostile_index {
        if repair_dedicated_combat(tag, hostile_index, inventory.combat_donor)? {
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: repaired {HOSTILE_NAME} combat objective from donor"
            ));
        }
    }

    let inventory = inspect_squads(tag)?;
    if let Some(ally_index) = inventory.ally_index {
        let squads_path = squads_path(tag)?;
        if clear_squad_fireteam_absorber(tag, &squads_path, ally_index)? {
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: cleared {ALLY_NAME} fireteam absorber (follow stays on this squad only)"
            ));
        }
    }

    // Existing dedicated squads may have been cloned from donors whose spawn
    // points only had positions (character type=-1, cell=-1, empty designer
    // cells). Runtime BuildPlan skips those, then falls back to hostile
    // scaffolds — so friendlies spawn as enemies on maps like d40. Stamp a
    // resolved palette index onto every unusable point.
    let inventory = inspect_squads(tag)?;
    let palette_donor = inventory
        .ally_donor
        .or(inventory.hostile_donor)
        .or(inventory.combat_donor);
    if let Some(ally_index) = inventory.ally_index {
        if let Some((palette_index, repaired_points)) =
            repair_dedicated_spawn_bindings(tag, ally_index, palette_donor)?
        {
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: repaired {ALLY_NAME} spawn bindings -> character type {palette_index} ({repaired_points} point(s))"
            ));
        }
    }
    if let Some(hostile_index) = inventory.hostile_index {
        if let Some((palette_index, repaired_points)) =
            repair_dedicated_spawn_bindings(tag, hostile_index, palette_donor)?
        {
            delta.changed = true;
            delta.lines.push(format!(
                "{tag_path}: repaired {HOSTILE_NAME} spawn bindings -> character type {palette_index} ({repaired_points} point(s))"
            ));
        }
    }

    Ok(delta)
}

#[derive(Clone, Copy)]
struct DonorChoice {
    index: usize,
    idle: bool,
}

struct SquadInventory {
    has_ally_name: bool,
    has_hostile_name: bool,
    ally_index: Option<usize>,
    hostile_index: Option<usize>,
    ally_donor: Option<DonorChoice>,
    hostile_donor: Option<DonorChoice>,
    /// Any combat (non-idle) donor — used to repair dedicated squads.
    combat_donor: Option<DonorChoice>,
}

fn inspect_squads(tag: &TagFile) -> Result<SquadInventory> {
    let root = tag.root();
    let field = root
        .field_path("squads")
        .or_else(|| root.field_path("squads!"))
        .ok_or_else(|| anyhow!("squads block was not found"))?;
    let block = field
        .as_block()
        .ok_or_else(|| anyhow!("squads is not a block"))?;

    let mut inventory = SquadInventory {
        has_ally_name: false,
        has_hostile_name: false,
        ally_index: None,
        hostile_index: None,
        ally_donor: None,
        hostile_donor: None,
        combat_donor: None,
    };

    for index in 0..block.len() {
        let Some(element) = block.element(index) else {
            continue;
        };
        let name = read_string_field(&element, "name").unwrap_or_default();
        // Never clone our own dedicated squads as donors.
        if name.eq_ignore_ascii_case(ALLY_NAME) {
            inventory.has_ally_name = true;
            inventory.ally_index = Some(index);
            continue;
        }
        if name.eq_ignore_ascii_case(HOSTILE_NAME) {
            inventory.has_hostile_name = true;
            inventory.hostile_index = Some(index);
            continue;
        }

        let team = read_short_enum(&element, "team").unwrap_or(-1);
        // Match runtime BuildPlan: positions alone are not enough. Need a
        // direct character-palette index or a cell that resolves to one.
        let Some(usable_palette) = first_usable_palette_index(&element) else {
            continue;
        };
        let _ = usable_palette;

        let objective = read_short_block_index(&element, "initial objective").unwrap_or(-1);
        let idle = objective < 0;
        let choice = DonorChoice { index, idle };
        if !idle {
            choose_donor(&mut inventory.combat_donor, choice);
        }
        let is_ally = team == 1 || team == 2;
        if is_ally {
            choose_donor(&mut inventory.ally_donor, choice);
        } else {
            choose_donor(&mut inventory.hostile_donor, choice);
        }
    }

    Ok(inventory)
}

fn first_usable_palette_index(squad: &blam_tags::TagStruct<'_>) -> Option<i16> {
    let spawn_points = squad
        .field("spawn points!")
        .or_else(|| squad.field("spawn points"))
        .and_then(|field| field.as_block())?;
    for point_index in 0..spawn_points.len() {
        let point = spawn_points.element(point_index)?;
        let character_type = read_short_block_index(&point, "character type").unwrap_or(-1);
        if character_type >= 0 {
            return Some(character_type);
        }
        let cell = read_short_block_index(&point, "cell").unwrap_or(-1);
        let resolved = resolve_cell_palette_index(squad, cell);
        if resolved >= 0 {
            return Some(resolved);
        }
    }
    None
}

/// Stamp a usable palette index onto every dedicated spawn point that BuildPlan
/// cannot resolve (character type &lt; 0 and cell unusable). Returns the palette
/// index written, or None when nothing needed repair.
fn repair_dedicated_spawn_bindings(
    tag: &mut TagFile,
    dedicated_index: usize,
    palette_donor: Option<DonorChoice>,
) -> Result<Option<(i16, usize)>> {
    let squads_path = squads_path(tag)?;
    let (spawn_path, unusable_points, fallback_palette) = {
        let root = tag.root();
        let field = root
            .field_path(&squads_path)
            .ok_or_else(|| anyhow!("squads block was not found"))?;
        let block = field
            .as_block()
            .ok_or_else(|| anyhow!("squads is not a block"))?;
        let element = block
            .element(dedicated_index)
            .ok_or_else(|| anyhow!("dedicated squad {dedicated_index} missing"))?;
        let spawn_path = if element.field("spawn points!").is_some() {
            "spawn points!"
        } else {
            "spawn points"
        };
        let Some(spawn_points) = element
            .field(spawn_path)
            .and_then(|field| field.as_block())
        else {
            return Ok(None);
        };
        let mut unusable = Vec::new();
        let mut local_palette = None;
        for point_index in 0..spawn_points.len() {
            let Some(point) = spawn_points.element(point_index) else {
                continue;
            };
            let character_type = read_short_block_index(&point, "character type").unwrap_or(-1);
            if character_type >= 0 {
                local_palette.get_or_insert(character_type);
                continue;
            }
            let cell = read_short_block_index(&point, "cell").unwrap_or(-1);
            let resolved = resolve_cell_palette_index(&element, cell);
            if resolved >= 0 {
                local_palette.get_or_insert(resolved);
                continue;
            }
            unusable.push(point_index);
        }
        if unusable.is_empty() {
            return Ok(None);
        }
        let from_donor = palette_donor.and_then(|donor| {
            block
                .element(donor.index)
                .and_then(|donor_element| first_usable_palette_index(&donor_element))
        });
        let palette = local_palette.or(from_donor).unwrap_or(0);
        (spawn_path.to_owned(), unusable, palette)
    };

    let repaired_points = unusable_points.len();
    for point_index in unusable_points {
        set_field(
            tag,
            &format!("{squads_path}[{dedicated_index}]/{spawn_path}[{point_index}]/character type"),
            TagFieldData::ShortBlockIndex(fallback_palette),
        )?;
    }
    Ok(Some((fallback_palette, repaired_points)))
}

fn clone_squad(tag: &mut TagFile, donor: usize, name: &str, team: i16) -> Result<()> {
    let squads_path = squads_path(tag)?;
    let new_index = {
        let mut root = tag.root_mut();
        let mut field = root
            .field_path_mut(&squads_path)
            .ok_or_else(|| anyhow!("squads block was not found"))?;
        let mut block = field
            .as_block_mut()
            .ok_or_else(|| anyhow!("squads is not a block"))?;
        block
            .duplicate_element(donor)
            .map_err(|error| anyhow!("could not duplicate squad {donor}: {error:?}"))?
    };

    // Keep the donor's initial objective/task so actor_new inherits combat
    // desire. Only rewrite identity (name + team) and drop fireteam absorber
    // so cloning a marine donor cannot vacuum nearby story squads.
    set_squad_identity(tag, &squads_path, new_index, name, team)?;
    if name.eq_ignore_ascii_case(ALLY_NAME) {
        let _ = clear_squad_fireteam_absorber(tag, &squads_path, new_index)?;
    }
    Ok(())
}

fn repair_dedicated_combat(
    tag: &mut TagFile,
    dedicated_index: usize,
    combat_donor: Option<DonorChoice>,
) -> Result<bool> {
    let squads_path = squads_path(tag)?;
    let dedicated_objective = {
        let root = tag.root();
        let field = root
            .field_path(&squads_path)
            .ok_or_else(|| anyhow!("squads block was not found"))?;
        let block = field
            .as_block()
            .ok_or_else(|| anyhow!("squads is not a block"))?;
        let element = block
            .element(dedicated_index)
            .ok_or_else(|| anyhow!("dedicated squad {dedicated_index} missing"))?;
        read_short_block_index(&element, "initial objective").unwrap_or(-1)
    };
    if dedicated_objective >= 0 {
        return Ok(false);
    }
    let Some(donor) = combat_donor else {
        return Ok(false);
    };

    let (objective, task) = {
        let root = tag.root();
        let field = root
            .field_path(&squads_path)
            .ok_or_else(|| anyhow!("squads block was not found"))?;
        let block = field
            .as_block()
            .ok_or_else(|| anyhow!("squads is not a block"))?;
        let element = block
            .element(donor.index)
            .ok_or_else(|| anyhow!("combat donor squad {} missing", donor.index))?;
        (
            read_short_block_index(&element, "initial objective").unwrap_or(-1),
            read_short_block_index(&element, "initial task").unwrap_or(-1),
        )
    };
    if objective < 0 {
        return Ok(false);
    }

    let path_prefix = format!("{squads_path}[{dedicated_index}]");
    set_field(
        tag,
        &format!("{path_prefix}/initial objective"),
        TagFieldData::ShortBlockIndex(objective),
    )?;
    let _ = set_field(
        tag,
        &format!("{path_prefix}/initial task"),
        TagFieldData::CustomShortBlockIndex(task),
    );
    Ok(true)
}

fn squads_path(tag: &TagFile) -> Result<String> {
    let root = tag.root();
    if root.field_path("squads").is_some() {
        Ok("squads".to_owned())
    } else if root.field_path("squads!").is_some() {
        Ok("squads!".to_owned())
    } else {
        bail!("squads block was not found")
    }
}

fn set_squad_identity(
    tag: &mut TagFile,
    squads_path: &str,
    index: usize,
    name: &str,
    team: i16,
) -> Result<()> {
    let path_prefix = format!("{squads_path}[{index}]");
    set_field(
        tag,
        &format!("{path_prefix}/name"),
        TagFieldData::String(name.to_owned()),
    )?;
    set_field(
        tag,
        &format!("{path_prefix}/team"),
        TagFieldData::ShortEnum {
            value: team,
            name: None,
        },
    )?;
    Ok(())
}

const SQUAD_FLAG_FIRETEAM_ABSORBER: i32 = 1 << 5;

fn clear_squad_fireteam_absorber(
    tag: &mut TagFile,
    squads_path: &str,
    index: usize,
) -> Result<bool> {
    let current = {
        let root = tag.root();
        let Some(field) = root.field_path(squads_path) else {
            return Ok(false);
        };
        let Some(block) = field.as_block() else {
            return Ok(false);
        };
        let Some(element) = block.element(index) else {
            return Ok(false);
        };
        match element.field("flags").and_then(|flags| flags.value()) {
            Some(TagFieldData::LongFlags { value, .. }) => value,
            Some(TagFieldData::LongInteger(value)) => value,
            _ => return Ok(false),
        }
    };
    if current & SQUAD_FLAG_FIRETEAM_ABSORBER == 0 {
        return Ok(false);
    }
    set_field(
        tag,
        &format!("{squads_path}[{index}]/flags"),
        TagFieldData::LongFlags {
            value: current & !SQUAD_FLAG_FIRETEAM_ABSORBER,
            names: vec![],
        },
    )?;
    Ok(true)
}

fn set_field(tag: &mut TagFile, path: &str, value: TagFieldData) -> Result<()> {
    let mut root = tag.root_mut();
    let mut field = root
        .field_path_mut(path)
        .ok_or_else(|| anyhow!("{path} was not found"))?;
    field
        .set(value)
        .map_err(|error| anyhow!("failed to set {path}: {error:?}"))?;
    Ok(())
}

fn read_string_field(element: &blam_tags::TagStruct<'_>, name: &str) -> Option<String> {
    let field = element.field(name)?;
    match field.value() {
        Some(TagFieldData::String(value)) | Some(TagFieldData::LongString(value)) => {
            Some(value.trim_matches(['\0', ' ']).to_owned())
        }
        _ => None,
    }
}

fn read_short_enum(element: &blam_tags::TagStruct<'_>, name: &str) -> Option<i16> {
    let field = element.field(name)?;
    match field.value() {
        Some(TagFieldData::ShortEnum { value, .. }) => Some(value),
        Some(TagFieldData::ShortInteger(value)) => Some(value),
        _ => None,
    }
}

fn read_short_block_index(element: &blam_tags::TagStruct<'_>, name: &str) -> Option<i16> {
    let field = element.field(name)?;
    match field.value() {
        Some(TagFieldData::ShortBlockIndex(value))
        | Some(TagFieldData::CustomShortBlockIndex(value)) => Some(value),
        Some(TagFieldData::ShortInteger(value)) => Some(value),
        _ => None,
    }
}

/// Prefer the first combat (non-idle) donor; otherwise keep the first usable donor.
fn choose_donor(slot: &mut Option<DonorChoice>, candidate: DonorChoice) {
    match *slot {
        None => *slot = Some(candidate),
        Some(existing) if existing.idle && !candidate.idle => {
            *slot = Some(candidate);
        }
        Some(_) => {}
    }
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
