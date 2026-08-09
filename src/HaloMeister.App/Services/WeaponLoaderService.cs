using System.Buffers.Binary;
using System.Text;
using HaloMeister.App.Models;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record LoadableWeapon(string Name, RuntimeTagEntry Tag)
{
    public string ImageUri => ProjectileSwapperService.WeaponIconUri(Tag.Name);
    public string TagPath => Tag.Name;
}

public sealed record WeaponModelVariant(
    int Index,
    uint StringId,
    string Name)
{
    public string Detail => $"Model variant {Index + 1:N0} · string-id 0x{StringId:X8}";
}

public sealed record WeaponVariantCatalog(
    RuntimeTagEntry Model,
    IReadOnlyList<WeaponModelVariant> Variants);

public sealed record StanchionDependencyFix(
    string FieldPath,
    string MissingReference,
    RuntimeTagEntry? Replacement,
    byte[]? SniperReferenceBytes,
    long SniperReferenceAddress,
    string? SniperReferenceDescription,
    long Address,
    byte[] OriginalBytes)
{
    public string ReplacementText => Replacement is not null
        ? $"→ [{Replacement.Group}] {Replacement.Name}"
        : SniperReferenceBytes is not null
            ? $"→ sniper rifle {SniperReferenceDescription}"
            : "No safe sniper-rifle match";
    public bool CanApply => Replacement is not null || SniperReferenceBytes is not null;
}

public sealed record StanchionEligibilityFix(
    long Address,
    uint OriginalFlags,
    uint ReplacementFlags)
{
    public const uint CannotBeUsedByPlayer = 1u << 22;
    public string Description =>
        "Clear weapon flag 22: cannot be used by player";
}

public sealed record StanchionImportPreview(
    LoadableWeapon Weapon,
    string AssetLoadMessage,
    int ValidReferenceCount,
    IReadOnlyList<StanchionDependencyFix> MissingReferences,
    StanchionEligibilityFix? EligibilityFix)
{
    public int SubstitutionCount => MissingReferences.Count(item => item.CanApply);
    public int UnresolvedCount => MissingReferences.Count(item => !item.CanApply);
    public int CompatibilityFixCount =>
        SubstitutionCount + (EligibilityFix is null ? 0 : 1);
    public bool IsReady => MissingReferences.Count == 0 && EligibilityFix is null;
    public bool CanApply => CompatibilityFixCount > 0 && UnresolvedCount == 0;
}

public sealed class WeaponLoaderService : IDisposable
{
    private const string StanchionAssetPath =
        "/Game/Tags/objects/Weapons/Rifle/sniper_rifle/stanchion-weapon";

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private int _warmedProcessId;

    public int ProcessId => _memory.ProcessId;

