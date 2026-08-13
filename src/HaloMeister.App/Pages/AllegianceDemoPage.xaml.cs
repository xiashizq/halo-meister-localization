using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HaloMeister.App.Pages;

public sealed partial class AllegianceDemoPage : Page, IActivatablePage
{
    private const string GameProcessName = "HaloCampaignEvolved";

    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly AllegianceDemoService _demo = new();
    private readonly FullPalettesOverlayService _builtinMod = new();
    private readonly DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };
    private readonly DispatcherTimer _hotkeyTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50),
    };
    private readonly ObservableCollection<AllegianceSquadItem> _squad = [];
    private readonly IReadOnlyList<PlayerTeamOption> _teamOptions =
        AllegianceDemoService.CreateTeamOptions();
    private readonly PlayerTeamOption _defaultTeam;
    private IReadOnlyList<EnemySpawnChoice> _characters = [];
    private int? _lastActorDatum;
    private int _lastApplyTeam = AllegianceDemoService.FriendlyTeam;
    private bool _busy;
    private bool _hotkeyWasDown;
    private bool _suppressHotkey;
    private bool _modNeedsUpdate;
    private int _modSyncBusy;
    private int[] _gamePids = [];
    private long _gamePidsExpiresTick;

    public AllegianceDemoPage()
    {
        InitializeComponent();
        _defaultTeam = _teamOptions.First(
            option => option.Value == AllegianceDemoService.FriendlyTeam);
        ApplyTeamComboBox.ItemsSource = _teamOptions;
        ApplyTeamComboBox.SelectedItem = _defaultTeam;
        SquadList.ItemsSource = _squad;
        _game.ConnectionChanged += OnConnectionChanged;
        _statusTimer.Tick += OnStatusTick;
        _hotkeyTimer.Tick += OnHotkeyTick;
        UpdateControls();
    }

    public void OnActivated()
    {
        UpdateControls();
        _statusTimer.Start();
        _hotkeyTimer.Start();
        _ = RefreshBuiltinModSyncAsync();
    }

    public void OnDeactivated()
    {
        _statusTimer.Stop();
        _hotkeyTimer.Stop();
        _hotkeyWasDown = false;
    }

    private void OnOpenBuiltinMod(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.NavigateTo("builtin-mod");

    private async Task RefreshBuiltinModSyncAsync()
    {
        if (Interlocked.Exchange(ref _modSyncBusy, 1) == 1)
            return;
        try
        {
            BuiltinModSyncStatus sync = await Task.Run(_builtinMod.GetSyncStatus);
            // Only prompt when the app bundle is intact and game files differ.
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

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (!EnsureBuiltinMod()) return;
        await RunBusy(async () =>
        {
            SpawnerCatalog catalog = await Task.Run(_demo.Connect);
            _characters = catalog.Characters;
            ApplyCharacterFilter();
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
            ShowStatus(
                L.Format("allegiance_demo.scanned", _characters.Count),
                InfoBarSeverity.Success);
        });
    }

    private void OnCharacterFilterChanged(object sender, TextChangedEventArgs e) =>
        ApplyCharacterFilter();

    private void ApplyCharacterFilter()
    {
        string filter = CharacterFilterBox.Text?.Trim() ?? "";
        IEnumerable<EnemySpawnChoice> query = _characters;
        if (filter.Length > 0)
        {
            query = query.Where(character =>
                character.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                character.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                character.TagPath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        CharacterList.ItemsSource = query.ToArray();
    }

    private void OnAddCharacterRow(object sender, RoutedEventArgs e)
    {
        if (_busy || !_game.IsConnected || !EnsureBuiltinMod()) return;
        if (sender is not FrameworkElement { DataContext: EnemySpawnChoice character })
            return;

        SpawnVariantChoice variant = character.Variants.FirstOrDefault()
            ?? throw new InvalidOperationException(L.Get("spawner.select_character_variant"));
        WeaponOption weaponPick = WeaponOption.Default;
        PlayerTeamOption team = _defaultTeam;

        IReadOnlyList<WeaponOption> weaponChoices =
        [
            WeaponOption.Default,
            .. (_demo.GetCompatibleWeapons(character)
                .Select(weapon => new WeaponOption(weapon.DisplayName, weapon))),
        ];

        AllegianceSquadItem? existing = _squad.FirstOrDefault(item =>
            item.Identity == AllegianceSquadItem.MakeIdentity(
                character,
                variant,
                team.Value,
                weaponPick.Weapon));
        if (existing is not null)
        {
            existing.Quantity = Math.Min(50, existing.Quantity + 1);
        }
        else
        {
            AllegianceSquadItem item = new(
                character,
                variant,
                quantity: 1,
                team,
                _teamOptions,
                weaponPick,
                weaponChoices);
            item.PropertyChanged += OnSquadItemPropertyChanged;
            _squad.Add(item);
        }

        UpdateControls();
    }

    private void OnSquadItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AllegianceSquadItem.Quantity)
            or nameof(AllegianceSquadItem.QuantityValue)
            or nameof(AllegianceSquadItem.SelectedTeam)
            or nameof(AllegianceSquadItem.SelectedWeapon))
        {
            UpdateControls();
        }
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AllegianceSquadItem item })
        {
            item.PropertyChanged -= OnSquadItemPropertyChanged;
            _squad.Remove(item);
            UpdateControls();
        }
    }

    private void OnClearSquad(object sender, RoutedEventArgs e)
    {
        foreach (AllegianceSquadItem item in _squad)
            item.PropertyChanged -= OnSquadItemPropertyChanged;
        _squad.Clear();
        UpdateControls();
    }

    private async void OnRecallBots(object sender, RoutedEventArgs e) =>
        await RecallBotsFromUiAsync();

    private async Task RecallBotsFromUiAsync()
    {
        if (!EnsureBuiltinMod()) return;
        await RunBusy(async () =>
        {
            AllegianceDemoService.BotRecallResult result =
                await _demo.RecallBotsAsync();
            if (result.Considered == 0)
            {
                ShowStatus(
                    L.Get("allegiance_demo.recall_none"),
                    InfoBarSeverity.Warning);
                return;
            }

            ShowStatus(
                L.Format(
                    "allegiance_demo.recall_done",
                    result.Teleported,
                    result.Considered,
                    result.Failed),
                result.Teleported > 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
        });
    }

    private async void OnRecallSettings(object sender, RoutedEventArgs e)
    {
        AllegianceBotRecallSettings current = _demo.RecallSettings;
        int pendingHotkey = current.HotkeyVirtualKey;

        var hostileToggle = new ToggleSwitch
        {
            Header = L.Get("allegiance_demo.recall_include_hostiles"),
            OnContent = L.Get("allegiance_demo.recall_hostiles_on"),
            OffContent = L.Get("allegiance_demo.recall_hostiles_off"),
            IsOn = current.IncludeHostiles,
        };
        var hotkeyToggle = new ToggleSwitch
        {
            Header = L.Get("allegiance_demo.recall_hotkey_enabled"),
            OnContent = L.Get("allegiance_demo.recall_hotkey_on"),
            OffContent = L.Get("allegiance_demo.recall_hotkey_off"),
            IsOn = current.HotkeyEnabled,
        };
        var hotkeyBox = new TextBox
        {
            Header = L.Get("allegiance_demo.recall_hotkey"),
            PlaceholderText = L.Get("allegiance_demo.recall_hotkey_press"),
            Text = AllegianceBotRecallSettings.FormatHotkey(pendingHotkey),
            IsReadOnly = true,
        };
        hotkeyBox.KeyDown += (_, args) =>
        {
            if (args.Key is VirtualKey.Escape or VirtualKey.Enter or
                VirtualKey.Tab or VirtualKey.Control or VirtualKey.Shift or
                VirtualKey.Menu or VirtualKey.LeftWindows or
                VirtualKey.RightWindows or VirtualKey.CapitalLock)
            {
                return;
            }

            pendingHotkey = (int)args.Key;
            hotkeyBox.Text = AllegianceBotRecallSettings.FormatHotkey(pendingHotkey);
            args.Handled = true;
        };
        var hint = new TextBlock
        {
            Text = L.Get("allegiance_demo.recall_settings_hint"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Opacity = 0.72,
            FontSize = 12,
        };

        var panel = new StackPanel { Spacing = 14, MinWidth = 320 };
        panel.Children.Add(hint);
        panel.Children.Add(hostileToggle);
        panel.Children.Add(hotkeyToggle);
        panel.Children.Add(hotkeyBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Get("allegiance_demo.recall_settings_title"),
            Content = panel,
            PrimaryButtonText = L.Get("common.done"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        _suppressHotkey = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }
        finally
        {
            _suppressHotkey = false;
            _hotkeyWasDown = false;
        }

        _demo.ApplyRecallSettings(new AllegianceBotRecallSettings
        {
            IncludeHostiles = hostileToggle.IsOn,
            HotkeyEnabled = hotkeyToggle.IsOn,
            HotkeyVirtualKey = pendingHotkey,
        });
        ShowStatus(
            L.Get("allegiance_demo.recall_settings_saved"),
            InfoBarSeverity.Success);
        UpdateControls();
    }

    private void OnHotkeyTick(object? sender, object e)
    {
        if (_suppressHotkey || _busy)
        {
            _hotkeyWasDown = false;
            return;
        }

        AllegianceBotRecallSettings settings = _demo.RecallSettings;
        if (!settings.HotkeyEnabled)
        {
            _hotkeyWasDown = false;
            return;
        }

        bool down = (GetAsyncKeyState(settings.HotkeyVirtualKey) & 0x8000) != 0;
        bool pressed = down && !_hotkeyWasDown;
        _hotkeyWasDown = down;
        if (!pressed || !IsGameForeground())
            return;
        if (!_demo.HasRecallTargets || !_demo.BridgeStatus.IsRuntimeReady)
            return;
        if (!_builtinMod.IsInstalled())
            return;

        _ = RecallBotsFromUiAsync();
    }

    private bool IsGameForeground()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return false;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return false;

        long now = Environment.TickCount64;
        if (now >= _gamePidsExpiresTick)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(GameProcessName);
                _gamePids = processes.Select(process => process.Id).ToArray();
                foreach (Process process in processes)
                    process.Dispose();
            }
            catch
            {
                _gamePids = [];
            }
            _gamePidsExpiresTick = now + 2000;
        }

        return _gamePids.Contains((int)pid);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private async void OnSpawnSquad(object sender, RoutedEventArgs e)
    {
        if (_squad.Count == 0 || !EnsureBuiltinMod()) return;

        await RunBusy(async () =>
        {
            int created = 0;
            int batchIndex = 0;
            bool anyHostileFallback = false;

            foreach (AllegianceSquadItem item in _squad.ToArray())
            {
                int remaining = item.Quantity;
                while (remaining > 0)
                {
                    int batchCount = Math.Min(5, remaining);
                    (float offsetX, float offsetY) = FormationOffset(batchIndex++);
                    AllegianceDemoSpawnResult spawn = await _demo.SpawnAsync(
                        item.Character,
                        item.Variant,
                        item.SelectedTeam.Value,
                        batchCount,
                        offsetX,
                        offsetY,
                        item.SelectedWeapon.Weapon);
                    if (spawn.SpawnResult.Outcome == ScriptOutcome.Failed)
                    {
                        string detail = string.IsNullOrWhiteSpace(spawn.SpawnResult.Message)
                            ? L.Get("allegiance_demo.batch_failed_unknown")
                            : spawn.SpawnResult.Message.Trim();
                        throw new InvalidOperationException(
                            L.Format("allegiance_demo.batch_failed", created, detail));
                    }
                    _lastActorDatum = spawn.ActorDatum ?? _lastActorDatum;
                    _lastApplyTeam = item.SelectedTeam.Value;
                    anyHostileFallback |=
                        spawn.ScaffoldDiagnosis?.UsedHostileFallback == true;
                    created += batchCount;
                    remaining -= batchCount;
                }
            }

            SyncApplyTeamCombo();
            LastActorText.Text = _lastActorDatum is not null
                ? L.Format("allegiance_demo.last_actor", created)
                : L.Get("allegiance_demo.last_actor_unknown");
            string status = L.Format("allegiance_demo.spawned_squad", created);
            if (_demo.TrackedBotCount > 0)
            {
                status += " " + L.Format(
                    "allegiance_demo.recall_tracked",
                    _demo.TrackedBotCount);
            }
            ShowStatus(
                status,
                anyHostileFallback ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        });
    }

    private async void OnApplyTeam(object sender, RoutedEventArgs e)
    {
        if (!EnsureBuiltinMod()) return;
        PlayerTeamOption? team = ApplyTeamComboBox.SelectedItem as PlayerTeamOption
            ?? _teamOptions.FirstOrDefault(option => option.Value == _lastApplyTeam);
        if (team is null) return;
        await RunBusy(async () =>
        {
            await _demo.ApplyObjectTeamAsync(team.Value, _lastActorDatum);
            _lastApplyTeam = team.Value;
            LastActorText.Text = L.Format("allegiance_demo.applied_team", team.Label);
            ShowStatus(
                L.Format("allegiance_demo.apply_ok", team.Label),
                InfoBarSeverity.Success);
        });
    }

    private void SyncApplyTeamCombo()
    {
        ApplyTeamComboBox.SelectedItem = _teamOptions.FirstOrDefault(
            option => option.Value == _lastApplyTeam)
            ?? ApplyTeamComboBox.SelectedItem;
    }

    private static (float X, float Y) FormationOffset(int batchIndex) =>
        AllegianceDemoService.BotFormationOffset(batchIndex);

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
        AdvancedExpander.IsEnabled = workspaceActive;
        ScanButton.IsEnabled = !_busy && connected && modReady;

        if (!modReady && _characters.Count > 0)
        {
            _characters = [];
            CharacterList.ItemsSource = Array.Empty<EnemySpawnChoice>();
        }

        CharacterFilterBox.IsEnabled = !_busy && modReady && _characters.Count > 0;
        CharacterList.IsEnabled = !_busy && connected && modReady && _characters.Count > 0;
        bool empty = _squad.Count == 0;
        EmptySquadPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SquadList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        SpawnSquadButton.IsEnabled =
            !_busy && connected && ready && modReady && !empty;
        ClearSquadButton.IsEnabled = !_busy && modReady && !empty;
        RecallSettingsButton.IsEnabled = !_busy && modReady;
        AllegianceBotRecallSettings recall = _demo.RecallSettings;
        RecallButton.IsEnabled =
            !_busy && connected && ready && modReady && _demo.HasRecallTargets;
        RecallButton.Content = recall.HotkeyEnabled
            ? L.Format("allegiance_demo.recall_with_hotkey", recall.HotkeyLabel)
            : L.Get("allegiance_demo.recall");
        ToolTipService.SetToolTip(
            RecallButton,
            recall.HotkeyEnabled
                ? L.Format("allegiance_demo.recall_tooltip_hotkey", recall.HotkeyLabel)
                : L.Get("allegiance_demo.recall_tooltip"));
        ApplyTeamButton.IsEnabled =
            !_busy && ready && modReady && ApplyTeamComboBox.SelectedItem is PlayerTeamOption;

        int friendly = _squad
            .Where(item => item.SelectedTeam.Value == AllegianceDemoService.FriendlyTeam)
            .Sum(item => item.Quantity);
        int hostile = _squad
            .Where(item => item.SelectedTeam.Value == AllegianceDemoService.HostileTeam)
            .Sum(item => item.Quantity);
        int total = friendly + hostile;
        string summary = L.Format(
            "allegiance_demo.roster_summary",
            friendly,
            hostile,
            total);
        RosterSummaryText.Text = summary;
        SpawnSummaryText.Text = empty
            ? L.Get("allegiance_demo.spawn_bar_empty")
            : summary;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateControls);

    private void OnStatusTick(object? sender, object e) => UpdateControls();

    private sealed record WeaponOption(string Label, AiWeaponChoice? Weapon)
    {
        public static WeaponOption Default { get; } =
            new(L.Get("allegiance_demo.weapon_default"), null);
    }

    private sealed class AllegianceSquadItem : ObservableObject
    {
        private int _quantity;
        private PlayerTeamOption _selectedTeam;
        private WeaponOption _selectedWeapon;

        public AllegianceSquadItem(
            EnemySpawnChoice character,
            SpawnVariantChoice variant,
            int quantity,
            PlayerTeamOption team,
            IReadOnlyList<PlayerTeamOption> teamChoices,
            WeaponOption weapon,
            IReadOnlyList<WeaponOption> weaponChoices)
        {
            Character = character;
            Variant = variant;
            _quantity = quantity;
            _selectedTeam = team;
            TeamChoices = teamChoices;
            _selectedWeapon = weapon;
            WeaponChoices = weaponChoices;
        }

        public EnemySpawnChoice Character { get; }
        public SpawnVariantChoice Variant { get; }
        public IReadOnlyList<PlayerTeamOption> TeamChoices { get; }
        public IReadOnlyList<WeaponOption> WeaponChoices { get; }

        public int Quantity
        {
            get => _quantity;
            set
            {
                int clamped = Math.Clamp(value, 1, 50);
                if (Set(ref _quantity, clamped))
                {
                    Raise(nameof(QuantityLabel));
                    Raise(nameof(QuantityValue));
                }
            }
        }

        public double QuantityValue
        {
            get => Quantity;
            set => Quantity = (int)Math.Round(value);
        }

        public PlayerTeamOption SelectedTeam
        {
            get => _selectedTeam;
            set
            {
                if (Set(ref _selectedTeam, value) && value is not null)
                    Raise(nameof(Detail));
            }
        }

        public WeaponOption SelectedWeapon
        {
            get => _selectedWeapon;
            set
            {
                if (Set(ref _selectedWeapon, value ?? WeaponOption.Default))
                    Raise(nameof(Detail));
            }
        }

        public string DisplayName => Character.DisplayName;

        public string CategoryLabel =>
            string.IsNullOrWhiteSpace(Character.Category)
                ? Variant.Name
                : $"{Character.Category} · {Variant.Name}";

        public string Detail =>
            string.Join(
                " · ",
                Variant.Name,
                SelectedTeam.Label,
                SelectedWeapon.Label);

        public string QuantityLabel => $"×{Quantity:N0}";

        public string Identity =>
            MakeIdentity(Character, Variant, SelectedTeam.Value, SelectedWeapon.Weapon);

        public static string MakeIdentity(
            EnemySpawnChoice character,
            SpawnVariantChoice variant,
            int team,
            AiWeaponChoice? weapon) =>
            $"{character.CharacterTag.Index}:{variant.StringId:X8}:{team}:" +
            $"{weapon?.Datum.ToString("X8") ?? "default"}";
    }
}
