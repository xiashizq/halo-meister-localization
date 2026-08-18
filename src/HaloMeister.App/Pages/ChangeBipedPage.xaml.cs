using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HaloMeister.App.Pages;

public sealed partial class ChangeBipedPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly PlayerBipedService _bipeds = new();
    private IReadOnlyList<PlayerBipedChoice> _allChoices = [];
    private PlayerBipedChoice? _selected;
    private PlayerBipedVariantChoice? _selectedVariant;
    private int _variantRequestVersion;
    private bool _busy;
    private bool _hasScanned;

    public ChangeBipedPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnGameConnectionChanged;
        RefreshCharacterOverlayList();
        UpdateChrome();
    }

    public void OnActivated()
    {
        RefreshCharacterOverlayList();
        UpdateChrome();
        if (_game.IsConnected && !_hasScanned)
            _ = ScanAsync(connectGame: false);
    }

    private async void OnConnectAndScan(object sender, RoutedEventArgs e)
        => await ScanAsync(connectGame: false);

    private async Task ScanAsync(bool connectGame)
    {
        await RunBusy(async () =>
        {
            if (connectGame && !_game.IsConnected)
                await Task.Run(_game.Connect);

            PlayerBipedSession session = await Task.Run(_bipeds.Connect);
            await _bipeds.WarmUpAsync();
            _hasScanned = true;
            ShowSession(session);
            ShowStatus(
                L.Format("change_biped.ready_found_bipeds", session.Choices.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            PlayerBipedSession session = await Task.Run(_bipeds.Refresh);
            ShowSession(session);
            ShowStatus(
                L.Format("change_biped.refreshed_bipeds", session.Choices.Count),
                InfoBarSeverity.Success);
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnBipedSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _selected = BipedList.SelectedItem as PlayerBipedChoice;
        _ = LoadVariantsForSelectionAsync(++_variantRequestVersion);
        UpdateChrome();
    }

    private void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedVariant = VariantPicker.SelectedItem as PlayerBipedVariantChoice;
        UpdateChrome();
    }

    private async void OnApplyTagRedirect(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } selected) return;

        await RunBusy(async () =>
        {
            NativeTagModExportResult overlay =
                await _bipeds.ExportTagRedirectOverlayAsync(selected, _selectedVariant);
            RefreshCharacterOverlayList();
            ShowStatus(
                L.Get("change_biped.character_overlay_built"),
                InfoBarSeverity.Success);
            DiagnosticText.Text =
                $"overlay={overlay.UtocPath}; variant={_selectedVariant?.Name ?? "(default)"}";
        });
    }

    private void OnRefreshCharacterOverlays(object sender, RoutedEventArgs e)
    {
        RefreshCharacterOverlayList();
        ShowStatus(
            L.Format(
                "change_biped.character_overlays_refreshed",
                CharacterOverlayList.Items.Count),
            InfoBarSeverity.Success);
    }

    private async void OnInstallCharacterOverlay(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CharacterOverlayPackage package })
            return;
        if (package.IsExpired)
        {
            ShowStatus(
                L.Get("change_biped.character_overlay_expired_blocked"),
                InfoBarSeverity.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(package.SourceUtocPath))
            return;

        await RunBusy(async () =>
        {
            NativeTagModInstallResult result = await Task.Run(
                () => _bipeds.InstallTagRedirectOverlay(package.SourceUtocPath!));
            RefreshCharacterOverlayList();
            ShowStatus(
                L.Get("change_biped.character_overlay_installed"),
                InfoBarSeverity.Success);
            DiagnosticText.Text =
                $"installed={string.Join(", ", result.InstalledFiles)}";
        });
    }

    private async void OnUninstallCharacterOverlay(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CharacterOverlayPackage package })
            return;

        await RunBusy(async () =>
        {
            IReadOnlyList<string> removed = await Task.Run(
                () => _bipeds.RemoveTagRedirectOverlay(package.Name));
            if (removed.Count == 0)
                throw new FileNotFoundException(
                    L.Get("change_biped.character_overlay_not_installed"));
            RefreshCharacterOverlayList();
            ShowStatus(
                L.Get("change_biped.character_overlay_removed"),
                InfoBarSeverity.Success);
            DiagnosticText.Text = $"removed={string.Join(", ", removed)}";
        });
    }

    private async void OnDeleteCharacterOverlay(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CharacterOverlayPackage package })
            return;

        await RunBusy(async () =>
        {
            IReadOnlyList<string> removed = await Task.Run(
                () => _bipeds.DeleteCharacterOverlayPackage(package.Name));
            RefreshCharacterOverlayList();
            ShowStatus(
                L.Get("change_biped.character_overlay_deleted"),
                InfoBarSeverity.Success);
            DiagnosticText.Text = $"deleted={string.Join(", ", removed)}";
        });
    }

    private void ShowSession(PlayerBipedSession session)
    {
        _allChoices = session.Choices;
        SearchBox.IsEnabled = true;
        ApplyFilter();

        _selected = session.Choices.FirstOrDefault();
        BipedList.SelectedItem = _selected;
        _ = LoadVariantsForSelectionAsync(++_variantRequestVersion);
        RefreshCharacterOverlayList();
        UpdateChrome();
    }

    private async Task LoadVariantsForSelectionAsync(int requestVersion)
    {
        PlayerBipedChoice? selected = _selected;
        _selectedVariant = null;
        VariantPicker.ItemsSource = null;
        VariantPicker.IsEnabled = false;
        UpdateChrome();
        if (selected is null || !_game.IsConnected)
            return;

        try
        {
            IReadOnlyList<PlayerBipedVariantChoice> variants = await Task.Run(
                () => _bipeds.ReadVariants(selected));
            if (requestVersion != _variantRequestVersion ||
                _selected?.BipedTag.Index != selected.BipedTag.Index)
                return;

            VariantPicker.ItemsSource = variants;
            VariantPicker.SelectedIndex = variants.Count > 0 ? 0 : -1;
            _selectedVariant = VariantPicker.SelectedItem as PlayerBipedVariantChoice;
        }
        catch
        {
            if (requestVersion != _variantRequestVersion ||
                _selected?.BipedTag.Index != selected.BipedTag.Index)
                return;
            VariantPicker.ItemsSource = null;
            _selectedVariant = null;
        }

        UpdateChrome();
    }

    private void RefreshCharacterOverlayList()
    {
        CharacterOverlayPackage[] packages =
            _bipeds.GetCharacterOverlayPackages().ToArray();
        CharacterOverlayList.ItemsSource = packages;
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        PlayerBipedChoice[] filtered = _allChoices
            .Where(choice =>
                query.Length == 0 ||
                choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                choice.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                choice.TagPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        BipedList.ItemsSource = filtered;
        CountText.Text = L.Format(
            "change_biped.count_filtered",
            filtered.Length,
            _allChoices.Count);
        if (_selected is not null &&
            filtered.Any(choice => choice.BipedTag.Index == _selected.BipedTag.Index))
            BipedList.SelectedItem = _selected;
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;

        _busy = true;
        BusyRing.IsActive = true;
        UpdateChrome();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(FormatUserFacingError(ex), InfoBarSeverity.Error);
            DiagnosticText.Text =
                $"{DateTimeOffset.Now:HH:mm:ss} · {FormatUserFacingError(ex)}";
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateChrome();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshCharacterOverlayList();
            UpdateChrome();
        });

    private void UpdateChrome()
    {
        ScriptingBridgeStatus bridge = _bipeds.BridgeStatus;
        bool gameReady = _game.IsConnected;
        bool bridgeReady = bridge.IsRuntimeReady && !bridge.IsStale;

        SetState(
            GameStateDot,
            GameStateText,
            gameReady,
            gameReady
                ? L.Format("change_biped.connected_pid", _game.ProcessId)
                : L.Get("change_biped.disconnected"));
        SetState(
            BridgeStateDot,
            BridgeStateText,
            bridgeReady,
            bridgeReady
                ? L.Format("change_biped.ready_version", bridge.RunningVersion)
                : L.Get("change_biped.not_ready"));
        SetState(
            ScanStateDot,
            ScanStateText,
            _hasScanned,
            _hasScanned
                ? L.Format("change_biped.bipeds_loaded", _allChoices.Count)
                : L.Get("change_biped.not_scanned"));

        ConnectScanButton.Content = L.Get("change_biped.scan_mission");
        ConnectScanButton.IsEnabled = !_busy && gameReady;
        RefreshButton.IsEnabled = !_busy && gameReady && _hasScanned;
        RefreshOverlaysButton.IsEnabled = !_busy;
        VariantPicker.IsEnabled =
            !_busy &&
            gameReady &&
            _hasScanned &&
            _selected is not null &&
            VariantPicker.Items.Count > 0;
        ApplyTagRedirectButton.IsEnabled =
            !_busy &&
            gameReady &&
            _hasScanned &&
            _selected is not null &&
            _selectedVariant is not null;
    }

    private static void SetState(
        Ellipse indicator,
        TextBlock label,
        bool ready,
        string text)
    {
        indicator.Fill = new SolidColorBrush(
            ready ? Colors.LimeGreen : Colors.Gray);
        label.Text = text;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Title = severity switch
        {
            InfoBarSeverity.Error => L.Get("change_biped.character_overlay_failed"),
            InfoBarSeverity.Success => L.Get("change_biped.character_overlay_success"),
            _ => L.Get("change_biped.change_character"),
        };
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static string FormatUserFacingError(Exception ex)
    {
        if (IsFileInUse(ex))
            return L.Get("change_biped.character_overlay_file_in_use");
        if (ex is DirectoryNotFoundException)
            return L.Get("change_biped.character_overlay_game_folder_missing");
        if (ex is FileNotFoundException)
            return string.IsNullOrWhiteSpace(ex.Message) || LooksTechnical(ex.Message)
                ? L.Get("change_biped.character_overlay_not_found")
                : ex.Message;
        if (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException or IOException)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message) && !LooksTechnical(ex.Message))
                return ex.Message;
        }
        return L.Get("change_biped.character_overlay_generic_error");
    }

    private static bool LooksTechnical(string message) =>
        message.Contains("[bipd]", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("[matg]", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("globals/globals", StringComparison.OrdinalIgnoreCase) ||
        message.Contains(".utoc", StringComparison.OrdinalIgnoreCase) ||
        message.Contains(".ucas", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("string-id", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Native exporter", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("tag reference", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("0x", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileInUse(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException io &&
                ((io.HResult & 0xFFFF) is 32 or 33 ||
                 io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
                 io.Message.Contains("正由另一进程使用", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }
}
