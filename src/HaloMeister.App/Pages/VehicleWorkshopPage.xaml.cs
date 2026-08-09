using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HaloMeister.App.Pages;

public sealed partial class VehicleWorkshopPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly VehicleWorkshopService _vehicles = new();
    private readonly FullPalettesOverlayService _fullPalettes = new();
    private IReadOnlyList<LoadableVehicle> _all = [];
    private IReadOnlyList<VehicleModelVariant> _variants = [];
    private LoadableVehicle? _selected;
    private VehicleModelVariant? _selectedVariant;
    private int _variantRequestVersion;
    private bool _busy;
    private bool _hasScanned;
    private bool _fullPalettesInstalled;

    public VehicleWorkshopPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnConnectionChanged;
        RefreshFullPalettesState();
        UpdateControls();
    }

    public void OnActivated()
    {
        RefreshFullPalettesState();
        UpdateControls();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            _all = await Task.Run(_vehicles.Connect);
            await Task.Run(_vehicles.WarmUpDefinitions);
            await _vehicles.WarmUpAsync();
            _hasScanned = true;
            SearchBox.IsEnabled = true;
            ApplyFilter();
            ShowStatus(
                L.Format("vehicle_workshop.found_vehicles", _all.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnToggleFullPalettes(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            if (_fullPalettes.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            bool installing = !_fullPalettesInstalled;
            string actionLabel = installing
                ? L.Get("vehicle_workshop.add_all_vehicles_weapons")
                : L.Get("vehicle_workshop.remove_all_vehicles_weapons");
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = actionLabel,
                Content = installing
                    ? L.Get("builtin_mod.install_confirm")
                    : L.Get("builtin_mod.remove_confirm"),
                PrimaryButtonText = actionLabel,
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(() =>
                installing ? _fullPalettes.Install() : _fullPalettes.Remove());
            RefreshFullPalettesState();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        int selectedIndex = _selected?.Tag.Index ?? -1;
        await RunBusy(async () =>
        {
            _all = await Task.Run(_vehicles.Refresh);
            _selected = _all.FirstOrDefault(item => item.Tag.Index == selectedIndex);
            ApplyFilter();
            ShowSelection();
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        LoadableVehicle[] filtered = _all
            .Where(vehicle =>
                query.Length == 0 ||
                vehicle.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                vehicle.TagPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        VehicleList.ItemsSource = filtered;
        VehicleList.SelectedItem = _selected;
        CountText.Text = L.Format(
            "vehicle_workshop.vehicles_shown_count",
            filtered.Length,
            _all.Count);
    }

    private void OnVehicleClicked(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as LoadableVehicle;
        ShowSelection();
    }

    private void ShowSelection()
    {
        bool selected = _selected is not null;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        SelectionDetails.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SelectedVehicleText.Text = _selected?.Name ?? "";
        SelectedPathText.Text = _selected?.TagPath ?? "";
        SelectedDatumText.Text = _selected?.Detail ?? "";
        SelectedVehicleImage.Source = _selected is null
            ? null
            : new BitmapImage(new Uri(_selected.ImageUri));
        EnablePlayerControlButton.Visibility =
            VehicleWorkshopService.SupportsPlayerControl(_selected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        AllowSeraphExitButton.Visibility =
            VehicleWorkshopService.IsSeraph(_selected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        _ = ShowVariantsAsync(_selected, ++_variantRequestVersion);
        UpdateControls();
    }

    private async Task ShowVariantsAsync(LoadableVehicle? selected, int requestVersion)
    {
        _variants = [];
        _selectedVariant = null;
        VariantPicker.ItemsSource = null;
        VariantSummaryText.Text = selected is null
            ? ""
            : L.Get("vehicle_workshop.reading_variants");
        UpdateVariantControls();

        if (selected is null)
            return;

        try
        {
            VehicleVariantCatalog catalog = await Task.Run(
                () => _vehicles.ReadVariants(selected));
            if (requestVersion != _variantRequestVersion ||
                _selected?.Tag.Index != selected.Tag.Index)
            {
                return;
            }

            _variants = catalog.Variants;
            VariantPicker.ItemsSource = _variants;
            VariantPicker.SelectedIndex = 0;
            _selectedVariant = VariantPicker.SelectedItem as VehicleModelVariant;
            VariantSummaryText.Text = L.Format(
                _variants.Count == 1
                    ? "vehicle_workshop.authored_variant_one"
                    : "vehicle_workshop.authored_variant_many",
                _variants.Count);
        }
        catch
        {
            if (requestVersion != _variantRequestVersion ||
                _selected?.Tag.Index != selected.Tag.Index)
            {
                return;
            }
            VariantSummaryText.Text = L.Get("vehicle_workshop.variants_unavailable");
        }

        UpdateVariantControls();
    }

    private void OnVariantSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _selectedVariant = VariantPicker.SelectedItem as VehicleModelVariant;
        UpdateVariantControls();
    }

    private async void OnSpawn(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } vehicle) return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _vehicles.SpawnAsync(
                vehicle,
                _selectedVariant);
            string message = _selectedVariant is null
                ? L.Format(
                    "vehicle_workshop.spawned_ahead",
                    vehicle.Name,
                    result.Message)
                : L.Format(
                    "vehicle_workshop.spawned_ahead_variant",
                    vehicle.Name,
                    _selectedVariant.Name,
                    result.Message);
            ShowStatus(message, InfoBarSeverity.Success);
        });
    }

    private async void OnEnablePlayerControl(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } vehicle) return;
        await RunBusy(async () =>
        {
            VehiclePlayerControlResult result = await Task.Run(
                () => _vehicles.EnablePlayerControl(vehicle));
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnAllowSeraphExit(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } vehicle) return;
        await RunBusy(async () =>
        {
            VehicleSeatExitResult result = await Task.Run(
                () => _vehicles.AllowSeraphPlayerExit(vehicle));
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            RefreshFullPalettesState();
            UpdateControls();
        }
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateControls);

    private void RefreshFullPalettesState()
    {
        try
        {
            _fullPalettesInstalled = _fullPalettes.IsInstalled();
        }
        catch
        {
            _fullPalettesInstalled = false;
        }
    }

    private void UpdateControls()
    {
        ScriptingBridgeStatus bridge = _vehicles.BridgeStatus;
        bool connected = !_busy && _game.IsConnected;
        ScanButton.IsEnabled = connected;
        FullPalettesButton.IsEnabled = !_busy;
        FullPalettesButton.Content = _fullPalettesInstalled
            ? L.Get("vehicle_workshop.remove_all_vehicles_weapons")
            : L.Get("vehicle_workshop.add_all_vehicles_weapons");
        RefreshButton.IsEnabled = connected && _hasScanned;
        BusyRing.IsActive = _busy;
        SpawnButton.IsEnabled =
            !_busy && _selected is not null && _game.IsConnected &&
            bridge.IsRuntimeReady && !bridge.IsStale;
        EnablePlayerControlButton.IsEnabled =
            !_busy && _game.IsConnected &&
            VehicleWorkshopService.SupportsPlayerControl(_selected);
        AllowSeraphExitButton.IsEnabled =
            !_busy && _game.IsConnected &&
            VehicleWorkshopService.IsSeraph(_selected);
        ConnectionText.Text = _hasScanned
            ? L.Format("vehicle_workshop.loaded_summary", _all.Count, bridge.Summary)
            : bridge.Summary;
        UpdateVariantControls();
    }

    private void UpdateVariantControls()
    {
        VariantPicker.IsEnabled =
            !_busy &&
            _game.IsConnected &&
            _variants.Count > 0;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
