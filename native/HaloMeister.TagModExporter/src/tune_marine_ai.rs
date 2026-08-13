use anyhow::{Context, Result, anyhow};
use blam_tags::fields::{TagFieldData, TagReferenceData};
use blam_tags::math::Bounds;
use blam_tags::TagFile;

const DEG: f32 = std::f32::consts::PI / 180.0;
const WEAP: u32 = u32::from_be_bytes(*b"weap");

struct GunPattern {
    weapon: &'static str,
    rate_of_fire: f32,
    tracking: f32,
    leading: f32,
    burst_duration: (f32, f32),
    burst_separation: (f32, f32),
    damage: f32,
    error_deg: f32,
    max_error_deg: f32,
}

/// Character-level firing patterns for the guns troopers actually spawn with.
/// Zero damage-modifier means "use weapon default"; values above 1 multiply.
const TROOPER_GUNS: &[GunPattern] = &[
    GunPattern {
        weapon: r"objects\weapons\rifle\assault_rifle\assault_rifle",
        rate_of_fire: 0.0,
        tracking: 1.0,
        leading: 0.9,
        burst_duration: (1.1, 1.7),
        burst_separation: (0.12, 0.28),
        damage: 1.85,
        error_deg: 0.35,
        max_error_deg: 1.2,
    },
    GunPattern {
        weapon: r"objects\weapons\rifle\sniper_rifle\sniper_rifle",
        rate_of_fire: 1.1,
        tracking: 1.0,
        leading: 0.95,
        burst_duration: (0.08, 0.16),
        burst_separation: (0.35, 0.55),
        damage: 2.3,
        error_deg: 0.12,
        max_error_deg: 0.4,
    },
    GunPattern {
        weapon: r"objects\weapons\rifle\assault_rifle\smg",
        rate_of_fire: 0.0,
        tracking: 1.0,
        leading: 0.85,
        burst_duration: (0.9, 1.4),
        burst_separation: (0.1, 0.22),
        damage: 1.7,
        error_deg: 0.45,
        max_error_deg: 1.4,
    },
];

