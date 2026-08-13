using System.Globalization;
using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record SpawnVariantChoice(
    string Name,
    byte[] StringIdBytes,
    short VariantIndex,
    int VariantBlockIndex,
    string? ImageUri = null)
{
    public uint StringId => StringIdBytes.Length == sizeof(uint)
        ? BinaryPrimitives.ReadUInt32LittleEndian(StringIdBytes)
        : 0;
    public string Detail => VariantIndex >= 0
        ? $"Model variant {VariantIndex}"
        : "Authored default";
}

/// <summary>
/// Result of selecting a scenario squad scaffold for an AI spawn.
/// </summary>
public sealed record SpawnScaffoldDiagnosis(
    int SquadIndex,
    string SquadName,
    short TeamIndex,
    short ObjectiveIndex,
    bool WantedFriendly,
    bool UsedDedicated,
    bool UsedHostileFallback,
    bool FireteamFollow,
    int AllyScaffoldCount,
    int HostileScaffoldCount,
    string Summary)
{
    public override string ToString() =>
        $"squad={SquadIndex}:{SquadName} team={TeamIndex} objective={ObjectiveIndex} " +
        $"wantedFriendly={WantedFriendly} dedicated={UsedDedicated} " +
        $"hostileFallback={UsedHostileFallback} fireteam={FireteamFollow} " +
        $"ally={AllyScaffoldCount} hostile={HostileScaffoldCount} | {Summary}";
}

/// <summary>
/// Inventory of usable spawn scaffolds in the loaded scenario.
/// </summary>
public sealed record SpawnScaffoldInventory(
    string ScenarioName,
    int AllyScaffoldCount,
    int HostileScaffoldCount,
    int DedicatedAllyCount,
    int DedicatedHostileCount,
    int IdleAllyCount)
{
    public bool NeedsDedicatedAlly =>
        AllyScaffoldCount == 0 && DedicatedAllyCount == 0;

    public override string ToString() =>
        $"scenario={ScenarioName} ally={AllyScaffoldCount} (idle={IdleAllyCount}) " +
        $"hostile={HostileScaffoldCount} dedicatedAlly={DedicatedAllyCount} " +
        $"dedicatedHostile={DedicatedHostileCount} needsDedicatedAlly={NeedsDedicatedAlly}";
}

public sealed record EnemySpawnChoice(
    RuntimeTagEntry CharacterTag,
    IReadOnlyList<SpawnVariantChoice> Variants)
{
    public string LeafName => CharacterTag.LeafName;
    public string DisplayName => FriendlyName(LeafName);
    public string TagPath => CharacterTag.Name;
    public string Category => CategorizeCharacter(CharacterTag.Name);
    public string VariantSummary => Variants.Count == 1
        ? "1 available variant"
        : $"{Variants.Count:N0} available variants";
    public string SearchText =>
        $"{DisplayName} {TagPath} {Category} {string.Join(' ', Variants.Select(item => item.Name))}";

    private static string CategorizeCharacter(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (ContainsAny(value, "elite", "grunt", "jackal", "hunter", "engineer",
                "prophet", "brute", "drone"))
            return "Covenant";
        if (ContainsAny(value, "flood", "infection", "carrier", "combat_form",
                "pureform"))
            return "Flood";
        if (ContainsAny(value, "marine", "crewman", "keyes", "johnson", "pilot",
                "spartan", "masterchief", "master_chief", "odst"))
            return "UNSC";
        if (ContainsAny(value, "sentinel", "monitor", "enforcer", "forerunner"))
            return "Forerunner";
        if (ContainsAny(value, "critter", "ambient", "wildlife"))
            return "Wildlife";
        return "Other";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    internal static string FriendlyName(string value)
    {
        string text = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed character"
            : string.Join(
                ' ',
                text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word =>
                        char.ToUpperInvariant(word[0]) + word[1..]));
    }
}

public sealed record ArmorSpawnChoice(
    RuntimeTagEntry BipedTag,
    IReadOnlyList<SpawnVariantChoice> Variants)
{
    public string DisplayName => "Johnson Spartan";
    public string TagPath => BipedTag.Name;
    public string Category => "UNSC companion";
    public string VariantSummary => $"{Variants.Count:N0} available armor sets";
    public string SearchText =>
        $"{DisplayName} {TagPath} {Category} {string.Join(' ', Variants.Select(item => item.Name))}";
}

public sealed record AiWeaponChoice(RuntimeTagEntry WeaponTag)
{
    public string DisplayName =>
        EnemySpawnChoice.FriendlyName(WeaponTag.LeafName);
    public string TagPath => WeaponTag.Name;
    public uint Datum =>
        RuntimeTagMemoryService.BuildRuntimeDatum(WeaponTag);
}

public sealed record SpawnerCatalog(
    IReadOnlyList<EnemySpawnChoice> Characters,
    IReadOnlyList<ArmorSpawnChoice> Armor,
    string ArmorStatus);

