using System.Runtime.InteropServices;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Pages;
using HaloMeister.App.Services;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace HaloMeister.App;

public sealed partial class MainWindow : Window
{
    private readonly AppState _state = AppState.Current;
    private readonly PlayFabProxyService _proxy = PlayFabProxyService.Current;
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly Ue4ssLoaderInstaller _loaderInstaller = new();
    private byte[]? _patchPayload;
    private bool _connectingToGame;
    private bool _installingBridge;
    private bool _cloudBusy;
    private bool _awaitingAuthCapture;
    private bool _authSavedDuringCapture;
    private int _navigationGeneration;
    private readonly Dictionary<Type, Page> _pageCache = new();
    private Page? _activePage;
    private readonly DispatcherTimer _statusDismissTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4),
    };
    private readonly DispatcherTimer _patchSerializeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        SetWindowIcon();
        ApplyBuildPolicy();
        // File-based tools must know the installation while the game is closed.
        _ = GameInstallationService.Current.BinaryDirectory;

        _state.DirtyChanged += UpdateChrome;
        _state.SaveLoaded += UpdateChrome;
        if (!BuildPolicy.IsRetail)
            _proxy.PatchPayloadProvider = GetPatchPayload;
        _proxy.Error += OnProxyError;
        _proxy.SessionChanged += OnPlayFabSessionChanged;
        _proxy.TrafficObserved += OnPlayFabTraffic;
        _game.ConnectionChanged += OnGameConnectionChanged;
        LocalizationService.Current.LanguageChanged += OnAppLanguageChanged;
        Closed += OnClosed;
        _statusDismissTimer.Tick += (_, _) =>
        {
            _statusDismissTimer.Stop();
            Status.IsOpen = false;
        };
        _patchSerializeTimer.Tick += OnPatchSerializeTick;

        TryLoadSavedPlayFabSession();
        Nav.SelectedItem = HomeNavItem;
        PresentPage(GetOrCreatePage(typeof(HomePage), out _));
        UpdateChrome();
        UpdateGameConnectionChrome();
        UpdateCloudActions();
    }

    public static MainWindow? Instance { get; private set; }

    public void SetLanguage(string language)
        => LocalizationService.Current.SetLanguage(language);

    /// <summary>
    /// Restores and focuses this window when another launch is redirected here.
    /// </summary>
    public void BringToForeground()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Hwnd);
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private void ApplyBuildPolicy()
    {
        if (!BuildPolicy.IsRetail)
            return;

        ToolTipService.SetToolTip(
            PatchSettingsButton,
            L.Get("shell.tip_retail_readonly"));
    }

    private void SetWindowIcon()
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "HaloMeisterIcon.ico");

        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
    }

    private void OnAppLanguageChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyShellLocalization();
            UpdateGameConnectionChrome();
            UpdateCloudActions();
            ApplyBuildPolicy();

            if (Nav.SelectedItem is not NavigationViewItem item)
                return;

            string? tag = item.Tag as string;
            if (tag is null)
                return;

            Type page = ResolvePageType(tag);
            _ = NavigateContentAsync(
                page,
                page == typeof(LiveToolsHubPage) ? tag : null,
                forceReload: true);
        });
    }

    private void ApplyShellLocalization()
    {
        AppTaglineText.Text = L.Get("shell.meteorite_saves_settings_live_tools");
        CloudTitleText.Text = L.Get("shell.playfab_cloud_save");
        GetUserDataButton.Label = L.Get("shell.download_save");
        PatchSettingsButton.Label = L.Get("shell.upload_changes");
        NavigationLoadingText.Text = L.Get("common.loading");

        HomeNavItem.Content = L.Get("shell.home");
        ProgressProfileNavItem.Content = L.Get("shell.progress_profile");
        CampaignProgressNavItem.Content = L.Get("shell.campaign_progress");
        ProfileNavItem.Content = L.Get("shell.profile_entitlements");
        RawNavItem.Content = L.Get("shell.raw_save_data");
        GameFilesNavItem.Content = L.Get("shell.game_files");
        CustomizationNavItem.Content = L.Get("shell.customization");
        ConfigNavItem.Content = L.Get("shell.game_settings");
        GameSavesNavItem.Content = L.Get("shell.game_saves");
        BuiltinModNavItem.Content = L.Get("shell.builtin_mod");
        LiveToolsNavItem.Content = L.Get("shell.live_tools");
        GameplayNavItem.Content = L.Get("shell.gameplay");
        SpawnEquipNavItem.Content = L.Get("shell.spawn_equip");
        AllegianceNavItem.Content = L.Get("shell.allegiance");
        PlayerAppearanceNavItem.Content = L.Get("shell.player_appearance");
        CameraWorldNavItem.Content = L.Get("shell.camera_world");
        ChangeBipedNavItem.Content = L.Get("shell.change_character");
        AdvancedNavItem.Content = L.Get("shell.advanced");
        // RuntimeTagsNavItem.Content = L.Get("shell.realtime_tags");
        ScriptingNavItem.Content = L.Get("shell.scripting");
        RemoteNavItem.Content = L.Get("shell.phone_remote");
        SetupNavItem.Content = L.Get("shell.setup");
        HelpNavItem.Content = L.Get("shell.help");
        // CommunityNavItem.Content = L.Get("shell.community_links");
    }

    private void UpdateChrome()
    {
        UpdateCloudActions();
        // DirtyChanged can fire per field edit; debounce the full-document serialize.
        _patchSerializeTimer.Stop();
        _patchSerializeTimer.Start();
    }

    private void OnPatchSerializeTick(object? sender, object e)
    {
        _patchSerializeTimer.Stop();
        try
        {
            Volatile.Write(ref _patchPayload, _state.Save?.Document.Serialize());
        }
        catch (Exception ex)
        {
            Report(L.Format("shell.patch_snapshot_failed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void OnConnectGame(object sender, RoutedEventArgs e)
        => await ConnectToGameAsync();

    public async Task ConnectToGameAsync()
    {
        if (_connectingToGame) return;

        _connectingToGame = true;
        UpdateGameConnectionChrome();
        try
        {
            await Task.Run(_game.Connect);
            Report(
                L.Format("shell.game_connected_msg", _game.ProcessId),
                InfoBarSeverity.Success,
                L.Get("shell.game_connected_title"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_connect"));
        }
        finally
        {
            _connectingToGame = false;
            UpdateGameConnectionChrome();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateGameConnectionChrome);

    private void UpdateGameConnectionChrome()
    {
        bool connected = _game.IsConnected;
        GameConnectionProgress.IsActive = _connectingToGame;
        GameConnectionProgress.Visibility =
            _connectingToGame ? Visibility.Visible : Visibility.Collapsed;
        GameConnectionIndicator.Visibility =
            _connectingToGame ? Visibility.Collapsed : Visibility.Visible;
        GameConnectionIndicator.Fill = connected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.LimeGreen)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorTertiaryBrush"];
        GameConnectionText.Text = connected
            ? L.Format("shell.connected_pid", _game.ProcessId)
            : L.Get("shell.game_disconnected");
        GameConnectionButton.Content = connected
            ? L.Get("common.reconnect")
            : L.Get("common.connect");
        GameConnectionButton.IsEnabled = !_connectingToGame;
    }

    public async Task LaunchGameAsync()
    {
        try
        {
            bool steam = GamePlatformPreference.Current.IsSteam;
            bool launched = await GamePlatformPreference.Current.LaunchGameAsync();
            Report(
                launched
                    ? L.Get(steam
                        ? "shell.launch_requested_steam"
                        : "shell.launch_requested")
                    : L.Get("shell.launch_rejected"),
                launched ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                launched ? L.Get("shell.launching_game") : L.Get("shell.could_not_launch"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_launch"));
        }
    }

    public async Task InstallLiveToolsAsync(bool forcePickFolder = false)
    {
        if (_installingBridge) return;

        string? selectedRoot = forcePickFolder
            ? null
            : _loaderInstaller.FindGameBinaryDirectory()
                ?? _loaderInstaller.FindInstalledBinaryDirectory();
        try
        {
            // forcePickFolder lets Setup recover from a wrong remembered path after a
            // failed or partial install. Without it, the first successful folder pick
            // permanently skips the picker even when the bridge never finished installing.
            if (forcePickFolder || (_bridge.FindInstalledMainPath() is null && selectedRoot is null))
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.ComputerFolder,
                };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                selectedRoot = folder.Path;
                GameInstallationService.Current.Remember(selectedRoot);
                if (forcePickFolder)
                    _bridge.ClearRememberedInstallLocation();
            }

            bool installLoader =
                selectedRoot is not null && !_loaderInstaller.IsInstalled(selectedRoot);
            if (installLoader)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = L.Get("shell.install_bridge_title"),
                    Content = L.Format(
                        "shell.install_bridge_body",
                        Ue4ssLoaderInstaller.Version),
                    PrimaryButtonText = L.Get("common.install"),
                    CloseButtonText = L.Get("common.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            _installingBridge = true;
            Ue4ssLoaderInstallResult? loaderResult = null;
            if (installLoader)
            {
                var downloadProgress = new Progress<Ue4ssDownloadProgress>(
                    ReportUe4ssDownloadProgress);
                loaderResult = await _loaderInstaller.InstallAsync(
                    selectedRoot!,
                    downloadProgress);
                selectedRoot = loaderResult.BinaryDirectory;
            }

            string installedPath = await Task.Run(
                () => _bridge.InstallOrUpdateBridge(selectedRoot));
            Report(
                loaderResult is null
                    ? L.Format("shell.bridge_installed_msg", installedPath)
                    : L.Format(
                        "shell.live_tools_installed_msg",
                        loaderResult.Version,
                        loaderResult.BackupDirectory),
                InfoBarSeverity.Success,
                loaderResult is null
                    ? L.Get("shell.bridge_installed_title")
                    : L.Get("shell.live_tools_installed_title"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_install_bridge"));
        }
        finally
        {
            _installingBridge = false;
        }
    }

    private void ReportUe4ssDownloadProgress(Ue4ssDownloadProgress progress)
    {
        // HttpClient completions may resume off the UI thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            string speed = FormatTransferSpeed(progress.BytesPerSecond);
            if (progress.TotalBytes is { } total && total > 0)
            {
                double percent = 100.0 * progress.BytesReceived / total;
                Report(
                    L.Format(
                        "shell.ue4ss_download_progress",
                        FormatTransferBytes(progress.BytesReceived),
                        FormatTransferBytes(total),
                        percent.ToString("0.0"),
                        speed),
                    InfoBarSeverity.Informational,
                    L.Get("shell.ue4ss_downloading_title"));
                return;
            }

            Report(
                L.Format(
                    "shell.ue4ss_download_progress_unknown",
                    FormatTransferBytes(progress.BytesReceived),
                    speed),
                InfoBarSeverity.Informational,
                L.Get("shell.ue4ss_downloading_title"));
        });
    }

    private static string FormatTransferBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        if (bytes >= mib)
            return $"{bytes / mib:0.00} MiB";
        if (bytes >= kib)
            return $"{bytes / kib:0.0} KiB";
        return $"{bytes} B";
    }

    private static string FormatTransferSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "—";
        return $"{FormatTransferBytes((long)bytesPerSecond)}/s";
    }

    public async Task UninstallLiveToolsAsync()
    {
        if (_installingBridge) return;

        try
        {
            if (!_bridge.HasRemovableInstall())
            {
                Report(
                    L.Get("shell.bridge_not_installed_msg"),
                    InfoBarSeverity.Informational,
                    L.Get("shell.bridge_uninstalled_title"));
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = L.Get("setup.uninstall"),
                Content = L.Get("setup.uninstall_confirm"),
                PrimaryButtonText = L.Get("setup.uninstall"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            _installingBridge = true;
            string removedPath = await Task.Run(_bridge.UninstallBridge);
            Report(
                string.IsNullOrEmpty(removedPath)
                    ? L.Get("shell.bridge_uninstalled_cleared_msg")
                    : L.Format("shell.bridge_uninstalled_msg", removedPath),
                InfoBarSeverity.Success,
                L.Get("shell.bridge_uninstalled_title"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_uninstall_bridge"));
        }
        finally
        {
            _installingBridge = false;
        }
    }

    public async Task ChangeLiveToolsFolderAsync()
    {
        if (_installingBridge) return;

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            GameInstallationService.Current.Remember(folder.Path);
            _bridge.ClearRememberedInstallLocation();
            _bridge.InvalidateStatusCaches();
            Report(
                L.Format("shell.bridge_folder_updated_msg", folder.Path),
                InfoBarSeverity.Success,
                L.Get("shell.bridge_folder_updated_title"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_change_bridge_folder"));
        }
    }

    public bool IsInstallingLiveTools => _installingBridge;

    private byte[]? GetPatchPayload()
    {
        if (_patchSerializeTimer.IsEnabled)
            OnPatchSerializeTick(null, EventArgs.Empty);
        byte[]? snapshot = Volatile.Read(ref _patchPayload);
        return snapshot?.ToArray();
    }

    public void ReportCrash(Exception ex)
        => Report(ex.Message, InfoBarSeverity.Error, L.Get("common.something_went_wrong"));

    private void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? title = null)
    {
        _statusDismissTimer.Stop();
        Status.Title = title ?? severity switch
        {
            InfoBarSeverity.Error => L.Get("common.something_went_wrong"),
            InfoBarSeverity.Warning => L.Get("common.careful"),
            InfoBarSeverity.Success => L.Get("common.done"),
            _ => L.Get("common.info"),
        };
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _statusDismissTimer.Start();
    }

    private static Type ResolvePageType(string? tag) => tag switch
    {
        "home" => typeof(HomePage),
        "phone-remote" => typeof(RemoteControlPage),
        "setup" => typeof(SetupPage),
        "help" => typeof(ReadmePage),
        "community" => typeof(CommunityPage),
        "campaign-progress" => typeof(CampaignProgressPage),
        "customization" => typeof(CustomizationPage),
        "profile" => typeof(ProfilePage),
        "raw" => typeof(RawPage),
        "config" => typeof(ConfigPage),
        "game-saves" => typeof(GameSavesPage),
        "builtin-mod" => typeof(BuiltinModPage),
        "live-gameplay" or "live-spawn" or "live-player" or "live-world" => typeof(LiveToolsHubPage),
        "live-allegiance" => typeof(AllegianceDemoPage),
        "change-biped" => typeof(ChangeBipedPage),
        "runtime-tags" => typeof(RuntimeTagsPage),
        "scripting" => typeof(ScriptingPage),
        _ => typeof(MissionsPage),
    };

    private async void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        string? tag = item.Tag as string;
        // Parent section headers have no tag and must not force a page navigation.
        if (string.IsNullOrEmpty(tag))
            return;

        // Return to the dispatcher first so the NavigationView can paint the
        // selected item before any page create / swap work runs.
        await Task.Yield();

        Type page = ResolvePageType(tag);
        await NavigateContentAsync(
            page,
            page == typeof(LiveToolsHubPage) ? tag : null);
    }

    private async Task NavigateContentAsync(
        Type page,
        object? parameter = null,
        bool forceReload = false)
    {
        if (forceReload)
            ResetPageCache();

        bool isCloudContext =
            page == typeof(CampaignProgressPage) ||
            page == typeof(ProfilePage) ||
            page == typeof(RawPage);
        CloudActionsBar.Visibility = isCloudContext
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Same live-tools shell: swap section without recreating the hub page.
        if (!forceReload &&
            page == typeof(LiveToolsHubPage) &&
            parameter is string section &&
            ContentFrame.Content is LiveToolsHubPage hub)
        {
            try
            {
                await hub.ShowSectionAsync(section);
            }
            catch (Exception ex)
            {
                App.LogCrash("Navigate", ex);
                Report(ex.Message, InfoBarSeverity.Error);
            }
            return;
        }

        if (!forceReload && ContentFrame.Content?.GetType() == page)
        {
            // Already visible: still refresh volatile status (bridge install, etc.).
            ActivatePage(ContentFrame.Content as Page);
            return;
        }

        bool coldStart = !_pageCache.ContainsKey(page);
        int generation = ++_navigationGeneration;
        if (coldStart)
        {
            SetNavigationLoading(true);
            await Task.Yield();
            if (generation != _navigationGeneration)
                return;
        }

        try
        {
            Page instance = GetOrCreatePage(page, out _);
            PresentPage(instance);

            if (page == typeof(LiveToolsHubPage) &&
                parameter is string liveSection &&
                instance is LiveToolsHubPage liveHub)
            {
                await liveHub.ShowSectionAsync(liveSection);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("Navigate", ex);
            Report(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (generation == _navigationGeneration)
                SetNavigationLoading(false);
        }
    }

    private Page GetOrCreatePage(Type pageType, out bool created)
    {
        if (_pageCache.TryGetValue(pageType, out Page? existing))
        {
            created = false;
            return existing;
        }

        Page page = (Page)Activator.CreateInstance(pageType)!;
        page.NavigationCacheMode = NavigationCacheMode.Required;
        _pageCache[pageType] = page;
        created = true;
        return page;
    }

    private void PresentPage(Page page)
    {
        if (!ReferenceEquals(_activePage, page))
        {
            DeactivatePage(_activePage);
            ContentFrame.Content = page;
            _activePage = page;
        }

        ActivatePage(page);
    }

    private static void ActivatePage(Page? page)
    {
        if (page is IActivatablePage activatable)
            activatable.OnActivated();
    }

    private static void DeactivatePage(Page? page)
    {
        if (page is IActivatablePage activatable)
            activatable.OnDeactivated();
    }

    private void ResetPageCache()
    {
        DeactivatePage(_activePage);
        _activePage = null;
        ContentFrame.Content = null;
        _pageCache.Clear();
        _navigationGeneration++;
    }

    private void SetNavigationLoading(bool loading)
    {
        NavigationLoadingOverlay.Visibility =
            loading ? Visibility.Visible : Visibility.Collapsed;
        NavigationLoadingRing.IsActive = loading;
    }

    public void NavigateTo(string tag)
    {
        NavigationViewItem? item = tag switch
        {
            "home" => HomeNavItem,
            "campaign-progress" => CampaignProgressNavItem,
            "profile" => ProfileNavItem,
            "raw" => RawNavItem,
            "customization" => CustomizationNavItem,
            "config" => ConfigNavItem,
            "game-saves" => GameSavesNavItem,
            "builtin-mod" => BuiltinModNavItem,
            "live-gameplay" => GameplayNavItem,
            "live-spawn" => SpawnEquipNavItem,
            "live-allegiance" => AllegianceNavItem,
            "live-player" => PlayerAppearanceNavItem,
            "live-world" => CameraWorldNavItem,
            "change-biped" => ChangeBipedNavItem,
            // "runtime-tags" => RuntimeTagsNavItem,
            "scripting" => ScriptingNavItem,
            "phone-remote" => RemoteNavItem,
            "setup" => SetupNavItem,
            "help" => HelpNavItem,
            // "community" => CommunityNavItem,
            _ => null,
        };

        if (item is not null)
            Nav.SelectedItem = item;
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);

            foreach (string extension in new[] { ".json", ".sav", ".dat", ".bin", ".txt", ".b64" })
                picker.FileTypeFilter.Add(extension);
            picker.FileTypeFilter.Add("*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            LoadFrom(file.Path);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    public void LoadFrom(string path)
    {
        try
        {
            HaloSave save = HaloSave.LoadFile(path);
            _state.Load(save);

            bool exact = save.VerifyRoundTrip(out string detail);
            IReadOnlyList<string> unknown = save.UnknownTags();

            string note = exact
                ? L.Format("shell.loaded_tags_verified", save.Tags.Count, detail)
                : L.Format("shell.loaded_tags_unverified", detail);

            if (unknown.Count > 0)
                note += " " + L.Format("shell.unknown_tags_note", unknown.Count);

            Report(note, exact ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _state.Unload();
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        if (string.IsNullOrEmpty(save.Envelope.SourcePath))
        {
            OnSaveAs(sender, e);
            return;
        }

        try
        {
            save.Save(save.Envelope.SourcePath!);
            _state.MarkClean();
            UpdateChrome();
            Report(L.Format("shell.written_with_bak", save.Envelope.SourcePath),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);

            picker.FileTypeChoices.Add(L.Get("shell.same_format"), new List<string> { System.IO.Path.GetExtension(save.Envelope.SourcePath ?? ".json") is { Length: > 0 } ext ? ext : ".json" });
            picker.FileTypeChoices.Add(L.Get("shell.json"), new List<string> { ".json" });
            picker.FileTypeChoices.Add(L.Get("shell.binary_save"), new List<string> { ".sav" });
            picker.FileTypeChoices.Add(L.Get("shell.base64_text"), new List<string> { ".txt" });
            picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(save.Envelope.SourcePath ?? "halo-save") + "-edited";

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            save.Save(file.Path, backup: false);
            _state.MarkClean();
            UpdateChrome();
            Report(L.Format("shell.written_to", file.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnCopyBase64(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        try
        {
            var package = new DataPackage();
            package.SetText(save.BuildBase64());
            Clipboard.SetContent(package);
            Report(L.Get("shell.base64_clipboard"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnPasteBase64(object sender, RoutedEventArgs e)
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
            {
                Report(L.Get("shell.clipboard_no_text"), InfoBarSeverity.Warning);
                return;
            }

            string text = await view.GetTextAsync();
            HaloSave save = HaloSave.LoadText(text);
            _state.Load(save);
            UpdateChrome();
            Report(L.Format("shell.loaded_from_clipboard", save.Tags.Count),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnVerify(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        bool exact = save.VerifyRoundTrip(out string detail);
        Report(exact
                ? L.Format("shell.verify_ok", detail)
                : L.Format("shell.verify_diff", detail),
            exact ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_state.Save?.Envelope.SourcePath is not { } path)
        {
            Report(L.Get("shell.nothing_to_reload"), InfoBarSeverity.Warning);
            return;
        }

        LoadFrom(path);
    }

    private void TryLoadSavedPlayFabSession()
    {
        if (!_proxy.HasSavedSession || _proxy.HasCapturedSession)
            return;

        try
        {
            _proxy.LoadSessionFromCredentialLocker();
        }
        catch (Exception ex)
        {
            Report(
                L.Format("shell.auth_load_failed", ex.Message),
                InfoBarSeverity.Warning,
                L.Get("shell.auth_unavailable"));
        }
    }

    private async void OnGetUserData(object sender, RoutedEventArgs e)
    {
        if (_state.IsDirty)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = L.Get("shell.replace_unsaved_title"),
                Content = L.Get("shell.replace_unsaved_body"),
                PrimaryButtonText = L.Get("shell.get_cloud_data"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        await RunCloudOperation(async () =>
        {
            PlayFabGetResult result = await _proxy.GetSaveFromPlayFabAsync();
            _state.Load(result.Save);
            Report(
                L.Format(
                    "shell.cloud_loaded_msg",
                    result.Save.Tags.Count,
                    result.DataVersion?.ToString() ?? L.Get("common.unknown"),
                    result.BackupPath),
                InfoBarSeverity.Success,
                L.Get("shell.user_data_loaded"));
        });
    }

    private void OnSaveAuth(object sender, RoutedEventArgs e)
    {
        if (_awaitingAuthCapture)
        {
            _awaitingAuthCapture = false;
            _authSavedDuringCapture = false;
            _proxy.Stop();
            UpdateCloudActions();
            Report(
                L.Get("shell.capture_cancelled_msg"),
                InfoBarSeverity.Informational,
                L.Get("shell.capture_cancelled"));
            return;
        }

        if (_proxy.HasCapturedSession && !_proxy.HasSavedSession)
        {
            try
            {
                string host = _proxy.SaveSessionToCredentialLocker();
                UpdateCloudActions();
                Report(
                    L.Format("shell.auth_saved_host", host),
                    InfoBarSeverity.Success,
                    L.Get("shell.auth_saved"));
            }
            catch (Exception ex)
            {
                Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_save_auth"));
            }
            return;
        }

        try
        {
            _authSavedDuringCapture = false;
            _awaitingAuthCapture = true;
            _proxy.Start();
            UpdateCloudActions();
            Report(
                L.Get("shell.waiting_auth_msg"),
                InfoBarSeverity.Informational,
                L.Get("shell.waiting_auth_title"));
        }
        catch (Exception ex)
        {
            _awaitingAuthCapture = false;
            _authSavedDuringCapture = false;
            UpdateCloudActions();
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_start_capture"));
        }
    }

    private async void OnPatchSettings(object sender, RoutedEventArgs e)
    {
        if (BuildPolicy.IsRetail)
        {
            Report(
                L.Get("shell.retail_readonly_msg"),
                InfoBarSeverity.Informational,
                L.Get("shell.readonly_title"));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = L.Get("shell.patch_title"),
            Content = L.Get("shell.patch_body"),
            PrimaryButtonText = L.Get("shell.backup_and_patch"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await RunCloudOperation(async () =>
        {
            PlayFabTestFlowResult result = await _proxy.RunGetPatchGetAsync();
            if (!result.Verified)
                throw new InvalidOperationException(
                    L.Format("shell.patch_verify_failed", result.Before.BackupPath));

            _state.Load(result.After.Save);
            Report(
                L.Format(
                    "shell.settings_patched_msg",
                    result.After.DataVersion?.ToString() ?? L.Get("common.unknown"),
                    result.Before.BackupPath),
                InfoBarSeverity.Success,
                L.Get("shell.settings_patched"));
        });
    }

    private async Task RunCloudOperation(Func<Task> operation)
    {
        if (_cloudBusy)
            return;

        _cloudBusy = true;
        UpdateCloudActions();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.playfab_failed"));
        }
        finally
        {
            _cloudBusy = false;
            UpdateCloudActions();
        }
    }

    private void OnPlayFabSessionChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_awaitingAuthCapture && !_authSavedDuringCapture)
            {
                try
                {
                    _proxy.SaveSessionToCredentialLocker();
                    _authSavedDuringCapture = true;
                    Report(
                        L.Get("shell.auth_captured_finishing"),
                        InfoBarSeverity.Success,
                        L.Get("shell.auth_saved"));
                }
                catch (Exception ex)
                {
                    Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_save_auth"));
                }
            }
            UpdateCloudActions();
        });
    }

    private void OnPlayFabTraffic(TrafficEntry entry)
    {
        if (!_awaitingAuthCapture ||
            !_authSavedDuringCapture ||
            !entry.IsPlayFab ||
            entry.StatusCode is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_awaitingAuthCapture || !_authSavedDuringCapture)
                return;
            _awaitingAuthCapture = false;
            _authSavedDuringCapture = false;
            _proxy.Stop();
            UpdateCloudActions();
            Report(
                BuildPolicy.IsRetail
                    ? L.Get("shell.auth_ready_retail")
                    : L.Get("shell.auth_ready_full"),
                InfoBarSeverity.Success,
                L.Get("shell.cloud_actions_ready"));
        });
    }

    private void UpdateCloudActions()
    {
        bool hasAuth = _proxy.HasCapturedSession;
        GetUserDataButton.IsEnabled = !_cloudBusy && !_awaitingAuthCapture && hasAuth;
        PatchSettingsButton.IsEnabled =
            !BuildPolicy.IsRetail &&
            !_cloudBusy && !_awaitingAuthCapture && hasAuth && _state.IsLoaded;
        SaveAuthButton.IsEnabled = !_cloudBusy;
        SaveAuthButton.Label = _awaitingAuthCapture
            ? L.Get("shell.cancel_authentication")
            : L.Get("shell.authenticate");
        CloudContextText.Text = _state.IsLoaded
            ? _state.IsDirty
                ? L.Get("shell.cloud_dirty")
                : L.Get("shell.cloud_clean")
            : hasAuth
                ? L.Get("shell.cloud_auth_ready")
                : L.Get("shell.cloud_need_auth");

        ToolTipService.SetToolTip(
            GetUserDataButton,
            hasAuth
                ? L.Format("shell.tip_load_blam", _proxy.SessionHost)
                : L.Get("shell.tip_save_auth_first"));
        ToolTipService.SetToolTip(
            SaveAuthButton,
            _awaitingAuthCapture
                ? L.Get("shell.tip_stop_capture")
                : _proxy.HasSavedSession
                    ? L.Get("shell.tip_refresh_session")
                    : L.Get("shell.tip_save_session"));
        ToolTipService.SetToolTip(
            PatchSettingsButton,
            BuildPolicy.IsRetail
                ? L.Get("shell.tip_retail_readonly")
                : hasAuth
                ? L.Get("shell.tip_patch_flow")
                : L.Get("shell.tip_save_auth_first"));
    }

    private void OnProxyError(string message)
        => DispatcherQueue.TryEnqueue(() => Report(message, InfoBarSeverity.Error));

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _patchSerializeTimer.Stop();
        _statusDismissTimer.Stop();
        RemoteControlService.Current.StopForShutdown(TimeSpan.FromSeconds(3));
        LocalizationService.Current.LanguageChanged -= OnAppLanguageChanged;
        _proxy.Error -= OnProxyError;
        _proxy.SessionChanged -= OnPlayFabSessionChanged;
        _proxy.TrafficObserved -= OnPlayFabTraffic;
        _proxy.PatchPayloadProvider = null;
        _proxy.Stop();
        _game.ConnectionChanged -= OnGameConnectionChanged;
        _game.Dispose();
    }
}
