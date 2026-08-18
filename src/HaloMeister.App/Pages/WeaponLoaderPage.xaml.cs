using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HaloMeister.App.Pages;

public sealed partial class WeaponLoaderPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly WeaponLoaderService _loader = new();
    private readonly ProjectileSwapperService _swapper = new();
    private readonly FullPalettesOverlayService _fullPalettes = new();
    private IReadOnlyList<LoadableWeapon> _weapons = [];
    private ProjectileSwapperSession? _projectileSession;
    private LoadableWeapon? _selected;
    private ProjectileSwapWeapon? _projectileWeapon;
    private RuntimeTagEntry? _selectedProjectile;
    private IReadOnlyList<WeaponModelVariant> _weaponVariants = [];
    private bool _variantsInspected;
    private StanchionImportPreview? _stanchionPreview;
    private bool _busy;
    private bool _hasScanned;
    private bool _fullPalettesInstalled;
    private int _variantRequestVersion;

    public WeaponLoaderPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnGameConnectionChanged;
        RefreshFullPalettesState();
        UpdateConnectionButtons();
        UpdateBridgeStatus();
    }

    public void OnActivated()
    {
        RefreshFullPalettesState();
        UpdateConnectionButtons();
        UpdateBridgeStatus();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            _weapons = await Task.Run(_loader.Connect);
            _projectileSession = await Task.Run(_swapper.Connect);
            await Task.Run(_loader.WarmUpDefinitions);
            await _loader.WarmUpAsync();
            _hasScanned = true;
            SearchBox.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            ResetProjectilePicker();
            ApplyFilter();
            UpdateBridgeStatus();
            ShowStatus(
                L.Format(
                    "weapon_loader.found_weapons_projectiles",
                    _weapons.Count,
                    _projectileSession.Projectiles.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        int selectedIndex = _selected?.Tag.Index ?? -1;
        await RunBusy(async () =>
        {
            _weapons = await Task.Run(_loader.Refresh);
            _projectileSession = await Task.Run(_swapper.Refresh);
            _selected = _weapons.FirstOrDefault(item => item.Tag.Index == selectedIndex);
            ResetProjectilePicker();
            ApplyFilter();
            ShowSelection();
            UpdateBridgeStatus();
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        LoadableWeapon[] filtered = _weapons
            .Where(weapon =>
                query.Length == 0 ||
                weapon.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                weapon.Tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        WeaponList.ItemsSource = filtered;
        WeaponList.SelectedItem = _selected;
        CountText.Text = L.Format(
            "weapon_loader.weapons_shown_count",
            filtered.Length,
            _weapons.Count);
    }

    private void OnWeaponClicked(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as LoadableWeapon;
        ResetProjectilePicker();
        ShowSelection();
    }

    private void ShowSelection()
    {
        bool hasSelection = _selected is not null;
        bool isStanchion = IsStanchion(_selected);

        EmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        SelectionDetails.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        StanchionPanel.Visibility = isStanchion ? Visibility.Visible : Visibility.Collapsed;

        SelectedWeaponText.Text = _selected?.Name ?? "";
        SelectedPathText.Text = _selected?.TagPath ?? "";
        SelectedWeaponImage.Source = _selected is null
            ? null
            : new BitmapImage(new Uri(_selected.ImageUri));
        _projectileWeapon = _selected is null
            ? null
            : _projectileSession?.Weapons.FirstOrDefault(weapon =>
                weapon.Tag.Index == _selected.Tag.Index);
        CurrentProjectileText.Text = _projectileWeapon?.CurrentProjectileText ??
            L.Get("weapon_loader.no_editable_projectile");
        _ = ShowVariantsAsync(_selected, ++_variantRequestVersion);
        UpdateImportButtons();
        UpdateProjectileControls();
    }

    private static bool IsStanchion(LoadableWeapon? weapon) =>
        weapon?.Tag.Name.EndsWith(
            @"objects\weapons\rifle\sniper_rifle\stanchion",
            StringComparison.OrdinalIgnoreCase) == true;

    private void ResetProjectilePicker()
    {
        _selectedProjectile = null;
        ProjectilePicker.Text = "";
        ProjectilePicker.ItemsSource = null;
    }

    private void OnProjectileTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput ||
            _projectileSession is null)
            return;

        string query = sender.Text.Trim();
        RuntimeTagEntry[] results = _projectileSession.Projectiles
            .Where(projectile =>
                query.Length == 0 ||
                projectile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                projectile.LeafName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ProjectileSwapperService.FriendlyName(projectile)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        sender.ItemsSource = results;
        _selectedProjectile = results.FirstOrDefault(projectile =>
            IsExactProjectileText(projectile, query));
        UpdateProjectileControls();
    }

    private void OnProjectilePickerGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox picker ||
            !picker.IsEnabled ||
            _projectileSession is null ||
            _projectileWeapon is null)
            return;

        // Disabled controls above this box are not hit-testable, so a click on
        // them can move focus here. Only open suggestions for a direct pointer focus.
        if (picker.FocusState != FocusState.Pointer)
            return;

        if (picker.Text.Trim().Length == 0)
            picker.ItemsSource = _projectileSession.Projectiles;
        picker.IsSuggestionListOpen = true;
    }

    private void OnProjectileSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        _selectedProjectile = args.SelectedItem as RuntimeTagEntry;
        sender.Text = _selectedProjectile?.DisplayName ?? "";
        UpdateProjectileControls();
    }

    private async void OnSwap(object sender, RoutedEventArgs e)
    {
        if (_projectileWeapon is not { } weapon ||
            _selectedProjectile is not { } projectile)
            return;

        await RunBusy(async () =>
        {
            await Task.Run(() => _swapper.Swap(weapon, projectile));
            _projectileSession = await Task.Run(_swapper.Refresh);
            ResetProjectilePicker();
            ShowSelection();
            ShowStatus(
                L.Format(
                    "weapon_loader.weapon_now_fires",
                    weapon.Name,
                    ProjectileSwapperService.FriendlyName(projectile)),
                InfoBarSeverity.Success);
        });
    }

    private void UpdateProjectileControls()
    {
        ProjectilePicker.IsEnabled =
            !_busy &&
            _game.IsConnected &&
            _projectileWeapon is not null;
        SwapButton.IsEnabled =
            !_busy &&
            _game.IsConnected &&
            _projectileWeapon is not null &&
            _selectedProjectile is not null &&
            _swapper.ProcessId != 0;
    }

    private static bool IsExactProjectileText(RuntimeTagEntry projectile, string text) =>
        projectile.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
        projectile.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase) ||
        ProjectileSwapperService.FriendlyName(projectile)
            .Equals(text, StringComparison.OrdinalIgnoreCase);

    private async Task ShowVariantsAsync(LoadableWeapon? selected, int requestVersion)
    {
        _weaponVariants = [];
        _variantsInspected = false;
        VariantList.ItemsSource = null;
        VariantSummaryText.Text = selected is null
            ? ""
            : L.Get("weapon_loader.reading_variants");

        if (selected is null)
        {
            UpdateVariantControls();
            return;
        }

        try
        {
            WeaponVariantCatalog catalog = await Task.Run(() => _loader.ReadVariants(selected));
            if (requestVersion != _variantRequestVersion ||
                _selected?.Tag.Index != selected.Tag.Index)
            {
                return;
            }
            _weaponVariants = catalog.Variants;
            _variantsInspected = true;
            VariantList.ItemsSource = _weaponVariants;
            VariantSummaryText.Text = _weaponVariants.Count == 0
                ? L.Get("weapon_loader.no_authored_variants")
                : L.Format(
                    _weaponVariants.Count == 1
                        ? "weapon_loader.authored_variant_one"
                        : "weapon_loader.authored_variant_many",
                    _weaponVariants.Count,
                    catalog.Model.LeafName);
        }
        catch (Exception ex)
        {
            if (requestVersion != _variantRequestVersion ||
                _selected?.Tag.Index != selected.Tag.Index)
            {
                return;
            }
            _variantsInspected = true;
            VariantSummaryText.Text = L.Format("weapon_loader.variants_unavailable", ex.Message);
        }

        UpdateVariantControls();
    }

    private async void OnEquipVariant(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } weapon ||
            sender is not FrameworkElement { Tag: WeaponModelVariant variant })
            return;

        await EquipAsync(weapon, variant);
    }

    private async void OnEquipDefault(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } weapon) return;
        await EquipAsync(weapon, null);
    }

    private async Task EquipAsync(LoadableWeapon weapon, WeaponModelVariant? variant)
    {
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _loader.LoadAsync(weapon, variant);
            ShowStatus(
                variant is null
                    ? L.Format(
                        "weapon_loader.weapon_handed_to_player",
                        weapon.Name,
                        result.Message)
                    : L.Format(
                        "weapon_loader.weapon_handed_to_player_variant",
                        weapon.Name,
                        variant.Name,
                        result.Message),
                InfoBarSeverity.Success);
        });
    }

    private async void OnImportStanchion(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            _stanchionPreview = await _loader.ImportStanchionAsync();
            _weapons = await Task.Run(_loader.Refresh);
            _selected = _weapons.FirstOrDefault(item =>
                item.Tag.Index == _stanchionPreview.Weapon.Tag.Index) ??
                _stanchionPreview.Weapon;
            ApplyFilter();
            ShowSelection();
            ShowImportPreview();

            InfoBarSeverity severity = _stanchionPreview.UnresolvedCount > 0
                ? InfoBarSeverity.Error
                : _stanchionPreview.CompatibilityFixCount > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
            ShowStatus(
                _stanchionPreview.IsReady
                    ? L.Get("weapon_loader.stanchion_ready")
                    : L.Format(
                        "weapon_loader.stanchion_loaded_with_issues",
                        _stanchionPreview.CompatibilityFixCount,
                        _stanchionPreview.UnresolvedCount),
                severity);
        });
    }

    private async void OnApplySubstitutions(object sender, RoutedEventArgs e)
    {
        if (_stanchionPreview is not { } preview) return;
        await RunBusy(async () =>
        {
            _stanchionPreview = await Task.Run(
                () => _loader.ApplyStanchionSubstitutions(preview));
            ShowImportPreview();
            ShowStatus(
                L.Get("weapon_loader.compatibility_fixes_applied"),
                InfoBarSeverity.Success);
        });
    }

    private void ShowImportPreview()
    {
        DependencyList.ItemsSource = _stanchionPreview?.MissingReferences;
        if (_stanchionPreview is null)
        {
            ImportSummaryText.Text = "";
            ApplySubstitutionsButton.IsEnabled = false;
            return;
        }

        ImportSummaryText.Text =
            L.Format(
                "weapon_loader.import_summary",
                _stanchionPreview.ValidReferenceCount,
                _stanchionPreview.SubstitutionCount,
                _stanchionPreview.EligibilityFix is null ? 0 : 1,
                _stanchionPreview.UnresolvedCount) +
            (_stanchionPreview.EligibilityFix is { } eligibility
                ? $"\n{eligibility.Description}"
                : "");
        ApplySubstitutionsButton.IsEnabled = !_busy && _stanchionPreview.CanApply;
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        ScanButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ProjectilePicker.IsEnabled = false;
        SwapButton.IsEnabled = false;
        VariantList.IsEnabled = false;
        EquipDefaultButton.IsEnabled = false;
        ImportStanchionButton.IsEnabled = false;
        ApplySubstitutionsButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            RefreshFullPalettesState();
            UpdateConnectionButtons();
            UpdateImportButtons();
            UpdateProjectileControls();
            UpdateVariantControls();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateConnectionButtons);

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

    private void UpdateConnectionButtons()
    {
        ScanButton.IsEnabled = !_busy && _game.IsConnected;
        RefreshButton.IsEnabled = !_busy && _game.IsConnected && _hasScanned;
        FullPalettesButton.IsEnabled = !_busy;
        FullPalettesButton.Content = _fullPalettesInstalled
            ? L.Get("vehicle_workshop.remove_all_vehicles_weapons")
            : L.Get("vehicle_workshop.add_all_vehicles_weapons");
        UpdateImportButtons();
        UpdateProjectileControls();
        UpdateVariantControls();
    }

    private void UpdateBridgeStatus()
    {
        ScriptingBridgeStatus status = _loader.BridgeStatus();
        ConnectionText.Text = _hasScanned
            ? L.Format(
                "weapon_loader.scanned_summary",
                _weapons.Count,
                _projectileSession?.Projectiles.Count ?? 0,
                status.Summary)
            : status.Summary;
    }

    private void UpdateImportButtons()
    {
        ScriptingBridgeStatus bridge = _loader.BridgeStatus();
        ImportStanchionButton.IsEnabled =
            !_busy &&
            IsStanchion(_selected) &&
            _loader.ProcessId != 0 &&
            bridge.IsRuntimeReady &&
            !bridge.IsStale;
        ApplySubstitutionsButton.IsEnabled =
            !_busy && _stanchionPreview?.CanApply == true;
    }

    private void UpdateVariantControls()
    {
        ScriptingBridgeStatus bridge = _loader.BridgeStatus();
        bool canEquip =
            !_busy &&
            _game.IsConnected &&
            _selected is not null &&
            _loader.ProcessId != 0 &&
            bridge.IsRuntimeReady &&
            !bridge.IsStale;
        bool hasVariants = _weaponVariants.Count > 0;
        VariantList.IsEnabled = canEquip && hasVariants;
        VariantList.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
        EquipDefaultButton.Visibility =
            _variantsInspected && !hasVariants
                ? Visibility.Visible
                : Visibility.Collapsed;
        EquipDefaultButton.IsEnabled = canEquip && _variantsInspected && !hasVariants;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