public sealed class EnemySpawnerService : IDisposable
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private int _warmedProcessId;

    public int ProcessId => _memory.ProcessId;
    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public SpawnerCatalog Connect()
    {
        WarmUpDefinitions();

        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to the game from the header first.");
        _tags = _memory.ReadTags();
        EnemySpawnChoice[] characters = _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.Name.Contains(@"\ai\", StringComparison.OrdinalIgnoreCase) &&
                !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(character => new EnemySpawnChoice(
                character,
                ReadVariants(character)))
            .Where(choice => choice.Variants.Count > 0)
            .OrderBy(choice => choice.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.TagPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<ArmorSpawnChoice> armor = ReadArmorChoices(out string armorStatus);
        return new SpawnerCatalog(characters, armor, armorStatus);
    }

    public async Task<ScriptExecutionResult> SpawnAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
        => await SpawnGroupAsync(
            choice,
            variant,
            1,
            cancellationToken: cancellationToken);

    public async Task<ScriptExecutionResult> SpawnGroupAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        int count,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = false,
        bool clearSquadObjective = false,
        ushort? campaignTeam = null,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        if (count is < 1 or > 5)
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "One native AI batch can contain between one and five actors.");
        if (campaignTeam is ushort team && team > 13)
            throw new ArgumentOutOfRangeException(nameof(campaignTeam));
        await WarmUpAsync(cancellationToken);
        WorldPoint playerPosition =
            await ReadPlayerPositionAsync(cancellationToken);
        // Select the scaffold first, then clear THAT squad's objective. The old
        // "nearest squad by distance" clear often patched a different encounter
        // than BuildPayload borrowed — especially when friendlies are far away.
        SpawnPlan plan = await Task.Run(
            () => BuildPlan(
                choice,
                variant,
                playerPosition,
                count,
                formationOffsetX,
                formationOffsetY,
                weapon,
                followPlayer,
                campaignTeam),
            cancellationToken);
        LastScaffoldDiagnosis = plan.Diagnosis;
        AppendScaffoldDiagnosisLog(plan.Diagnosis);
        // Dedicated hm_ally/hm_hostile keep their combat objective so actors
        // are born with attack desire. Only strip encounter hooks when
        // borrowing a normal mission squad.
        bool clearObjective =
            clearSquadObjective && !plan.Diagnosis.UsedDedicated;
        IReadOnlyList<MemoryPatch> objectivePatches = clearObjective
            ? BeginClearSquadObjective(plan.Template)
            : [];
        // Clear task "suppress combat" and squad blind/deaf/braindead so
        // actor_new inherits a fightable order (not a muted companion escort).
        // Best-effort only: never abort the spawn if the patch fails.
        IReadOnlyList<MemoryPatch> unsuppressPatches = [];
        try
        {
            unsuppressPatches = BeginUnsuppressScaffoldCombat(plan.Template);
        }
        catch
        {
            unsuppressPatches = [];
        }
        if (plan.Diagnosis.FireteamFollow)
            ClearDedicatedAllyFireteamAbsorber(plan.Template);
        try
        {
            if (plan.Diagnosis.FireteamFollow)
                await TryLockFireteamAbsorbAsync(cancellationToken);
            // ai_team payload carries squad team override so actor_new is born
            // into the intended campaign team (post-spawn object_team alone
            // cannot fully reverse birth-time combat disposition).
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                followPlayer || count > 1 || campaignTeam is not null
                    ? ScriptLanguage.BlamAiTeamSpawn
                    : ScriptLanguage.BlamAiSpawn,
                plan.Payload,
                TimeSpan.FromSeconds(20),
                cancellationToken: cancellationToken);
            string message = string.IsNullOrWhiteSpace(plan.Diagnosis.Summary)
                ? result.Message
                : $"{result.Message} {plan.Diagnosis.Summary}";
            if (unsuppressPatches.Count > 0)
                message = $"{message} task=unsuppressed";
            if (result.Outcome == ScriptOutcome.Failed)
                AppendScaffoldDiagnosisLogLine(
                    $"FAIL squad={plan.Diagnosis.SquadName} {result.Message}");
            return result with { Message = message };
        }
        finally
        {
            try { RestorePatches(unsuppressPatches); }
            catch { /* best-effort */ }
            RestorePatches(objectivePatches);
            // Mute-flag restore writes the original squad flags word, which
            // would turn fireteam absorber back on. Re-clear after restore.
            if (plan.Diagnosis.FireteamFollow)
            {
                try { ClearDedicatedAllyFireteamAbsorber(plan.Template); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Diagnosis from the most recent AI scaffold selection. Useful for
    /// comparing quiet zones vs combat zones when friendlies flip hostile.
    /// </summary>
    public SpawnScaffoldDiagnosis? LastScaffoldDiagnosis { get; private set; }

    public static string ScaffoldDiagnosisLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Meteorite",
        "Saved",
        "HaloMeister",
        "AllegianceDemo",
        "scaffold-diagnosis.log");

    public const string DedicatedAllySquadName = "hm_ally";
    public const string DedicatedHostileSquadName = "hm_hostile";

    /// <summary>
    /// Scans the loaded scenario for usable ally/hostile/dedicated scaffolds
    /// without spawning. Writes one inventory line to the diagnosis log.
    /// </summary>
    public SpawnScaffoldInventory ProbeScaffoldInventory()
    {
        if (_tags.Count == 0)
            _tags = _memory.ReadTags();
        RuntimeTagEntry scenario = _tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "No loaded [scnr] tag with readable data was found. Load a campaign mission first.");
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(scenario);
        RuntimeTagFieldValue squads = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "squads_block")
            ?? throw new InvalidDataException("The loaded scenario has no readable squads.");

        RuntimeTagFieldValue? palette = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "character_palette_block" &&
            field.CanOpenBlock);
        int ally = 0;
        int hostile = 0;
        int dedicatedAlly = 0;
        int dedicatedHostile = 0;
        int idleAlly = 0;
        for (int squadIndex = 0; squadIndex < Math.Min(squads.ChildCount, 2048); squadIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> squad = ReadBlock(
                scenario, squads, squadIndex);
            RuntimeTagFieldValue? team = squad.FirstOrDefault(field =>
                field.Type == "short_enum" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "team",
                    StringComparison.OrdinalIgnoreCase));
            if (team is null ||
                !short.TryParse(
                    team.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out short teamIndex))
                continue;
            RuntimeTagFieldValue? spawnPoints = squad.FirstOrDefault(field =>
                field.ChildBlockDefinition == "spawn_points_block" &&
                field.CanOpenBlock &&
                field.ChildCount > 0);
            if (spawnPoints is null)
                continue;

            string name = ReadSquadName(squad);
            bool isFriendly = teamIndex is 1 or 2;
            short objective = ReadOptionalShort(squad, "initial objective") ?? -1;
            // Match BuildPlan: name alone is not enough. d40 shipped hm_ally with
            // spawn points that had character type=-1 and cell=-1, so Probe said
            // dedicatedAlly=1 while Spawn still borrowed hostile scaffolds.
            bool buildPlanUsable = palette is not null &&
                SquadHasBuildPlanUsableSpawnPoint(
                    scenario, squad, palette, spawnPoints);
            if (IsDedicatedName(name, DedicatedAllySquadName))
            {
                if (buildPlanUsable)
                    dedicatedAlly++;
            }
            else if (IsDedicatedName(name, DedicatedHostileSquadName))
            {
                if (buildPlanUsable)
                    dedicatedHostile++;
            }
            if (!buildPlanUsable)
                continue;
            if (isFriendly)
            {
                ally++;
                if (objective < 0)
                    idleAlly++;
            }
            else
            {
                hostile++;
            }
        }

        var inventory = new SpawnScaffoldInventory(
            scenario.Name,
            ally,
            hostile,
            dedicatedAlly,
            dedicatedHostile,
            idleAlly);
        AppendScaffoldInventoryLog(inventory);
        return inventory;
    }

    private static void AppendScaffoldDiagnosisLog(SpawnScaffoldDiagnosis diagnosis)
    {
        AppendScaffoldDiagnosisLogLine($"SPAWN {diagnosis}");
    }

    private static void AppendScaffoldDiagnosisLogLine(string line)
    {
        try
        {
            string? directory = Path.GetDirectoryName(ScaffoldDiagnosisLogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(
                ScaffoldDiagnosisLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // Diagnosis logging must never break spawn.
        }
    }

    private static void AppendScaffoldInventoryLog(SpawnScaffoldInventory inventory)
    {
        try
        {
            string? directory = Path.GetDirectoryName(ScaffoldDiagnosisLogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(
                ScaffoldDiagnosisLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PROBE {inventory}{Environment.NewLine}");
        }
        catch
        {
            // Probe logging must never break scan.
        }
    }

    private static bool IsDedicatedName(string name, string expected) =>
        string.Equals(name.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string ReadSquadName(IReadOnlyList<RuntimeTagFieldValue> squad)
    {
        RuntimeTagFieldValue? field = squad.FirstOrDefault(item =>
            (item.Type is "string" or "long_string") &&
            string.Equals(
                CleanFieldName(item.Name),
                "name",
                StringComparison.OrdinalIgnoreCase));
        return field?.Value.Trim('\0', ' ') ?? "";
    }

    private static short? ReadOptionalShort(
        IReadOnlyList<RuntimeTagFieldValue> squad,
        string fieldName)
    {
        RuntimeTagFieldValue? field = squad.FirstOrDefault(item =>
            string.Equals(
                CleanFieldName(item.Name),
                fieldName,
                StringComparison.OrdinalIgnoreCase) &&
            item.Size >= 2);
        if (field is null)
            return null;
        if (short.TryParse(
                field.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out short parsed))
            return parsed;
        return null;
    }

    /// <summary>
    /// Temporarily writes <c>&lt;none&gt;</c> (-1) into the borrowed scaffold
    /// squad's authored <c>initial objective</c> / <c>initial task</c> so
    /// <c>actor_new</c> does not inherit that encounter's combat hook.
    /// </summary>
    private List<MemoryPatch> BeginClearSquadObjective(SpawnTemplate template)
    {
        if (template.ObjectiveAddress == 0 || template.TaskAddress == 0)
            return [];

        var patches = new List<MemoryPatch>(2);
        byte[] none = BitConverter.GetBytes((short)-1);
        foreach (long address in new[]
                 {
                     template.ObjectiveAddress,
                     template.TaskAddress,
                 })
        {
            byte[] original = _memory.ReadBytes(address, 2);
            if (original.AsSpan().SequenceEqual(none))
                continue;
            _memory.WriteVerified(address, none);
            patches.Add(new MemoryPatch(address, original));
        }
        return patches;
    }

    // tasks_block.flags bit 4 = "suppress combat"
    private const ushort TaskFlagSuppressCombat = 1 << 4;
    // Also clear blind/deaf/braindead on the same word when present.
    private const ushort TaskFlagMuteMask =
        TaskFlagSuppressCombat | (1 << 6) | (1 << 7) | (1 << 8);
    // squads_block.flags bits 0-2 = blind/deaf/braindead
    private const uint SquadFlagMuteMask = 0b111;
    // bit 5 = fireteam absorber (nearby player-team squads join the fireteam)
    private const uint SquadFlagFireteamAbsorber = 1u << 5;

    private List<MemoryPatch> BeginUnsuppressScaffoldCombat(SpawnTemplate template)
    {
        var patches = new List<MemoryPatch>(2);
        if (template.TaskFlagsAddress != 0)
        {
            ushort flags = unchecked((ushort)ReadInt16(template.TaskFlagsAddress));
            ushort cleared = (ushort)(flags & ~TaskFlagMuteMask);
            if (cleared != flags)
            {
                byte[] original = _memory.ReadBytes(template.TaskFlagsAddress, 2);
                _memory.WriteVerified(
                    template.TaskFlagsAddress,
                    BitConverter.GetBytes(cleared));
                patches.Add(new MemoryPatch(template.TaskFlagsAddress, original));
            }
        }

        if (template.SquadFlagsAddress != 0)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(template.SquadFlagsAddress, 4));
            uint mask = SquadFlagMuteMask;
            if (IsDedicatedName(template.SquadName, DedicatedAllySquadName))
                mask |= SquadFlagFireteamAbsorber;
            uint cleared = flags & ~mask;
            if (cleared != flags)
            {
                byte[] original = _memory.ReadBytes(template.SquadFlagsAddress, 4);
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, cleared);
                _memory.WriteVerified(template.SquadFlagsAddress, bytes);
                patches.Add(new MemoryPatch(template.SquadFlagsAddress, original));
            }
        }

        return patches;
    }

    /// <summary>
    /// Keep fireteam follow on <c>hm_ally</c> only. Clearing absorber here is
    /// persistent for the loaded mission so cloned donor flags cannot vacuum
    /// nearby story squads into the player's fireteam.
    /// </summary>
    private void ClearDedicatedAllyFireteamAbsorber(SpawnTemplate template)
    {
        if (template.SquadFlagsAddress == 0 ||
            !IsDedicatedName(template.SquadName, DedicatedAllySquadName))
        {
            return;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
            _memory.ReadBytes(template.SquadFlagsAddress, 4));
        uint cleared = flags & ~SquadFlagFireteamAbsorber;
        if (cleared == flags)
            return;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, cleared);
        _memory.WriteVerified(template.SquadFlagsAddress, bytes);
    }

    private async Task TryLockFireteamAbsorbAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                string.Join(
                    '\n',
                    [
                        "ai_player_set_fireteam_max_squad_absorb_distance (player_get 0) 0",
                        $"ai_set_fireteam_absorber {DedicatedAllySquadName} false",
                    ]),
                TimeSpan.FromSeconds(8),
                cancellationToken);
        }
        catch
        {
            // Follow still works via native fireteam; absorb lock is best-effort.
        }
    }

    private long ResolveTaskFlagsAddress(
        RuntimeTagEntry scenario,
        RuntimeTagFieldValue objectives,
        short objectiveIndex,
        short taskIndex)
    {
        if (objectiveIndex < 0 ||
            objectiveIndex >= objectives.ChildCount ||
            taskIndex < 0)
        {
            return 0;
        }

        RuntimeTagFieldValue? tasks = ReadBlock(
            scenario,
            objectives,
            objectiveIndex).FirstOrDefault(field =>
                field.ChildBlockDefinition == "tasks_block" &&
                field.CanOpenBlock);
        if (tasks is null || taskIndex >= tasks.ChildCount)
            return 0;

        RuntimeTagFieldValue? flags = ReadBlock(scenario, tasks, taskIndex)
            .FirstOrDefault(field =>
                field.Type == "word_flags" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "flags",
                    StringComparison.OrdinalIgnoreCase) &&
                field.Size >= 2);
        return flags?.Address ?? 0;
    }

    private bool TaskHasFlag(
        RuntimeTagEntry scenario,
        RuntimeTagFieldValue objectives,
        short objectiveIndex,
        short taskIndex,
        ushort flag)
    {
        long address = ResolveTaskFlagsAddress(
            scenario,
            objectives,
            objectiveIndex,
            taskIndex);
        if (address == 0)
            return false;
        ushort flags = unchecked((ushort)ReadInt16(address));
        return (flags & flag) != 0;
    }

    private void RestorePatches(IReadOnlyList<MemoryPatch> patches)
    {
        for (int index = patches.Count - 1; index >= 0; index--)
        {
            MemoryPatch patch = patches[index];
            try
            {
                _memory.WriteVerified(patch.Address, patch.Original);
            }
            catch
            {
                // Best-effort restore after spawn; authored squad data is temporary.
            }
        }
    }

    public void WarmUpDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("char") || !_definitions.HasSchema("scnr"))
            throw new InvalidDataException(
                "The loaded definitions do not provide both [char] and [scnr] schemas.");
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_warmedProcessId == _memory.ProcessId && _warmedProcessId != 0)
            return;

        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady || status.IsStale)
            return;

        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.PlayerPosition,
                "read",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (result.Outcome == ScriptOutcome.Confirmed)
                _warmedProcessId = _memory.ProcessId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Optional prewarm; spawning remains available if this build does
            // not expose the player-position capability.
        }
    }

    public async Task<ScriptExecutionResult> SpawnTeamAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        return await SpawnGroupAsync(
            choice,
            variant,
            5,
            cancellationToken: cancellationToken);
    }

    public IReadOnlyList<AiWeaponChoice> GetCompatibleWeapons(
        EnemySpawnChoice _) =>
        GetAllWeapons();

    /// <summary>
    /// Every loaded <c>weap</c> tag currently in the process (not limited to the
    /// character's authored <c>character_weapons_block</c>).
    /// </summary>
    public IReadOnlyList<AiWeaponChoice> GetAllWeapons()
    {
        if (!_memory.IsConnected)
            return [];
        _tags = _memory.ReadTags();
        return _tags
            .Where(tag =>
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                !string.IsNullOrWhiteSpace(tag.Name) &&
                !tag.Name.Contains(@"\null\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiWeaponChoice(group.First()))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<EnemySpawnChoice> GetCharacterFamilyVariants(
        EnemySpawnChoice choice)
    {
        if (!_memory.IsConnected)
            return [choice];
        _tags = _memory.ReadTags();
        string family = CharacterFamily(choice.CharacterTag.Name);
        EnemySpawnChoice[] choices = _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.Name.Contains(@"\ai\", StringComparison.OrdinalIgnoreCase) &&
                !tag.Name.Contains(@"\null\", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    CharacterFamily(tag.Name),
                    family,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(character => new EnemySpawnChoice(
                character,
                ReadVariants(character)))
            .Where(candidate => candidate.Variants.Count > 0)
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return choices.Length == 0
            ? [choice]
            : choices;
    }

    public IReadOnlyList<AiWeaponChoice> GetJohnsonCompatibleWeapons()
    {
        if (!_memory.IsConnected)
            return [];
        _tags = _memory.ReadTags();
        RuntimeTagEntry? scenario = _tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0);
        if (scenario is null)
            return [];
        RuntimeTagFieldValue? palette = ReadRoot(scenario).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "scenario_weapon_palette_block",
                StringComparison.OrdinalIgnoreCase));
        if (palette is null)
            return [];

        var weapons = new List<AiWeaponChoice>();
        for (int index = 0; index < Math.Min(palette.ChildCount, 1024); index++)
        {
            RuntimeTagFieldValue? reference = ReadBlock(scenario, palette, index)
                .FirstOrDefault(field => field.IsTagReference);
            RuntimeTagEntry? weapon = reference is null
                ? null
                : _tags.FirstOrDefault(tag =>
                    tag.Index == reference.ReferencedTagIndex &&
                    string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                    tag.DataAddress > 0);
            if (weapon is not null && IsSpartanCompatibleWeapon(weapon.Name))
                weapons.Add(new AiWeaponChoice(weapon));
        }
        return weapons
            .GroupBy(item => item.WeaponTag.Index)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Matches <c>actor_type_enum</c> in the Campaign Evolved character schema.
    /// </summary>
    public static IReadOnlyList<string> ActorTypeNames { get; } =
    [
        "none",
        "player",
        "marine",
        "crew",
        "spartan",
        "elite",
        "jackal",
        "grunt",
        "brute",
        "hunter",
        "prophet",
        "bugger",
        "scarab",
        "engineer",
        "skirmisher",
        "combat_form",
        "infection_form",
        "carrier_form",
        "pure_form_stealth",
        "pure_form_tank",
        "pure_form_ranged",
        "sentinel",
        "mule",
        "mounted_weapon",
    ];

    public const int ActorTypeMarine = 2;
    public const int ActorTypeSpartan = 4;

    public async Task<ScriptExecutionResult> SpawnArmorWithJohnsonAiAsync(
        ArmorSpawnChoice armor,
        SpawnVariantChoice armorVariant,
        int count,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = true,
        int? actorTypeIndex = null,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        _tags = _memory.ReadTags();
        RuntimeTagEntry spartan = _tags.FirstOrDefault(tag =>
                tag.Index == armor.BipedTag.Index &&
                string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "The selected Spartan biped is no longer loaded. Rescan the mission.");
        RuntimeTagEntry donor = RequireFriendlyDonorCharacter();
        // Armor has no selected [char]; borrow voice (and combat, when present)
        // from a loaded spartan/marine character so the shell does not keep the
        // donor's default marine lines.
        RuntimeTagEntry? voiceDonor =
            FindCharacterWithVoice(ActorTypeSpartan)
            ?? FindCharacterWithVoice(ActorTypeMarine);
        return await SpawnWithFriendlyDonorAiAsync(
            donor,
            spartan,
            armorVariant,
            count,
            formationOffsetX,
            formationOffsetY,
            weapon,
            followPlayer,
            applySpartanShields: true,
            actorTypeIndex ?? ActorTypeSpartan,
            combatDonor: voiceDonor,
            cancellationToken);
    }

    /// <summary>
    /// Friendly character spawning via a loaded [char] donor. Prefers
    /// <c>ai/generic.character</c>, then common UNSC ranks; never unique story
    /// NPCs like Johnson. Temporarily retargets the donor unit [bipd] and
    /// combat/voice blocks to the selected character, then restores.
    /// </summary>
    public async Task<ScriptExecutionResult> SpawnCharacterWithJohnsonAiAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        int count,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = true,
        int? actorTypeIndex = null,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        _tags = _memory.ReadTags();
        RuntimeTagEntry character = _tags.FirstOrDefault(tag =>
                tag.Index == choice.CharacterTag.Index &&
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "That character tag is no longer loaded. Rescan the mission.");
        RuntimeTagFieldValue unit = ReadRoot(character).FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "unit",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The selected character {character.Name} has no authored unit reference.");
        RuntimeTagEntry biped = _tags.FirstOrDefault(tag =>
                tag.Index == unit.ReferencedTagIndex &&
                string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(
                "The selected character's [bipd] unit is not published in the live tag table.");
        // Character variants are actor variants on [char], not biped model
        // variants. Appearance comes from the swapped [bipd] defaults; the
        // donor keeps its own actor variant for AI placement.
        _ = variant;

        RuntimeTagEntry donor = RequireFriendlyDonorCharacter();
        int resolvedType = actorTypeIndex
            ?? TryReadCharacterActorType(character)
            ?? ActorTypeMarine;
        return await SpawnWithFriendlyDonorAiAsync(
            donor,
            biped,
            modelVariant: null,
            count,
            formationOffsetX,
            formationOffsetY,
            weapon,
            followPlayer,
            applySpartanShields: false,
            resolvedType,
            combatDonor: character,
            cancellationToken);
    }

    public int? TryReadCharacterActorType(EnemySpawnChoice choice)
    {
        if (!_memory.IsConnected)
            return null;
        _tags = _memory.ReadTags();
        RuntimeTagEntry? character = _tags.FirstOrDefault(tag =>
            tag.Index == choice.CharacterTag.Index &&
            string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0);
        return character is null ? null : TryReadCharacterActorType(character);
    }

    private async Task<ScriptExecutionResult> SpawnWithFriendlyDonorAiAsync(
        RuntimeTagEntry donor,
        RuntimeTagEntry biped,
        SpawnVariantChoice? modelVariant,
        int count,
        float formationOffsetX,
        float formationOffsetY,
        AiWeaponChoice? weapon,
        bool followPlayer,
        bool applySpartanShields,
        int actorTypeIndex,
        RuntimeTagEntry? combatDonor,
        CancellationToken cancellationToken)
    {
        RuntimeTagFieldValue donorUnit = ReadRoot(donor).FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "unit",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The friendly donor character {donor.Name} has no unit reference.");
        RuntimeTagFieldValue? defaultVariant = modelVariant is null
            ? null
            : ReadRoot(biped).FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase));
        if (modelVariant is not null && defaultVariant is null)
            throw new InvalidDataException(
                $"The biped {biped.Name} has no default model variant.");

        byte[] originalUnit = _memory.ReadBytes(donorUnit.Address, 16);
        byte[] bipedReference = _memory.BuildTagReference(biped);
        byte[]? originalVariant = defaultVariant is null
            ? null
            : _memory.ReadBytes(defaultVariant.Address, sizeof(uint));
        IReadOnlyList<MemoryPatch> shieldPatches = [];
        IReadOnlyList<MemoryPatch> combatPatches = [];
        IReadOnlyList<MemoryPatch> typePatches = [];
        bool patchedUnit =
            !originalUnit.AsSpan().SequenceEqual(bipedReference);
        bool patchedVariant =
            modelVariant is not null &&
            originalVariant is not null &&
            !originalVariant.AsSpan().SequenceEqual(modelVariant.StringIdBytes);
        if (patchedUnit)
            _memory.WriteVerified(donorUnit.Address, bipedReference);
        try
        {
            if (combatDonor is not null)
                combatPatches = ApplyCombatDonorProperties(donor, combatDonor);
            typePatches = ApplyDonorActorType(donor, actorTypeIndex);
            if (applySpartanShields)
                shieldPatches = ApplyAuthoredSpartanShields(donor);
            if (patchedVariant && defaultVariant is not null && modelVariant is not null)
                _memory.WriteVerified(
                    defaultVariant.Address,
                    modelVariant.StringIdBytes);
            var donorChoice = new EnemySpawnChoice(
                donor,
                ReadVariants(donor));
            SpawnVariantChoice donorVariant =
                donorChoice.Variants.FirstOrDefault()
                ?? throw new InvalidDataException(
                    $"The friendly donor character {donor.Name} exposes no actor variant.");
            return await SpawnGroupAsync(
                donorChoice,
                donorVariant,
                count,
                formationOffsetX,
                formationOffsetY,
                weapon,
                followPlayer,
                cancellationToken: cancellationToken);
        }
        finally
        {
            if (_memory.IsConnected)
            {
                RestorePatches(shieldPatches);
                RestorePatches(typePatches);
                RestorePatches(combatPatches);
                if (patchedVariant && defaultVariant is not null && originalVariant is not null)
                    _memory.WriteVerified(
                        defaultVariant.Address,
                        originalVariant);
                if (patchedUnit)
                    _memory.WriteVerified(
                        donorUnit.Address,
                        originalUnit);
            }
        }
    }

    private int? TryReadCharacterActorType(RuntimeTagEntry character)
    {
        RuntimeTagFieldValue? general = ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            field.ChildCount > 0 &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_general_block",
                StringComparison.OrdinalIgnoreCase));
        if (general is null)
            return null;
        RuntimeTagFieldValue? typeField = ReadBlock(character, general, 0)
            .FirstOrDefault(field =>
                field.Type == "short_enum" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "type",
                    StringComparison.OrdinalIgnoreCase));
        if (typeField is null || typeField.Size < sizeof(short))
            return null;
        short value = ReadInt16(typeField.Address);
        return value >= 0 && value < ActorTypeNames.Count ? value : null;
    }

    private IReadOnlyList<MemoryPatch> ApplyDonorActorType(
        RuntimeTagEntry donor,
        int actorTypeIndex)
    {
        if (actorTypeIndex < 0 || actorTypeIndex >= ActorTypeNames.Count)
            throw new ArgumentOutOfRangeException(nameof(actorTypeIndex));
        RuntimeTagFieldValue? general = ReadRoot(donor).FirstOrDefault(field =>
            field.CanOpenBlock &&
            field.ChildCount > 0 &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_general_block",
                StringComparison.OrdinalIgnoreCase));
        if (general is null)
            return [];
        RuntimeTagFieldValue? typeField = ReadBlock(donor, general, 0)
            .FirstOrDefault(field =>
                field.Type == "short_enum" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "type",
                    StringComparison.OrdinalIgnoreCase));
        if (typeField is null || typeField.Size < sizeof(short))
            return [];

        short current = ReadInt16(typeField.Address);
        if (current == actorTypeIndex)
            return [];
        byte[] original = _memory.ReadBytes(typeField.Address, sizeof(short));
        byte[] replacement = BitConverter.GetBytes((short)actorTypeIndex);
        _memory.WriteVerified(typeField.Address, replacement);
        return [new MemoryPatch(typeField.Address, original)];
    }

    public async Task<ScriptExecutionResult> SpawnBodyAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        await WarmUpAsync(cancellationToken);

        RuntimeTagEntry? character = FindCachedCharacter(choice.CharacterTag);
        if (character is null)
        {
            _tags = _memory.ReadTags();
            character = FindCachedCharacter(choice.CharacterTag);
        }
        if (character is null)
            throw new InvalidOperationException(
                "That character tag is no longer loaded. Rescan the mission.");
        RuntimeTagFieldValue unit = ReadRoot(character).FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "unit",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The selected character {character.Name} has no authored unit reference.");
        RuntimeTagEntry biped = _tags.FirstOrDefault(tag =>
                tag.Index == unit.ReferencedTagIndex &&
                string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(
                "The selected character's [bipd] unit is not published in the live tag table.");

        return await SpawnVariantBodyCoreAsync(biped, variant, cancellationToken);
    }

    public async Task<ScriptExecutionResult> SpawnArmorAsync(
        ArmorSpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        await WarmUpAsync(cancellationToken);

        RuntimeTagEntry? biped = FindCachedBiped(choice.BipedTag);
        if (biped is null)
        {
            _tags = _memory.ReadTags();
            biped = FindCachedBiped(choice.BipedTag);
        }
        if (biped is null)
            throw new InvalidOperationException(
                "The Spartan biped is no longer loaded. Rescan the mission.");

        return await SpawnVariantBodyCoreAsync(biped, variant, cancellationToken);
    }

    private RuntimeTagEntry? FindCachedCharacter(RuntimeTagEntry characterTag)
        => _tags.FirstOrDefault(tag =>
            tag.Index == characterTag.Index &&
            string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase));

    private RuntimeTagEntry? FindCachedBiped(RuntimeTagEntry bipedTag)
        => _tags.FirstOrDefault(tag =>
            tag.Index == bipedTag.Index &&
            string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tag.Name, bipedTag.Name, StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0);

    private async Task<ScriptExecutionResult> SpawnVariantBodyCoreAsync(
        RuntimeTagEntry biped,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken)
    {
        RuntimeTagFieldValue defaultVariant = ReadRoot(biped).FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The biped {biped.Name} does not expose its default model variant.");
        if (defaultVariant.Size != sizeof(uint) ||
            variant.StringIdBytes.Length != sizeof(uint))
            throw new InvalidDataException(
                "The selected model variant does not use a four-byte string ID.");

        byte[] original = _memory.ReadBytes(defaultVariant.Address, sizeof(uint));
        bool patched = !original.AsSpan().SequenceEqual(variant.StringIdBytes);
        if (patched)
            _memory.WriteVerified(defaultVariant.Address, variant.StringIdBytes);

        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamBipedVariantSpawn,
                $"{RuntimeTagMemoryService.BuildRuntimeDatum(biped):X8},{variant.StringId:X8}",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (result.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(result.Message);
            return result;
        }
        finally
        {
            // Keep the authored default patched through the native bridge's
            // deferred model-initialization window, but never leave the loaded
            // biped tag modified after this one spawn transaction.
            if (patched && _memory.IsConnected)
                _memory.WriteVerified(defaultVariant.Address, original);
        }
    }

    private async Task<WorldPoint> ReadPlayerPositionAsync(
        CancellationToken cancellationToken)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerPosition,
            "current",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int markerOffset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        string[] values = markerOffset < 0
            ? []
            : result.Message[(markerOffset + marker.Length)..]
                .Trim()
                .Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 3 ||
            !float.TryParse(
                values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(
                values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(
                values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            throw new InvalidDataException(
                "The game returned an invalid player position. Resume a campaign checkpoint and try again.");
        return new WorldPoint(x, y, z);
    }

    private SpawnPlan BuildPlan(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        WorldPoint playerPosition,
        int placementCount = 1,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = false,
        ushort? campaignTeam = null)
    {
        if (placementCount is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(placementCount));
        RuntimeTagEntry scenario = _tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "No loaded [scnr] tag with readable data was found. Load a campaign mission first.");
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(scenario);
        RuntimeTagFieldValue palette = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "character_palette_block")
            ?? throw new InvalidDataException(
                "The loaded scenario has no readable character palette.");
        RuntimeTagFieldValue squads = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "squads_block")
            ?? throw new InvalidDataException("The loaded scenario has no readable squads.");
        RuntimeTagFieldValue? objectives = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "objectives_block" &&
            field.CanOpenBlock);

        int hostileSquads = 0;
        int allySquads = 0;
        int squadsWithSpawnPoints = 0;
        int inspectedSpawnPoints = 0;
        int indexedSpawnPoints = 0;
        int cellBasedSpawnPoints = 0;
        SpawnTemplate? nearest = null;
        int nearestPriority = int.MaxValue;
        for (int squadIndex = 0; squadIndex < Math.Min(squads.ChildCount, 2048); squadIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> squad = ReadBlock(
                scenario, squads, squadIndex);
            RuntimeTagFieldValue? team = squad.FirstOrDefault(field =>
                field.Type == "short_enum" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "team",
                    StringComparison.OrdinalIgnoreCase));
            if (team is null ||
                !short.TryParse(
                    team.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out short teamIndex))
                continue;
            // Only player/human scaffolds are treated as allies for borrowing.
            // "default" (0) is a common Covenant authored fallback; team 7 is
            // covenant_player and must not be preferred for UNSC-friendly demos.
            bool isFriendlyTeam = teamIndex is 1 or 2;
            bool isHostile = !isFriendlyTeam;
            if (isHostile)
                hostileSquads++;
            else
                allySquads++;
            string squadName = ReadSquadName(squad);
            RuntimeTagFieldValue? objectiveField = squad.FirstOrDefault(field =>
                field.Type == "short_block_index" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "initial objective",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? taskField = squad.FirstOrDefault(field =>
                string.Equals(
                    CleanFieldName(field.Name),
                    "initial task",
                    StringComparison.OrdinalIgnoreCase));
            short objectiveIndex = ReadOptionalShort(squad, "initial objective") ?? -1;
            short taskIndex = ReadOptionalShort(squad, "initial task") ?? -1;
            bool hasCombatObjective = objectiveIndex >= 0;
            bool followsPlayer =
                followPlayer &&
                isFriendlyTeam &&
                objectives is not null &&
                SquadFollowsPlayer(scenario, squad, objectives);
            bool suppressesCombat =
                objectives is not null &&
                TaskHasFlag(
                    scenario,
                    objectives,
                    objectiveIndex,
                    taskIndex,
                    TaskFlagSuppressCombat);
            RuntimeTagFieldValue? squadFlagsField = squad.FirstOrDefault(field =>
                field.Type == "long_flags" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "flags",
                    StringComparison.OrdinalIgnoreCase) &&
                field.Size >= 4);
            long taskFlagsAddress = objectives is null
                ? 0
                : ResolveTaskFlagsAddress(
                    scenario,
                    objectives,
                    objectiveIndex,
                    taskIndex);

            RuntimeTagFieldValue? spawnPoints = squad.FirstOrDefault(field =>
                field.ChildBlockDefinition == "spawn_points_block" &&
                field.CanOpenBlock);
            if (spawnPoints is null) continue;
            squadsWithSpawnPoints++;
            for (int pointIndex = 0;
                 pointIndex < Math.Min(spawnPoints.ChildCount, 256);
                 pointIndex++)
            {
                inspectedSpawnPoints++;
                IReadOnlyList<RuntimeTagFieldValue> point = ReadBlock(
                    scenario, spawnPoints, pointIndex);
                RuntimeTagFieldValue? characterType = point.FirstOrDefault(field =>
                    field.Type == "short_block_index" &&
                    field.Name.StartsWith("character type", StringComparison.OrdinalIgnoreCase));
                RuntimeTagFieldValue? position = point.FirstOrDefault(field =>
                    field.Type == "real_point_3d" &&
                    field.Name.StartsWith("position", StringComparison.OrdinalIgnoreCase));
                RuntimeTagFieldValue? actorVariant = point.FirstOrDefault(field =>
                    field.Type == "string_id" &&
                    string.Equals(
                        CleanFieldName(field.Name),
                        "actor variant name",
                        StringComparison.OrdinalIgnoreCase));
                if (characterType is null || position is null ||
                    actorVariant is null || actorVariant.Size != 4)
                    continue;
                short paletteIndex = ReadInt16(characterType.Address);
                if (paletteIndex < 0)
                {
                    RuntimeTagFieldValue? cellIndexField = point.FirstOrDefault(field =>
                        field.Type == "custom_short_block_index" &&
                        field.Name.StartsWith("cell", StringComparison.OrdinalIgnoreCase));
                    if (cellIndexField is null) continue;
                    short cellIndex = ReadInt16(cellIndexField.Address);
                    if (cellIndex < 0) continue;
                    paletteIndex = FindCellPaletteIndex(scenario, squad, cellIndex);
                    if (paletteIndex < 0) continue;
                    cellBasedSpawnPoints++;
                }
                else
                {
                    indexedSpawnPoints++;
                }
                if (paletteIndex >= palette.ChildCount) continue;

                RuntimeTagFieldValue? reference = ReadBlock(
                    scenario, palette, paletteIndex).FirstOrDefault(field =>
                        field.IsTagReference);
                if (reference is null) continue;
                RuntimeTagEntry? sourceCharacter = _tags.FirstOrDefault(tag =>
                    tag.Index == reference.ReferencedTagIndex &&
                    string.Equals(
                        tag.Group,
                        "char",
                        StringComparison.OrdinalIgnoreCase));
                if (sourceCharacter is null ||
                    sourceCharacter.Name.Contains(
                        @"\null\",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                WorldPoint templatePosition = ReadPoint(position.Address);
                double distanceSquared =
                    Math.Pow(templatePosition.X - playerPosition.X, 2) +
                    Math.Pow(templatePosition.Y - playerPosition.Y, 2) +
                    Math.Pow(templatePosition.Z - playerPosition.Z, 2);
                bool exactCharacter =
                    sourceCharacter.Index == choice.CharacterTag.Index;
                bool sameCharacterFamily = string.Equals(
                    CharacterFamily(sourceCharacter.Name),
                    CharacterFamily(choice.CharacterTag.Name),
                    StringComparison.OrdinalIgnoreCase);
                // Prefer a scaffold that already matches the intended birth
                // allegiance. Old logic ranked unmatched hostile (2) above
                // unmatched friendly (3), so allegiance demos kept borrowing
                // Covenant squads even when spawning as Player.
                bool preferFriendlyScaffold =
                    followPlayer ||
                    campaignTeam is 1 or 2;
                bool preferHostileScaffold =
                    campaignTeam is ushort explicitTeam &&
                    explicitTeam is not (1 or 2);
                int priority = exactCharacter ? 0 : sameCharacterFamily ? 1 : 2;
                if (preferFriendlyScaffold)
                {
                    if (!isFriendlyTeam)
                        priority += 20;
                    // Allies need combat hooks for attack desire. Still avoid
                    // borrowing a fighting Covenant wave when spawning friendlies.
                    if (hasCombatObjective)
                        priority += isFriendlyTeam ? -5 : 12;
                    else if (isFriendlyTeam)
                        priority += 4;
                    if (IsDedicatedName(squadName, DedicatedAllySquadName))
                        priority -= 50;
                    if (IsDedicatedName(squadName, DedicatedHostileSquadName))
                        priority += 40;
                }
                else if (preferHostileScaffold)
                {
                    if (isFriendlyTeam)
                        priority += 20;
                    // Hostiles need combat hooks for attack desire; idle
                    // scaffolds spawn as standing props.
                    if (hasCombatObjective)
                        priority -= 8;
                    else
                        priority += 6;
                    if (IsDedicatedName(squadName, DedicatedHostileSquadName))
                        priority -= 50;
                    if (IsDedicatedName(squadName, DedicatedAllySquadName))
                        priority += 40;
                }
                else if (!isHostile)
                {
                    priority += 1;
                }
                // Task "suppress combat" freezes shooting desire — never prefer it.
                if (suppressesCombat)
                    priority += 30;
                // Native fireteam already handles follow. Preferring authored
                // follow tasks often selects suppress-combat companion orders.
                if (followsPlayer)
                    priority += preferFriendlyScaffold ? 8 : -10;
                if (nearest is null ||
                    priority < nearestPriority ||
                    (priority == nearestPriority &&
                     distanceSquared < nearest.DistanceSquared))
                {
                    nearestPriority = priority;
                    nearest = new SpawnTemplate(
                        squadIndex,
                        teamIndex,
                        squadName,
                        objectiveIndex,
                        team.Address,
                        reference.Address,
                        position.Address,
                        actorVariant.Address,
                        objectiveField is { Size: >= 2 } ? objectiveField.Address : 0,
                        taskField is { Size: >= 2 } ? taskField.Address : 0,
                        taskFlagsAddress,
                        squadFlagsField?.Address ?? 0,
                        distanceSquared);
                }
            }
        }

        if (nearest is null)
        {
            throw new InvalidOperationException(
                (placementCount > 1
                    ? $"No scenario squad has {placementCount} usable spawn points in the loaded mission. "
                    : "No scenario squad has a usable spawn point in the loaded mission area. ") +
                $"Inspected {squads.ChildCount:N0} squads: {allySquads:N0} ally / {hostileSquads:N0} hostile, " +
                $"{squadsWithSpawnPoints:N0} with spawn-point blocks, " +
                $"{inspectedSpawnPoints:N0} spawn points, and {indexedSpawnPoints:N0} with " +
                $"a direct character-palette index ({cellBasedSpawnPoints:N0} resolved through cells).");
        }

        bool preferFriendly =
            followPlayer || campaignTeam is 1 or 2;
        SpawnScaffoldDiagnosis diagnosis = BuildScaffoldDiagnosis(
            nearest,
            preferFriendly,
            allySquads,
            hostileSquads,
            followPlayer);
        // Native fireteam is squad-wide. Only hm_ally should follow; borrowing
        // a mission squad would drag that whole encounter onto the player.
        bool fireteamFollow = diagnosis.FireteamFollow;

        if (followPlayer || placementCount > 1 || campaignTeam is not null)
        {
            // actor_new inherits the borrowed scenario squad's team at birth.
            // Explicit campaignTeam wins; otherwise friendly companions force
            // Player (1) and hostile batches force Covenant (3).
            ushort teamOverride = campaignTeam
                ?? (followPlayer ? (ushort)1 : (ushort)3);
            var parts = new List<string>(4 + placementCount * 3)
            {
                nearest.SquadIndex.ToString("X4", CultureInfo.InvariantCulture),
                nearest.TeamAddress.ToString("X16", CultureInfo.InvariantCulture),
                teamOverride.ToString("X4", CultureInfo.InvariantCulture),
            };
            for (int index = 0; index < placementCount; index++)
            {
                parts.Add(nearest.ReferenceAddress.ToString("X16", CultureInfo.InvariantCulture));
                parts.Add(nearest.PositionAddress.ToString("X16", CultureInfo.InvariantCulture));
                parts.Add(nearest.VariantAddress.ToString("X16", CultureInfo.InvariantCulture));
            }
            parts.Add(Convert.ToHexString(_memory.BuildTagReference(choice.CharacterTag)));
            parts.Add(Convert.ToHexString(variant.StringIdBytes));
            if (weapon is not null)
                parts.Add(weapon.Datum.ToString("X8", CultureInfo.InvariantCulture));
            return new SpawnPlan(
                AppendFormationOffset(
                    string.Join(',', parts),
                    formationOffsetX,
                    formationOffsetY,
                    fireteamFollow),
                nearest,
                diagnosis);
        }

        if (placementCount > 1)
            throw new InvalidOperationException(
                $"No scenario squad has {placementCount} usable spawn points. " +
                "Try this action in a larger encounter area or use Spawn ahead of player.");
        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{nearest.SquadIndex:X4},{nearest.ReferenceAddress:X16}," +
            $"{nearest.PositionAddress:X16}," +
            $"{nearest.VariantAddress:X16}," +
            $"{Convert.ToHexString(_memory.BuildTagReference(choice.CharacterTag))}," +
            $"{Convert.ToHexString(variant.StringIdBytes)}");
        if (weapon is not null)
            payload += "," +
                weapon.Datum.ToString("X8", CultureInfo.InvariantCulture);
        return new SpawnPlan(
            AppendFormationOffset(
                payload,
                formationOffsetX,
                formationOffsetY,
                fireteamFollow),
            nearest,
            diagnosis);
    }

    private static SpawnScaffoldDiagnosis BuildScaffoldDiagnosis(
        SpawnTemplate template,
        bool wantedFriendly,
        int allyScaffoldCount,
        int hostileScaffoldCount,
        bool followPlayer)
    {
        bool usedDedicated =
            (wantedFriendly &&
             IsDedicatedName(template.SquadName, DedicatedAllySquadName)) ||
            (!wantedFriendly &&
             IsDedicatedName(template.SquadName, DedicatedHostileSquadName));
        bool usedHostileFallback =
            wantedFriendly && template.TeamIndex is not (1 or 2);
        bool fireteamFollow =
            followPlayer &&
            wantedFriendly &&
            usedDedicated;
        string name = string.IsNullOrWhiteSpace(template.SquadName)
            ? $"#{template.SquadIndex}"
            : template.SquadName;
        string summary;
        if (usedDedicated)
        {
            summary =
                $"scaffold=dedicated:{name} team={template.TeamIndex} " +
                $"objective={template.ObjectiveIndex} " +
                $"ally={allyScaffoldCount} hostile={hostileScaffoldCount}";
        }
        else if (usedHostileFallback)
        {
            summary =
                $"Borrowed hostile scaffold {name} " +
                $"(authored team {template.TeamIndex}, objective {template.ObjectiveIndex}); " +
                $"no BuildPlan-usable ally scaffold in this mission " +
                $"(ally={allyScaffoldCount}, hostile={hostileScaffoldCount}). " +
                $"Reinstall built-in mod if {DedicatedAllySquadName} is missing or has " +
                $"character type/cell=-1 spawn points.";
        }
        else
        {
            summary =
                $"scaffold={name} team={template.TeamIndex} " +
                $"objective={template.ObjectiveIndex} " +
                $"ally={allyScaffoldCount} hostile={hostileScaffoldCount}";
        }

        if (wantedFriendly)
        {
            summary += fireteamFollow
                ? " follow=hm_ally"
                : " follow=off";
        }

        return new SpawnScaffoldDiagnosis(
            template.SquadIndex,
            template.SquadName,
            template.TeamIndex,
            template.ObjectiveIndex,
            wantedFriendly,
            usedDedicated,
            usedHostileFallback,
            fireteamFollow,
            allyScaffoldCount,
            hostileScaffoldCount,
            summary);
    }

    private static string AppendFormationOffset(
        string payload,
        float x,
        float y,
        bool followPlayer)
    {
        if (x == 0 && y == 0 && !followPlayer)
            return payload;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{payload};{x:R};{y:R};{(followPlayer ? 1 : 0)}");
    }

    private bool SquadHasBuildPlanUsableSpawnPoint(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagFieldValue> squad,
        RuntimeTagFieldValue palette,
        RuntimeTagFieldValue spawnPoints)
    {
        for (int pointIndex = 0;
             pointIndex < Math.Min(spawnPoints.ChildCount, 256);
             pointIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> point = ReadBlock(
                scenario, spawnPoints, pointIndex);
            RuntimeTagFieldValue? characterType = point.FirstOrDefault(field =>
                field.Type == "short_block_index" &&
                field.Name.StartsWith("character type", StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? position = point.FirstOrDefault(field =>
                field.Type == "real_point_3d" &&
                field.Name.StartsWith("position", StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? actorVariant = point.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "actor variant name",
                    StringComparison.OrdinalIgnoreCase));
            if (characterType is null || position is null ||
                actorVariant is null || actorVariant.Size != 4)
                continue;
            short paletteIndex = ReadInt16(characterType.Address);
            if (paletteIndex < 0)
            {
                RuntimeTagFieldValue? cellIndexField = point.FirstOrDefault(field =>
                    field.Type == "custom_short_block_index" &&
                    field.Name.StartsWith("cell", StringComparison.OrdinalIgnoreCase));
                if (cellIndexField is null) continue;
                short cellIndex = ReadInt16(cellIndexField.Address);
                if (cellIndex < 0) continue;
                paletteIndex = FindCellPaletteIndex(scenario, squad, cellIndex);
                if (paletteIndex < 0) continue;
            }
            if (paletteIndex >= palette.ChildCount) continue;
            RuntimeTagFieldValue? reference = ReadBlock(
                scenario, palette, paletteIndex).FirstOrDefault(field =>
                    field.IsTagReference);
            if (reference is null) continue;
            RuntimeTagEntry? sourceCharacter = _tags.FirstOrDefault(tag =>
                tag.Index == reference.ReferencedTagIndex &&
                string.Equals(
                    tag.Group,
                    "char",
                    StringComparison.OrdinalIgnoreCase));
            if (sourceCharacter is null ||
                sourceCharacter.Name.Contains(
                    @"\null\",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }

    private short FindCellPaletteIndex(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagFieldValue> squad,
        short cellIndex)
    {
        foreach (RuntimeTagFieldValue cells in squad.Where(field =>
                     field.ChildBlockDefinition == "cell_block" &&
                     field.CanOpenBlock &&
                     cellIndex < field.ChildCount))
        {
            IReadOnlyList<RuntimeTagFieldValue> cell = ReadBlock(
                scenario, cells, cellIndex);
            RuntimeTagFieldValue? choices = cell.FirstOrDefault(field =>
                field.ChildBlockDefinition == "character_palette_choice_block" &&
                field.CanOpenBlock);
            if (choices is null) continue;

            for (int choiceIndex = 0;
                 choiceIndex < Math.Min(choices.ChildCount, 128);
                 choiceIndex++)
            {
                RuntimeTagFieldValue? characterType = ReadBlock(
                    scenario, choices, choiceIndex).FirstOrDefault(field =>
                        field.Type == "short_block_index" &&
                        field.Name.StartsWith(
                            "character type",
                            StringComparison.OrdinalIgnoreCase));
                if (characterType is null) continue;
                short paletteIndex = ReadInt16(characterType.Address);
                if (paletteIndex >= 0) return paletteIndex;
            }
        }
        return -1;
    }

    private static string CharacterFamily(string path)
    {
        string normalized = path.Replace('\\', '/');
        const string marker = "objects/characters/";
        int markerIndex = normalized.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return string.Empty;
        int familyStart = markerIndex + marker.Length;
        int familyEnd = normalized.IndexOf('/', familyStart);
        return familyEnd < 0
            ? normalized[familyStart..]
            : normalized[familyStart..familyEnd];
    }

    private RuntimeTagEntry RequireFriendlyDonorCharacter() =>
        FindFriendlyDonorCharacter()
        ?? throw new InvalidOperationException(
            "No friendly [char] AI donor is loaded in this mission. " +
            "Prefer ai/generic.character, otherwise a common marine/ODST/crewman AI tag. " +
            "Unique story characters such as Johnson are not used.");

    /// <summary>
    /// Picks a generic friendly AI shell. Prefers <c>ai/generic</c>, then common
    /// UNSC ranks. Unique story characters (Johnson, Keyes, Noble Team, etc.)
    /// are excluded because mission scripts often respawn or specially voice
    /// those identities.
    /// </summary>
    private RuntimeTagEntry? FindFriendlyDonorCharacter() =>
        _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.Name.Contains(@"\ai\", StringComparison.OrdinalIgnoreCase) &&
                IsFriendlyDonorCandidate(tag.Name) &&
                !IsStoryExclusiveCharacter(tag.Name) &&
                HasUnitReference(tag))
            .OrderByDescending(tag => FriendlyDonorScore(tag.Name))
            .ThenBy(tag => tag.Name.Length)
            .FirstOrDefault();

    private bool HasUnitReference(RuntimeTagEntry character) =>
        ReadRoot(character).Any(field =>
            field.IsTagReference &&
            string.Equals(
                CleanFieldName(field.Name),
                "unit",
                StringComparison.OrdinalIgnoreCase));

    private static bool IsFriendlyDonorCandidate(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (IsAiGenericCharacter(value))
            return true;
        return PathContainsAny(
            value,
            "marine",
            "odst",
            "crewman",
            "pilot",
            "trooper",
            "army");
    }

    private static bool PathContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static bool IsAiGenericCharacter(string normalizedPath) =>
        normalizedPath.EndsWith("/ai/generic", StringComparison.Ordinal) ||
        normalizedPath.Contains("/ai/generic/", StringComparison.Ordinal) ||
        (normalizedPath.Contains("/ai/", StringComparison.Ordinal) &&
         normalizedPath.EndsWith("/generic", StringComparison.Ordinal));

    private static bool IsStoryExclusiveCharacter(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        return PathContainsAny(
            value,
            "johnson",
            "keyes",
            "miranda",
            "halsey",
            "cortana",
            "spark",
            "guilty",
            "carter",
            "kat",
            "emile",
            "jun",
            "jorge",
            "noble",
            "buck",
            "dare",
            "dutch",
            "mickey",
            "romeo",
            "arbiter",
            "halfjaw",
            "masterchief",
            "master_chief",
            "/chief/",
            "chief_");
    }

    private static int FriendlyDonorScore(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        string leaf = value[(value.LastIndexOf('/') + 1)..];
        // Engine-provided blank AI shell: no story respawn hooks, intended for
        // retargeting unit/combat/voice onto another body.
        if (IsAiGenericCharacter(value) || leaf is "generic")
            return 200;
        if (leaf is "marine" or "marine_odst" or "odst")
            return 100;
        if (leaf.Contains("odst", StringComparison.Ordinal))
            return 95;
        if (leaf.Contains("crewman", StringComparison.Ordinal))
            return 90;
        if (leaf.StartsWith("marine", StringComparison.Ordinal))
            return 85;
        if (leaf.Contains("trooper", StringComparison.Ordinal))
            return 80;
        if (leaf.Contains("pilot", StringComparison.Ordinal))
            return 70;
        if (leaf.Contains("army", StringComparison.Ordinal))
            return 60;
        return 10;
    }

    private bool SquadFollowsPlayer(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagFieldValue> squad,
        RuntimeTagFieldValue objectives)
    {
        RuntimeTagFieldValue? objectiveField = squad.FirstOrDefault(field =>
            field.Type == "short_block_index" &&
            string.Equals(
                CleanFieldName(field.Name),
                "initial objective",
                StringComparison.OrdinalIgnoreCase));
        RuntimeTagFieldValue? taskField = squad.FirstOrDefault(field =>
            string.Equals(
                CleanFieldName(field.Name),
                "initial task",
                StringComparison.OrdinalIgnoreCase));
        if (objectiveField is null || taskField is null)
            return false;
        short objectiveIndex = ReadInt16(objectiveField.Address);
        short taskIndex = ReadInt16(taskField.Address);
        if (objectiveIndex < 0 || objectiveIndex >= objectives.ChildCount ||
            taskIndex < 0)
            return false;

        RuntimeTagFieldValue? tasks = ReadBlock(
            scenario,
            objectives,
            objectiveIndex).FirstOrDefault(field =>
                field.ChildBlockDefinition == "tasks_block" &&
                field.CanOpenBlock);
        if (tasks is null || taskIndex >= tasks.ChildCount)
            return false;
        IReadOnlyList<RuntimeTagFieldValue> task =
            ReadBlock(scenario, tasks, taskIndex);
        RuntimeTagFieldValue? follow = task.FirstOrDefault(field =>
            field.Type == "short_enum" &&
            string.Equals(
                CleanFieldName(field.Name),
                "follow",
                StringComparison.OrdinalIgnoreCase));
        if (follow is null)
            return false;
        short followMode = ReadInt16(follow.Address);
        return followMode is 1 or 3 or 4;
    }

    private IReadOnlyList<MemoryPatch> ApplyCombatDonorProperties(
        RuntimeTagEntry johnson,
        RuntimeTagEntry donor)
    {
        var patches = new List<MemoryPatch>();
        try
        {
            RuntimeTagFieldValue? johnsonStyle = ReadRoot(johnson).FirstOrDefault(
                field =>
                    field.IsTagReference &&
                    string.Equals(
                        CleanFieldName(field.Name),
                        "style",
                        StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? donorStyle = ReadRoot(donor).FirstOrDefault(
                field =>
                    field.IsTagReference &&
                    string.Equals(
                        CleanFieldName(field.Name),
                        "style",
                        StringComparison.OrdinalIgnoreCase));
            if (johnsonStyle is not null &&
                donorStyle is not null &&
                johnsonStyle.Size == donorStyle.Size &&
                johnsonStyle.Size > 0)
            {
                byte[] original = _memory.ReadBytes(
                    johnsonStyle.Address,
                    johnsonStyle.Size);
                byte[] replacement = _memory.ReadBytes(
                    donorStyle.Address,
                    donorStyle.Size);
                if (!original.AsSpan().SequenceEqual(replacement))
                {
                    _memory.WriteVerified(johnsonStyle.Address, replacement);
                    patches.Add(new MemoryPatch(johnsonStyle.Address, original));
                }
            }

            string[] combatBlocks =
            [
                "character_engage_block",
                "character_charge_block",
                "character_weapons_block",
                "character_target_block",
                "character_firing_pattern_properties_block",
                "character_grenades_block",
            ];
            foreach (string blockDefinition in combatBlocks)
                patches.AddRange(
                    CloneMatchingBlockElements(johnson, donor, blockDefinition));
            // Voice uses nested dialogue tag refs; shallow-copying the parent
            // block only rewires pointers and often leaves the marine donor
            // udlg in place when layouts differ. Copy dialogue refs in-place.
            patches.AddRange(CloneVoiceDialogueReferences(johnson, donor));
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    /// <summary>
    /// Copies <c>udlg</c> tag references (and default dialogue effect ids) from
    /// <paramref name="source"/> into <paramref name="target"/>'s existing voice
    /// slots without relocating nested block pointers across tags.
    /// </summary>
    private IReadOnlyList<MemoryPatch> CloneVoiceDialogueReferences(
        RuntimeTagEntry target,
        RuntimeTagEntry source)
    {
        RuntimeTagFieldValue? targetProps = ReadRoot(target).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_voice_properties_block",
                StringComparison.OrdinalIgnoreCase));
        RuntimeTagFieldValue? sourceProps = ReadRoot(source).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_voice_properties_block",
                StringComparison.OrdinalIgnoreCase));
        if (targetProps is null ||
            sourceProps is null ||
            targetProps.ChildCount <= 0 ||
            sourceProps.ChildCount <= 0)
            return [];

        var patches = new List<MemoryPatch>();
        try
        {
            int propCount = Math.Min(targetProps.ChildCount, sourceProps.ChildCount);
            for (int propIndex = 0; propIndex < propCount; propIndex++)
            {
                IReadOnlyList<RuntimeTagFieldValue> targetFields =
                    ReadBlock(target, targetProps, propIndex);
                IReadOnlyList<RuntimeTagFieldValue> sourceFields =
                    ReadBlock(source, sourceProps, propIndex);

                RuntimeTagFieldValue? targetEffect = FindFieldByCleanName(
                    targetFields,
                    "default dialogue effect id");
                RuntimeTagFieldValue? sourceEffect = FindFieldByCleanName(
                    sourceFields,
                    "default dialogue effect id");
                if (targetEffect is not null &&
                    sourceEffect is not null &&
                    targetEffect.Size == sourceEffect.Size &&
                    targetEffect.Size > 0)
                {
                    byte[] original = _memory.ReadBytes(
                        targetEffect.Address,
                        targetEffect.Size);
                    byte[] replacement = _memory.ReadBytes(
                        sourceEffect.Address,
                        sourceEffect.Size);
                    if (!original.AsSpan().SequenceEqual(replacement))
                    {
                        _memory.WriteVerified(targetEffect.Address, replacement);
                        patches.Add(new MemoryPatch(targetEffect.Address, original));
                    }
                }

                RuntimeTagFieldValue? targetVoices = targetFields.FirstOrDefault(
                    field =>
                        field.CanOpenBlock &&
                        string.Equals(
                            field.ChildBlockDefinition,
                            "character_voice_block",
                            StringComparison.OrdinalIgnoreCase));
                RuntimeTagFieldValue? sourceVoices = sourceFields.FirstOrDefault(
                    field =>
                        field.CanOpenBlock &&
                        string.Equals(
                            field.ChildBlockDefinition,
                            "character_voice_block",
                            StringComparison.OrdinalIgnoreCase));
                if (targetVoices is null ||
                    sourceVoices is null ||
                    targetVoices.ChildCount <= 0 ||
                    sourceVoices.ChildCount <= 0)
                    continue;

                int voiceCount = Math.Min(
                    Math.Min(targetVoices.ChildCount, sourceVoices.ChildCount),
                    16);
                for (int voiceIndex = 0; voiceIndex < voiceCount; voiceIndex++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> targetVoice =
                        ReadBlock(target, targetVoices, voiceIndex);
                    IReadOnlyList<RuntimeTagFieldValue> sourceVoice =
                        ReadBlock(source, sourceVoices, voiceIndex);
                    RuntimeTagFieldValue? targetDialogue = targetVoice.FirstOrDefault(
                        field =>
                            field.IsTagReference &&
                            CleanFieldName(field.Name)
                                .StartsWith("dialogue", StringComparison.OrdinalIgnoreCase));
                    RuntimeTagFieldValue? sourceDialogue = sourceVoice.FirstOrDefault(
                        field =>
                            field.IsTagReference &&
                            CleanFieldName(field.Name)
                                .StartsWith("dialogue", StringComparison.OrdinalIgnoreCase));
                    if (targetDialogue is null ||
                        sourceDialogue is null ||
                        targetDialogue.Size != sourceDialogue.Size ||
                        targetDialogue.Size <= 0)
                        continue;

                    byte[] original = _memory.ReadBytes(
                        targetDialogue.Address,
                        targetDialogue.Size);
                    byte[] replacement = _memory.ReadBytes(
                        sourceDialogue.Address,
                        sourceDialogue.Size);
                    if (original.AsSpan().SequenceEqual(replacement))
                        continue;
                    _memory.WriteVerified(targetDialogue.Address, replacement);
                    patches.Add(new MemoryPatch(targetDialogue.Address, original));
                }
            }
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    private RuntimeTagEntry? FindCharacterWithVoice(int preferredActorType)
    {
        return _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                !IsStoryExclusiveCharacter(tag.Name) &&
                HasVoiceDialogue(tag))
            .OrderByDescending(tag =>
            {
                int? type = TryReadCharacterActorType(tag);
                if (type == preferredActorType)
                    return 100;
                if (type == ActorTypeSpartan)
                    return 80;
                if (type == ActorTypeMarine)
                    return 60;
                return 10;
            })
            .ThenBy(tag => tag.Name.Length)
            .FirstOrDefault();
    }

    private bool HasVoiceDialogue(RuntimeTagEntry character)
    {
        RuntimeTagFieldValue? props = ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            field.ChildCount > 0 &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_voice_properties_block",
                StringComparison.OrdinalIgnoreCase));
        if (props is null)
            return false;
        IReadOnlyList<RuntimeTagFieldValue> fields = ReadBlock(character, props, 0);
        RuntimeTagFieldValue? voices = fields.FirstOrDefault(field =>
            field.CanOpenBlock &&
            field.ChildCount > 0 &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_voice_block",
                StringComparison.OrdinalIgnoreCase));
        if (voices is null)
            return false;
        return ReadBlock(character, voices, 0).Any(field =>
            field.IsTagReference &&
            CleanFieldName(field.Name)
                .StartsWith("dialogue", StringComparison.OrdinalIgnoreCase) &&
            field.ReferencedTagIndex >= 0);
    }

    private IReadOnlyList<MemoryPatch> CloneMatchingBlockElements(
        RuntimeTagEntry target,
        RuntimeTagEntry source,
        string blockDefinition)
    {
        RuntimeTagFieldValue? targetBlock = ReadRoot(target).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                blockDefinition,
                StringComparison.OrdinalIgnoreCase));
        RuntimeTagFieldValue? sourceBlock = ReadRoot(source).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                blockDefinition,
                StringComparison.OrdinalIgnoreCase));
        if (targetBlock is null ||
            sourceBlock is null ||
            targetBlock.ChildCount <= 0 ||
            sourceBlock.ChildCount <= 0 ||
            targetBlock.ChildElementSize <= 0 ||
            targetBlock.ChildElementSize != sourceBlock.ChildElementSize)
            return [];

        int count = Math.Min(
            Math.Min(targetBlock.ChildCount, sourceBlock.ChildCount),
            16);
        var patches = new List<MemoryPatch>(count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                long targetAddress =
                    targetBlock.ChildAddress +
                    (long)index * targetBlock.ChildElementSize;
                long sourceAddress =
                    sourceBlock.ChildAddress +
                    (long)index * sourceBlock.ChildElementSize;
                byte[] original = _memory.ReadBytes(
                    targetAddress,
                    targetBlock.ChildElementSize);
                byte[] replacement = _memory.ReadBytes(
                    sourceAddress,
                    sourceBlock.ChildElementSize);
                if (original.AsSpan().SequenceEqual(replacement))
                    continue;
                _memory.WriteVerified(targetAddress, replacement);
                patches.Add(new MemoryPatch(targetAddress, original));
            }
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    private IReadOnlyList<MemoryPatch> ApplyAuthoredSpartanShields(
        RuntimeTagEntry johnson)
    {
        RuntimeTagFieldValue? johnsonVitality = FindVitalityBlock(johnson);
        if (johnsonVitality is null)
            return [];
        IReadOnlyList<RuntimeTagFieldValue> johnsonFields =
            ReadBlock(johnson, johnsonVitality, 0);
        RuntimeTagFieldValue? currentShield = FindFieldByCleanName(
            johnsonFields,
            "normal shield vitality");
        if (currentShield is not null &&
            ReadSingle(currentShield.Address) > 0)
            return [];

        RuntimeTagEntry? donor = _tags
            .Where(tag =>
                tag.Index != johnson.Index &&
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            .Select(tag => (Tag: tag, Vitality: FindVitalityBlock(tag)))
            .Where(candidate => candidate.Vitality is not null)
            .Select(candidate => (
                candidate.Tag,
                Fields: ReadBlock(candidate.Tag, candidate.Vitality!, 0)))
            .Where(candidate =>
            {
                RuntimeTagFieldValue? field = FindFieldByCleanName(
                    candidate.Fields,
                    "normal shield vitality");
                return field is not null && ReadSingle(field.Address) > 0;
            })
            .OrderByDescending(candidate => ShieldDonorScore(candidate.Tag.Name))
            .ThenBy(candidate => candidate.Tag.Name.Length)
            .Select(candidate => candidate.Tag)
            .FirstOrDefault();
        if (donor is null)
            return [];

        RuntimeTagFieldValue donorVitality =
            FindVitalityBlock(donor)
            ?? throw new InvalidDataException(
                "The selected shield donor no longer exposes vitality data.");
        IReadOnlyList<RuntimeTagFieldValue> donorFields =
            ReadBlock(donor, donorVitality, 0);
        string[] copiedFields =
        [
            "normal shield vitality",
            "legendary shield vitality",
            "shield recharge delay time",
            "shield recharge time",
        ];
        var patches = new List<MemoryPatch>();
        try
        {
            foreach (string fieldName in copiedFields)
            {
                RuntimeTagFieldValue? target =
                    FindFieldByCleanName(johnsonFields, fieldName);
                RuntimeTagFieldValue? source =
                    FindFieldByCleanName(donorFields, fieldName);
                if (target is null || source is null ||
                    target.Size <= 0 || target.Size != source.Size)
                    continue;
                byte[] original = _memory.ReadBytes(target.Address, target.Size);
                byte[] replacement = _memory.ReadBytes(source.Address, source.Size);
                if (original.AsSpan().SequenceEqual(replacement))
                    continue;
                _memory.WriteVerified(target.Address, replacement);
                patches.Add(new MemoryPatch(target.Address, original));
            }
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    private IReadOnlyList<MemoryPatch> ApplyAuthoredWeapon(
        RuntimeTagEntry character,
        RuntimeTagEntry weapon)
    {
        RuntimeTagFieldValue? weapons = ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_weapons_block",
                StringComparison.OrdinalIgnoreCase));
        if (weapons is null || weapons.ChildCount <= 0)
            throw new InvalidDataException(
                $"The character {character.Name} has no authored weapon slots.");

        byte[] replacement = _memory.BuildTagReference(weapon);
        var patches = new List<MemoryPatch>();
        try
        {
            for (int index = 0;
                 index < Math.Min(weapons.ChildCount, 100);
                 index++)
            {
                RuntimeTagFieldValue? reference = ReadBlock(
                    character,
                    weapons,
                    index).FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "weapon",
                            StringComparison.OrdinalIgnoreCase));
                if (reference is null || reference.Size != replacement.Length)
                    continue;
                byte[] original = _memory.ReadBytes(
                    reference.Address,
                    reference.Size);
                if (original.AsSpan().SequenceEqual(replacement))
                    continue;
                _memory.WriteVerified(reference.Address, replacement);
                patches.Add(new MemoryPatch(reference.Address, original));
            }
            if (patches.Count == 0)
            {
                bool alreadySelected = Enumerable.Range(
                        0,
                        Math.Min(weapons.ChildCount, 100))
                    .Select(index => ReadBlock(character, weapons, index))
                    .SelectMany(fields => fields)
                    .Where(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "weapon",
                            StringComparison.OrdinalIgnoreCase))
                    .Any(field => _memory.ReadBytes(field.Address, field.Size)
                        .AsSpan().SequenceEqual(replacement));
                if (!alreadySelected)
                    throw new InvalidDataException(
                        $"The character {character.Name} exposes no writable authored weapon reference.");
            }
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    private RuntimeTagFieldValue? FindVitalityBlock(RuntimeTagEntry character) =>
        ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            field.ChildCount > 0 &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_vitality_block",
                StringComparison.OrdinalIgnoreCase));

    private static RuntimeTagFieldValue? FindFieldByCleanName(
        IEnumerable<RuntimeTagFieldValue> fields,
        string name) =>
        fields.FirstOrDefault(field =>
            string.Equals(
                CleanFieldName(field.Name),
                name,
                StringComparison.OrdinalIgnoreCase));

    private float ReadSingle(long address) =>
        BinaryPrimitives.ReadSingleLittleEndian(
            _memory.ReadBytes(address, sizeof(float)));

    private static int ShieldDonorScore(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (value.Contains("masterchief", StringComparison.Ordinal) ||
            value.Contains("master_chief", StringComparison.Ordinal) ||
            value.Contains("spartan", StringComparison.Ordinal))
            return 3;
        if (value.Contains("elite", StringComparison.Ordinal))
            return 2;
        return 1;
    }

    private static bool IsSpartanCompatibleWeapon(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (value.Contains("turret", StringComparison.Ordinal) ||
            value.Contains("mounted", StringComparison.Ordinal) ||
            value.Contains("grenade", StringComparison.Ordinal) ||
            value.Contains("equipment", StringComparison.Ordinal) ||
            value.Contains("bomb", StringComparison.Ordinal))
            return false;
        return new[]
        {
            "assault_rifle", "battle_rifle", "shotgun", "sniper",
            "smg", "rocket", "pistol", "magnum", "plasma_rifle",
            "plasma_pistol", "needler", "carbine", "beam_rifle",
            "brute_shot",
        }.Any(name => value.Contains(name, StringComparison.Ordinal));
    }

    private IReadOnlyList<AiWeaponChoice> ReadCompatibleWeapons(
        RuntimeTagEntry character)
    {
        RuntimeTagFieldValue? weapons = ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_weapons_block",
                StringComparison.OrdinalIgnoreCase));
        if (weapons is null)
            return [];

        var results = new List<AiWeaponChoice>();
        for (int index = 0;
             index < Math.Min(weapons.ChildCount, 100);
             index++)
        {
            RuntimeTagFieldValue? reference = ReadBlock(
                character,
                weapons,
                index).FirstOrDefault(field =>
                    field.IsTagReference &&
                    string.Equals(
                        CleanFieldName(field.Name),
                        "weapon",
                        StringComparison.OrdinalIgnoreCase));
            if (reference is null)
                continue;
            RuntimeTagEntry? weapon = _tags.FirstOrDefault(tag =>
                tag.Index == reference.ReferencedTagIndex &&
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0);
            if (weapon is not null)
                results.Add(new AiWeaponChoice(weapon));
        }
        return results
            .GroupBy(item => item.WeaponTag.Index)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<SpawnVariantChoice> ReadVariants(RuntimeTagEntry character)
    {
        var results = new List<SpawnVariantChoice>();
        IReadOnlyList<RuntimeTagFieldValue> root;
        try
        {
            root = ReadRoot(character);
        }
        catch
        {
            return results;
        }

        RuntimeTagFieldValue? variants = root.FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_variants_block",
                StringComparison.OrdinalIgnoreCase));
        if (variants is null)
        {
            return [new SpawnVariantChoice("Authored default", new byte[4], -1, -1)];
        }

        for (int index = 0; index < Math.Min(variants.ChildCount, 128); index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields;
            try
            {
                fields = ReadBlock(character, variants, index);
            }
            catch
            {
                continue;
            }
            RuntimeTagFieldValue? name = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "variant name",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? variantIndex = fields.FirstOrDefault(field =>
                field.Type == "short_integer" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "variant index",
                    StringComparison.OrdinalIgnoreCase));
            if (name is null || name.Size != 4 || variantIndex is null)
                continue;

            byte[] stringId = _memory.ReadBytes(name.Address, 4);
            short skinIndex = ReadInt16(variantIndex.Address);
            results.Add(new SpawnVariantChoice(
                skinIndex >= 0 ? $"Skin variant {skinIndex}" : "Authored default",
                stringId,
                skinIndex,
                index));
        }
        if (results.Count == 0)
            results.Add(new SpawnVariantChoice(
                "Authored default", new byte[4], -1, -1));
        return results
            .GroupBy(
                item => $"{item.StringId:X8}:{item.VariantIndex}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.VariantIndex)
            .ToArray();
    }

    private IReadOnlyList<ArmorSpawnChoice> ReadArmorChoices(out string status)
    {
        if (!_definitions.HasSchema("bipd") || !_definitions.HasSchema("hlmt"))
        {
            status = "The loaded tag definitions do not include [bipd] and [hlmt].";
            return [];
        }

        CustomizationCategory? armorCatalog = CustomizationCatalog.Categories
            .FirstOrDefault(category =>
                string.Equals(category.Group, "Armor", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    category.TagSegment,
                    "MasterChief",
                    StringComparison.OrdinalIgnoreCase));
        if (armorCatalog is null)
        {
            status = "Halo Meister's Master Chief armor catalog is unavailable.";
            return [];
        }

        var candidates = new List<(ArmorSpawnChoice Choice, int Score, string ModelPath)>();
        int usableBipeds = 0;
        int resolvedModels = 0;
        foreach (RuntimeTagEntry biped in _tags.Where(IsUsableBiped))
        {
            usableBipeds++;
            try
            {
                RuntimeTagFieldValue? modelReference = ReadRoot(biped)
                    .FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "model",
                            StringComparison.OrdinalIgnoreCase));
                RuntimeTagEntry? model = modelReference is null
                    ? null
                    : _tags.FirstOrDefault(tag =>
                        tag.Index == modelReference.ReferencedTagIndex &&
                        string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
                        tag.DataAddress > 0 &&
                        tag.RootCount > 0);
                if (model is null) continue;
                resolvedModels++;

                RuntimeTagFieldValue? variants = ReadRoot(model).FirstOrDefault(field =>
                    field.CanOpenBlock &&
                    string.Equals(
                        field.ChildBlockDefinition,
                        "model_variant_block",
                        StringComparison.OrdinalIgnoreCase));
                if (variants is null) continue;

                var choices = new List<SpawnVariantChoice>();
                foreach (CosmeticChoice cosmetic in armorCatalog.Choices)
                {
                    if (!CustomizationCatalog.TryGetMasterChiefModelVariantIndex(
                            cosmetic,
                            out int index) ||
                        index < 0 ||
                        index >= variants.ChildCount)
                        continue;
                    RuntimeTagFieldValue? name = ReadBlock(model, variants, index)
                        .FirstOrDefault(field =>
                            field.Type == "string_id" &&
                            string.Equals(
                                CleanFieldName(field.Name),
                                "name",
                                StringComparison.OrdinalIgnoreCase));
                    if (name?.Size != sizeof(uint)) continue;
                    choices.Add(new SpawnVariantChoice(
                        cosmetic.Name,
                        _memory.ReadBytes(name.Address, name.Size),
                        checked((short)index),
                        index,
                        cosmetic.ImageUri));
                }
                if (choices.Count == 0) continue;

                int score =
                    SpartanNameScore(biped.Name) +
                    SpartanNameScore(model.Name) +
                    Math.Min(choices.Count, 40);
                if (string.Equals(
                        biped.Name.Replace('/', '\\'),
                        @"objects\characters\spartans\spartans",
                        StringComparison.OrdinalIgnoreCase))
                    score += 1000;
                if (score <= choices.Count) continue;
                candidates.Add((
                    new ArmorSpawnChoice(biped, choices),
                    score,
                    model.Name));
            }
            catch
            {
                // One malformed or partially published biped must not hide a
                // usable Spartan model elsewhere in the live tag table.
            }
        }

        (ArmorSpawnChoice Choice, int Score, string ModelPath)[] ranked = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Choice.Variants.Count)
            .ThenBy(candidate => candidate.Choice.TagPath.Length)
            .ToArray();
        if (ranked.Length == 0)
        {
            status =
                $"Scanned {usableBipeds:N0} usable [bipd] tags and resolved " +
                $"{resolvedModels:N0} [hlmt] models, but none exposed a recognized " +
                "Master Chief/Spartan armor model.";
            return [];
        }

        (ArmorSpawnChoice Choice, int Score, string ModelPath) selected = ranked[0];
        status =
            $"Resolved {selected.Choice.Variants.Count:N0} armor variants from " +
            $"{selected.Choice.TagPath} -> {selected.ModelPath}.";
        return [selected.Choice];
    }

    private static bool IsUsableBiped(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0 &&
        !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase) &&
        !tag.Name.Contains("/stimuli/", StringComparison.OrdinalIgnoreCase);

    private static int SpartanNameScore(string name)
    {
        string value = name.Replace('\\', '/').ToLowerInvariant();
        int score = 0;
        if (value.Contains("masterchief", StringComparison.Ordinal) ||
            value.Contains("master_chief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("chief", StringComparison.Ordinal)) score += 60;
        if (value.Contains("player", StringComparison.Ordinal)) score += 40;
        if (value.Contains("spartan", StringComparison.Ordinal)) score += 30;
        return score;
    }

    private short ReadInt16(long address) =>
        BinaryPrimitives.ReadInt16LittleEndian(_memory.ReadBytes(address, 2));

    private WorldPoint ReadPoint(long address)
    {
        byte[] bytes = _memory.ReadBytes(address, 12);
        return new WorldPoint(
            BinaryPrimitives.ReadSingleLittleEndian(bytes),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8)));
    }

    private IReadOnlyList<RuntimeTagFieldValue> ReadRoot(RuntimeTagEntry tag) =>
        _definitions.ReadRootFields(
            tag.Group,
            tag.DataAddress,
            _memory.ReadBytes,
            Resolve);

    private IReadOnlyList<RuntimeTagFieldValue> ReadBlock(
        RuntimeTagEntry tag,
        RuntimeTagFieldValue block,
        int index) =>
        _definitions.ReadBlockFields(
            tag.Group,
            block.ChildBlockDefinition!,
            block.ChildAddress,
            index,
            _memory.ReadBytes,
            Resolve);

    private long? Resolve(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    public void Dispose() { }

    private sealed record SpawnTemplate(
        int SquadIndex,
        short TeamIndex,
        string SquadName,
        short ObjectiveIndex,
        long TeamAddress,
        long ReferenceAddress,
        long PositionAddress,
        long VariantAddress,
        long ObjectiveAddress,
        long TaskAddress,
        long TaskFlagsAddress,
        long SquadFlagsAddress,
        double DistanceSquared);

    private sealed record SpawnPlan(
        string Payload,
        SpawnTemplate Template,
        SpawnScaffoldDiagnosis Diagnosis);

    private sealed record MemoryPatch(long Address, byte[] Original);

    private readonly record struct WorldPoint(float X, float Y, float Z);
}