/// Retune Superior Marines trooper AI: press, shoot sooner, hide less, melee
/// when close. Angle fields that were authored as degrees are stored as radians.
pub fn apply_aggressive_trooper(bytes: &[u8]) -> Result<(Vec<u8>, Vec<String>)> {
    let mut tag = TagFile::read_from_bytes(bytes)
        .context("could not parse bundled trooper.character")?;
    let mut lines = Vec::new();

    // Drop "fight stable"; keep moving and allow closing past ideal range.
    set_flags(&mut tag, "engage properties[0]/flags", 2 | 128)?;
    set_bounds(&mut tag, "engage properties[0]/Reposition bounds", 2.5, 4.5)?;
    set_real(&mut tag, "engage properties[0]/fight flank chance", 0.9)?;
    set_bounds(
        &mut tag,
        "engage properties[0]/default combat range",
        3.0,
        22.0,
    )?;
    set_bounds(
        &mut tag,
        "engage properties[0]/default firing range",
        2.0,
        28.0,
    )?;
    lines.push("engage: press + flank, combat range 3-22".to_owned());

    // consider range 0 meant they never wanted melee.
    set_real(&mut tag, "charge properties[0]/melee consider range", 4.0)?;
    set_real(&mut tag, "charge properties[0]/melee chance", 0.45)?;
    set_real(&mut tag, "charge properties[0]/melee attack range", 1.1)?;
    set_real(&mut tag, "charge properties[0]/melee abort range", 4.5)?;
    set_real(&mut tag, "charge properties[0]/melee attack delay timer", 2.0)?;
    set_real(&mut tag, "charge properties[0]/proximity berserk range", 3.5)?;
    set_fraction(
        &mut tag,
        "charge properties[0]/proximity berserk chance",
        0.4,
    )?;
    lines.push("charge: melee consider 4wu, chance 0.45, proximity berserk".to_owned());

    set_real(&mut tag, "evasion properties[0]/dive retreat chance", 0.0)?;
    set_real(&mut tag, "evasion properties[0]/Evasion chance", 0.45)?;
    lines.push("evasion: no dive-retreat".to_owned());

    set_bounds(
        &mut tag,
        "cover properties[0]/hide behind cover time",
        1.2,
        3.0,
    )?;
    set_real(&mut tag, "cover properties[0]/Cover vitality threshold", 0.25)?;
    set_real(&mut tag, "cover properties[0]/Cover danger threshold", 2.0)?;
    set_real(
        &mut tag,
        "cover properties[0]/minimum defensive distance from target",
        14.0,
    )?;
    set_real(&mut tag, "cover properties[0]/Cover check delay", 8.0)?;
    lines.push("cover: hide 1.2-3s, only when badly hurt".to_owned());

    set_real(&mut tag, "retreat properties[0]/Proximity threshold", 0.0)?;
    set_real(&mut tag, "retreat properties[0]/leader dead retreat chance", 0.0)?;
    set_real(&mut tag, "retreat properties[0]/peer dead retreat chance", 0.0)?;
    set_real(
        &mut tag,
        "retreat properties[0]/second peer dead retreat chance",
        0.0,
    )?;
    set_bounds(
        &mut tag,
        r"retreat properties[0]/min\max cower timeout bounds",
        0.2,
        0.8,
    )?;
    set_angle(&mut tag, "retreat properties[0]/zig-zag angle", 25.0 * DEG)?;
    lines.push("retreat: disabled peer/leader flee".to_owned());

    set_bounds(&mut tag, "search properties[0]/search time", 2.0, 6.0)?;
    set_bounds(
        &mut tag,
        "pre-search properties[0]/max presearch time",
        4.0,
        6.0,
    )?;
    set_real(
        &mut tag,
        "pre-search properties[0]/uncover weight",
        4.0,
    )?;
    set_real(
        &mut tag,
        "pre-search properties[0]/destroy cover weight",
        2.0,
    )?;

    // AR / sniper / SMG: open fire immediately and stay at the accurate end.
    for index in 0..3 {
        set_bounds(
            &mut tag,
            &format!("weapons properties[{index}]/first burst delay time"),
            0.02,
            0.08,
        )?;
        set_bounds(
            &mut tag,
            &format!("weapons properties[{index}]/normal accuracy bounds"),
            0.95,
            1.0,
        )?;
        set_real(
            &mut tag,
            &format!("weapons properties[{index}]/normal accuracy time"),
            0.05,
        )?;
        set_bounds(
            &mut tag,
            &format!("weapons properties[{index}]/heroic accuracy bounds"),
            0.97,
            1.0,
        )?;
        set_real(
            &mut tag,
            &format!("weapons properties[{index}]/heroic accuracy time"),
            0.04,
        )?;
        set_bounds(
            &mut tag,
            &format!("weapons properties[{index}]/legendary accuracy bounds"),
            1.0,
            1.0,
        )?;
        set_real(
            &mut tag,
            &format!("weapons properties[{index}]/legendary accuracy time"),
            0.03,
        )?;
    }
    set_bounds(
        &mut tag,
        "weapons properties[0]/normal combat range",
        2.0,
        18.0,
    )?;
    set_bounds(
        &mut tag,
        "weapons properties[2]/normal combat range",
        1.0,
        14.0,
    )?;
    lines.push("weapons: instant first burst, accuracy pinned high".to_owned());

    apply_firing_pattern(
        &mut tag,
        0,
        0.0,
        0.9,
        0.7,
        (0.6, 0.9),
        (0.8, 1.4),
        1.35,
        1.5,
        4.0,
    )?;
    for gun in TROOPER_GUNS {
        let index = duplicate_firing_pattern(&mut tag, 0)?;
        set_weapon(&mut tag, index, gun.weapon)?;
        apply_firing_pattern(
            &mut tag,
            index,
            gun.rate_of_fire,
            gun.tracking,
            gun.leading,
            gun.burst_duration,
            gun.burst_separation,
            gun.damage,
            gun.error_deg,
            gun.max_error_deg,
        )?;
    }
    lines.push(
        "firing: AR/SMG/sniper laser + 1.7-2.3x damage; flak aim/damage fixed".to_owned(),
    );

    set_flags(&mut tag, "grenades properties[0]/grenades flags", 2)?;
    set_short(&mut tag, "grenades properties[0]/minimum enemy count", 1)?;
    set_fraction(&mut tag, "grenades properties[0]/grenade chance", 0.5)?;
    set_real(&mut tag, "grenades properties[0]/grenade throw delay", 4.0)?;
    lines.push("grenades: chance 0.5, delay 4s, throw at one enemy".to_owned());

    let serialized = tag
        .write_to_bytes()
        .context("could not serialize aggressive trooper.character")?;
    TagFile::read_from_bytes(&serialized)
        .context("aggressive trooper.character failed round-trip parse")?;
    Ok((serialized, lines))
}

