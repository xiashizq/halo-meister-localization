using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class CheatGlobalsPage : Page, IActivatablePage
{
    private readonly CheatGlobalsService _cheats = new();
    private readonly PlayerModifiersService _modifiers =
        PlayerModifiersService.Current;
    private readonly PlayerTeamService _playerTeam = new();
    private readonly AllegianceDemoService _globalAllegiance = new();
    private IReadOnlyList<CheatGlobalItem> _items = [];
    private IReadOnlyList<PlayerModifierItem> _modifierItems = [];
    private readonly Dictionary<string, bool> _loaded =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerModifierOption> _loadedModifiers =
        new(StringComparer.Ordinal);
    private bool _busy;
    private bool _loading;
    private bool _loadingModifiers;
    private bool _loadingTeam;
    private PlayerTeamState? _teamState;
    private int _statusVersion;
    private string _section = "quick-cheats";

    public CheatGlobalsPage()
    {
        InitializeComponent();
        PlayerTeamComboBox.ItemsSource = PlayerTeamService.Options;
        // Global ai_allegiance pairs player with a concrete campaign team.
        PlayerTeamOption[] globalTeams = PlayerTeamService.Options
            .Where(option => option.Value > 0)
            .ToArray();
        GlobalAllegianceTeamComboBox.ItemsSource = globalTeams;
        GlobalAllegianceTeamComboBox.SelectedItem = globalTeams.FirstOrDefault(
            option => option.Value == AllegianceDemoService.HostileTeam)
            ?? globalTeams.FirstOrDefault();
        ShowSection(_section);
        UpdateBridgeStatus();
        UpdateButtons();
    }

    public void ShowSection(string section)
    {
        _section = section switch
        {
            "player-traits" => "player-traits",
            "allegiance" => "allegiance",
            _ => "quick-cheats",
        };

        QuickCheatsPanel.Visibility =
            _section == "quick-cheats" ? Visibility.Visible : Visibility.Collapsed;
        PlayerTraitsPanel.Visibility =
            _section == "player-traits" ? Visibility.Visible : Visibility.Collapsed;
        AllegiancePanel.Visibility =
            _section == "allegiance" ? Visibility.Visible : Visibility.Collapsed;

        UpdateSummary();
        UpdateButtons();
    }

    public void OnActivated()
    {
        UpdateBridgeStatus();
        UpdateButtons();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            IReadOnlyList<CheatGlobalItem> items = await _cheats.ReadAsync();
            ShowItems(items);
            ShowStatus(
                L.Format("cheat_globals.read_all_cheats", items.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnDisableAll(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            CheatGlobalItem[] enabled = _items
                .Where(item => item.IsEnabled)
                .ToArray();
            foreach (CheatGlobalItem item in enabled)
            {
                await _cheats.SetAsync(item.Name, false);
                item.IsEnabled = false;
                _loaded[item.Name] = false;
            }
            ShowItems(await _cheats.ReadAsync());
            ShowStatus(
                enabled.Length == 0
                    ? L.Get("cheat_globals.all_cheats_already_off")
                    : L.Format("cheat_globals.turned_off_cheats", enabled.Length),
                InfoBarSeverity.Success);
        });
    }

    private async void OnGlobalToggled(object sender, RoutedEventArgs e)
    {
        if (_loading ||
            _busy ||
            sender is not ToggleSwitch toggle ||
            toggle.DataContext is not CheatGlobalItem item ||
            !_loaded.TryGetValue(item.Name, out bool previous) ||
            previous == toggle.IsOn)
        {
            return;
        }

        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await _cheats.SetAsync(item.Name, toggle.IsOn);
            _loaded[item.Name] = toggle.IsOn;
            item.IsEnabled = toggle.IsOn;
            UpdateSummary();
            ShowStatus(
                L.Format(
                    "cheat_globals.cheat_now_on_off",
                    item.DisplayName,
                    L.Get(toggle.IsOn ? "cheat_globals.on" : "cheat_globals.off")),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loading = true;
            toggle.IsOn = previous;
            item.IsEnabled = previous;
            _loading = false;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private async void OnRefreshModifiers(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            IReadOnlyList<PlayerModifierItem> items =
                await Task.Run(_modifiers.Read);
            ShowModifiers(items);
            ShowStatus(
                L.Format("cheat_globals.loaded_modifiers", items.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRestoreModifiers(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            int restored = await Task.Run(_modifiers.Restore);
            ShowModifiers(await Task.Run(_modifiers.Read));
            ShowStatus(
                restored == 0
                    ? L.Get("cheat_globals.no_modifiers_to_restore")
                    : L.Format("cheat_globals.restored_modifiers", restored),
                InfoBarSeverity.Success);
        });
    }

    private async void OnModifierChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingModifiers ||
            _busy ||
            sender is not ComboBox combo ||
            combo.DataContext is not PlayerModifierItem item ||
            combo.SelectedItem is not PlayerModifierOption selected ||
            !_loadedModifiers.TryGetValue(
                item.Name,
                out PlayerModifierOption? previous) ||
            previous.Value == selected.Value)
        {
            return;
        }

        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await Task.Run(() =>
                _modifiers.Set(item.Name, selected.Value));
            _loadedModifiers[item.Name] = selected;
            item.SelectedOption = selected;
            UpdateSummary();
            ShowStatus(
                L.Format("cheat_globals.modifier_now_value", item.DisplayName, selected.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingModifiers = true;
            combo.SelectedItem = previous;
            item.SelectedOption = previous;
            _loadingModifiers = false;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private async void OnLoadPlayerTeam(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ShowPlayerTeam(await _playerTeam.ReadAsync());
            ShowStatus(
                L.Get("cheat_globals.loaded_player_team"),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRestorePlayerTeam(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ShowPlayerTeam(await _playerTeam.RestoreAsync());
            ShowStatus(
                L.Get("cheat_globals.restored_player_team"),
                InfoBarSeverity.Success);
        });
    }

    private async void OnGlobalAllegianceAlly(object sender, RoutedEventArgs e) =>
        await SubmitGlobalAllegianceAsync(breakAllegiance: false);

    private async void OnGlobalAllegianceBreak(object sender, RoutedEventArgs e) =>
        await SubmitGlobalAllegianceAsync(breakAllegiance: true);

    private async Task SubmitGlobalAllegianceAsync(bool breakAllegiance)
    {
        if (GlobalAllegianceTeamComboBox.SelectedItem is not PlayerTeamOption team)
            return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _globalAllegiance.SubmitAllegianceAsync(
                team.Value,
                breakAllegiance);
            string verb = breakAllegiance
                ? L.Format(
                    "cheat_globals.global_allegiance_break_ok",
                    AllegianceDemoService.HaloScriptTeamName(team.Value))
                : L.Format(
                    "cheat_globals.global_allegiance_submit_ok",
                    AllegianceDemoService.HaloScriptTeamName(team.Value));
            if (result.Outcome == ScriptOutcome.Failed)
            {
                ShowStatus(result.Message, InfoBarSeverity.Error);
                return;
            }
            ShowStatus(
                $"{verb} {result.Message}",
                result.Outcome == ScriptOutcome.Confirmed
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Informational);
        });
    }

    private async void OnPlayerTeamChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingTeam ||
            _busy ||
            _teamState is null ||
            sender is not ComboBox combo ||
            combo.SelectedItem is not PlayerTeamOption selected ||
            selected.Value == _teamState.Selected.Value)
        {
            return;
        }

        if (selected.Value < 0)
        {
            if (_teamState.HasSnapshot)
            {
                await RunBusy(async () =>
                {
                    ShowPlayerTeam(await _playerTeam.RestoreAsync());
                    ShowStatus(
                        L.Get("cheat_globals.restored_player_team"),
                        InfoBarSeverity.Success);
                });
            }
            else
            {
                _loadingTeam = true;
                combo.SelectedItem = _teamState.Selected;
                _loadingTeam = false;
                PlayerTeamDescriptionText.Text = _teamState.Selected.Description;
            }
            return;
        }

        PlayerTeamOption previous = _teamState.Selected;
        PlayerTeamDescriptionText.Text = selected.Description;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            ShowPlayerTeam(await _playerTeam.SetAsync(selected.Value));
            ShowStatus(
                L.Format("cheat_globals.allegiance_now", selected.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loadingTeam = true;
            combo.SelectedItem = previous;
            _loadingTeam = false;
            PlayerTeamDescriptionText.Text = previous.Description;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private void ShowItems(IReadOnlyList<CheatGlobalItem> items)
    {
        _loading = true;
        _items = items;
        _loaded.Clear();
        foreach (CheatGlobalItem item in items)
            _loaded[item.Name] = item.IsEnabled;
        GlobalsList.ItemsSource = items;
        CheatsEmptyPanel.Visibility =
            items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
            QuickCheatsPanel.ChangeView(null, 0, null, true));
        _loading = false;
        UpdateSummary();
        UpdateButtons();
    }

    private void ShowModifiers(IReadOnlyList<PlayerModifierItem> items)
    {
        _loadingModifiers = true;
        _modifierItems = items;
        _loadedModifiers.Clear();
        foreach (PlayerModifierItem item in items)
            _loadedModifiers[item.Name] = item.SelectedOption;
        ModifierList.ItemsSource = items;
        ModifiersEmptyPanel.Visibility =
            items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
            PlayerTraitsPanel.ChangeView(null, 0, null, true));
        _loadingModifiers = false;
        UpdateSummary();
        UpdateButtons();
    }

    private void ShowPlayerTeam(PlayerTeamState state)
    {
        _loadingTeam = true;
        _teamState = state;
        PlayerTeamOption selected = PlayerTeamService.Options.FirstOrDefault(
                option => option.Value == state.Selected.Value)
            ?? state.Selected;
        if (!PlayerTeamService.Options.Contains(selected))
        {
            PlayerTeamComboBox.ItemsSource =
                PlayerTeamService.Options.Append(selected).ToArray();
        }
        else
        {
            PlayerTeamComboBox.ItemsSource = PlayerTeamService.Options;
        }
        PlayerTeamComboBox.SelectedItem = selected;
        PlayerTeamDescriptionText.Text = selected.Description;
        _loadingTeam = false;
        UpdateSummary();
        UpdateButtons();
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private void UpdateSummary()
    {
        SummaryText.Text = _section switch
        {
            "player-traits" =>
                _modifierItems.Count == 0
                    ? L.Get("cheat_globals.traits_not_loaded")
                    : L.Format(
                        "cheat_globals.traits_non_default_summary",
                        _modifierItems.Count(item =>
                            !item.SelectedOption.Label.Equals(
                                "Default",
                                StringComparison.OrdinalIgnoreCase)),
                        _modifierItems.Count),
            "allegiance" =>
                _teamState is null
                    ? L.Get("cheat_globals.allegiance_not_loaded")
                    : L.Format(
                        "cheat_globals.allegiance_label",
                        _teamState.Selected.Label),
            _ =>
                _items.Count == 0
                    ? L.Get("cheat_globals.cheats_not_loaded")
                    : L.Format(
                        "cheat_globals.cheats_active_summary",
                        _items.Count(item => item.IsEnabled),
                        _items.Count),
        };
    }

    private void UpdateButtons()
    {
        ScriptingBridgeStatus bridge = _cheats.BridgeStatus;
        bool ready = !_busy && bridge.IsRuntimeReady && !bridge.IsStale;
        RefreshButton.IsEnabled = ready;
        DisableAllButton.IsEnabled =
            ready && _items.Any(item => item.IsEnabled);
        GlobalsList.IsEnabled = !_busy && _items.Count > 0;
        RefreshModifiersButton.IsEnabled = !_busy;
        RestoreModifiersButton.IsEnabled =
            !_busy && _modifiers.HasChanges;
        ModifierList.IsEnabled = !_busy && _modifierItems.Count > 0;
        LoadPlayerTeamButton.IsEnabled = ready;
        PlayerTeamComboBox.IsEnabled = ready && _teamState is not null;
        RestorePlayerTeamButton.IsEnabled =
            ready && _teamState?.HasSnapshot == true;
        bool hasGlobalTeam =
            GlobalAllegianceTeamComboBox.SelectedItem is PlayerTeamOption;
        GlobalAllegianceTeamComboBox.IsEnabled = ready;
        GlobalAllegianceAllyButton.IsEnabled = ready && hasGlobalTeam;
        GlobalAllegianceBreakButton.IsEnabled = ready && hasGlobalTeam;
    }

    private void UpdateBridgeStatus()
    {
        BridgeStatusText.Text = _cheats.BridgeStatus.Summary;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        int version = ++_statusVersion;
        if (severity == InfoBarSeverity.Success)
        {
            StatusBar.IsOpen = false;
            SuccessStatusText.Text = message;
            SuccessPanel.Visibility = Visibility.Visible;
            _ = DismissSuccessAsync(version);
            return;
        }

        SuccessPanel.Visibility = Visibility.Collapsed;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private async Task DismissSuccessAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (version == _statusVersion)
            SuccessPanel.Visibility = Visibility.Collapsed;
    }
}