    public IReadOnlyList<LoadableWeapon> Connect()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to the game from the header first.");
        return Refresh();
    }

    public IReadOnlyList<LoadableWeapon> Refresh()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running game first.");
        _tags = _memory.ReadTags();
        LoadableWeapon[] weapons = _tags
            .Where(tag => string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(tag => new LoadableWeapon(ProjectileSwapperService.FriendlyName(tag), tag))
            .OrderBy(weapon => weapon.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(weapon => weapon.Tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (weapons.Length == 0)
            throw new InvalidDataException("No loaded [weap] tags were found in this mission.");
        return weapons;
    }

    /// <summary>
    /// Loads the schemas used by the variant inspector before the user selects
    /// a weapon, preventing a full definitions-directory parse on the UI thread.
    /// </summary>
    public void WarmUpDefinitions() => EnsureDefinitions();

    public async Task<ScriptExecutionResult> LoadAsync(
        LoadableWeapon selected,
        WeaponModelVariant? variant = null,
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        // Move one-time native hook install off the first pickup click.
        await WarmUpAsync(cancellationToken);

        RuntimeTagEntry live = _tags.FirstOrDefault(tag =>
                tag.Index == selected.Tag.Index &&
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tag.Name, selected.Tag.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "That weapon tag is no longer loaded. Refresh and select it again.");
        uint datum = RuntimeTagMemoryService.BuildRuntimeDatum(live);
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamWeaponLoad,
            datum.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        if (variant is not null &&
            variant.Index != 0 &&
            variant.StringId != 0)
            result = await ApplyVariantAsync(selected, variant, cancellationToken);

        return result;
    }

    /// <summary>
    /// Loads the native Blam bridge with a read-only player-position request.
    /// This moves the one-time DLL and bridge initialization work out of the
    /// first weapon pickup, where it otherwise stalls the simulation.
    /// </summary>
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
            // A build can expose weapon loading without the optional player
            // position capability. Keep loading functional when optional
            // prewarming is unavailable.
        }
    }

    public WeaponVariantCatalog ReadVariants(LoadableWeapon selected)
    {
        EnsureDefinitions();
        RuntimeTagEntry live = FindLiveWeapon(selected);
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            live.Group, live.DataAddress, _memory.ReadBytes, ResolveOrNull);
        RuntimeTagFieldValue modelReference = root.FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "model",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"{selected.Name} does not expose a model reference.");
        RuntimeTagEntry model = _tags.FirstOrDefault(tag =>
                tag.Index == modelReference.ReferencedTagIndex &&
                string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.RootCount > 0)
            ?? throw new InvalidDataException(
                $"{selected.Name} does not resolve to a loaded [hlmt] model.");

        IReadOnlyList<RuntimeTagFieldValue> modelRoot = _definitions.ReadRootFields(
            model.Group, model.DataAddress, _memory.ReadBytes, ResolveOrNull);
        RuntimeTagFieldValue variants = modelRoot.FirstOrDefault(field =>
                field.CanOpenBlock &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "model_variant_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"{selected.Name}'s model does not expose authored variants.");

        var result = new List<WeaponModelVariant>(variants.ChildCount);
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
                    CleanFieldName(field.Name),
                    "name",
                    StringComparison.OrdinalIgnoreCase));
            if (name is null) continue;
            uint stringId = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(name.Address, name.Size));
            result.Add(new WeaponModelVariant(
                index,
                stringId,
                FriendlyVariantName(live, stringId, index)));
        }

        if (result.Count == 0)
            throw new InvalidDataException(
                $"{selected.Name}'s model has no usable authored variants.");
        return new WeaponVariantCatalog(model, result);
    }

    public async Task<ScriptExecutionResult> ApplyVariantAsync(
        LoadableWeapon selected,
        WeaponModelVariant variant,
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        RuntimeTagEntry live = FindLiveWeapon(selected);
        WeaponVariantCatalog catalog = ReadVariants(selected);
        WeaponModelVariant current = catalog.Variants.FirstOrDefault(item =>
                item.Index == variant.Index &&
                item.StringId == variant.StringId)
            ?? throw new InvalidOperationException(
                "That model variant is no longer available. Refresh and select it again.");
        string selector = NormalizeWeaponSelector(live.LeafName);
        if (selector.Length == 0)
            throw new InvalidDataException(
                $"Could not derive an inventory selector for {selected.Name}.");

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamWeaponVariant,
            $"{selector},{current.StringId:X8}",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    public async Task<StanchionImportPreview> ImportStanchionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running game first.");
        EnsureDefinitions();

        RuntimeTagEntry? stanchion = FindStanchion(_tags);
        string loadMessage = "The Stanchion tag was already present in this mission.";
        if (stanchion is null)
        {
            ScriptingBridgeStatus status = _bridge.GetStatus();
            if (!status.IsRuntimeReady)
                throw new InvalidOperationException(
                    L.Get("bridge.error_not_responding_repair"));
            if (status.IsStale)
                throw new InvalidOperationException(status.Summary);

            ScriptExecutionResult loaded = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamTagAssetLoad,
                StanchionAssetPath,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (loaded.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(loaded.Message);
            loadMessage = loaded.Message;

            // The cooked-tag subsystem can publish the Blam entry a few frames after
            // Unreal finishes loading the data asset.
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                _tags = _memory.ReadTags();
                stanchion = FindStanchion(_tags);
                if (stanchion is not null) break;
                await Task.Delay(200, cancellationToken);
            }
            while (DateTimeOffset.UtcNow < deadline);
        }

        if (stanchion is null)
            throw new InvalidOperationException(
                "Unreal loaded the packaged Stanchion tag asset, but the game's cooked-tag " +
                "subsystem did not publish it into the live Blam tag table. No memory was " +
                "changed. This mission cannot safely import it through the engine-owned route.");

        RuntimeTagEntry sniper = FindSniperRifle(_tags)
            ?? throw new InvalidDataException(
                "The mission has no loaded sniper-rifle [weap] tag to use as the substitution template.");
        return AnalyzeStanchion(stanchion, sniper, loadMessage);
    }

    public StanchionImportPreview ApplyStanchionSubstitutions(StanchionImportPreview preview)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("The game is no longer connected.");
        if (preview.UnresolvedCount > 0)
            throw new InvalidOperationException(
                $"{preview.UnresolvedCount:N0} missing references have no unambiguous " +
                "sniper-rifle equivalent. Nothing was written.");
        if (preview.CompatibilityFixCount == 0)
            return preview;

        _tags = _memory.ReadTags();
        RuntimeTagEntry stanchion = FindStanchion(_tags)
            ?? throw new InvalidOperationException("The imported Stanchion tag is no longer loaded.");
        RuntimeTagEntry sniper = FindSniperRifle(_tags)
            ?? throw new InvalidOperationException("The sniper-rifle template is no longer loaded.");

        var completed = new List<(long Address, byte[] Bytes)>();
        try
        {
            foreach (StanchionDependencyFix fix in preview.MissingReferences)
            {
                byte[] current = _memory.ReadBytes(fix.Address, 16);
                if (!current.AsSpan().SequenceEqual(fix.OriginalBytes))
                    throw new InvalidOperationException(
                        $"{fix.FieldPath} changed after the preview. Refresh the import preview.");

                byte[] replacementBytes;
                if (fix.Replacement is { } replacement)
                {
                    RuntimeTagEntry liveReplacement = _tags.FirstOrDefault(tag =>
                            tag.Index == replacement.Index &&
                            RuntimeTagMemoryService.BuildRuntimeDatum(tag) ==
                            RuntimeTagMemoryService.BuildRuntimeDatum(replacement))
                        ?? throw new InvalidOperationException(
                            $"Replacement tag {replacement.Name} is no longer loaded.");
                    replacementBytes = _memory.BuildTagReference(liveReplacement);
                }
                else if (fix.SniperReferenceBytes is { } sniperBytes)
                {
                    byte[] liveSniperBytes = _memory.ReadBytes(fix.SniperReferenceAddress, 16);
                    if (!liveSniperBytes.AsSpan().SequenceEqual(sniperBytes))
                        throw new InvalidOperationException(
                            $"The sniper-rifle source for {fix.FieldPath} changed after the preview.");
                    replacementBytes = sniperBytes;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"{fix.FieldPath} has no verified replacement.");
                }

                _memory.WriteVerified(fix.Address, replacementBytes);
                completed.Add((fix.Address, current));
            }

            if (preview.EligibilityFix is { } eligibility)
            {
                byte[] current = _memory.ReadBytes(eligibility.Address, sizeof(uint));
                uint liveFlags = BinaryPrimitives.ReadUInt32LittleEndian(current);
                if (liveFlags != eligibility.OriginalFlags)
                    throw new InvalidOperationException(
                        "The Stanchion player-eligibility flags changed after the preview.");
                byte[] replacement = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32LittleEndian(
                    replacement, eligibility.ReplacementFlags);
                _memory.WriteVerified(eligibility.Address, replacement);
                completed.Add((eligibility.Address, current));
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

        StanchionImportPreview verified = AnalyzeStanchion(
            stanchion,
            sniper,
            $"Applied {completed.Count:N0} Stanchion compatibility fixes.");
        if (!verified.IsReady)
        {
            foreach ((long address, byte[] bytes) in completed.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(address, bytes); }
                catch { }
            }
            throw new InvalidDataException(
                "The Stanchion still needed compatibility fixes after verification; all writes were rolled back.");
        }
        return verified;
    }

    public ScriptingBridgeStatus BridgeStatus() => _bridge.GetStatus();

    public void Dispose() { }

    private StanchionImportPreview AnalyzeStanchion(
        RuntimeTagEntry stanchion,
        RuntimeTagEntry sniper,
        string loadMessage)
    {
        IReadOnlyList<TagReferenceSnapshot> stanchionReferences = ReadReferences(stanchion);
        IReadOnlyList<TagReferenceSnapshot> sniperReferences = ReadReferences(sniper);
        StanchionEligibilityFix? eligibilityFix =
            ReadEligibilityFix(stanchion, sniper);
        Dictionary<string, TagReferenceSnapshot> sniperByPath = sniperReferences
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var missing = new List<StanchionDependencyFix>();
        int valid = 0;
        foreach (TagReferenceSnapshot reference in stanchionReferences)
        {
            if (reference.IsNull) continue;
            if (reference.Target is not null)
            {
                valid++;
                continue;
            }

            RuntimeTagEntry? replacement = null;
            byte[]? sniperReferenceBytes = null;
            long sniperReferenceAddress = 0;
            string? sniperReferenceDescription = null;
            if (sniperByPath.TryGetValue(reference.Path, out TagReferenceSnapshot? exact) &&
                !exact.IsNull)
            {
                // Several references used by the stock sniper do not currently
                // resolve to a published table entry, but the engine still uses
                // them successfully. The exact schema-relative field is itself
                // the compatibility proof; some inherited Baboon allowed-group
                // metadata for [snd!] and [jpt!] is encoded as non-fourCC values.
                // If Stanchion already contains the identical reference, it is
                // covered by the known-good sniper template.
                if (reference.RawBytes.AsSpan().SequenceEqual(exact.RawBytes))
                {
                    valid++;
                    continue;
                }

                if (exact.Target is { } exactTarget)
                {
                    replacement = exactTarget;
                }
                else
                {
                    // Copy the complete reference from the same schema-relative
                    // sniper field. Do not reconstruct a datum whose target is not
                    // published in the mission's visible tag table.
                    sniperReferenceBytes = exact.RawBytes;
                    sniperReferenceAddress = exact.Field.Address;
                    sniperReferenceDescription = exact.ReferenceDescription;
                }
            }
            else
            {
                RuntimeTagEntry[] candidates = sniperReferences
                    .Where(item =>
                        item.Target is not null &&
                        string.Equals(
                            item.FieldName,
                            reference.FieldName,
                            StringComparison.OrdinalIgnoreCase) &&
                        _definitions.IsTagGroupCompatible(
                            item.Target.Group, reference.Field.AllowedTagGroups))
                    .Select(item => item.Target!)
                    .DistinctBy(item => item.Index)
                    .ToArray();
                if (candidates.Length == 1)
                    replacement = candidates[0];
            }

            missing.Add(new StanchionDependencyFix(
                reference.Path,
                reference.ReferenceDescription,
                replacement,
                sniperReferenceBytes,
                sniperReferenceAddress,
                sniperReferenceDescription,
                reference.Field.Address,
                reference.RawBytes));
        }

        return new StanchionImportPreview(
            new LoadableWeapon(ProjectileSwapperService.FriendlyName(stanchion), stanchion),
            loadMessage,
            valid,
            missing,
            eligibilityFix);
    }

    private StanchionEligibilityFix? ReadEligibilityFix(
        RuntimeTagEntry stanchion,
        RuntimeTagEntry sniper)
    {
        IReadOnlyList<RuntimeTagFieldValue> stanchionRoot = _definitions.ReadRootFields(
            stanchion.Group, stanchion.DataAddress, _memory.ReadBytes, ResolveOrNull);
        IReadOnlyList<RuntimeTagFieldValue> sniperRoot = _definitions.ReadRootFields(
            sniper.Group, sniper.DataAddress, _memory.ReadBytes, ResolveOrNull);
        RuntimeTagFieldValue? stanchionFlags = stanchionRoot.FirstOrDefault(field =>
            field.Type == "long_flags" &&
            string.Equals(field.Name, "flags", StringComparison.OrdinalIgnoreCase));
        RuntimeTagFieldValue? sniperFlags = sniperRoot.FirstOrDefault(field =>
            field.Type == "long_flags" &&
            string.Equals(field.Name, "flags", StringComparison.OrdinalIgnoreCase));
        if (stanchionFlags is null || sniperFlags is null)
            throw new InvalidDataException(
                "The [weap] schema did not expose the weapon eligibility flags.");

        uint stanchionValue = BinaryPrimitives.ReadUInt32LittleEndian(
            _memory.ReadBytes(stanchionFlags.Address, sizeof(uint)));
        uint sniperValue = BinaryPrimitives.ReadUInt32LittleEndian(
            _memory.ReadBytes(sniperFlags.Address, sizeof(uint)));
        uint mask = StanchionEligibilityFix.CannotBeUsedByPlayer;
        if ((stanchionValue & mask) == 0 || (sniperValue & mask) != 0)
            return null;
        return new StanchionEligibilityFix(
            stanchionFlags.Address,
            stanchionValue,
            stanchionValue & ~mask);
    }

    private IReadOnlyList<TagReferenceSnapshot> ReadReferences(RuntimeTagEntry tag)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            tag.Group, tag.DataAddress, _memory.ReadBytes, ResolveOrNull);
        var result = new List<TagReferenceSnapshot>();
        var visited = new HashSet<(string Definition, long Address, int Element)>();
        // Both weapons use the same [weap] schema. A schema-relative path makes
        // corresponding Stanchion and sniper-rifle fields directly comparable.
        Visit(root, "weap", 0);
        return result;

        void Visit(
            IReadOnlyList<RuntimeTagFieldValue> fields,
            string parentPath,
            int depth)
        {
            if (depth > 10 || result.Count >= 10_000) return;
            foreach (RuntimeTagFieldValue field in fields)
            {
                string fieldName = CleanFieldName(field.Name);
                string path = parentPath + " / " + fieldName;
                if (field.IsTagReference && field.Size == 16)
                {
                    byte[] raw;
                    try { raw = _memory.ReadBytes(field.Address, 16); }
                    catch { continue; }
                    uint datum = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
                    string group = DecodeReferenceGroup(raw);
                    bool isNull = datum == uint.MaxValue || IsAllZero(raw.AsSpan(0, 12));
                    RuntimeTagEntry? target = null;
                    if (!isNull)
                    {
                        int index = (ushort)datum;
                        target = _tags.FirstOrDefault(candidate =>
                            candidate.Index == index &&
                            RuntimeTagMemoryService.BuildRuntimeDatum(candidate) == datum &&
                            (group.Length == 0 ||
                             string.Equals(candidate.Group, group, StringComparison.OrdinalIgnoreCase)) &&
                            _definitions.IsTagGroupCompatible(
                                candidate.Group, field.AllowedTagGroups));
                    }
                    string description = group.Length == 0
                        ? $"datum 0x{datum:X8}"
                        : $"[{group}] datum 0x{datum:X8}";
                    result.Add(new TagReferenceSnapshot(
                        path, fieldName, group, field, raw, isNull, description, target));
                }

                if (!field.CanOpenBlock || depth == 10) continue;
                int elements = Math.Min(field.ChildCount, 256);
                for (int element = 0; element < elements; element++)
                {
                    var key = (field.ChildBlockDefinition!, field.ChildAddress, element);
                    if (!visited.Add(key)) continue;
                    IReadOnlyList<RuntimeTagFieldValue> children;
                    try
                    {
                        children = _definitions.ReadBlockFields(
                            tag.Group,
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
                    Visit(children, $"{path}[{element}]", depth + 1);
                }
            }
        }
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("weap") || !_definitions.HasSchema("hlmt"))
            throw new InvalidDataException(
                "The loaded definitions do not provide the [weap] and [hlmt] schemas.");
    }

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private bool ReferenceGroupCompatible(
        TagReferenceSnapshot reference,
        IReadOnlyCollection<string> allowedGroups) =>
        reference.ReferenceGroup.Length == 0 ||
        _definitions.IsTagGroupCompatible(reference.ReferenceGroup, allowedGroups);

    private static RuntimeTagEntry? FindStanchion(IEnumerable<RuntimeTagEntry> tags) =>
        tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(tag.LeafName, "stanchion", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(tag.LeafName, "stanchion-weapon", StringComparison.OrdinalIgnoreCase)));

    private static RuntimeTagEntry? FindSniperRifle(IEnumerable<RuntimeTagEntry> tags) =>
        tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tag.LeafName, "sniper_rifle", StringComparison.OrdinalIgnoreCase));

    private static string DecodeReferenceGroup(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return "";
        Span<byte> group = stackalloc byte[4];
        bytes[..4].CopyTo(group);
        group.Reverse();
        return Encoding.ASCII.GetString(group).Trim('\0', ' ', '\u00FF');
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            if (value != 0) return false;
        return true;
    }

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private RuntimeTagEntry FindLiveWeapon(LoadableWeapon selected) =>
        _tags.FirstOrDefault(tag =>
                tag.Index == selected.Tag.Index &&
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tag.Name, selected.Tag.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "That weapon tag is no longer loaded. Refresh and select it again.");

    private static string NormalizeWeaponSelector(string value) =>
        new(value
            .Where(character => char.IsAsciiLetterOrDigit(character))
            .Select(char.ToLowerInvariant)
            .ToArray());

    private string FriendlyVariantName(RuntimeTagEntry weapon, uint stringId, int index)
    {
        if (_memory.TryGetStringIdName(stringId, out string? authored) &&
            !string.IsNullOrWhiteSpace(authored))
            return authored!;

        if (index == 0) return "Default";
        string leaf = NormalizeWeaponSelector(weapon.LeafName);
        return (leaf, index) switch
        {
            ("assaultrifle", 1) => "Flawless AR",
            ("assaultrifle", 3) => "Gilded Onyx / Milestone",
            ("assaultrifle", 4) => "Reflex Mix",
            ("assaultrifle", 7) => "Promotional AR (red)",
            ("assaultrifle", 10) => "Silver Anniversary",
            ("battlerifle", 1) => "Laconian Lance",
            ("energysword", 2) => "Subanese Fang",
            ("flakcannon", 1) or ("fuelrodcannon", 1) => "Colossus Fuel Rod Cannon",
            ("magnum", 1) => "Cold Iron: Keyes",
            ("needler", 1) => "Stone Needler",
            ("sniperrifle", 2) or ("stanchion", 2) => "Savage Tooth",
            ("rocketlauncher", 1) or ("spnkr", 1) => "Proto Type SPNKr",
            _ => $"Variant {index + 1:00}",
        };
    }

    private sealed record TagReferenceSnapshot(
        string Path,
        string FieldName,
        string ReferenceGroup,
        RuntimeTagFieldValue Field,
        byte[] RawBytes,
        bool IsNull,
        string ReferenceDescription,
        RuntimeTagEntry? Target);
}
