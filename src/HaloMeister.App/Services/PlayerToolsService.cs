using System.Buffers.Binary;
using System.Globalization;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record PlayerCoordinates(float X, float Y, float Z)
{
    public string ToPayload() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{X:R},{Y:R},{Z:R}");
}

public sealed class PlayerToolsService
{
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<long, ScaleCameraPatch> _scaleCameraPatches = [];
    private int _scaleProcessId;

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public async Task<PlayerCoordinates> ReadPositionAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerPosition,
            "current",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int offset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        string[] values = offset < 0
            ? []
            : result.Message[(offset + marker.Length)..]
                .Trim()
                .Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 3 ||
            !float.TryParse(
                values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(
                values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(
                values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
            !float.IsFinite(x) ||
            !float.IsFinite(y) ||
            !float.IsFinite(z))
        {
            throw new InvalidDataException(
                "The game returned an invalid player position. Resume a campaign checkpoint and try again.");
        }
        return new PlayerCoordinates(x, y, z);
    }

    public async Task TeleportAsync(
        PlayerCoordinates destination,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        if (!float.IsFinite(destination.X) ||
            !float.IsFinite(destination.Y) ||
            !float.IsFinite(destination.Z))
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                "Teleport coordinates must be finite numbers.");
        }

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerTeleport,
            destination.ToPayload(),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task SetNoClipAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerNoClip,
            enabled ? "1" : "0",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task SetInputSuppressedAsync(
        bool suppressed,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerInput,
            suppressed ? "suppress" : "restore",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task SetScaleAsync(
        float scale,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        if (!float.IsFinite(scale) || scale is < 0.25f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                "Player scale must be between 0.25 and 1.0.");
        }

        try
        {
            await Task.Run(RestoreExpandedCameraHeights, cancellationToken);

            string value = scale.ToString("R", CultureInfo.InvariantCulture);
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                $"(object_set_scale (player_get 0) {value} 10)",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (result.Outcome == ScriptOutcome.Failed)
                throw new InvalidOperationException(result.Message);

            ScriptExecutionResult normalizeResult = await _bridge.ExecuteAsync(
                ScriptLanguage.PlayerWeaponNormalize,
                "normalize",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (normalizeResult.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(normalizeResult.Message);

            await SetPrimaryWeaponScaleAsync(scale, cancellationToken);
        }
        catch
        {
            await Task.Run(RestoreExpandedCameraHeights, cancellationToken);
            throw;
        }
    }

    public async Task SetPrimaryWeaponScaleAsync(
        float scale,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        if (!float.IsFinite(scale) || scale is < 0.25f or > 4f)
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                "Weapon scale must be between 0.25 and 4.0.");

        string value = scale.ToString("R", CultureInfo.InvariantCulture);
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            $"(object_set_scale (unit_get_primary_weapon (player_get 0)) {value} 10)",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Failed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task<int> ReadActivePlayerTagIndexAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerUnitTagRead,
            "read",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int offset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        string value = offset < 0
            ? ""
            : result.Message[(offset + marker.Length)..].Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int tagIndex) ||
            tagIndex is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                "The game returned an invalid controlled-player unit tag index.");
        }
        return tagIndex;
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
    }

    private void ApplyExpandedCameraHeights(int playerTagIndex, float scale)
    {
        EnsureMemoryConnected();
        EnsureDefinitions();
        ResetScalePatchesForNewProcess();
        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry player = tags.FirstOrDefault(tag =>
                tag.Index == playerTagIndex &&
                tag.Group.Equals("bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(
                "The controlled player's live [bipd] tag is unavailable.");

        if (_scaleCameraPatches.Count > 0 &&
            _scaleCameraPatches.Values.Any(patch => patch.TagIndex != player.Index))
        {
            RestoreExpandedCameraHeights();
        }

        IReadOnlyList<RuntimeTagFieldValue> fields = _definitions.ReadRootFields(
            player.Group,
            player.DataAddress,
            _memory.ReadBytes,
            ResolveOffset);
        string[] names =
        [
            "standing camera height",
            "running camera height",
            "crouching camera height",
            "crouch walking camera height",
        ];
        var writes = new List<RuntimeMemoryWrite>();
        foreach (string name in names)
        {
            RuntimeTagFieldValue field = fields.FirstOrDefault(item =>
                item.Type.Equals("real", StringComparison.OrdinalIgnoreCase) &&
                CleanFieldName(item.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    $"The player biped tag has no '{name}' field.");
            if (field.Size != sizeof(float))
                throw new InvalidDataException(
                    $"The player biped '{name}' field has an unexpected size.");

            byte[] original;
            if (_scaleCameraPatches.TryGetValue(field.Address, out ScaleCameraPatch? patch))
            {
                original = patch.Original;
            }
            else
            {
                original = _memory.ReadBytes(field.Address, sizeof(float));
            }

            float baseHeight = BinaryPrimitives.ReadSingleLittleEndian(original);
            float scaledHeight = baseHeight * scale;
            if (!float.IsFinite(baseHeight) || !float.IsFinite(scaledHeight))
                throw new InvalidDataException($"The player biped '{name}' value is invalid.");

            byte[] replacement = new byte[sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(replacement, scaledHeight);
            byte[] expected = _memory.ReadBytes(field.Address, sizeof(float));
            writes.Add(new RuntimeMemoryWrite(field.Address, expected, replacement));
            _scaleCameraPatches[field.Address] = new ScaleCameraPatch(
                player.Index, original, replacement);
        }
        if (writes.Count > 0)
            _memory.ApplyTransaction(writes);
    }

    private void RestoreExpandedCameraHeights()
    {
        if (_scaleCameraPatches.Count == 0)
            return;
        EnsureMemoryConnected();
        ResetScalePatchesForNewProcess();
        foreach ((long address, ScaleCameraPatch patch) in _scaleCameraPatches.ToArray())
        {
            try
            {
                byte[] current = _memory.ReadBytes(address, patch.Applied.Length);
                if (current.AsSpan().SequenceEqual(patch.Applied))
                    _memory.WriteVerified(address, patch.Applied, patch.Original);
            }
            catch
            {
                // Do not restore data that the game unloaded or changed meanwhile.
            }
            _scaleCameraPatches.Remove(address);
        }
    }

    private void EnsureMemoryConnected()
    {
        if (!_memory.IsConnected)
            _memory.Connect();
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("bipd"))
            throw new InvalidDataException("The biped tag definition is unavailable.");
    }

    private void ResetScalePatchesForNewProcess()
    {
        if (_scaleProcessId == _memory.ProcessId)
            return;
        _scaleCameraPatches.Clear();
        _scaleProcessId = _memory.ProcessId;
    }

    private long? ResolveOffset(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private sealed record ScaleCameraPatch(
        int TagIndex,
        byte[] Original,
        byte[] Applied);
}
