using System.Buffers.Binary;
using HaloMeister.App.Models;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record LoadableVehicle(string Name, RuntimeTagEntry Tag)
{
    public string DisplayName => Name;
    public string TagPath => Tag.Name;
    public string ImageUri => VehicleIconUri(Tag.Name);
    public string Category => Categorize(Tag.Name);
    public string VariantSummary => "Loaded vehicle";
    public string SearchText => $"{DisplayName} {TagPath} {Category}";
    public string Detail => $"[vehi] 0x{RuntimeTagMemoryService.BuildRuntimeDatum(Tag):X8}";

    /// <summary>
    /// Local wiki / concept preview for known vehicle families; otherwise the
    /// shared missing.png placeholder (never a wrong sibling vehicle).
    /// </summary>
    public static string VehicleIconUri(string tagPath)
    {
        string path = tagPath.Replace('\\', '/').ToLowerInvariant();
        string icon =
            path.Contains("warthog", StringComparison.Ordinal) ? "warthog.png" :
            path.Contains("ghost", StringComparison.Ordinal) ? "ghost.png" :
            path.Contains("banshee", StringComparison.Ordinal) ? "banshee.png" :
            path.Contains("scorpion", StringComparison.Ordinal) ? "scorpion.png" :
            path.Contains("wraith", StringComparison.Ordinal) ? "wraith.png" :
            path.Contains("pelican", StringComparison.Ordinal) ? "pelican.png" :
            path.Contains("mongoose", StringComparison.Ordinal) ? "mongoose.png" :
            path.Contains("chopper", StringComparison.Ordinal) ? "chopper.png" :
            path.Contains("hornet", StringComparison.Ordinal) ? "hornet.png" :
            path.Contains("falcon", StringComparison.Ordinal) ? "falcon.png" :
            path.Contains("phantom", StringComparison.Ordinal) ? "phantom.png" :
            path.Contains("seraph", StringComparison.Ordinal) ? "seraph.png" :
            path.Contains("sabre", StringComparison.Ordinal) ? "sabre.png" :
            path.Contains("longsword", StringComparison.Ordinal) ? "longsword.png" :
            path.Contains("scarab", StringComparison.Ordinal) ? "scarab.png" :
            path.Contains("revenant", StringComparison.Ordinal) ? "revenant.png" :
            path.Contains("shade", StringComparison.Ordinal) ? "shade.png" :
            path.Contains("ag_turret", StringComparison.Ordinal) ||
            path.Contains("burden_of_proof", StringComparison.Ordinal) ? "ag_turret.png" :
            path.Contains("watchtower", StringComparison.Ordinal) ? "watchtower.png" :
            path.Contains("weevil", StringComparison.Ordinal) ||
            path.Contains("guntower", StringComparison.Ordinal) ||
            path.Contains("gun_tower", StringComparison.Ordinal) ? "weevil.png" :
            path.Contains("tuning_fork", StringComparison.Ordinal) ||
            path.Contains("spirit", StringComparison.Ordinal) ? "spirit.png" :
            "missing.png";
        return $"ms-appx:///Assets/VehicleIcons/{icon}";
    }

    private static string Categorize(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (ContainsAny(value, "banshee", "pelican", "spirit", "phantom",
                "hornet", "falcon", "seraph", "sabre", "longsword", "tuning_fork"))
            return "Aircraft";
        if (ContainsAny(value, "warthog", "ghost", "scorpion", "wraith",
                "mongoose", "chopper", "revenant", "scarab"))
            return "Ground";
        if (ContainsAny(value, "turret", "shade", "gun_tower", "guntower",
                "watchtower", "weevil", "ag_turret", "burden_of_proof"))
            return "Turrets";
        return "Other";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}

public sealed record VehicleModelVariant(
    int Index,
    uint StringId,
    string Name)
{
    public string Detail =>
        $"Model variant {Index + 1:N0} · string-id 0x{StringId:X8}";
}

public sealed record VehicleVariantCatalog(
    RuntimeTagEntry Model,
    IReadOnlyList<VehicleModelVariant> Variants);

public sealed record VehiclePlayerControlResult(
    int ChangedSeatCount,
    bool WasAlreadyEnabled,
    bool RemappedSeatLabel,
    string VehicleName)
{
    public string Message => WasAlreadyEnabled
        ? L.Format("vehicle_workshop.player_control_already_enabled", VehicleName)
        : L.Format("vehicle_workshop.player_control_enabled", VehicleName);
}

public sealed record VehicleSeatExitResult(
    int ChangedSeatCount,
    bool WasAlreadyAllowed,
    bool RemappedSeatLabel)
{
    public string Message
    {
        get
        {
            if (WasAlreadyAllowed)
                return L.Get("vehicle_workshop.seraph_exit_already_allowed");
            return RemappedSeatLabel
                ? L.Get("vehicle_workshop.seraph_exit_allowed_with_label")
                : L.Get("vehicle_workshop.seraph_exit_allowed");
        }
    }
}

public sealed class VehicleWorkshopService : IDisposable
{
    private const uint InvisibleSeat = 1u << 0;
    private const uint DriverSeat = 1u << 2;
    // unit_seat_flags option index 4: "third person camera"
    private const uint ThirdPersonCamera = 1u << 4;
    private const uint InvalidForPlayer = 1u << 13;
    // unit_seat_flags option index 27: "disallow exit"
    private const uint DisallowExit = 1u << 27;
    private const uint PlayerBlockingFlags = InvisibleSeat | InvalidForPlayer;
    // Same player-blocking clears as Pelican, plus Seraph's native "disallow exit".
    private const uint SeraphExitBlockingFlags =
        InvisibleSeat | InvalidForPlayer | DisallowExit;
    private const short AiSeatTypeDriver = 5;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private int _warmedProcessId;

    public int ProcessId => _memory.ProcessId;
    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public IReadOnlyList<LoadableVehicle> Connect()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_header_first"));
        return Refresh();
    }

    public IReadOnlyList<LoadableVehicle> Refresh()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_game_first"));

        _tags = _memory.ReadTags();
        // Match the weapon loader: list every loaded [vehi] path. Prefer a
        // resolved root when the same path appears more than once.
        LoadableVehicle[] vehicles = _tags
            .Where(tag =>
                string.Equals(tag.Group, "vehi", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(tag => tag.DataAddress > 0 ? 1 : 0)
                .ThenByDescending(tag => tag.RootCount > 0 ? 1 : 0)
                .ThenBy(tag => tag.Index)
                .First())
            .Select(tag => new LoadableVehicle(FriendlyName(tag), tag))
            .OrderBy(vehicle => vehicle.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.TagPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (vehicles.Length == 0)
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_vehicles"));
        return vehicles;
    }

    public VehicleVariantCatalog ReadVariants(LoadableVehicle selected)
    {
        EnsureDefinitions();
        RuntimeTagEntry live = FindLive(selected)
            ?? throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_tag_unloaded"));
        if (live.DataAddress <= 0 || live.RootCount <= 0)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_tag_not_ready"));

        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            live.Group, live.DataAddress, _memory.ReadBytes, ResolveOrNull);
        RuntimeTagFieldValue modelReference = root.FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "model",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Format("vehicle_workshop.error_no_model", selected.Name));
        RuntimeTagEntry model = _tags.FirstOrDefault(tag =>
                tag.Index == modelReference.ReferencedTagIndex &&
                string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.RootCount > 0)
            ?? throw new InvalidDataException(
                L.Format("vehicle_workshop.error_no_hlmt", selected.Name));

        IReadOnlyList<RuntimeTagFieldValue> modelRoot = _definitions.ReadRootFields(
            model.Group, model.DataAddress, _memory.ReadBytes, ResolveOrNull);
        RuntimeTagFieldValue variants = modelRoot.FirstOrDefault(field =>
                field.CanOpenBlock &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "model_variant_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Format("vehicle_workshop.error_no_variants", selected.Name));

        var result = new List<VehicleModelVariant>(variants.ChildCount);
        for (int index = 0; index < variants.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields =
                _definitions.ReadBlockFields(
                    model.Group,
                    variants.ChildBlockDefinition!,
                    variants.ChildAddress,
                    index,
                    _memory.ReadBytes,
                    ResolveOrNull);
            RuntimeTagFieldValue? name = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                field.Size == sizeof(uint) &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "name",
                    StringComparison.OrdinalIgnoreCase));
            if (name is null) continue;
            uint stringId = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(name.Address, name.Size));
            result.Add(new VehicleModelVariant(
                index,
                stringId,
                FriendlyVariantName(stringId, index)));
        }

        if (result.Count == 0)
            throw new InvalidDataException(
                L.Format("vehicle_workshop.error_no_variants", selected.Name));
        return new VehicleVariantCatalog(model, result);
    }

    public IReadOnlyList<SpawnVariantChoice> ReadSpawnVariants(LoadableVehicle selected)
    {
        try
        {
            return ReadVariants(selected).Variants
                .Select(variant =>
                {
                    byte[] bytes = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes, variant.StringId);
                    return new SpawnVariantChoice(
                        variant.Name,
                        bytes,
                        checked((short)variant.Index),
                        variant.Index);
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public async Task<ScriptExecutionResult> SpawnAsync(
        LoadableVehicle selected,
        VehicleModelVariant? variant = null,
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        // Move one-time native hook install off the first spawn click.
        await WarmUpAsync(cancellationToken);

        RuntimeTagEntry? live = FindLive(selected);
        if (live is null)
        {
            _tags = _memory.ReadTags();
            live = FindLive(selected);
        }
        if (live is null)
            throw new InvalidOperationException(L.Get("vehicle_workshop.error_tag_unloaded"));
        if (live.DataAddress <= 0 || live.RootCount <= 0)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_tag_not_ready"));

        uint datum = RuntimeTagMemoryService.BuildRuntimeDatum(live);
        if (variant is null || variant.StringId == 0)
        {
            ScriptExecutionResult plain = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamSpawn,
                datum.ToString("X8"),
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (plain.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(plain.Message);
            return plain;
        }

        // Same pattern as armor/enemy variant spawn: temporarily author the
        // vehicle's default model variant, then create via the native path that
        // also calls object_set_variant on the new datum.
        EnsureDefinitions();
        RuntimeTagFieldValue defaultVariant = _definitions.ReadRootFields(
                live.Group, live.DataAddress, _memory.ReadBytes, ResolveOrNull)
            .FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Format("vehicle_workshop.error_no_default_variant", selected.Name));
        if (defaultVariant.Size != sizeof(uint))
            throw new InvalidDataException(
                L.Format("vehicle_workshop.error_no_default_variant", selected.Name));

        byte[] original = _memory.ReadBytes(defaultVariant.Address, sizeof(uint));
        byte[] next = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(next, variant.StringId);
        bool patched = !original.AsSpan().SequenceEqual(next);
        if (patched)
            _memory.WriteVerified(defaultVariant.Address, next);

        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamBipedVariantSpawn,
                $"{datum:X8},{variant.StringId:X8}",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (result.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(result.Message);
            return result;
        }
        finally
        {
            if (patched && _memory.IsConnected)
                _memory.WriteVerified(defaultVariant.Address, original);
        }
    }

    public async Task<ScriptExecutionResult> SpawnAsync(
        LoadableVehicle selected,
        SpawnVariantChoice? variant,
        CancellationToken cancellationToken = default)
    {
        VehicleModelVariant? mapped = variant is null || variant.StringId == 0
            ? null
            : new VehicleModelVariant(
                variant.VariantBlockIndex,
                variant.StringId,
                variant.Name);
        return await SpawnAsync(selected, mapped, cancellationToken);
    }

    public void WarmUpDefinitions() => EnsureDefinitions();

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
            // Prewarming is optional; spawning remains available on builds
            // without the player-position capability.
        }
    }

    public VehiclePlayerControlResult EnablePelicanPlayerControl(LoadableVehicle selected) =>
        EnablePlayerControl(selected);

    public VehiclePlayerControlResult EnablePlayerControl(LoadableVehicle selected)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_game_first"));
        if (!SupportsPlayerControl(selected))
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_player_control_unsupported"));

        EnsureDefinitions();
        _tags = _memory.ReadTags();
        RuntimeTagEntry live = FindLive(selected)
            ?? throw new InvalidOperationException(
                L.Format("vehicle_workshop.error_player_control_unloaded", selected.Name));

        uint warthogDriverLabel;
        try
        {
            warthogDriverLabel = _memory.ResolveStringId("warthog_d");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_warthog_driver_label"),
                ex);
        }

        uint? nativeDriverLabel = ResolveNativeDriverLabel(selected);

        IReadOnlyList<SeatPatchField> seats = ReadSeats(live);
        IReadOnlyList<SeatPatchField> targets = SelectDriverSeats(
            seats,
            nativeDriverLabel,
            warthogDriverLabel);
        if (targets.Count == 0)
            throw new InvalidDataException(
                L.Format("vehicle_workshop.error_player_control_no_driver_seat", selected.Name));

        SeatPatchField[] needingWork = targets
            .Where(seat =>
                seat.Label != warthogDriverLabel ||
                (seat.Flags & PlayerBlockingFlags) != 0 ||
                (seat.Flags & ThirdPersonCamera) == 0)
            .ToArray();
        if (needingWork.Length == 0)
            return new VehiclePlayerControlResult(0, true, false, selected.Name);

        var completed = new List<(long Address, byte[] Original)>();
        bool remappedLabel = false;
        try
        {
            foreach (SeatPatchField seat in needingWork)
            {
                byte[] currentFlags = _memory.ReadBytes(seat.FlagsAddress, sizeof(uint));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(currentFlags);
                if (flags != seat.Flags)
                    throw new InvalidOperationException(
                        L.Format(
                            "vehicle_workshop.error_player_control_flags_changed",
                            selected.Name,
                            seat.Index));

                byte[] currentLabel = _memory.ReadBytes(seat.LabelAddress, sizeof(uint));
                uint label = BinaryPrimitives.ReadUInt32LittleEndian(currentLabel);
                if (label != seat.Label)
                    throw new InvalidOperationException(
                        L.Format(
                            "vehicle_workshop.error_player_control_label_changed",
                            selected.Name,
                            seat.Index));

                uint nextFlags = (flags & ~PlayerBlockingFlags) | ThirdPersonCamera;
                if (nextFlags != flags)
                {
                    byte[] replacement = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(replacement, nextFlags);
                    _memory.WriteVerified(seat.FlagsAddress, replacement);
                    completed.Add((seat.FlagsAddress, currentFlags));
                }

                if (label != warthogDriverLabel)
                {
                    byte[] replacement = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        replacement, warthogDriverLabel);
                    _memory.WriteVerified(seat.LabelAddress, replacement);
                    completed.Add((seat.LabelAddress, currentLabel));
                    remappedLabel = true;
                }
            }

            IReadOnlyList<SeatPatchField> verified = SelectDriverSeats(
                ReadSeats(live),
                nativeDriverLabel,
                warthogDriverLabel);
            if (verified.Count == 0 ||
                verified.Any(seat =>
                    seat.Label != warthogDriverLabel ||
                    (seat.Flags & PlayerBlockingFlags) != 0 ||
                    (seat.Flags & ThirdPersonCamera) == 0))
            {
                throw new InvalidDataException(
                    L.Format(
                        "vehicle_workshop.error_player_control_verify_failed",
                        selected.Name));
            }
        }
        catch
        {
            foreach ((long address, byte[] original) in completed.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(address, original); }
                catch { }
            }
            throw;
        }

        return new VehiclePlayerControlResult(
            needingWork.Length, false, remappedLabel, selected.Name);
    }

    public VehicleSeatExitResult AllowSeraphPlayerExit(LoadableVehicle selected)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_game_first"));
        if (!IsSeraph(selected))
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_seraph_only"));

        EnsureDefinitions();
        _tags = _memory.ReadTags();
        RuntimeTagEntry live = FindLive(selected)
            ?? throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_seraph_unloaded"));
        if (live.DataAddress <= 0)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_tag_not_ready"));

        // Same trick as Pelican: remap the cockpit seat label to warthog_d so the
        // engine treats exit/enter like a normal player-driven vehicle seat.
        uint warthogDriverLabel;
        try
        {
            warthogDriverLabel = _memory.ResolveStringId("warthog_d");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_warthog_driver_label"),
                ex);
        }

        uint? seraphDriverLabel = _memory.TryResolveStringId("seraph_d", out uint seraphId)
            ? seraphId
            : null;

        IReadOnlyList<SeatPatchField> seats = ReadSeats(live);
        IReadOnlyList<SeatPatchField> targets = SelectDriverSeats(
            seats,
            seraphDriverLabel,
            warthogDriverLabel);
        if (targets.Count == 0)
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_seraph_no_seats"));

        SeatPatchField[] needingWork = targets
            .Where(seat =>
                seat.Label != warthogDriverLabel ||
                (seat.Flags & SeraphExitBlockingFlags) != 0)
            .ToArray();
        if (needingWork.Length == 0)
            return new VehicleSeatExitResult(0, true, false);

        var completed = new List<(long Address, byte[] Original)>();
        bool remappedLabel = false;
        try
        {
            foreach (SeatPatchField seat in needingWork)
            {
                byte[] currentFlags = _memory.ReadBytes(seat.FlagsAddress, sizeof(uint));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(currentFlags);
                if (flags != seat.Flags)
                    throw new InvalidOperationException(
                        L.Format(
                            "vehicle_workshop.error_seraph_flags_changed",
                            seat.Index));

                byte[] currentLabel = _memory.ReadBytes(seat.LabelAddress, sizeof(uint));
                uint label = BinaryPrimitives.ReadUInt32LittleEndian(currentLabel);
                if (label != seat.Label)
                    throw new InvalidOperationException(
                        L.Format(
                            "vehicle_workshop.error_seraph_label_changed",
                            seat.Index));

                uint nextFlags = flags & ~SeraphExitBlockingFlags;
                if (nextFlags != flags)
                {
                    byte[] replacement = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(replacement, nextFlags);
                    _memory.WriteVerified(seat.FlagsAddress, replacement);
                    completed.Add((seat.FlagsAddress, currentFlags));
                }

                if (label != warthogDriverLabel)
                {
                    byte[] replacement = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        replacement, warthogDriverLabel);
                    _memory.WriteVerified(seat.LabelAddress, replacement);
                    completed.Add((seat.LabelAddress, currentLabel));
                    remappedLabel = true;
                }
            }

            IReadOnlyList<SeatPatchField> verified = SelectDriverSeats(
                ReadSeats(live),
                seraphDriverLabel,
                warthogDriverLabel);
            if (verified.Count == 0 ||
                verified.Any(seat =>
                    seat.Label != warthogDriverLabel ||
                    (seat.Flags & SeraphExitBlockingFlags) != 0))
            {
                throw new InvalidDataException(
                    L.Get("vehicle_workshop.error_seraph_exit_verify_failed"));
            }
        }
        catch
        {
            foreach ((long address, byte[] original) in completed.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(address, original); }
                catch { }
            }
            throw;
        }

        return new VehicleSeatExitResult(needingWork.Length, false, remappedLabel);
    }

    public static bool IsPelican(LoadableVehicle? vehicle) =>
        vehicle is not null && IsPelicanPath(vehicle.Tag.Name);

    public static bool IsBurdenOfProofTurret(LoadableVehicle? vehicle) =>
        vehicle is not null && IsBurdenOfProofTurretPath(vehicle.Tag.Name);

    public static bool IsAgTurretTwo(LoadableVehicle? vehicle) =>
        vehicle is not null && IsAgTurretTwoPath(vehicle.Tag.Name);

    public static bool SupportsPlayerControl(LoadableVehicle? vehicle) =>
        IsPelican(vehicle) ||
        IsBurdenOfProofTurret(vehicle) ||
        IsAgTurretTwo(vehicle);

    public static bool IsSeraph(LoadableVehicle? vehicle) =>
        vehicle is not null && IsSeraphPath(vehicle.Tag.Name);

    public static string FriendlyName(RuntimeTagEntry tag)
    {
        string text = tag.LeafName.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed vehicle"
            : string.Join(
                ' ',
                text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word =>
                        char.ToUpperInvariant(word[0]) + word[1..]));
    }

    public void Dispose() { }

    private RuntimeTagEntry? FindLive(LoadableVehicle selected) =>
        _tags.FirstOrDefault(tag =>
            tag.Index == selected.Tag.Index &&
            string.Equals(tag.Group, "vehi", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tag.Name, selected.Tag.Name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SeatPatchField> SelectDriverSeats(
        IReadOnlyList<SeatPatchField> seats,
        uint? nativeDriverLabel,
        uint warthogDriverLabel)
    {
        if (nativeDriverLabel is uint nativeLabel)
        {
            SeatPatchField[] byNativeLabel = seats
                .Where(seat => seat.Label == nativeLabel)
                .ToArray();
            if (byNativeLabel.Length > 0) return byNativeLabel;
        }

        SeatPatchField[] alreadyWarthog = seats
            .Where(seat => seat.Label == warthogDriverLabel)
            .ToArray();
        if (alreadyWarthog.Length > 0) return alreadyWarthog;

        SeatPatchField[] byFlag = seats
            .Where(seat => (seat.Flags & DriverSeat) != 0)
            .ToArray();
        if (byFlag.Length > 0) return byFlag;

        SeatPatchField[] byAi = seats
            .Where(seat => seat.AiSeatType == AiSeatTypeDriver)
            .ToArray();
        if (byAi.Length > 0) return byAi;

        // Campaign aircraft often omit the driver flag; seat 0 is the cockpit.
        return seats.Count > 0 ? [seats[0]] : [];
    }

    private IReadOnlyList<SeatPatchField> ReadSeats(RuntimeTagEntry vehicle)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            vehicle.Group, vehicle.DataAddress, _memory.ReadBytes, ResolveOrNull);
        // Vehicle roots nest unit fields as "unit / seats". Prefer the concrete
        // unit_seat_block definition so "powered seats" cannot win.
        RuntimeTagFieldValue seats = root.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "unit_seat_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? root.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "seats",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_seats_block"));
        if (seats.ChildBlockDefinition is null ||
            seats.ChildCount < 0 ||
            seats.ChildCount > 128 ||
            (seats.ChildCount > 0 && seats.ChildAddress <= 0))
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_invalid_seats_block"));

        var result = new List<SeatPatchField>();
        for (int index = 0; index < seats.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields = _definitions.ReadBlockFields(
                vehicle.Group,
                seats.ChildBlockDefinition,
                seats.ChildAddress,
                index,
                _memory.ReadBytes,
                ResolveOrNull);
            RuntimeTagFieldValue? flagsField = fields.FirstOrDefault(field =>
                field.Type == "long_flags" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "flags",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? labelField = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "label",
                    StringComparison.OrdinalIgnoreCase));
            if (flagsField is null || labelField is null) continue;

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(flagsField.Address, sizeof(uint)));
            uint label = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(labelField.Address, sizeof(uint)));
            short aiSeatType = 0;
            RuntimeTagFieldValue? aiSeatTypeField = fields.FirstOrDefault(field =>
                field.Type == "short_enum" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "ai seat type",
                    StringComparison.OrdinalIgnoreCase));
            if (aiSeatTypeField is not null)
            {
                aiSeatType = BinaryPrimitives.ReadInt16LittleEndian(
                    _memory.ReadBytes(aiSeatTypeField.Address, sizeof(short)));
            }

            result.Add(new SeatPatchField(
                index,
                flagsField.Address,
                flags,
                labelField.Address,
                label,
                aiSeatType));
        }
        return result;
    }

    private static string LeafFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("vehi") || !_definitions.HasSchema("hlmt"))
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_vehi_schema"));
    }

    private string FriendlyVariantName(uint stringId, int index)
    {
        if (_memory.TryGetStringIdName(stringId, out string? name) &&
            !string.IsNullOrWhiteSpace(name))
            return name!;
        return index == 0
            ? L.Get("vehicle_workshop.variant_default")
            : L.Format("vehicle_workshop.variant_numbered", index + 1);
    }

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private uint? ResolveNativeDriverLabel(LoadableVehicle selected)
    {
        if (IsPelican(selected) &&
            _memory.TryResolveStringId("pelican_d", out uint pelicanId))
            return pelicanId;
        return null;
    }

    private static bool IsPelicanPath(string path) =>
        path.Contains("pelican", StringComparison.OrdinalIgnoreCase);

    private static bool IsBurdenOfProofTurretPath(string path)
    {
        string value = path.Replace('\\', '/');
        return value.Contains("burden_of_proof_turret", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAgTurretTwoPath(string path)
    {
        string value = path.Replace('\\', '/');
        return value.Contains("ag_turret_two", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSeraphPath(string path)
    {
        string value = path.Replace('\\', '/');
        return value.Contains("seraph", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SeatPatchField(
        int Index,
        long FlagsAddress,
        uint Flags,
        long LabelAddress,
        uint Label,
        short AiSeatType);
}
