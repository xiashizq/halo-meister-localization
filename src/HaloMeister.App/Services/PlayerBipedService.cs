using HaloMeister.App.Models;
using HaloMeister.App.Localization;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HaloMeister.App.Services;

public sealed record PlayerBipedChoice(
    string Name,
    string Category,
    RuntimeTagEntry BipedTag,
    bool IsOriginal)
{
    public string TagPath => BipedTag.Name.Replace('\\', '/');
    public string Detail => $"{Category} · {TagPath}";
}

public sealed record PlayerBipedVariantChoice(
    string Name,
    byte[] StringIdBytes,
    bool IsDefault)
{
    public uint StringId => StringIdBytes.Length == sizeof(uint)
        ? BinaryPrimitives.ReadUInt32LittleEndian(StringIdBytes)
        : 0;
}

public sealed record PlayerBipedSession(
    IReadOnlyList<PlayerBipedChoice> Choices);

public sealed record BumpPossessionResult(
    ScriptExecutionResult Spawn,
    bool Transferred,
    int? ActiveTagIndex);

public sealed class PlayerBipedService : IDisposable
{
    public const string CharacterOverlayStem = "ZZ_HM_BIPED_REDIRECT_P";
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly PlayerToolsService _playerTools = new();
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private readonly List<AppliedMemorySnapshot> _tagPatch = [];
    private long _capturedGlobalsNameAddress;
    private int _warmedProcessId;

    public int ProcessId => _memory.ProcessId;
    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();
    public bool CanRestore =>
        _memory.IsConnected &&
        _tagPatch.Count > 0 &&
        _tags.Any(tag => tag.NameAddress == _capturedGlobalsNameAddress &&
                         string.Equals(tag.Group, "matg", StringComparison.OrdinalIgnoreCase));

