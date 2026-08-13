mod ensure_demo_squads;
mod expand_palettes;
mod tune_marine_ai;

use anyhow::{Context, Result, anyhow, bail};
use base64::Engine;
use blam_tags::fields::{StringIdData, TagFieldData, TagFieldType, TagReferenceData};
use blam_tags::iostore::IoStoreArchive;
use blam_tags::math;
use blam_tags::{TagFile, TagStructMut};
use serde::Deserialize;
use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ModDocument {
    format: String,
    version: u32,
    tags: Vec<ModTag>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ModTag {
    group: String,
    name: String,
    patches: Vec<Patch>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Patch {
    field: String,
    #[serde(rename = "type")]
    field_type: String,
    offset: usize,
    size: usize,
    #[serde(default)]
    blocks: Vec<BlockStep>,
    data: Option<String>,
    reference_group: Option<String>,
    reference_name: Option<String>,
    string_id_name: Option<String>,
    #[serde(default)]
    clear_reference: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct BlockStep {
    offset: usize,
    definition: String,
    element: usize,
    element_size: usize,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("ERROR: {error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut args = std::env::args_os().skip(1);
    let mut paks = None;
    let mut definitions = None;
    let mut mod_path = None;
    let mut output = None;
    let mut inspect = None;
    let mut expand_palettes = false;
    let mut ensure_demo_squads = false;
    let mut dump_demo_squads = false;
    let mut dump_filter = None;
    let mut list_ai_characters = false;
    let mut dry_run = false;
    while let Some(argument) = args.next() {
        match argument.to_string_lossy().as_ref() {
            "--paks" => paks = args.next().map(PathBuf::from),
            "--definitions" => definitions = args.next().map(PathBuf::from),
            "--mod" => mod_path = args.next().map(PathBuf::from),
            "--output" => output = args.next().map(PathBuf::from),
            "--inspect" => inspect = args.next().map(|value| value.to_string_lossy().into_owned()),
            "--expand-palettes" => expand_palettes = true,
            "--ensure-demo-squads" => ensure_demo_squads = true,
            "--dump-demo-squads" => dump_demo_squads = true,
            "--filter" => {
                dump_filter = args.next().map(|value| value.to_string_lossy().into_owned())
            }
            "--list-ai-characters" => list_ai_characters = true,
            "--dry-run" => dry_run = true,
            other => bail!("unknown argument '{other}'"),
        }
    }
    let paks = paks.context("--paks is required")?;
    let archives = open_archives(&paks)?;
    if dump_demo_squads {
        let lines = ensure_demo_squads::dump_dedicated_squad_usability(
            &archives,
            dump_filter.as_deref(),
        )?;
        for line in lines {
            println!("{line}");
        }
        return Ok(());
    }
    if list_ai_characters {
        let curated = expand_palettes::list_ai_character_catalog(&archives)?;
        let all = expand_palettes::list_all_ai_character_paths(&archives)?;
        println!("# Curated AI character catalog (diagnostic only)");
        for path in &curated {
            println!("{path}");
        }
        println!(
            "Curated AI catalog: {} (hard-rejected excluded) / {} AI character(s) in packs",
            curated.len(),
            all.len()
        );
        return Ok(());
    }
    if expand_palettes {
        let output = priority_output(output.context("--output is required with --expand-palettes")?);
        let report = expand_palettes::expand_all_mission_palettes(&archives, &output, dry_run)?;
        for line in &report.lines {
            println!("{line}");
        }
        println!(
            "Summary: {} / {} scenario(s) changed from catalogs of {} biped(s)/{} vehicle(s)/{} weapon(s)/{} safe AI character(s); +{} biped, +{} vehicle, +{} weapon, +{} character palette entries ({} skipped by 64-cap); +{} hm_ally ({} hostile-fallback), +{} hm_hostile",
            report.scenarios_changed,
            report.scenarios_seen,
            report.biped_catalog,
            report.vehicle_catalog,
            report.weapon_catalog,
            report.character_catalog,
            report.biped_added_total,
            report.vehicle_added_total,
            report.weapon_added_total,
            report.character_added_total,
            report.character_skipped_cap,
            report.ally_added,
            report.ally_from_hostile_fallback,
            report.hostile_added
        );
        return Ok(());
    }
    if ensure_demo_squads {
        let output =
            priority_output(output.context("--output is required with --ensure-demo-squads")?);
        let report =
            ensure_demo_squads::ensure_all_mission_demo_squads(&archives, &output, dry_run)?;
        for line in &report.lines {
            println!("{line}");
        }
        println!(
            "Summary: {} / {} scenario(s) changed; +{} hm_ally ({} from hostile fallback), +{} hm_hostile; {} skipped (no donor)",
            report.scenarios_changed,
            report.scenarios_seen,
            report.ally_added,
            report.ally_from_hostile_fallback,
            report.hostile_added,
            report.skipped_missing_donor
        );
        return Ok(());
    }
    let definitions = definitions.context("--definitions is required")?;
    let groups = load_groups(&definitions)?;
    if let Some(identity) = inspect {
        let (group, name) = identity
            .split_once(':')
            .context("--inspect expects fourCC:tag/path")?;
        let group_name = groups
            .get(group)
            .with_context(|| format!("no definition for tag group [{group}]"))?;
        let suffix = expected_suffix(name, group_name);
        let (archive, rel_path) = find_tag(&archives, &suffix)
            .with_context(|| format!("could not find [{group}] {name}"))?;
        let bytes = archives[archive].read(&rel_path)?;
        let tag = TagFile::read_from_bytes(&bytes)?;
        for field in tag.root().fields() {
            println!(
                "+0x{:X}\t{}\t{}\t{:?}",
                field.definition().offset(),
                field.type_name().replace(' ', "_"),
                field.clean_name(),
                field.value()
            );
        }
        return Ok(());
    }
    let mod_path = mod_path.context("--mod is required")?;
    let output = priority_output(output.context("--output is required")?);

    let document: ModDocument = serde_json::from_slice(
        &std::fs::read(&mod_path)
            .with_context(|| format!("could not read {}", mod_path.display()))?,
    )
    .context("could not parse the Halo Meister tag mod")?;
    if document.format != "halo-meister.runtime-tag-mod" || document.version != 1 {
        bail!(
            "unsupported tag mod format/version: {}/{}",
            document.format,
            document.version
        );
    }
    if document.tags.is_empty() {
        bail!("the tag mod contains no tags");
    }

    let mut edited = Vec::new();
    for mod_tag in &document.tags {
        let group_name = groups
            .get(&mod_tag.group)
            .with_context(|| format!("no definition for tag group [{}]", mod_tag.group))?;
        let suffix = expected_suffix(&mod_tag.name, group_name);
        let (archive_index, rel_path) = find_tag(&archives, &suffix).with_context(|| {
            format!(
                "could not find [{}] {} ({suffix}) in the mounted IoStore containers",
                mod_tag.group, mod_tag.name
            )
        })?;
        let bytes = archives[archive_index]
            .read(&rel_path)
            .with_context(|| format!("could not read {rel_path}"))?;
        let mut tag = TagFile::read_from_bytes(&bytes)
            .with_context(|| format!("could not parse {rel_path}"))?;
        for patch in &mod_tag.patches {
            apply_patch(tag.root_mut(), &patch.blocks, patch).with_context(|| {
                format!(
                    "could not apply [{}] {} / {}",
                    mod_tag.group, mod_tag.name, patch.field
                )
            })?;
        }
        let serialized = tag
            .write_to_bytes()
            .with_context(|| format!("could not serialize {rel_path}"))?;
        // Prove the bytes are a complete readable tag before packing them.
        TagFile::read_from_bytes(&serialized)
            .with_context(|| format!("serialized verification failed for {rel_path}"))?;
        edited.push((archive_index, rel_path, serialized));
    }

    let overrides: Vec<(&IoStoreArchive, &str, &[u8])> = edited
        .iter()
        .map(|(archive, path, bytes)| {
            (&archives[*archive], path.as_str(), bytes.as_slice())
        })
        .collect();
    blam_tags::iostore::writer::write_mod_container_ex(&overrides, &[], &output)
        .with_context(|| format!("could not write {}", output.display()))?;
    println!(
        "Exported {} tag(s) to {}",
        edited.len(),
        output.display()
    );
    Ok(())
}

fn load_groups(definitions: &Path) -> Result<HashMap<String, String>> {
    let mut groups = HashMap::new();
    for entry in std::fs::read_dir(definitions)
        .with_context(|| format!("could not read definitions at {}", definitions.display()))?
    {
        let path = entry?.path();
        if path.extension().and_then(|value| value.to_str()) != Some("json")
            || path.file_name().is_some_and(|name| name.to_string_lossy().starts_with('_'))
        {
            continue;
        }
        let value: serde_json::Value = match serde_json::from_slice(&std::fs::read(&path)?) {
            Ok(value) => value,
            Err(_) => continue,
        };
        let (Some(tag), Some(name)) = (
            value.get("tag").and_then(|item| item.as_str()),
            value.get("name").and_then(|item| item.as_str()),
        ) else {
            continue;
        };
        groups.insert(tag.to_owned(), name.to_owned());
    }
    Ok(groups)
}

fn open_archives(paks: &Path) -> Result<Vec<IoStoreArchive>> {
    let mut paths: Vec<PathBuf> = std::fs::read_dir(paks)
        .with_context(|| format!("could not read Paks directory {}", paks.display()))?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| {
            path.extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("utoc"))
                && !path
                    .file_name()
                    .is_some_and(|name| name.eq_ignore_ascii_case("global.utoc"))
        })
        .collect();
    paths.sort_by_key(|path| (chunk_number(path), path.clone()));
    let mut archives: Vec<Option<IoStoreArchive>> = paths
        .iter()
        .map(|path| IoStoreArchive::open(path).ok())
        .collect();
    for index in 0..archives.len() {
        let needs_recovery = archives[index]
            .as_ref()
            .is_some_and(|archive| archive.entries().is_empty());
        if !needs_recovery {
            continue;
        }
        let mut target = archives[index].take().unwrap();
        let references: Vec<&IoStoreArchive> =
            archives.iter().filter_map(|archive| archive.as_ref()).collect();
        target.recover_entries(&references, None);
        archives[index] = Some(target);
    }
    let mounted: Vec<IoStoreArchive> = archives.into_iter().flatten().collect();
    if mounted.is_empty() {
        bail!("no readable IoStore containers found in {}", paks.display());
    }
    Ok(mounted)
}

fn find_tag(archives: &[IoStoreArchive], suffix: &str) -> Option<(usize, String)> {
    let suffix = suffix.replace('\\', "/").to_ascii_lowercase();
    let mut found = None;
    for (index, archive) in archives.iter().enumerate() {
        for entry in archive.ublock_entries() {
            let normalized = entry.path.replace('\\', "/").to_ascii_lowercase();
            if normalized.ends_with(&suffix) {
                found = Some((index, entry.path.clone()));
            }
        }
    }
    found
}

fn expected_suffix(tag_path: &str, group_name: &str) -> String {
    format!(
        "meteorite/content/tags/{}-{}.ubulk",
        tag_path
            .trim_matches(['\\', '/'])
            .replace('\\', "/")
            .to_ascii_lowercase(),
        group_name.to_ascii_lowercase()
    )
}

fn apply_patch(
    mut structure: TagStructMut<'_>,
    blocks: &[BlockStep],
    patch: &Patch,
) -> Result<()> {
    if let Some((step, remaining)) = blocks.split_first() {
        if step.element_size == 0 || step.definition.is_empty() {
            bail!("invalid block traversal");
        }
        let ordinal = find_field_ordinal(
            &structure,
            step.offset,
            Some(TagFieldType::Block),
            None,
        )
        .with_context(|| format!("block at +0x{:X} was not found", step.offset))?;
        let mut field = structure.field_at_mut(ordinal).unwrap();
        let mut block = field
            .as_block_mut()
            .with_context(|| format!("field at +0x{:X} is not a block", step.offset))?;
        let element = block.element_mut(step.element).with_context(|| {
            format!(
                "block '{}' has no element {}",
                step.definition, step.element
            )
        })?;
        return apply_patch(element, remaining, patch);
    }

    let ordinal = find_field_ordinal(
        &structure,
        patch.offset,
        None,
        Some(&patch.field_type),
    )
        .with_context(|| format!("field at +0x{:X} was not found", patch.offset))?;
    let mut field = structure.field_at_mut(ordinal).unwrap();
    let actual = field.as_ref().type_name().replace(' ', "_");
    if !actual.eq_ignore_ascii_case(&patch.field_type) {
        bail!(
            "field type mismatch at +0x{:X}: mod says '{}', cooked tag says '{}'",
            patch.offset,
            patch.field_type,
            actual
        );
    }
    let value = patch_value(patch, field.as_ref().field_type())?;
    field
        .set(value)
        .map_err(|error| anyhow!("the cooked field rejected the value: {error:?}"))?;
    Ok(())
}

fn find_field_ordinal(
    structure: &TagStructMut<'_>,
    offset: usize,
    expected: Option<TagFieldType>,
    expected_name: Option<&str>,
) -> Option<usize> {
    structure
        .as_ref()
        .fields()
        .enumerate()
        .find(|(_, field)| {
            field.definition().offset() as usize == offset
                && expected.is_none_or(|kind| field.field_type() == kind)
                && expected_name.is_none_or(|name| {
                    field
                        .type_name()
                        .replace(' ', "_")
                        .eq_ignore_ascii_case(name)
                })
        })
        .map(|(ordinal, _)| ordinal)
}

fn patch_value(patch: &Patch, actual: TagFieldType) -> Result<TagFieldData> {
    if patch.clear_reference {
        if actual != TagFieldType::TagReference {
            bail!("a cleared reference targeted a non-reference field");
        }
        return Ok(TagFieldData::TagReference(TagReferenceData {
            group_tag_and_name: None,
        }));
    }
    if patch.reference_name.is_some() {
        if actual != TagFieldType::TagReference {
            bail!("a semantic tag reference targeted a non-reference field");
        }
        let group = patch
            .reference_group
            .as_deref()
            .context("referenceGroup is missing")?;
        let name = patch
            .reference_name
            .as_deref()
            .context("referenceName is missing")?;
        return Ok(TagFieldData::TagReference(TagReferenceData {
            group_tag_and_name: Some((fourcc(group)?, name.replace('\\', "/"))),
        }));
    }
    if patch.string_id_name.is_some() {
        if actual != TagFieldType::StringId {
            bail!("a semantic string-id targeted a non-string_id field");
        }
        return Ok(TagFieldData::StringId(StringIdData {
            string: patch.string_id_name.clone().unwrap_or_default(),
        }));
    }
    let data = base64::engine::general_purpose::STANDARD
        .decode(patch.data.as_deref().context("patch data is missing")?)
        .context("patch data is not valid base64")?;
    if data.len() != patch.size {
        bail!(
            "patch declares {} byte(s), but carries {}",
            patch.size,
            data.len()
        );
    }
    decode_value(&patch.field_type, &data)
}

fn decode_value(kind: &str, data: &[u8]) -> Result<TagFieldData> {
    let i16_at = |offset| i16::from_le_bytes(data[offset..offset + 2].try_into().unwrap());
    let u16_at = |offset| u16::from_le_bytes(data[offset..offset + 2].try_into().unwrap());
    let i32_at = |offset| i32::from_le_bytes(data[offset..offset + 4].try_into().unwrap());
    let u32_at = |offset| u32::from_le_bytes(data[offset..offset + 4].try_into().unwrap());
    let i64_at = |offset| i64::from_le_bytes(data[offset..offset + 8].try_into().unwrap());
    let f32_at = |offset| f32::from_le_bytes(data[offset..offset + 4].try_into().unwrap());
    let floats = || (0..data.len() / 4).map(|index| f32_at(index * 4)).collect::<Vec<_>>();
    Ok(match kind {
        "string" => TagFieldData::String(c_string(data)),
        "long_string" => TagFieldData::LongString(c_string(data)),
        "char_integer" => TagFieldData::CharInteger(data[0] as i8),
        "byte_integer" => TagFieldData::ByteInteger(data[0]),
        "char_block_index" => TagFieldData::CharBlockIndex(data[0] as i8),
        "char_enum" => TagFieldData::CharEnum { value: data[0] as i8, name: None },
        "byte_flags" => TagFieldData::ByteFlags { value: data[0], names: vec![] },
        "short_integer" => TagFieldData::ShortInteger(i16_at(0)),
        "word_integer" => TagFieldData::WordInteger(u16_at(0)),
        "short_block_index" => TagFieldData::ShortBlockIndex(i16_at(0)),
        "short_enum" => TagFieldData::ShortEnum { value: i16_at(0), name: None },
        "word_flags" => TagFieldData::WordFlags { value: u16_at(0), names: vec![] },
        "long_integer" => TagFieldData::LongInteger(i32_at(0)),
        "dword_integer" => TagFieldData::DwordInteger(u32_at(0)),
        "long_block_index" => TagFieldData::LongBlockIndex(i32_at(0)),
        "long_enum" => TagFieldData::LongEnum { value: i32_at(0), name: None },
        "long_flags" => TagFieldData::LongFlags { value: i32_at(0), names: vec![] },
        "long_block_flags" => TagFieldData::LongBlockFlags(i32_at(0)),
        "tag" => TagFieldData::Tag(u32_at(0)),
        "int64_integer" => TagFieldData::Int64Integer(i64_at(0)),
        "real" => TagFieldData::Real(f32_at(0)),
        "real_fraction" => TagFieldData::RealFraction(f32_at(0)),
        "angle" => TagFieldData::Angle(f32_at(0)),
        "real_bounds" => { let v=floats(); TagFieldData::RealBounds(math::Bounds { lower:v[0], upper:v[1] }) },
        "angle_bounds" => { let v=floats(); TagFieldData::AngleBounds(math::Bounds { lower:v[0], upper:v[1] }) },
        "fraction_bounds" => { let v=floats(); TagFieldData::FractionBounds(math::Bounds { lower:v[0], upper:v[1] }) },
        "real_point_2d" => { let v=floats(); TagFieldData::RealPoint2d(math::RealPoint2d { x:v[0], y:v[1] }) },
        "real_point_3d" => { let v=floats(); TagFieldData::RealPoint3d(math::RealPoint3d { x:v[0], y:v[1], z:v[2] }) },
        "real_vector_2d" => { let v=floats(); TagFieldData::RealVector2d(math::RealVector2d { i:v[0], j:v[1] }) },
        "real_vector_3d" => { let v=floats(); TagFieldData::RealVector3d(math::RealVector3d { i:v[0], j:v[1], k:v[2] }) },
        "real_euler_angles_2d" => { let v=floats(); TagFieldData::RealEulerAngles2d(math::RealEulerAngles2d { yaw:v[0], pitch:v[1] }) },
        "real_euler_angles_3d" => { let v=floats(); TagFieldData::RealEulerAngles3d(math::RealEulerAngles3d { yaw:v[0], pitch:v[1], roll:v[2] }) },
        "real_plane_2d" => { let v=floats(); TagFieldData::RealPlane2d(math::RealPlane2d { i:v[0], j:v[1], d:v[2] }) },
        "real_plane_3d" => { let v=floats(); TagFieldData::RealPlane3d(math::RealPlane3d { i:v[0], j:v[1], k:v[2], d:v[3] }) },
        "real_quaternion" => { let v=floats(); TagFieldData::RealQuaternion(math::RealQuaternion { i:v[0], j:v[1], k:v[2], w:v[3] }) },
        "real_rgb_color" => { let v=floats(); TagFieldData::RealRgbColor(math::RealRgbColor { red:v[0], green:v[1], blue:v[2] }) },
        "real_argb_color" => { let v=floats(); TagFieldData::RealArgbColor(math::RealArgbColor { alpha:v[0], red:v[1], green:v[2], blue:v[3] }) },
        "string_id" => bail!("native export of raw runtime string_id values is not safe; edit another field or use Baboon to choose the named string"),
        other => bail!("native export does not support field type '{other}'"),
    })
}

fn c_string(data: &[u8]) -> String {
    let end = data.iter().position(|byte| *byte == 0).unwrap_or(data.len());
    String::from_utf8_lossy(&data[..end]).into_owned()
}

fn fourcc(value: &str) -> Result<u32> {
    let bytes: [u8; 4] = value
        .as_bytes()
        .try_into()
        .map_err(|_| anyhow!("'{value}' is not a four-character tag group"))?;
    Ok(u32::from_be_bytes(bytes))
}

fn chunk_number(path: &Path) -> u32 {
    let stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("");
    let lowered = stem.to_ascii_lowercase();
    let Some(rest) = lowered.strip_prefix("pakchunk") else {
        // Non-pakchunk overlays share one priority band and then sort by path.
        // Name _P packs lexicographically later (e.g. ZZ_*) so they win over
        // earlier scenario-editing overlays such as MMYJ_FULL_VEHI_WAP_P.
        return u32::MAX - 1;
    };
    rest.chars()
        .take_while(|character| character.is_ascii_digit())
        .collect::<String>()
        .parse()
        .unwrap_or(u32::MAX - 1)
}

fn priority_output(path: PathBuf) -> PathBuf {
    let stem = path.file_stem().and_then(|value| value.to_str()).unwrap_or("mod");
    if stem.to_ascii_lowercase().ends_with("_p") {
        return path.with_extension("utoc");
    }
    path.with_file_name(format!("{stem}_P.utoc"))
}
