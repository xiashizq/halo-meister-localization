using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record ProjectileSwapWeapon(
    string Name,
    RuntimeTagEntry Tag,
    IReadOnlyList<RuntimeTagFieldValue> ProjectileFields,
    IReadOnlyList<RuntimeTagEntry> CurrentProjectiles)
{
    public string ImageUri => ProjectileSwapperService.WeaponIconUri(Tag.Name);

    public string CurrentProjectileText => CurrentProjectiles.Count switch
    {
        0 => "No current projectile",
        1 => $"Current: {ProjectileSwapperService.FriendlyName(CurrentProjectiles[0])}",
        _ => "Current: " + string.Join(", ", CurrentProjectiles.Select(ProjectileSwapperService.FriendlyName)),
    };
}

public sealed record ProjectileSwapperSession(
    IReadOnlyList<ProjectileSwapWeapon> Weapons,
    IReadOnlyList<RuntimeTagEntry> Projectiles);

public sealed class ProjectileSwapperService : IDisposable
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private IReadOnlyList<RuntimeTagEntry> _tags = [];

    public int ProcessId => _memory.ProcessId;

    public ProjectileSwapperSession Connect()
    {
        EnsureDefinitions();
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to the game from the header first.");
        _tags = _memory.ReadTags();
        return BuildSession();
    }

    public ProjectileSwapperSession Refresh()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running game first.");
        _tags = _memory.ReadTags();
        return BuildSession();
    }

    public void Swap(ProjectileSwapWeapon weapon, RuntimeTagEntry projectile)
    {
        RuntimeTagEntry liveProjectile = _tags.FirstOrDefault(tag =>
                tag.Index == projectile.Index &&
                string.Equals(tag.Group, "proj", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "That projectile is no longer loaded. Refresh and choose it again.");
        RuntimeTagEntry liveWeapon = _tags.FirstOrDefault(tag =>
                tag.Index == weapon.Tag.Index &&
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "That weapon is no longer loaded. Refresh and choose it again.");

        ProjectileSwapWeapon current = InspectWeapon(liveWeapon)
            ?? throw new InvalidDataException(
                $"{weapon.Name} has no writable barrel projectile reference.");
        byte[] replacement = _memory.BuildTagReference(liveProjectile);
        var completed = new List<(long Address, byte[] Bytes)>();
        try
        {
            foreach (RuntimeTagFieldValue field in current.ProjectileFields)
            {
                byte[] original = _memory.ReadBytes(field.Address, field.Size);
                _memory.WriteVerified(field.Address, replacement);
                completed.Add((field.Address, original));
            }
        }
        catch
        {
            foreach ((long address, byte[] bytes) in completed.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(address, bytes); }
                catch { }
            }
            throw;
        }
    }

    public void Dispose() { }

    public static string FriendlyName(RuntimeTagEntry tag)
    {
        string text = tag.LeafName.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed"
            : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    public static string WeaponIconUri(string tagPath)
    {
        string path = tagPath.Replace('\\', '/').ToLowerInvariant();
        string icon =
            path.Contains("frag_grenade", StringComparison.Ordinal) ||
            path.Contains("assault_bomb", StringComparison.Ordinal) ? "T_UI_FragGrenade_WeaponIcon.png" :
            path.Contains("plasma_grenade", StringComparison.Ordinal) ? "T_UI_PlasmaGrenade_WeaponIcon.png" :
            path.Contains("banshee", StringComparison.Ordinal) &&
            (path.Contains("bomb", StringComparison.Ordinal) ||
             path.Contains("fuel", StringComparison.Ordinal)) ? "T_UI_BansheeBomb_WeaponIcon.png" :
            path.Contains("banshee", StringComparison.Ordinal) ||
            path.Contains("hornet", StringComparison.Ordinal) ||
            path.Contains("pelican", StringComparison.Ordinal) ? "T_UI_BansheeDualCannon_WeaponIcon.png" :
            path.Contains("wraith", StringComparison.Ordinal) &&
            (path.Contains("mortar", StringComparison.Ordinal) ||
             path.Contains("main", StringComparison.Ordinal)) ? "T_UI_WraithMainGun_Icon.png" :
            path.Contains("wraith", StringComparison.Ordinal) ? "T_UI_WraithTurret_Icon.png" :
            path.Contains("scorpion", StringComparison.Ordinal) &&
            (path.Contains("cannon", StringComparison.Ordinal) ||
             path.Contains("main", StringComparison.Ordinal)) ? "T_IU_ScorpionMainGun_Icon.png" :
            path.Contains("scorpion", StringComparison.Ordinal) ? "T_UI_ScorpionTurret_Icon.png" :
            path.Contains("seraph", StringComparison.Ordinal) &&
            path.Contains("missile", StringComparison.Ordinal) ? "T_UI_SeraphMissiles_Icons.png" :
            path.Contains("seraph", StringComparison.Ordinal) ? "T_UI_SeraphTurret_Icon.png" :
            path.Contains("warthog", StringComparison.Ordinal) ||
            path.Contains("machinegun", StringComparison.Ordinal) ||
            path.Contains("chaingun", StringComparison.Ordinal) ? "T_UI_WarthogTurret_Icon.png" :
            path.Contains("shade", StringComparison.Ordinal) ||
            path.Contains("phantom", StringComparison.Ordinal) ||
            path.Contains("spirit", StringComparison.Ordinal) ? "T_UI_ShadeTurret_Icon.png" :
            path.Contains("ghost", StringComparison.Ordinal) ||
            path.Contains("chopper", StringComparison.Ordinal) ||
            path.Contains("mongoose", StringComparison.Ordinal) ? "T_UI_Ghost_WeaponIcon.png" :
            path.Contains("needle_rifle", StringComparison.Ordinal) ? "T_UI_NeedleRifle_WeaponIcon.png" :
            path.Contains("needler", StringComparison.Ordinal) ? "T_UI_Needler_WeaponIcon.png" :
            path.Contains("plasma_pistol", StringComparison.Ordinal) ||
            path.Contains("jackal_shield", StringComparison.Ordinal) ? "T_UI_PlasmaPistol_WeaponIcon.png" :
            path.Contains("plasma_rifle", StringComparison.Ordinal) ||
            path.Contains("plasma_repeater", StringComparison.Ordinal) ||
            path.Contains("plasma_carbine", StringComparison.Ordinal) ? "T_UI_PlasmaRifleIcon.png" :
            // SMG lives under assault_rifle/; match it before the AR fallback.
            path.Contains("/smg", StringComparison.Ordinal) ||
            path.Contains("smg-", StringComparison.Ordinal) ||
            path.Contains("smg_", StringComparison.Ordinal) ? "T_UI_SMG_WeaponIcon.png" :
            path.Contains("assault_rifle", StringComparison.Ordinal) ? "T_UI_AssaultRifle_WeaponIcon.png" :
            path.Contains("battle_rifle", StringComparison.Ordinal) ||
            path.Contains("/dmr/", StringComparison.Ordinal) ? "T_UI_BattleRifle_WeaponIcon.png" :
            path.Contains("beam_rifle", StringComparison.Ordinal) ||
            path.Contains("focus_rifle", StringComparison.Ordinal) ||
            path.Contains("spartan_laser", StringComparison.Ordinal) ? "T_UI_BeamRifle_WeaponIcon.png" :
            path.Contains("energy_sword", StringComparison.Ordinal) ||
            path.Contains("gravity_hammer", StringComparison.Ordinal) ||
            path.Contains("skirmisher", StringComparison.Ordinal) ? "T_UI_EnergySword_WeaponIcon.png" :
            path.Contains("fuel_rod", StringComparison.Ordinal) ||
            path.Contains("flak_cannon", StringComparison.Ordinal) ||
            path.Contains("plasma_launcher", StringComparison.Ordinal) ||
            path.Contains("concussion", StringComparison.Ordinal) ? "T_UI_FuelRod_WeaponIcon.png" :
            path.Contains("shotgun", StringComparison.Ordinal) ? "T_UI_Shotgun_WeaponIcon.png" :
            path.Contains("stanchion", StringComparison.Ordinal) ||
            path.Contains("sniper_rifle", StringComparison.Ordinal) ? "T_UI_SniperRifle_WeaponIcon.png" :
            path.Contains("spike_rifle", StringComparison.Ordinal) ||
            path.Contains("spiker", StringComparison.Ordinal) ? "T_UI_SpikerRifle_WeaponIcon.png" :
            path.Contains("rocket_launcher", StringComparison.Ordinal) ||
            path.Contains("spnkr", StringComparison.Ordinal) ||
            path.Contains("grenade_launcher", StringComparison.Ordinal) ? "T_UI_SPNKR_WeaponIcon.png" :
            path.Contains("sentinel", StringComparison.Ordinal) ||
            path.Contains("target_laser", StringComparison.Ordinal) ? "T_UI_SentinelBeam_WeaponIcon.png" :
            path.Contains("brute_shot", StringComparison.Ordinal) ||
            path.Contains("bruteshot", StringComparison.Ordinal) ? "wiki_brute_shot.png" :
            path.Contains("mauler", StringComparison.Ordinal) ||
            path.Contains("excavator", StringComparison.Ordinal) ? "wiki_mauler.png" :
            path.Contains("magnum", StringComparison.Ordinal) ||
            path.Contains("pistol", StringComparison.Ordinal) ? "T_UI_Pistol_WeaponIcon.png" :
            "missing.png";
        return $"ms-appx:///Assets/WeaponIcons/{icon}";
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("weap"))
            throw new InvalidDataException(
                "The loaded definitions do not provide the [weap] schema.");
    }

    private ProjectileSwapperSession BuildSession()
    {
        RuntimeTagEntry[] projectiles = _tags
            .Where(tag => string.Equals(
                tag.Group, "proj", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ProjectileSwapWeapon[] weapons = _tags
            .Where(tag =>
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => InspectWeapon(group.First()))
            .OfType<ProjectileSwapWeapon>()
            .OrderBy(weapon => weapon.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(weapon => weapon.Tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (weapons.Length == 0)
            throw new InvalidDataException(
                $"Found {_tags.Count(tag => string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase)):N0} " +
                "loaded [weap] tags, but their barrel projectile fields could not be read from the schema.");
        if (projectiles.Length == 0)
            throw new InvalidDataException("No projectile tags are loaded in this mission.");
        return new ProjectileSwapperSession(weapons, projectiles);
    }

    private ProjectileSwapWeapon? InspectWeapon(RuntimeTagEntry weapon)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            weapon.Group, weapon.DataAddress, _memory.ReadBytes, ResolveOrNull);
        var projectileFields = new List<RuntimeTagFieldValue>();
        var currentProjectiles = new List<RuntimeTagEntry>();
        var visited = new HashSet<(string Definition, long Address, int Element)>();
        Visit(root, false, 0);

        return projectileFields.Count == 0
            ? null
            : new ProjectileSwapWeapon(
                FriendlyName(weapon), weapon, projectileFields, currentProjectiles);

        void Visit(
            IReadOnlyList<RuntimeTagFieldValue> fields,
            bool insideBarrels,
            int depth)
        {
            if (depth > 10) return;
            foreach (RuntimeTagFieldValue field in fields)
            {
                string fieldName = CleanFieldName(field.Name);
                if (insideBarrels &&
                    field.IsTagReference &&
                    string.Equals(fieldName, "projectile", StringComparison.OrdinalIgnoreCase))
                {
                    projectileFields.Add(field);
                    RuntimeTagEntry? current = _tags.FirstOrDefault(tag =>
                        tag.Index == field.ReferencedTagIndex &&
                        string.Equals(tag.Group, "proj", StringComparison.OrdinalIgnoreCase));
                    if (current is not null &&
                        currentProjectiles.All(item => item.Index != current.Index))
                        currentProjectiles.Add(current);
                }

                if (!field.CanOpenBlock) continue;
                bool childInsideBarrels = insideBarrels ||
                    string.Equals(fieldName, "barrels", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        field.ChildBlockDefinition,
                        "weapon_barrels",
                        StringComparison.OrdinalIgnoreCase);
                for (int element = 0; element < field.ChildCount; element++)
                {
                    var key = (field.ChildBlockDefinition!, field.ChildAddress, element);
                    if (!visited.Add(key)) continue;
                    IReadOnlyList<RuntimeTagFieldValue> children;
                    try
                    {
                        children = _definitions.ReadBlockFields(
                            weapon.Group,
                            field.ChildBlockDefinition!,
                            field.ChildAddress,
                            element,
                            _memory.ReadBytes,
                            ResolveOrNull);
                    }
                    catch
                    {
                        continue;
                    }
                    Visit(children, childInsideBarrels, depth + 1);
                }
            }
        }
    }

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }
}