    public PlayerBipedSession Connect()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("bipd"))
            throw new InvalidDataException(L.Get("change_biped.error_definitions_missing"));

        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("change_biped.error_connect_game_first"));
        _tags = _memory.ReadTags();
        return BuildSession();
    }

    public PlayerBipedSession Refresh()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("change_biped.error_connect_game_first"));
        _tags = _memory.ReadTags();
        return BuildSession();
    }

    public async Task<BumpPossessionResult> SpawnForBumpPossessionAsync(
        PlayerBipedChoice choice,
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        RuntimeTagEntry target = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index && IsUsableBiped(tag))
            ?? throw new InvalidOperationException(L.Get("change_biped.error_character_not_loaded"));
        uint datum = RuntimeTagMemoryService.BuildRuntimeDatum(target);
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamBipedPossess,
            datum.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        int? activeTagIndex = null;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                activeTagIndex = await _playerTools.ReadActivePlayerTagIndexAsync(cancellationToken);
                if (activeTagIndex == target.Index)
                    return new BumpPossessionResult(result, Transferred: true, activeTagIndex);

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch
        {
            await DisableBumpPossessionAsync(CancellationToken.None);
            throw;
        }

        await DisableBumpPossessionAsync(CancellationToken.None);
        return new BumpPossessionResult(result, Transferred: false, activeTagIndex);
    }

    public async Task<ScriptExecutionResult> DisableBumpPossessionAsync(
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamBumpPossessionOff,
            "off",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    /// <summary>
    /// Patches only globals/globals.globals. The change takes effect when the
    /// player representation is created again; it never writes scenario data.
    /// </summary>
    public TagBipedPatchResult ApplyTagRedirect(PlayerBipedChoice choice)
    {
        EnsureTagPatchReady();
        if (_tagPatch.Count > 0)
            throw new InvalidOperationException(L.Get("change_biped.error_redirect_already_active"));

        _tags = _memory.ReadTags();
        RuntimeTagEntry target = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index && IsUsableBiped(tag))
            ?? throw new InvalidOperationException(L.Get("change_biped.error_character_not_loaded"));
        RuntimeTagEntry globals = FindGlobalsMatg();
        PlayerRepresentationLocation representation = FindPlayerRepresentations(globals)
            .Select(location => (Location: location, Score: PlayerNameScore(location.Biped.Name) - location.ElementIndex))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Location.ElementIndex)
            .Select(item => item.Location)
            .FirstOrDefault()
            ?? throw new InvalidDataException(L.Get("change_biped.error_player_data_missing"));
        RuntimeTagFieldValue customizationGlobals = FindCustomizationGlobals(globals);

        byte[] unit = _memory.BuildTagReference(target);
        byte[] variant = ReadDefaultModelVariant(target);
        byte[] clearedCustomization = BuildNullTagReference();
        var writes = new[]
        {
            CreatePatch(representation.Unit, unit),
            CreatePatch(representation.Variant, variant),
            CreatePatch(customizationGlobals, clearedCustomization),
        };
        _memory.ApplyTransaction(writes.Select(write =>
            new RuntimeMemoryWrite(write.Address, write.OriginalBytes, write.AppliedBytes)));
        _tagPatch.AddRange(writes);
        _capturedGlobalsNameAddress = globals.NameAddress;

        uint variantId = BinaryPrimitives.ReadUInt32LittleEndian(variant);
        string variantName = _memory.TryGetStringIdName(variantId, out string? name)
            ? name!
            : $"0x{variantId:X8}";
        return new TagBipedPatchResult(target.Name, variantName);
    }

    public void RestoreTagRedirect()
    {
        if (!CanRestore)
            throw new InvalidOperationException(L.Get("change_biped.error_nothing_to_restore"));

        _memory.ApplyTransaction(_tagPatch.Select(write =>
            new RuntimeMemoryWrite(write.Address, write.AppliedBytes, write.OriginalBytes)));
        _tagPatch.Clear();
        _capturedGlobalsNameAddress = 0;
    }

    public async Task<ScriptExecutionResult> RespawnFromTagRedirectAsync(
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            "unit_kill (player_get 0)",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Failed)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    public IReadOnlyList<PlayerBipedVariantChoice> ReadVariants(PlayerBipedChoice choice)
    {
        EnsureTagPatchReady();
        if (_tags.Count == 0 ||
            !_tags.Any(tag => tag.Index == choice.BipedTag.Index && IsUsableBiped(tag)))
            _tags = _memory.ReadTags();
        RuntimeTagEntry target = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index && IsUsableBiped(tag))
            ?? throw new InvalidOperationException(L.Get("change_biped.error_character_not_loaded"));

        byte[] defaultBytes = ReadDefaultModelVariant(target);
        uint defaultId = BinaryPrimitives.ReadUInt32LittleEndian(defaultBytes);
        string defaultName = FormatVariantName(defaultId, L.Get("change_biped.default_variant"));

        var variants = new List<PlayerBipedVariantChoice>();
        RuntimeTagEntry? model = FindBipedModel(target);
        if (model is not null)
        {
            foreach (PlayerBipedVariantChoice variant in ReadModelVariants(model))
            {
                bool isDefault = variant.StringId == defaultId;
                variants.Add(variant with
                {
                    Name = isDefault
                        ? $"{variant.Name} ({L.Get("change_biped.default_variant")})"
                        : variant.Name,
                    IsDefault = isDefault,
                });
            }
        }

        if (variants.Count == 0)
        {
            variants.Add(new PlayerBipedVariantChoice(
                defaultName,
                defaultBytes,
                IsDefault: true));
        }
        else if (!variants.Any(variant => variant.IsDefault))
        {
            variants.Insert(0, new PlayerBipedVariantChoice(
                defaultName,
                defaultBytes,
                IsDefault: true));
        }

        return variants
            .OrderByDescending(variant => variant.IsDefault)
            .ThenBy(variant => variant.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Creates a persistent cooked-tag patch for the selected biped. The
    /// result is intentionally semantic: no runtime datum or arena address is
    /// carried into the exported IoStore overlay.
    /// </summary>
    public RuntimeTagModDocument BuildTagRedirectMod(
        PlayerBipedChoice choice,
        PlayerBipedVariantChoice? variantChoice = null)
    {
        EnsureTagPatchReady();
        _tags = _memory.ReadTags();
        RuntimeTagEntry target = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index && IsUsableBiped(tag))
            ?? throw new InvalidOperationException(L.Get("change_biped.error_character_not_loaded"));
        RuntimeTagEntry globals = FindGlobalsMatg();
        (PlayerRepresentationLocation Location, RuntimeTagFieldValue Block) representation =
            FindPlayerRepresentationPatchTarget(globals);
        RuntimeTagFieldValue customization = FindCustomizationGlobals(globals);

        uint variantId = variantChoice?.StringId
            ?? BinaryPrimitives.ReadUInt32LittleEndian(ReadDefaultModelVariant(target));
        if (!_memory.TryGetStringIdName(variantId, out string? variantName) ||
            string.IsNullOrWhiteSpace(variantName) ||
            string.Equals(variantName, "NONE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.Get("change_biped.error_variant_unavailable"));

        List<RuntimeTagModBlockStep> blocks =
        [
            new RuntimeTagModBlockStep
            {
                Offset = representation.Block.Offset,
                Definition = representation.Block.ChildBlockDefinition!,
                Element = representation.Location.ElementIndex,
                ElementSize = representation.Block.ChildElementSize,
            },
        ];
        return new RuntimeTagModDocument
        {
            Name = $"Character redirect: {target.Name} / {variantName}",
            GameBuildId = RuntimeTagEditSessionService.SupportedBuildId,
            Tags =
            [
                new RuntimeTagModTag
                {
                    Group = globals.Group,
                    Name = globals.Name,
                    Patches =
                    [
                        new RuntimeTagModPatch
                        {
                            Field = representation.Location.Unit.Name,
                            Type = representation.Location.Unit.Type,
                            Offset = representation.Location.Unit.Offset,
                            Size = representation.Location.Unit.Size,
                            Blocks = blocks,
                            ReferenceGroup = target.Group,
                            ReferenceName = target.Name,
                        },
                        new RuntimeTagModPatch
                        {
                            Field = representation.Location.Variant.Name,
                            Type = representation.Location.Variant.Type,
                            Offset = representation.Location.Variant.Offset,
                            Size = representation.Location.Variant.Size,
                            Blocks = blocks,
                            StringIdName = variantName,
                        },
                        new RuntimeTagModPatch
                        {
                            Field = customization.Name,
                            Type = customization.Type,
                            Offset = customization.Offset,
                            Size = customization.Size,
                            ClearReference = true,
                        },
                    ],
                },
            ],
        };
    }

    public async Task<NativeTagModExportResult> ExportTagRedirectOverlayAsync(
        PlayerBipedChoice choice,
        PlayerBipedVariantChoice? variantChoice = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeTagModDocument mod = BuildTagRedirectMod(choice, variantChoice);
        string outputDirectory = GetCharacterOverlayDirectory();
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, CharacterOverlayStem + ".utoc");
        var exporter = new NativeTagModExportService();
        return await exporter.ExportAsync(mod, output);
    }

    public NativeTagModInstallResult InstallTagRedirectOverlay(string utocPath)
    {
        string stem = Path.GetFileNameWithoutExtension(utocPath);
        string localDir = GetCharacterOverlayDirectory();
        if (IsCharacterOverlayExpired(localDir, null, stem, existsLocally: true))
            throw new InvalidOperationException(
                L.Get("change_biped.character_overlay_expired_blocked"));
        return new NativeTagModExportService().ReplaceManagedOverlay(utocPath, stem);
    }

    public IReadOnlyList<string> RemoveTagRedirectOverlay(string stem)
        => new NativeTagModExportService().RemoveManagedOverlay(stem);

    public bool IsTagRedirectOverlayInstalled()
        => new NativeTagModExportService().IsManagedOverlayInstalled(CharacterOverlayStem);

    public IReadOnlyList<string> DeleteCharacterOverlayPackage(string stem)
    {
        string directory = GetCharacterOverlayDirectory();
        var removed = new List<string>();
        foreach (string extension in new[] { ".utoc", ".ucas", ".pak", ".hmtagmod" })
        {
            string path = Path.Combine(directory, stem + extension);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed.Add(path);
        }
        if (removed.Count == 0)
            throw new FileNotFoundException(L.Get("change_biped.character_overlay_not_found"));
        return removed;
    }

    public IReadOnlyList<CharacterOverlayPackage> GetCharacterOverlayPackages()
    {
        string localDir = GetCharacterOverlayDirectory();
        var exporter = new NativeTagModExportService();
        string? paks = exporter.TryResolvePaksDirectory();
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(localDir))
        {
            foreach (string utoc in Directory.EnumerateFiles(localDir, "*_P.utoc"))
                stems.Add(Path.GetFileNameWithoutExtension(utoc));
        }

        if (paks is not null)
        {
            if (NativeTagModExportService.HasCompleteTriplet(paks, CharacterOverlayStem))
                stems.Add(CharacterOverlayStem);
            foreach (string utoc in Directory.EnumerateFiles(paks, "ZZ_HM_BIPED*_P.utoc"))
                stems.Add(Path.GetFileNameWithoutExtension(utoc));
        }

        return stems
            .Select(stem =>
            {
                string? sourceUtoc = Path.Combine(localDir, stem + ".utoc");
                bool existsLocally = Directory.Exists(localDir) &&
                    NativeTagModExportService.HasCompleteTriplet(localDir, stem);
                if (!existsLocally) sourceUtoc = null;

                bool isInstalled = paks is not null &&
                    NativeTagModExportService.HasCompleteTriplet(paks, stem);
                // Allow uninstall for complete or partial installs that only
                // exist under Meteorite/Content/Paks (no local package copy).
                bool hasInstalledFiles = paks is not null &&
                    NativeTagModExportService.HasAnyOverlayFiles(paks, stem);
                DateTime modified = DateTime.MinValue;
                if (existsLocally && sourceUtoc is not null)
                    modified = File.GetLastWriteTime(sourceUtoc);
                else if (hasInstalledFiles && paks is not null)
                {
                    string installedUtoc = Path.Combine(paks, stem + ".utoc");
                    if (File.Exists(installedUtoc))
                        modified = File.GetLastWriteTime(installedUtoc);
                }

                bool contentsMatch = false;
                if (existsLocally && isInstalled && paks is not null)
                {
                    string? localFingerprint = TryFingerprintOverlayTriplet(localDir, stem);
                    string? installedFingerprint = TryFingerprintOverlayTriplet(paks, stem);
                    contentsMatch =
                        localFingerprint is not null &&
                        installedFingerprint is not null &&
                        string.Equals(
                            localFingerprint,
                            installedFingerprint,
                            StringComparison.Ordinal);
                }

                bool isExpired = IsCharacterOverlayExpired(localDir, paks, stem, existsLocally);

                // Install only when a local package exists, matches the current
                // game build, and either nothing is installed or files differ.
                bool canInstall =
                    existsLocally &&
                    !isExpired &&
                    !string.IsNullOrWhiteSpace(sourceUtoc) &&
                    (!isInstalled || !contentsMatch);

                bool installedOnly = !existsLocally && hasInstalledFiles;
                string status = isExpired
                    ? L.Get("change_biped.overlay_status_expired")
                    : (existsLocally, isInstalled, contentsMatch) switch
                {
                    (true, true, true) => L.Get("change_biped.overlay_status_local_installed"),
                    (true, true, false) => L.Get("change_biped.overlay_status_local_outdated_install"),
                    (true, false, _) => L.Get("change_biped.overlay_status_local_only"),
                    (false, true, _) => L.Get("change_biped.overlay_status_installed_only"),
                    _ when installedOnly => L.Get("change_biped.overlay_status_installed_only"),
                    _ => L.Get("change_biped.overlay_status_unknown"),
                };

                return new CharacterOverlayPackage(
                    stem,
                    sourceUtoc,
                    modified,
                    existsLocally,
                    isInstalled || hasInstalledFiles,
                    IsExpired: isExpired,
                    CanInstall: canInstall,
                    // Keep enabled for installed-only packs; click path reports
                    // file-in-use if the game still holds the overlay open.
                    CanUninstall: hasInstalledFiles,
                    CanDelete: existsLocally,
                    status);
            })
            .OrderByDescending(package => package.Modified)
            .ThenBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Character packs are stamped with <see cref="RuntimeTagEditSessionService.SupportedBuildId"/>
    /// at export time. Anything older or missing that stamp is unusable after a
    /// Campaign Evolved content update.
    /// </summary>
    private static bool IsCharacterOverlayExpired(
        string localDir,
        string? paks,
        string stem,
        bool existsLocally)
    {
        string? sidecar = null;
        if (existsLocally)
        {
            string localSidecar = Path.Combine(localDir, stem + ".hmtagmod");
            if (File.Exists(localSidecar))
                sidecar = localSidecar;
        }
        if (sidecar is null && paks is not null)
        {
            string installedSidecar = Path.Combine(paks, stem + ".hmtagmod");
            if (File.Exists(installedSidecar))
                sidecar = installedSidecar;
        }

        if (sidecar is null)
            return true;

        try
        {
            RuntimeTagModDocument document = new RuntimeTagModService().Load(sidecar);
            return !string.Equals(
                document.GameBuildId,
                RuntimeTagEditSessionService.SupportedBuildId,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static string GetCharacterOverlayDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister", "CharacterOverlays");

    private static string? TryFingerprintOverlayTriplet(string directory, string stem)
    {
        try
        {
            var parts = new List<string>(3);
            foreach (string extension in new[] { ".utoc", ".ucas", ".pak" })
            {
                string path = Path.Combine(directory, stem + extension);
                byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
                parts.Add($"{new FileInfo(path).Length}:{Convert.ToHexString(hash)}");
            }
            return string.Join('|', parts);
        }
        catch
        {
            return null;
        }
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
            // Optional prewarm; a player-position bridge operation is not
            // available on every supported build.
        }
    }

    public void Dispose() { }

    private PlayerBipedSession BuildSession()
    {
        // Runtime can expose the same biped twice with mixed separators
        // (objects/characters/... vs objects\characters\...). Keep one entry.
        PlayerBipedChoice[] choices = _tags
            .Where(IsUsableBiped)
            .GroupBy(tag => Normalize(tag.Name), StringComparer.Ordinal)
            .Select(group => PreferBipedTag(group))
            .Select(tag => new PlayerBipedChoice(
                DisplayName(tag),
                Categorize(tag.Name),
                tag,
                IsOriginal: false))
            .OrderBy(choice => CategoryOrder(choice.Category))
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.TagPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
            throw new InvalidDataException(L.Get("change_biped.error_no_characters"));
        return new PlayerBipedSession(choices);
    }

    private static RuntimeTagEntry PreferBipedTag(IEnumerable<RuntimeTagEntry> group) =>
        group
            .OrderByDescending(tag => tag.Name.Contains('/') ? 1 : 0)
            .ThenByDescending(tag => tag.RootCount)
            .ThenByDescending(tag => tag.DataAddress > 0 ? 1 : 0)
            .ThenBy(tag => tag.Index)
            .First();

    private void EnsureTagPatchReady()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("change_biped.error_connect_game_first"));
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("matg") || !_definitions.HasSchema("bipd"))
            throw new InvalidDataException(L.Get("change_biped.error_definitions_missing"));
    }

    private RuntimeTagFieldValue FindCustomizationGlobals(RuntimeTagEntry globals)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            globals.Group,
            globals.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);
        RuntimeTagFieldValue field = root.FirstOrDefault(item =>
                item.IsTagReference &&
                string.Equals(
                    CleanFieldName(item.Name),
                    "player model customization globals",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(L.Get("change_biped.error_player_data_missing"));
        if (field.Size != 16)
            throw new InvalidDataException(L.Get("change_biped.error_player_data_invalid"));
        return field;
    }

    private RuntimeTagEntry FindGlobalsMatg()
    {
        RuntimeTagEntry[] candidates = _tags
            .Where(tag =>
                string.Equals(tag.Group, "matg", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            .ToArray();
        RuntimeTagEntry? exact = candidates.FirstOrDefault(tag =>
        {
            string path = Normalize(tag.Name);
            return path is "globals/globals" or "globals/globals.globals" ||
                   path.EndsWith("/globals/globals", StringComparison.Ordinal) ||
                   path.EndsWith("/globals/globals.globals", StringComparison.Ordinal);
        });
        if (exact is not null) return exact;

        return candidates
                .OrderByDescending(tag =>
                    Normalize(tag.Name).Contains("globals", StringComparison.Ordinal) ? 1 : 0)
                .FirstOrDefault()
            ?? throw new InvalidDataException(L.Get("change_biped.error_player_data_missing"));
    }

    private IEnumerable<PlayerRepresentationLocation> FindPlayerRepresentations(
        RuntimeTagEntry? ownerFilter = null)
    {
        IEnumerable<RuntimeTagEntry> owners = ownerFilter is not null
            ? [ownerFilter]
            : _tags.Where(tag =>
                tag.DataAddress > 0 &&
                (string.Equals(tag.Group, "matg", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase)));

        foreach (RuntimeTagEntry owner in owners)
        {
            IReadOnlyList<RuntimeTagFieldValue> root;
            try
            {
                root = _definitions.ReadRootFields(
                    owner.Group,
                    owner.DataAddress,
                    _memory.ReadBytes,
                    ResolveOrNull);
            }
            catch
            {
                continue;
            }

            foreach (RuntimeTagFieldValue block in root.Where(field =>
                         field.CanOpenBlock &&
                         string.Equals(
                             field.ChildBlockDefinition,
                             "player_representation_block",
                             StringComparison.OrdinalIgnoreCase)))
            {
                for (int element = 0; element < block.ChildCount; element++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> fields;
                    try
                    {
                        fields = _definitions.ReadBlockFields(
                            owner.Group,
                            block.ChildBlockDefinition!,
                            block.ChildAddress,
                            element,
                            _memory.ReadBytes,
                            ResolveOrNull);
                    }
                    catch
                    {
                        continue;
                    }

                    RuntimeTagFieldValue? unit = fields.FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "third person unit",
                            StringComparison.OrdinalIgnoreCase));
                    RuntimeTagFieldValue? variant = fields.FirstOrDefault(field =>
                        field.Type == "string_id" &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "third person variant",
                            StringComparison.OrdinalIgnoreCase));
                    RuntimeTagEntry? biped = unit is null
                        ? null
                        : _tags.FirstOrDefault(tag =>
                            tag.Index == unit.ReferencedTagIndex &&
                            IsUsableBiped(tag));
                    if (unit is null || variant is null || variant.Size != 4 || biped is null)
                        continue;

                    yield return new PlayerRepresentationLocation(
                        owner.Group,
                        element,
                        biped,
                        unit,
                        variant);
                }
            }
        }
    }

    private (PlayerRepresentationLocation Location, RuntimeTagFieldValue Block)
        FindPlayerRepresentationPatchTarget(RuntimeTagEntry globals)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            globals.Group, globals.DataAddress, _memory.ReadBytes, ResolveOrNull);
        RuntimeTagFieldValue block = root.FirstOrDefault(field =>
                field.CanOpenBlock &&
                string.Equals(field.ChildBlockDefinition, "player_representation_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(L.Get("change_biped.error_player_data_missing"));
        PlayerRepresentationLocation location = FindPlayerRepresentations(globals)
            .Select(item => (Location: item, Score: PlayerNameScore(item.Biped.Name) - item.ElementIndex))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Location.ElementIndex)
            .Select(item => item.Location)
            .FirstOrDefault()
            ?? throw new InvalidDataException(L.Get("change_biped.error_player_data_missing"));
        return (location, block);
    }

    private static byte[] BuildNullTagReference()
    {
        // Guerilla-style cleared tag_reference: invalid group + invalid datum.
        byte[] reference = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(reference.AsSpan(0), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(reference.AsSpan(12), uint.MaxValue);
        return reference;
    }

    private byte[] ReadDefaultModelVariant(RuntimeTagEntry biped)
    {
        IReadOnlyList<RuntimeTagFieldValue> fields = _definitions.ReadRootFields(
            biped.Group,
            biped.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);
        RuntimeTagFieldValue variant = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(L.Get("change_biped.error_variant_unavailable"));
        if (variant.Size != 4)
            throw new InvalidDataException(L.Get("change_biped.error_variant_unavailable"));
        return _memory.ReadBytes(variant.Address, variant.Size);
    }

    private RuntimeTagEntry? FindBipedModel(RuntimeTagEntry biped)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            biped.Group,
            biped.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);
        RuntimeTagFieldValue? modelReference = root.FirstOrDefault(field =>
            field.IsTagReference &&
            string.Equals(
                CleanFieldName(field.Name),
                "model",
                StringComparison.OrdinalIgnoreCase));
        if (modelReference is null)
            return null;
        return _tags.FirstOrDefault(tag =>
            tag.Index == modelReference.ReferencedTagIndex &&
            string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0 &&
            tag.RootCount > 0);
    }

    private IEnumerable<PlayerBipedVariantChoice> ReadModelVariants(RuntimeTagEntry model)
    {
        IReadOnlyList<RuntimeTagFieldValue> modelRoot = _definitions.ReadRootFields(
            model.Group,
            model.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);
        RuntimeTagFieldValue? variants = modelRoot.FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "model_variant_block",
                StringComparison.OrdinalIgnoreCase));
        if (variants is null || variants.ChildCount <= 0)
            yield break;

        for (int index = 0; index < variants.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields = _definitions.ReadBlockFields(
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
            if (name is null)
                continue;

            byte[] bytes = _memory.ReadBytes(name.Address, name.Size);
            uint stringId = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            yield return new PlayerBipedVariantChoice(
                FormatVariantName(stringId, L.Format("change_biped.variant_numbered", index + 1)),
                bytes,
                IsDefault: false);
        }
    }

    private string FormatVariantName(uint stringId, string fallback)
    {
        if (_memory.TryGetStringIdName(stringId, out string? name) &&
            !string.IsNullOrWhiteSpace(name) &&
            !string.Equals(name, "NONE", StringComparison.OrdinalIgnoreCase))
            return name!;
        return fallback;
    }

    private AppliedMemorySnapshot CreatePatch(RuntimeTagFieldValue field, byte[] value)
    {
        if (field.Size != value.Length || !_memory.IsWritable(field.Address, field.Size))
            throw new UnauthorizedAccessException(L.Get("change_biped.error_memory_not_writable"));
        return new AppliedMemorySnapshot(
            field.Address,
            _memory.ReadBytes(field.Address, field.Size),
            value);
    }

    private static bool IsUsableBiped(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0 &&
        !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase) &&
        !tag.Name.Contains("/stimuli/", StringComparison.OrdinalIgnoreCase);

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static int PlayerNameScore(string name)
    {
        string value = Normalize(name);
        int score = 0;
        if (value.Contains("masterchief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("master_chief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("chief", StringComparison.Ordinal)) score += 60;
        if (value.Contains("player", StringComparison.Ordinal)) score += 40;
        if (value.Contains("spartan", StringComparison.Ordinal)) score += 30;
        return score;
    }

    private static string Categorize(string path)
    {
        string value = Normalize(path);
        if (value.Contains("flood", StringComparison.Ordinal) ||
            value.Contains("infection", StringComparison.Ordinal) ||
            value.Contains("combat_form", StringComparison.Ordinal)) return "Flood";
        if (value.Contains("elite", StringComparison.Ordinal)) return "Elite";
        if (value.Contains("grunt", StringComparison.Ordinal)) return "Grunt";
        if (value.Contains("jackal", StringComparison.Ordinal) ||
            value.Contains("skirmisher", StringComparison.Ordinal)) return "Jackal";
        if (value.Contains("hunter", StringComparison.Ordinal)) return "Hunter";
        if (value.Contains("marine", StringComparison.Ordinal) ||
            value.Contains("crewman", StringComparison.Ordinal)) return "Human";
        if (value.Contains("chief", StringComparison.Ordinal) ||
            value.Contains("spartan", StringComparison.Ordinal)) return "Spartan";
        if (value.Contains("sentinel", StringComparison.Ordinal) ||
            value.Contains("monitor", StringComparison.Ordinal)) return "Forerunner";
        return "Other";
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Spartan" => 0,
        "Elite" => 1,
        "Grunt" => 2,
        "Jackal" => 3,
        "Hunter" => 4,
        "Flood" => 5,
        "Human" => 6,
        "Forerunner" => 7,
        _ => 8,
    };

    private static string DisplayName(RuntimeTagEntry tag)
    {
        string value = Normalize(tag.Name);
        if (value.Contains("meteorite", StringComparison.Ordinal) ||
            value.Contains("prequel", StringComparison.Ordinal) ||
            value.Contains("mkiv", StringComparison.Ordinal) ||
            value.Contains("mark_iv", StringComparison.Ordinal))
            return "Mark IV (prequel mission)";
        return Humanize(tag.LeafName);
    }

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private static string Normalize(string value) =>
        value.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("-", "_", StringComparison.Ordinal)
            .ToLowerInvariant();

    private static string Humanize(string value)
    {
        string text = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed biped"
            : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private sealed record AppliedMemorySnapshot(
        long Address,
        byte[] OriginalBytes,
        byte[] AppliedBytes);

    private sealed record PlayerRepresentationLocation(
        string OwnerGroup,
        int ElementIndex,
        RuntimeTagEntry Biped,
        RuntimeTagFieldValue Unit,
        RuntimeTagFieldValue Variant);
}

public sealed record TagBipedPatchResult(string TagPath, string VariantName);
public sealed record CharacterOverlayPackage(
    string Name,
    string? SourceUtocPath,
    DateTime Modified,
    bool ExistsLocally,
    bool IsInstalled,
    bool IsExpired,
    bool CanInstall,
    bool CanUninstall,
    bool CanDelete,
    string Status);
