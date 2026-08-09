using System.Globalization;
using System.Text.RegularExpressions;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record AllegianceDemoSpawnResult(
    ScriptExecutionResult SpawnResult,
    int? ActorDatum,
    SpawnScaffoldDiagnosis? ScaffoldDiagnosis);

public sealed record ObjectTeamResult(
    int UnitDatum,
    int ActorDatum,
    int Team,
    string RawMessage);

/// <summary>
/// Friend/foe demo: prefer a matching scaffold squad, birth as player (1) or
/// covenant (3). Friendlies register as a player fireteam companion and follow;
/// hostiles do not. Optional post reinforce via <c>ai_object_set_team</c> +
/// the object allegiance table. Spawned squads are re-pulsed into combat so
/// they stay engaged instead of drifting back to idle/follow.
/// </summary>
public sealed class AllegianceDemoService
{
    private static readonly Regex ActorDatumPattern = new(
        @"first actor datum 0x([0-9A-Fa-f]{8})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ScriptingBridgeService _bridge =
        ScriptingBridgeService.Current;
    private readonly EnemySpawnerService _spawner = new();
    private readonly object _combatLock = new();
    private readonly Dictionary<string, bool> _combatSquads =
        new(StringComparer.OrdinalIgnoreCase);
    private int _maintainBusy;
    private Timer? _maintainTimer;

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public bool HasCombatMaintainTargets
    {
        get
        {
            lock (_combatLock)
                return _combatSquads.Count > 0;
        }
    }

    /// <summary>Friendly = player (1). Hostile = covenant (3).</summary>
    public const int FriendlyTeam = 1;
    public const int HostileTeam = 3;

    public static IReadOnlyList<PlayerTeamOption> CreateTeamOptions() =>
    [
        new(
            L.Get("allegiance_demo.stance_friendly"),
            FriendlyTeam,
            L.Get("allegiance_demo.stance_friendly_desc")),
        new(
            L.Get("allegiance_demo.stance_hostile"),
            HostileTeam,
            L.Get("allegiance_demo.stance_hostile_desc")),
    ];

    public SpawnerCatalog Connect() => _spawner.Connect();

    public SpawnScaffoldInventory ProbeScaffolds() =>
        _spawner.ProbeScaffoldInventory();

    public IReadOnlyList<AiWeaponChoice> GetCompatibleWeapons(
        EnemySpawnChoice character) =>
        _spawner.GetCompatibleWeapons(character);

    public string ScaffoldDiagnosisLogPath =>
        EnemySpawnerService.ScaffoldDiagnosisLogPath;

    public async Task<AllegianceDemoSpawnResult> SpawnAsync(
        EnemySpawnChoice character,
        SpawnVariantChoice variant,
        int campaignTeam = FriendlyTeam,
        int count = 1,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        CancellationToken cancellationToken = default)
    {
        if (campaignTeam is not (FriendlyTeam or HostileTeam))
            throw new ArgumentOutOfRangeException(nameof(campaignTeam));
        if (count is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(count));

        // Friendly prefers an ally scaffold and registers as a player fireteam
        // companion so they follow. Hostile prefers a hostile scaffold and does
        // not join the player's fireteam. Native keeps squad team patched
        // through actor_new + finalize.
        //
        // Keep initial objective/task for both stances so actor_new inherits
        // attack desire. Dedicated hm_* always keep combat objective.
        bool followPlayer = campaignTeam == FriendlyTeam;
        ScriptExecutionResult result = await _spawner.SpawnGroupAsync(
            character,
            variant,
            count,
            formationOffsetX,
            formationOffsetY,
            weapon,
            followPlayer: followPlayer,
            clearSquadObjective: false,
            campaignTeam: (ushort)campaignTeam,
            cancellationToken: cancellationToken);
        int? actor = TryParseActorDatum(result.Message);
        SpawnScaffoldDiagnosis? diagnosis = _spawner.LastScaffoldDiagnosis;
        result = await WakeSpawnedSquadAsync(
            result,
            diagnosis,
            preserveFireteam: followPlayer,
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Confirmed)
        {
            string squadName = !string.IsNullOrWhiteSpace(diagnosis?.SquadName)
                ? diagnosis!.SquadName
                : followPlayer
                    ? EnemySpawnerService.DedicatedAllySquadName
                    : EnemySpawnerService.DedicatedHostileSquadName;
            TrackCombatSquad(squadName, preserveFireteam: followPlayer);
            if (followPlayer)
            {
                result = result with
                {
                    Message = $"{result.Message} follow=fireteam combat=maintain",
                };
            }
            else
            {
                result = result with
                {
                    Message = $"{result.Message} combat=maintain",
                };
            }
        }
        return new AllegianceDemoSpawnResult(result, actor, diagnosis);
    }

    public void TrackCombatSquad(string squadName, bool preserveFireteam)
    {
        if (string.IsNullOrWhiteSpace(squadName))
            return;
        lock (_combatLock)
            _combatSquads[squadName.Trim()] = preserveFireteam;
        EnsureMaintainTimer();
    }

    public void ClearCombatMaintain()
    {
        lock (_combatLock)
            _combatSquads.Clear();
        Timer? timer = Interlocked.Exchange(ref _maintainTimer, null);
        timer?.Dispose();
    }

    private void EnsureMaintainTimer()
    {
        if (_maintainTimer is not null)
            return;
        // Pulse often enough that actors cannot cool back to idle/follow.
        var timer = new Timer(
            static state =>
            {
                if (state is not AllegianceDemoService service)
                    return;
                _ = service.MaintainCombatAsync();
            },
            this,
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromMilliseconds(1200));
        if (Interlocked.CompareExchange(ref _maintainTimer, timer, null) is not null)
            timer.Dispose();
    }

    /// <summary>
    /// Re-assert force-active / combat status / berserk for every tracked
    /// spawn squad. Safe to call from a UI timer; overlaps are skipped.
    /// </summary>
    public async Task MaintainCombatAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _maintainBusy, 1) == 1)
            return;
        try
        {
            ScriptingBridgeStatus status = BridgeStatus;
            if (!status.IsRuntimeReady || status.IsStale)
                return;

            List<KeyValuePair<string, bool>> squads;
            lock (_combatLock)
                squads = _combatSquads.ToList();
            if (squads.Count == 0)
                return;

            foreach ((string squadName, bool preserveFireteam) in squads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PulseCombatAsync(
                    squadName,
                    preserveFireteam,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Timer tick cancellation is fine.
        }
        finally
        {
            Interlocked.Exchange(ref _maintainBusy, 0);
        }
    }

    private async Task PulseCombatAsync(
        string squadName,
        bool preserveFireteam,
        CancellationToken cancellationToken)
    {
        // Keep them in the hottest combat band. Friendlies skip ai_renew so
        // fireteam membership survives the pulse.
        var lines = new List<string>
        {
            $"ai_suppress_combat {squadName} false",
            $"ai_force_active {squadName} true",
            $"ai_set_weapon_up {squadName} true",
            $"ai_berserk {squadName} true",
        };
        if (preserveFireteam)
        {
            lines.Add($"ai_prefer_target_team {squadName} covenant");
            lines.Add($"ai_prefer_target_team {squadName} brute");
            lines.Add($"ai_prefer_target_team {squadName} flood");
        }
        else
        {
            lines.Add($"ai_prefer_target (players) true");
        }

        await TryRunHaloScriptAsync(lines, cancellationToken);
        // Status enum spelling differs by build — try both, ignore failures.
        if (!await TryRunHaloScriptAsync(
                [$"ai_set_combat_status {squadName} dangerous_enemy"],
                cancellationToken))
        {
            await TryRunHaloScriptAsync(
                [$"ai_set_combat_status {squadName} ai_combat_status_dangerous_enemy"],
                cancellationToken);
        }
    }

    /// <summary>
    /// Best-effort combat wake. Hostiles renew + lock onto the player.
    /// Friendlies skip <c>ai_renew</c> (keeps fireteam follow) but raise
    /// combat status, weapon readiness, preferred hostile teams, and berserk
    /// so they actually open fire instead of idle-following.
    /// </summary>
    private async Task<ScriptExecutionResult> WakeSpawnedSquadAsync(
        ScriptExecutionResult spawnResult,
        SpawnScaffoldDiagnosis? diagnosis,
        bool preserveFireteam,
        CancellationToken cancellationToken)
    {
        if (spawnResult.Outcome != ScriptOutcome.Confirmed)
            return spawnResult;

        string fallback = preserveFireteam
            ? EnemySpawnerService.DedicatedAllySquadName
            : EnemySpawnerService.DedicatedHostileSquadName;
        string squadName = !string.IsNullOrWhiteSpace(diagnosis?.SquadName)
            ? diagnosis!.SquadName
            : fallback;

        var wakeTags = new List<string>();
        if (preserveFireteam)
        {
            if (await TryRunHaloScriptAsync(
                    [
                        $"ai_suppress_combat {squadName} false",
                        $"ai_force_active {squadName} true",
                        $"ai_set_weapon_up {squadName} true",
                    ],
                    cancellationToken))
            {
                wakeTags.Add("armed");
            }

            // Enum spelling varies by build; try both forms.
            if (await TryRunHaloScriptAsync(
                    [$"ai_set_combat_status {squadName} dangerous_enemy"],
                    cancellationToken) ||
                await TryRunHaloScriptAsync(
                    [$"ai_set_combat_status {squadName} ai_combat_status_dangerous_enemy"],
                    cancellationToken))
            {
                wakeTags.Add("status");
            }

            if (await TryRunHaloScriptAsync(
                    [
                        $"ai_prefer_target_team {squadName} covenant",
                        $"ai_prefer_target_team {squadName} brute",
                        $"ai_prefer_target_team {squadName} flood",
                    ],
                    cancellationToken))
            {
                wakeTags.Add("hunt");
            }

            // Optional: if dedicated hostiles exist in the mission, grant magic LOS.
            string hostileSquad = EnemySpawnerService.DedicatedHostileSquadName;
            if (await TryRunHaloScriptAsync(
                    [$"ai_magically_see {squadName} {hostileSquad}"],
                    cancellationToken))
            {
                wakeTags.Add("see");
            }

            if (await TryRunHaloScriptAsync(
                    [$"ai_berserk {squadName} true"],
                    cancellationToken))
            {
                wakeTags.Add("berserk");
            }
        }
        else
        {
            if (await TryRunHaloScriptAsync(
                    [
                        $"ai_suppress_combat {squadName} false",
                        $"ai_force_active {squadName} true",
                        $"ai_renew {squadName}",
                        $"ai_magically_see_object {squadName} (player_get 0)",
                    ],
                    cancellationToken))
            {
                wakeTags.Add("see");
            }

            if (await TryRunHaloScriptAsync(
                    [
                        $"ai_prefer_target (players) true",
                        $"ai_berserk {squadName} true",
                    ],
                    cancellationToken))
            {
                wakeTags.Add("berserk");
            }
        }

        if (wakeTags.Count == 0)
            return spawnResult;

        return spawnResult with
        {
            Message = $"{spawnResult.Message} wake={squadName}:{string.Join('+', wakeTags)}",
        };
    }

    private async Task<bool> TryRunHaloScriptAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                string.Join('\n', lines),
                TimeSpan.FromSeconds(8),
                cancellationToken);
            return result.Outcome == ScriptOutcome.Confirmed;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ObjectTeamResult> ApplyObjectTeamAsync(
        int team,
        int? actorDatum = null,
        CancellationToken cancellationToken = default)
    {
        if (team is not (FriendlyTeam or HostileTeam))
            throw new ArgumentOutOfRangeException(nameof(team));

        // Minimal payload: target,team. Lua appends player unit for combat-aim clear.
        string payload = actorDatum is int actor
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"a{(uint)actor:X8},{team}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"last,{team}");

        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.ObjectTeam,
            payload,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return ParseObjectTeam(result.Message);
    }

    public async Task<ScriptExecutionResult> SubmitAllegianceAsync(
        int team,
        bool breakAllegiance,
        CancellationToken cancellationToken = default)
    {
        string teamName = HaloScriptTeamName(team);
        string verb = breakAllegiance ? "ai_allegiance_break" : "ai_allegiance";
        string expression =
            $"{verb} player {teamName}\n{verb} {teamName} player";
        EnsureBridgeReady();
        return await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            expression,
            TimeSpan.FromSeconds(10),
            cancellationToken);
    }

    public static string HaloScriptTeamName(int team) =>
        team switch
        {
            0 => "default",
            1 => "player",
            2 => "human",
            3 => "covenant",
            4 => "brute",
            5 => "mule",
            6 => "spare",
            7 => "covenant_player",
            8 => "flood",
            9 => "sentinel",
            10 => "heretic",
            11 => "prophet",
            12 => "guilty",
            13 => "berserk_hostile_to_all",
            _ => throw new ArgumentOutOfRangeException(nameof(team)),
        };

    private static int? TryParseActorDatum(string message)
    {
        Match match = ActorDatumPattern.Match(message);
        if (!match.Success)
            return null;
        if (!uint.TryParse(
                match.Groups[1].Value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint datum))
            return null;
        return unchecked((int)datum);
    }

    private static ObjectTeamResult ParseObjectTeam(string message)
    {
        Dictionary<string, string> fields = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => parts[1],
                StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("unit", out string? unitText) ||
            !fields.TryGetValue("actor", out string? actorText) ||
            !fields.TryGetValue("team", out string? teamText) ||
            !TryParseHexDatum(unitText, out int unit) ||
            !TryParseHexDatum(actorText, out int actor) ||
            !int.TryParse(
                teamText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int team))
        {
            throw new InvalidDataException(
                "The native object-team hook returned an invalid result.");
        }

        return new ObjectTeamResult(unit, actor, team, message);
    }

    private static bool TryParseHexDatum(string text, out int value)
    {
        value = 0;
        ReadOnlySpan<char> span = text.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            span = span[2..];
        if (!uint.TryParse(
                span,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint datum))
            return false;
        value = unchecked((int)datum);
        return true;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
        {
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding_restart"));
        }
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
        if (status.RunningVersion is < 102)
        {
            throw new InvalidOperationException(
                L.Get("allegiance_demo.requires_bridge_v102"));
        }
    }
}
