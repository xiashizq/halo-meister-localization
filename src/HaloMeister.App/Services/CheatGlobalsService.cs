using System.Text;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public enum CheatGlobalBackend
{
    SkullHook,
    HaloScript,
}

public sealed record CheatGlobalDefinition(
    string Name,
    string DisplayNameKey,
    string DescriptionKey,
    CheatGlobalBackend Backend);

public sealed class CheatGlobalItem
{
    public required CheatGlobalDefinition Definition { get; init; }
    public bool IsEnabled { get; set; }

    public string Name => Definition.Name;
    public string DisplayName => L.Get(Definition.DisplayNameKey);
    public string Description => L.Get(Definition.DescriptionKey);
}

public sealed class CheatGlobalsService
{
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly Dictionary<string, bool> _scriptCheatState =
        new(StringComparer.Ordinal);

    public static IReadOnlyList<CheatGlobalDefinition> Catalog { get; } =
    [
        new(
            "infinite_health",
            "cheat_globals.infinite_health",
            "cheat_globals.infinite_health_desc",
            CheatGlobalBackend.SkullHook),
        new(
            "infinite_ammo",
            "cheat_globals.infinite_ammo",
            "cheat_globals.infinite_ammo_desc",
            CheatGlobalBackend.SkullHook),
        new(
            "jetpack",
            "cheat_globals.jetpack",
            "cheat_globals.jetpack_desc",
            CheatGlobalBackend.SkullHook),
        new(
            "deathless_player",
            "cheat_globals.deathless_player",
            "cheat_globals.deathless_player_desc",
            CheatGlobalBackend.HaloScript),
        new(
            "ai_disregard",
            "cheat_globals.ai_disregard",
            "cheat_globals.ai_disregard_desc",
            CheatGlobalBackend.HaloScript),
    ];

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public async Task<IReadOnlyList<CheatGlobalItem>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        Dictionary<string, bool> skullValues = await ReadSkullHookValuesAsync(
            cancellationToken);
        return Catalog.Select(definition => new CheatGlobalItem
        {
            Definition = definition,
            IsEnabled = definition.Backend switch
            {
                CheatGlobalBackend.SkullHook =>
                    skullValues.TryGetValue(definition.Name, out bool enabled) &&
                    enabled,
                CheatGlobalBackend.HaloScript =>
                    _scriptCheatState.TryGetValue(definition.Name, out bool scriptEnabled) &&
                    scriptEnabled,
                _ => false,
            },
        }).ToArray();
    }

    public async Task SetAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        CheatGlobalDefinition definition = Catalog.FirstOrDefault(
                item => item.Name == name)
            ?? throw new ArgumentOutOfRangeException(nameof(name));
        EnsureBridgeReady();

        switch (definition.Backend)
        {
            case CheatGlobalBackend.SkullHook:
                await SetSkullHookAsync(name, enabled, cancellationToken);
                break;
            case CheatGlobalBackend.HaloScript:
                await SetHaloScriptCheatAsync(name, enabled, cancellationToken);
                _scriptCheatState[name] = enabled;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(definition));
        }
    }

    private async Task SetSkullHookAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamCheatGlobalWrite,
            $"{name}={(enabled ? 1 : 0)}",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        Dictionary<string, bool> values = ParseValues(result.Message);
        if (!values.TryGetValue(name, out bool actual) || actual != enabled)
            throw new InvalidDataException(L.Get("cheat_globals.error_not_retained"));
    }

    private async Task SetHaloScriptCheatAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken)
    {
        string script = name switch
        {
            "deathless_player" => BuildDeathlessScript(enabled),
            "ai_disregard" => BuildAiDisregardScript(enabled),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            script,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (result.Outcome is not (ScriptOutcome.Submitted or ScriptOutcome.Confirmed))
            throw new InvalidOperationException(result.Message);
    }

    private static string BuildDeathlessScript(bool enabled)
    {
        string flag = enabled ? "true" : "false";
        var builder = new StringBuilder();
        for (int index = 0; index <= 3; index++)
            builder.AppendLine($"object_cannot_die (player_get {index}) {flag}");
        return builder.ToString().TrimEnd();
    }

    // player_get is the live player unit. (players) submits but is not targeted.
    private static string BuildAiDisregardScript(bool enabled)
    {
        string flag = enabled ? "true" : "false";
        var builder = new StringBuilder();
        for (int index = 0; index <= 3; index++)
        {
            builder.AppendLine($"ai_disregard (player_get {index}) {flag}");
            builder.AppendLine(
                $"ai_disregard (unit_get_vehicle (player_get {index})) {flag}");
            if (enabled)
                builder.AppendLine($"ai_prefer_target (player_get {index}) false");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<Dictionary<string, bool>> ReadSkullHookValuesAsync(
        CancellationToken cancellationToken)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamCheatGlobalsRead,
            "read",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return ParseValues(result.Message);
    }

    private static Dictionary<string, bool> ParseValues(string message)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string line in message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator + 2 != line.Length ||
                (line[^1] != '0' && line[^1] != '1'))
            {
                throw new InvalidDataException(
                    L.Get("cheat_globals.error_invalid_hook_value"));
            }
            values[line[..separator]] = line[^1] == '1';
        }
        return values;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(L.Get("bridge.error_not_responding_restart"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
    }
}
