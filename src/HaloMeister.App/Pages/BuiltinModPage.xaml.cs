using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class BuiltinModPage : Page, IActivatablePage
{
    private bool _busy;
    private BuiltinModListItem[] _items = [];

    public BuiltinModPage() => InitializeComponent();

    public void OnActivated() => _ = RefreshStatusAsync();

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        _ = RefreshStatusAsync();

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BuiltinModListItem item })
            return;

        await RunBusy(async () =>
        {
            var service = new FullPalettesOverlayService(item.Definition);
            if (service.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            bool update = item.State is
                BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete;
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = update
                    ? L.Get("builtin_mod.update")
                    : L.Get("builtin_mod.install"),
                Content = L.Format(
                    update
                        ? "builtin_mod.item_update_confirm"
                        : "builtin_mod.item_install_confirm",
                    item.Title),
                PrimaryButtonText = update
                    ? L.Get("builtin_mod.update")
                    : L.Get("builtin_mod.install"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(service.Install);
            await RefreshStatusAsync();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BuiltinModListItem item })
            return;

        await RunBusy(async () =>
        {
            var service = new FullPalettesOverlayService(item.Definition);
            if (service.IsGameRunning)
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
                Content = L.Format("builtin_mod.item_remove_confirm", item.Title),
                PrimaryButtonText = L.Get("builtin_mod.remove"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(service.Remove);
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
            IReadOnlyList<BuiltinModCatalogStatus> catalog =
                await Task.Run(FullPalettesOverlayService.GetCatalogStatuses);
            _items = catalog.Select(entry => CreateListItem(entry, _busy)).ToArray();
            ModList.ItemsSource = _items;

            BuiltinModCatalogStatus? prompt = catalog.FirstOrDefault(entry =>
                entry.Sync.NeedsUpdatePrompt);
            if (prompt is not null)
            {
                ShowStatus(prompt.Sync.Message, InfoBarSeverity.Warning);
            }
            else if (catalog.Any(entry =>
                entry.Sync.State == BuiltinModSyncState.BundleTampered))
            {
                BuiltinModCatalogStatus tampered = catalog.First(entry =>
                    entry.Sync.State == BuiltinModSyncState.BundleTampered);
                ShowStatus(tampered.Sync.Message, InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            ModList.ItemsSource = Array.Empty<BuiltinModListItem>();
        }
        finally
        {
            BusyRing.IsActive = _busy;
            RefreshButton.IsEnabled = !_busy;
        }
    }

    private static BuiltinModListItem CreateListItem(
        BuiltinModCatalogStatus entry,
        bool busy)
    {
        bool update = entry.Sync.State is
            BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete;
        string notes = string.Join(
            Environment.NewLine,
            entry.Definition.NoteKeys.Select(L.Get));
        return new BuiltinModListItem(
            Definition: entry.Definition,
            Title: L.Get(entry.Definition.TitleKey),
            Description: L.Get(entry.Definition.DescriptionKey),
            Notes: notes,
            NotesVisibility: string.IsNullOrWhiteSpace(notes)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Status: entry.Sync.Message,
            Stem: entry.Definition.Stem,
            InstallLabel: update
                ? L.Get("builtin_mod.update")
                : L.Get("builtin_mod.install"),
            CanInstall: !busy && entry.Sync.CanInstall,
            CanRemove: !busy && entry.Sync.CanRemove,
            State: entry.Sync.State);
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        if (_items.Length > 0)
        {
            _items = _items
                .Select(item => item with { CanInstall = false, CanRemove = false })
                .ToArray();
            ModList.ItemsSource = _items;
        }
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
            RefreshButton.IsEnabled = true;
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

public sealed record BuiltinModListItem(
    BuiltinModDefinition Definition,
    string Title,
    string Description,
    string Notes,
    Visibility NotesVisibility,
    string Status,
    string Stem,
    string InstallLabel,
    bool CanInstall,
    bool CanRemove,
    BuiltinModSyncState State);