fn set_real(tag: &mut TagFile, path: &str, value: f32) -> Result<()> {
    set_field(tag, path, TagFieldData::Real(value))
}

fn set_fraction(tag: &mut TagFile, path: &str, value: f32) -> Result<()> {
    set_field(tag, path, TagFieldData::RealFraction(value)).or_else(|_| {
        set_field(tag, path, TagFieldData::Real(value))
    })
}

fn set_angle(tag: &mut TagFile, path: &str, radians: f32) -> Result<()> {
    set_field(tag, path, TagFieldData::Angle(radians))
}

fn set_bounds(tag: &mut TagFile, path: &str, lower: f32, upper: f32) -> Result<()> {
    set_field(
        tag,
        path,
        TagFieldData::RealBounds(Bounds { lower, upper }),
    )
}

fn set_flags(tag: &mut TagFile, path: &str, value: i32) -> Result<()> {
    set_field(
        tag,
        path,
        TagFieldData::LongFlags {
            value,
            names: vec![],
        },
    )
}

fn set_short(tag: &mut TagFile, path: &str, value: i16) -> Result<()> {
    set_field(tag, path, TagFieldData::ShortInteger(value))
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

fn duplicate_firing_pattern(tag: &mut TagFile, donor: usize) -> Result<usize> {
    let mut root = tag.root_mut();
    let mut field = root
        .field_path_mut("firing pattern properties")
        .ok_or_else(|| anyhow!("firing pattern properties was not found"))?;
    let mut block = field
        .as_block_mut()
        .ok_or_else(|| anyhow!("firing pattern properties is not a block"))?;
    block
        .duplicate_element(donor)
        .map_err(|error| anyhow!("could not duplicate firing pattern {donor}: {error:?}"))
}

fn set_weapon(tag: &mut TagFile, index: usize, path: &str) -> Result<()> {
    set_field(
        tag,
        &format!("firing pattern properties[{index}]/weapon"),
        TagFieldData::TagReference(TagReferenceData {
            group_tag_and_name: Some((WEAP, path.to_owned())),
        }),
    )
}

fn apply_firing_pattern(
    tag: &mut TagFile,
    index: usize,
    rate_of_fire: f32,
    tracking: f32,
    leading: f32,
    burst_duration: (f32, f32),
    burst_separation: (f32, f32),
    damage: f32,
    error_deg: f32,
    max_error_deg: f32,
) -> Result<()> {
    let prefix = format!("firing pattern properties[{index}]/firing patterns[0]");
    set_real(tag, &format!("{prefix}/rate of fire"), rate_of_fire)?;
    set_real(tag, &format!("{prefix}/target tracking"), tracking)?;
    set_real(tag, &format!("{prefix}/target leading"), leading)?;
    set_real(tag, &format!("{prefix}/burst origin radius"), 0.0)?;
    set_angle(tag, &format!("{prefix}/burst origin angle"), 0.25 * DEG)?;
    set_bounds(tag, &format!("{prefix}/burst return length"), 0.0, 0.0)?;
    set_angle(tag, &format!("{prefix}/burst return angle"), 0.0)?;
    set_bounds(
        tag,
        &format!("{prefix}/burst duration"),
        burst_duration.0,
        burst_duration.1,
    )?;
    set_bounds(
        tag,
        &format!("{prefix}/burst separation"),
        burst_separation.0,
        burst_separation.1,
    )?;
    set_real(tag, &format!("{prefix}/weapon damage modifier"), damage)?;
    set_angle(tag, &format!("{prefix}/projectile error"), error_deg * DEG)?;
    set_angle(tag, &format!("{prefix}/burst angular velocity"), 0.0)?;
    set_angle(
        tag,
        &format!("{prefix}/maximum error angle"),
        max_error_deg * DEG,
    )?;
    Ok(())
}
