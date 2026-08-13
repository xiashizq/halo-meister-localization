using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class BuiltinModPage : Page, IActivatablePage
{
    private readonly FullPalettesOverlayService _mod = new();
    private bool _busy;
    private BuiltinModSyncStatus? _sync;

    public BuiltinModPage() => InitializeComponent();

    public void OnActivated() => _ = RefreshStatusAsync();

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        _ = RefreshStatusAsync();

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            if (_mod.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            bool update = _sync?.State is
                BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete;
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = update
                    ? L.Get("builtin_mod.update")
                    : L.Get("builtin_mod.install"),
                Content = update
                    ? L.Get("builtin_mod.update_confirm")
                    : L.Get("builtin_mod.install_confirm"),
                PrimaryButtonText = update
                    ? L.Get("builtin_mod.update")
                    : L.Get("builtin_mod.install"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(_mod.Install);
            await RefreshStatusAsync();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            if (_mod.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("builtin_mod.remove"),
                Content = L.Get("builtin_mod.remove_confirm"),
                PrimaryButtonText = L.Get("builtin_mod.remove"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(_mod.Remove);
            await RefreshStatusAsync();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async Task RefreshStatusAsync()
    {
        BusyRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        try
        {
            BuiltinModSyncStatus sync = await Task.Run(_mod.GetSyncStatus);
            _sync = sync;
            ApplySyncStatus(sync);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            InstallButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
        }
        finally
        {
            BusyRing.IsActive = _busy;
            RefreshButton.IsEnabled = !_busy;
        }
    }

    private void ApplySyncStatus(BuiltinModSyncStatus sync)
    {
        StatusText.Text = sync.Message;
        bool update = sync.State is
            BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete;
        InstallButton.Content = update
            ? L.Get("builtin_mod.update")
            : L.Get("builtin_mod.install");
        InstallButton.IsEnabled = !_busy && sync.CanInstall;
        RemoveButton.IsEnabled = !_busy && sync.CanRemove;
        RefreshButton.IsEnabled = !_busy;
        RestartBanner.IsOpen = true;

        if (sync.NeedsUpdatePrompt)
        {
            ShowStatus(sync.Message, InfoBarSeverity.Warning);
        }
        else if (sync.State == BuiltinModSyncState.BundleTampered)
        {
            ShowStatus(sync.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        InstallButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            await RefreshStatusAsync();
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            if (_sync is BuiltinModSyncStatus sync)
                ApplySyncStatus(sync);
            else
                await RefreshStatusAsync();
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
