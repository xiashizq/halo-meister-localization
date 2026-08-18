using System.Collections.ObjectModel;
using System.ComponentModel;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class AiBattlePage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly AllegianceDemoService _demo = new();
    private readonly FullPalettesOverlayService _builtinMod = new();
    private readonly DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };
    private readonly ObservableCollection<BattleRosterItem> _friendly = [];
    private readonly ObservableCollection<BattleRosterItem> _hostile = [];
    private IReadOnlyList<EnemySpawnChoice> _characters = [];
    private bool _busy;
    private bool _modNeedsUpdate;
    private int _modSyncBusy;

    public AiBattlePage()
    {
        InitializeComponent();
        FriendlyList.ItemsSource = _friendly;
        HostileList.ItemsSource = _hostile;
        _game.ConnectionChanged += OnConnectionChanged;
        _statusTimer.Tick += OnStatusTick;
        UpdateControls();
    }

    public void OnActivated()
    {
        UpdateControls();
        _statusTimer.Start();
        _ = RefreshBuiltinModSyncAsync();
        if (_game.IsConnected && _builtinMod.IsInstalled() && _characters.Count == 0)
            _ = ScanQuietAsync();
    }

    public void OnDeactivated() => _statusTimer.Stop();

    private void OnOpenBuiltinMod(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.NavigateTo("builtin-mod");

    private async void OnAddFriendly(object sender, RoutedEventArgs e) =>
        await AddCharacterAsync(friendly: true);

    private async void OnAddHostile(object sender, RoutedEventArgs e) =>
        await AddCharacterAsync(friendly: false);

    private async Task AddCharacterAsync(bool friendly)
    {
        if (_busy || !_game.IsConnected || !EnsureBuiltinMod())
            return;

        if (_characters.Count == 0)
        {
            await RunBusy(async () =>
            {
                await EnsureCatalogAsync();
                AddRow(friendly);
            });
            return;
        }

        AddRow(friendly);
    }

    private void AddRow(bool friendly)
    {
        EnemySpawnChoice? choice = friendly
            ? FindPreferred(_characters, "trooper", "marine")
            : FindPreferred(_characters, "elite");
        if (choice is null)
        {
            ShowStatus(
                L.Get(friendly
                    ? "ai_battle.no_default_friendly"
                    : "ai_battle.no_default_hostile"),
                InfoBarSeverity.Warning);
            return;
        }

        IReadOnlyList<WeaponOption> weapons = CreateWeaponChoices(choice);
        BattleRosterItem item = new(
            choice,
            _characters,
            weapons,
            PreferredWeaponOption(choice, weapons));
        item.PropertyChanged += OnRosterItemPropertyChanged;
        (friendly ? _friendly : _hostile).Add(item);
        LoadWeaponVariants(item);
        UpdateControls();
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BattleRosterItem item })
            return;
        item.PropertyChanged -= OnRosterItemPropertyChanged;
        _friendly.Remove(item);
        _hostile.Remove(item);
        UpdateControls();
    }

    private void OnRosterItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BattleRosterItem item)
            return;
        if (e.PropertyName is nameof(BattleRosterItem.SelectedCharacter))
            ReloadWeapons(item);
        if (e.PropertyName is nameof(BattleRosterItem.SelectedWeapon))
            LoadWeaponVariants(item);
    }

    private IReadOnlyList<WeaponOption> CreateWeaponChoices(EnemySpawnChoice character) =>
    [
        WeaponOption.Default,
        .. (_demo.GetCompatibleWeapons(character)
            .Select(weapon => new WeaponOption(weapon.DisplayName, weapon))),
    ];

    private void ReloadWeapons(BattleRosterItem item)
    {
        IReadOnlyList<WeaponOption> choices = CreateWeaponChoices(item.SelectedCharacter);
        item.ReplaceWeapons(choices, PreferredWeaponOption(item.SelectedCharacter, choices));
        LoadWeaponVariants(item);
    }

    private static readonly string[] EliteWeaponHints =
    [
        "plasma_rifle", "plasma_repeater", "energy_sword", "carbine",
        "needler", "plasma_pistol",
    ];
    private static readonly string[] MarineWeaponHints =
    [
        "assault_rifle", "battle_rifle", "smg", "magnum", "pistol", "shotgun",
    ];

    private WeaponOption PreferredWeaponOption(
        EnemySpawnChoice character,
        IReadOnlyList<WeaponOption> choices)
    {
        AiWeaponChoice? resolved = FindAuthoredOrHintedWeapon(character);
        if (resolved is null)
            return choices[0];
        return choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.Weapon?.TagPath,
                    resolved.TagPath,
                    StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
    }

    private AiWeaponChoice? FindAuthoredOrHintedWeapon(EnemySpawnChoice character)
    {
        AiWeaponChoice? authored = _demo.GetAuthoredWeapons(character).FirstOrDefault();
        if (authored is not null)
            return authored;

        IReadOnlyList<AiWeaponChoice> all = _demo.GetCompatibleWeapons(character);
        string haystack = $"{character.TagPath} {character.LeafName}";
        string[] hints = haystack.Contains("elite", StringComparison.OrdinalIgnoreCase)
            ? EliteWeaponHints
            : MarineWeaponHints;
        foreach (string hint in hints)
        {
            string label = hint.Replace('_', ' ');
            AiWeaponChoice? match = all.FirstOrDefault(weapon =>
                weapon.TagPath.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                weapon.DisplayName.Contains(label, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return all.FirstOrDefault();
    }

    private AiWeaponChoice? ResolveSpawnWeapon(BattleRosterItem item) =>
        item.SelectedWeapon.Weapon ?? FindAuthoredOrHintedWeapon(item.SelectedCharacter);

    private void LoadWeaponVariants(BattleRosterItem item)
    {
        AiWeaponChoice? weapon = item.SelectedWeapon.Weapon;
        item.ResetWeaponVariants();
        if (weapon is null)
            return;
        uint datum = weapon.Datum;
        _ = Task.Run(() =>
        {
            IReadOnlyList<WeaponModelVariant> variants;
            try
            {
                variants = _demo.GetWeaponVariants(weapon);
            }
            catch
            {
                variants = [];
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (item.SelectedWeapon.Weapon?.Datum != datum)
                    return;
                item.SetWeaponVariants(variants);
            });
        });
    }

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_friendly.Count + _hostile.Count == 0 || !EnsureBuiltinMod())
            return;

        await RunBusy(async () =>
        {
            await EnsureCatalogAsync(refreshRoster: false);
            // One allegiance pass before spawn is enough; campaignTeam on the
            // spawn payload stamps birth team so we do not need per-actor
            // ObjectTeam round-trips (those were the main stutter source).
            await ApplyPeaceAllegianceAsync();

            bool anyHostileFallback = false;
            int createdFriendly = await SpawnRosterBatchedAsync(
                _friendly,
                AllegianceDemoService.HumanTeam,
                FriendlyOffset,
                fallback => anyHostileFallback |= fallback);
            int createdHostile = await SpawnRosterBatchedAsync(
                _hostile,
                AllegianceDemoService.HostileTeam,
                HostileOffset,
                fallback => anyHostileFallback |= fallback);

            ShowStatus(
                L.Format("ai_battle.generated", createdFriendly, createdHostile),
                anyHostileFallback ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        });
    }

    /// <summary>
    /// Collapse identical character/weapon rows into native batches of up to 5
    /// so N troopers share one bridge spawn instead of N sequential ones.
    /// </summary>
    private async Task<int> SpawnRosterBatchedAsync(
        IReadOnlyList<BattleRosterItem> roster,
        int campaignTeam,
        Func<int, (float X, float Y)> offsetFor,
        Action<bool> onFallback)
    {
        if (roster.Count == 0)
            return 0;

        int created = 0;
        int batchIndex = 0;
        foreach (SpawnBatch batch in BuildSpawnBatches(roster))
        {
            int remaining = batch.Count;
            while (remaining > 0)
            {
                int batchCount = Math.Min(5, remaining);
                (float offsetX, float offsetY) = offsetFor(batchIndex++);
                AllegianceDemoSpawnResult spawn = await _demo.SpawnAsync(
                    batch.Character,
                    batch.Variant,
                    campaignTeam,
                    batchCount,
                    offsetX,
                    offsetY,
                    batch.Weapon,
                    batch.WeaponVariant,
                    followPlayer: false);
                if (spawn.SpawnResult.Outcome == ScriptOutcome.Failed)
                {
                    string detail = string.IsNullOrWhiteSpace(spawn.SpawnResult.Message)
                        ? L.Get("allegiance_demo.batch_failed_unknown")
                        : spawn.SpawnResult.Message.Trim();
                    throw new InvalidOperationException(
                        L.Format("allegiance_demo.batch_failed", created, detail));
                }

                onFallback(spawn.ScaffoldDiagnosis?.UsedHostileFallback == true);
                created += batchCount;
                remaining -= batchCount;
            }
        }

        return created;
    }

    private IEnumerable<SpawnBatch> BuildSpawnBatches(IReadOnlyList<BattleRosterItem> roster)
    {
        // Preserve first-seen order so formation still reads left→right / ally→enemy.
        var order = new List<string>();
        var groups = new Dictionary<string, SpawnBatch>(StringComparer.Ordinal);
        foreach (BattleRosterItem item in roster)
        {
            AiWeaponChoice? weapon = ResolveSpawnWeapon(item);
            WeaponModelVariant? weaponVariant = item.SelectedWeaponVariant.Variant;
            string key =
                $"{item.SelectedCharacter.TagPath}\0{item.Variant.StringId:X8}\0" +
                $"{weapon?.Datum.ToString("X8") ?? "default"}\0" +
                $"{weaponVariant?.StringId.ToString("X8") ?? "default"}";
            if (groups.TryGetValue(key, out SpawnBatch? existing))
            {
                groups[key] = existing with { Count = existing.Count + 1 };
                continue;
            }

            order.Add(key);
            groups[key] = new SpawnBatch(
                item.SelectedCharacter,
                item.Variant,
                weapon,
                weaponVariant,
                Count: 1);
        }

        foreach (string key in order)
            yield return groups[key];
    }

    private sealed record SpawnBatch(
        EnemySpawnChoice Character,
        SpawnVariantChoice Variant,
        AiWeaponChoice? Weapon,
        WeaponModelVariant? WeaponVariant,
        int Count);

    private async void OnCeasefire(object sender, RoutedEventArgs e)
    {
        if (!EnsureBuiltinMod())
            return;
        await RunBusy(async () =>
        {
            await ApplyPeaceAllegianceAsync();
            ShowStatus(L.Get("ai_battle.ceasefire_ok"), InfoBarSeverity.Success);
        });
    }

    private async void OnWar(object sender, RoutedEventArgs e)
    {
        if (!EnsureBuiltinMod())
            return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _demo.SubmitAllegiancePairsAsync(
            [
                (AllegianceDemoService.HumanTeam, AllegianceDemoService.HostileTeam, true),
                (AllegianceDemoService.FriendlyTeam, AllegianceDemoService.HumanTeam, false),
                (AllegianceDemoService.FriendlyTeam, AllegianceDemoService.HostileTeam, false),
            ]);
            if (result.Outcome == ScriptOutcome.Failed)
            {
                ShowStatus(result.Message, InfoBarSeverity.Error);
                return;
            }

            await _demo.WakeBattleCombatAsync();
            ShowStatus(
                L.Get("ai_battle.war_ok"),
                result.Outcome == ScriptOutcome.Confirmed
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Informational);
        });
    }

    private async Task ApplyPeaceAllegianceAsync()
    {
        ScriptExecutionResult result = await _demo.SubmitAllegiancePairsAsync(
        [
            (AllegianceDemoService.FriendlyTeam, AllegianceDemoService.HumanTeam, false),
            (AllegianceDemoService.FriendlyTeam, AllegianceDemoService.HostileTeam, false),
            (AllegianceDemoService.HumanTeam, AllegianceDemoService.HostileTeam, false),
        ]);
        if (result.Outcome == ScriptOutcome.Failed)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? L.Get("ai_battle.allegiance_failed")
                    : result.Message.Trim());
        }
    }

    private async Task ScanQuietAsync()
    {
        if (_busy || !_game.IsConnected || !_builtinMod.IsInstalled())
            return;
        await RunBusy(() => EnsureCatalogAsync());
    }

    private async Task EnsureCatalogAsync(bool refreshRoster = true)
    {
        SpawnerCatalog catalog = await Task.Run(_demo.Connect);
        _characters = catalog.Characters;
        if (refreshRoster)
        {
            foreach (BattleRosterItem item in _friendly.Concat(_hostile))
            {
                item.ReplaceCatalog(_characters);
                ReloadWeapons(item);
            }
        }

        SpawnScaffoldInventory inventory = await Task.Run(_demo.ProbeScaffolds);
        if (inventory.NeedsDedicatedAlly)
        {
            ShowStatus(
                L.Format("allegiance_demo.scanned", _characters.Count) +
                " " +
                L.Get("allegiance_demo.needs_dedicated_ally"),
                InfoBarSeverity.Warning);
            return;
        }

        if (_characters.Count == 0)
        {
            throw new InvalidOperationException(L.Format("allegiance_demo.scanned", 0));
        }
    }

    private async Task RefreshBuiltinModSyncAsync()
    {
        if (Interlocked.Exchange(ref _modSyncBusy, 1) == 1)
            return;
        try
        {
            BuiltinModSyncStatus sync = await Task.Run(_builtinMod.GetSyncStatus);
            _modNeedsUpdate = sync.NeedsUpdatePrompt;
            DispatcherQueue.TryEnqueue(UpdateControls);
        }
        catch
        {
            _modNeedsUpdate = false;
        }
        finally
        {
            Interlocked.Exchange(ref _modSyncBusy, 0);
        }
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateControls();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private bool EnsureBuiltinMod()
    {
        if (_builtinMod.IsInstalled()) return true;
        ModRequiredBanner.IsOpen = true;
        ShowStatus(
            L.Get("allegiance_demo.mod_required_body"),
            InfoBarSeverity.Warning);
        return false;
    }

    private void UpdateControls()
    {
        ScriptingBridgeStatus bridge = _demo.BridgeStatus;
        BridgeStatusText.Text = bridge.Summary;
        BusyRing.IsActive = _busy;
        bool connected = _game.IsConnected;
        bool ready = bridge.IsRuntimeReady;
        bool modReady = _builtinMod.IsInstalled();
        if (!modReady)
        {
            ModRequiredBanner.Title = L.Get("allegiance_demo.mod_required_title");
            ModRequiredBanner.Message = L.Get("allegiance_demo.mod_required_body");
            ModRequiredBanner.Severity = InfoBarSeverity.Warning;
            ModRequiredBanner.IsOpen = true;
        }
        else if (_modNeedsUpdate)
        {
            ModRequiredBanner.Title = L.Get("allegiance_demo.mod_update_title");
            ModRequiredBanner.Message = L.Get("allegiance_demo.mod_update_body");
            ModRequiredBanner.Severity = InfoBarSeverity.Warning;
            ModRequiredBanner.IsOpen = true;
        }
        else
        {
            ModRequiredBanner.IsOpen = false;
        }

        bool workspaceActive = !_busy && modReady;
        WorkspacePanel.IsHitTestVisible = workspaceActive;
        WorkspacePanel.Opacity = workspaceActive ? 1 : 0.45;
        AddFriendlyButton.IsEnabled = !_busy && connected && modReady;
        AddHostileButton.IsEnabled = !_busy && connected && modReady;

        bool friendlyEmpty = _friendly.Count == 0;
        bool hostileEmpty = _hostile.Count == 0;
        EmptyFriendlyPanel.Visibility =
            friendlyEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHostilePanel.Visibility =
            hostileEmpty ? Visibility.Visible : Visibility.Collapsed;
        FriendlyList.Visibility =
            friendlyEmpty ? Visibility.Collapsed : Visibility.Visible;
        HostileList.Visibility =
            hostileEmpty ? Visibility.Collapsed : Visibility.Visible;

        FriendlyCountText.Text = friendlyEmpty
            ? ""
            : L.Format("ai_battle.count", _friendly.Count);
        HostileCountText.Text = hostileEmpty
            ? ""
            : L.Format("ai_battle.count", _hostile.Count);

        GenerateButton.IsEnabled =
            !_busy && connected && ready && modReady &&
            (!friendlyEmpty || !hostileEmpty);
        CeasefireButton.IsEnabled = !_busy && connected && ready && modReady;
        WarButton.IsEnabled = !_busy && connected && ready && modReady;

        if (!modReady && _characters.Count > 0)
            _characters = [];
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (!_game.IsConnected)
            _characters = [];
        DispatcherQueue.TryEnqueue(UpdateControls);
    }

    private void OnStatusTick(object? sender, object e) => UpdateControls();

    private static (float X, float Y) FriendlyOffset(int slot) =>
        (-1.4f - slot * 0.9f, 1.5f);

    private static (float X, float Y) HostileOffset(int slot) =>
        (1.4f + slot * 0.9f, 1.5f);

    private static EnemySpawnChoice? FindPreferred(
        IReadOnlyList<EnemySpawnChoice> characters,
        params string[] keywords)
    {
        foreach (string keyword in keywords)
        {
            EnemySpawnChoice? match = characters
                .Select(choice => (choice, score: ScoreKeyword(choice, keyword)))
                .Where(item => item.score < int.MaxValue)
                .OrderBy(item => item.score)
                .ThenBy(item => item.choice.LeafName.Length)
                .ThenBy(item => item.choice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.choice)
                .FirstOrDefault();
            if (match is not null)
                return match;
        }

        return characters.FirstOrDefault();
    }

    private static int ScoreKeyword(EnemySpawnChoice choice, string keyword)
    {
        string leaf = choice.LeafName;
        if (leaf.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (choice.DisplayName.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (leaf.StartsWith(keyword + "_", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (leaf.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (choice.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return 4;
        if (choice.TagPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return 5;
        return int.MaxValue;
    }

    private sealed record WeaponOption(string Label, AiWeaponChoice? Weapon)
    {
        public static WeaponOption Default { get; } =
            new(L.Get("allegiance_demo.weapon_default"), null);
    }

    private sealed record WeaponVariantOption(string Label, WeaponModelVariant? Variant)
    {
        public static WeaponVariantOption Default { get; } =
            new(L.Get("allegiance_demo.weapon_variant_default"), null);
    }

    private sealed class BattleRosterItem : ObservableObject
    {
        private EnemySpawnChoice _selectedCharacter;
        private WeaponOption _selectedWeapon = WeaponOption.Default;
        private WeaponVariantOption _selectedWeaponVariant = WeaponVariantOption.Default;
        private IReadOnlyList<WeaponOption> _weaponChoices = [WeaponOption.Default];
        private IReadOnlyList<WeaponVariantOption> _weaponVariantChoices =
            [WeaponVariantOption.Default];

        public BattleRosterItem(
            EnemySpawnChoice character,
            IReadOnlyList<EnemySpawnChoice> characterChoices,
            IReadOnlyList<WeaponOption> weaponChoices,
            WeaponOption selectedWeapon)
        {
            _selectedCharacter = character;
            CharacterChoices = characterChoices;
            Variant = RequireVariant(character);
            _weaponChoices = weaponChoices;
            _selectedWeapon = selectedWeapon;
        }

        public IReadOnlyList<EnemySpawnChoice> CharacterChoices { get; private set; }
        public SpawnVariantChoice Variant { get; private set; }
        public IReadOnlyList<WeaponOption> WeaponChoices => _weaponChoices;
        public IReadOnlyList<WeaponVariantOption> WeaponVariantChoices =>
            _weaponVariantChoices;

        public EnemySpawnChoice SelectedCharacter
        {
            get => _selectedCharacter;
            set
            {
                if (value is null)
                    return;
                if (!Set(ref _selectedCharacter, value))
                    return;
                Variant = RequireVariant(value);
            }
        }

        public WeaponOption SelectedWeapon
        {
            get => _selectedWeapon;
            set
            {
                if (!Set(ref _selectedWeapon, value ?? WeaponOption.Default))
                    return;
                ResetWeaponVariants();
                Raise(nameof(CanSelectWeaponVariant));
            }
        }

        public WeaponVariantOption SelectedWeaponVariant
        {
            get => _selectedWeaponVariant;
            set => Set(ref _selectedWeaponVariant, value ?? WeaponVariantOption.Default);
        }

        public bool CanSelectWeaponVariant =>
            SelectedWeapon.Weapon is not null && _weaponVariantChoices.Count > 1;

        public void ReplaceCatalog(IReadOnlyList<EnemySpawnChoice> characters)
        {
            CharacterChoices = characters;
            Raise(nameof(CharacterChoices));
            EnemySpawnChoice? match = characters.FirstOrDefault(choice =>
                string.Equals(
                    choice.TagPath,
                    SelectedCharacter.TagPath,
                    StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                SelectedCharacter = match;
        }

        public void ReplaceWeapons(
            IReadOnlyList<WeaponOption> weaponChoices,
            WeaponOption selected)
        {
            _weaponChoices = weaponChoices;
            _selectedWeapon = selected;
            Raise(nameof(WeaponChoices));
            Raise(nameof(SelectedWeapon));
            ResetWeaponVariants();
            Raise(nameof(CanSelectWeaponVariant));
        }

        public void ResetWeaponVariants()
        {
            _weaponVariantChoices = [WeaponVariantOption.Default];
            _selectedWeaponVariant = WeaponVariantOption.Default;
            Raise(nameof(WeaponVariantChoices));
            Raise(nameof(SelectedWeaponVariant));
            Raise(nameof(CanSelectWeaponVariant));
        }

        public void SetWeaponVariants(IReadOnlyList<WeaponModelVariant> variants)
        {
            _weaponVariantChoices =
            [
                WeaponVariantOption.Default,
                .. variants.Select(item => new WeaponVariantOption(item.Name, item)),
            ];
            _selectedWeaponVariant = WeaponVariantOption.Default;
            Raise(nameof(WeaponVariantChoices));
            Raise(nameof(SelectedWeaponVariant));
            Raise(nameof(CanSelectWeaponVariant));
        }

        private static SpawnVariantChoice RequireVariant(EnemySpawnChoice character) =>
            character.Variants.FirstOrDefault()
            ?? throw new InvalidOperationException(
                L.Get("spawner.select_character_variant"));
    }
}
