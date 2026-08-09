using HaloMeister.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HaloMeister.App.Pages;

public sealed partial class LiveToolsHubPage : Page, IActivatablePage
{
    private sealed record ToolDefinition(
        string LabelKey,
        Symbol Icon,
        Type PageType,
        bool Enabled = true,
        string? Parameter = null);

    private sealed record ToolTarget(Type PageType, string? Parameter);

    private readonly Dictionary<Type, Page> _toolCache = new();
    private string? _section;
    private Page? _activeTool;
    private string? _activeParameter;

    public LiveToolsHubPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    public void OnActivated()
    {
        ActivateTool(_activeTool ?? ToolFrame.Content as Page);
    }

    public void OnDeactivated()
    {
        DeactivateTool(_activeTool);
    }

    public async Task ShowSectionAsync(string section)
    {
        // Let the shell finish painting the selected nav item first.
        await Task.Yield();

        ToolLoadingText.Text = L.Get("common.loading");
        bool sectionChanged = !string.Equals(_section, section, StringComparison.Ordinal);
        _section = section;

        if (sectionChanged)
            Configure(section);

        if (ToolNav.SelectedItem is NavigationViewItem { Tag: ToolTarget target })
            await ShowToolAsync(target);
    }

    private void Configure(string section)
    {
        ToolDefinition[] tools = section switch
        {
            "live-spawn" =>
            [
                new("live_hub.builtin_mod", Symbol.Library, typeof(BuiltinModPage)),
                new("live_hub.characters", Symbol.Add, typeof(SpawnerPage)),
                new("live_hub.squads", Symbol.People, typeof(SquadsPage)),
                new("live_hub.weapons", Symbol.Bullets, typeof(WeaponLoaderPage)),
                new("live_hub.vehicles", Symbol.Directions, typeof(VehicleWorkshopPage)),
            ],
            "live-player" =>
            [
                new("live_hub.player_tools", Symbol.Contact, typeof(PlayerToolsPage)),
                new("live_hub.armor_mixer", Symbol.Edit, typeof(ArmorMixerPage)),
            ],
            "live-world" =>
            [
                new("live_hub.machinima", Symbol.Video, typeof(AdvancedMachinimaPage)),
                new("live_hub.boundaries", Symbol.Map, typeof(BoundaryVolumesPage)),
            ],
            _ =>
            [
                new(
                    "cheat_globals.quick_cheats",
                    Symbol.Manage,
                    typeof(CheatGlobalsPage),
                    Parameter: "quick-cheats"),
                new(
                    "cheat_globals.player_traits",
                    Symbol.Contact,
                    typeof(CheatGlobalsPage),
                    Parameter: "player-traits"),
                new(
                    "cheat_globals.allegiance",
                    Symbol.People,
                    typeof(CheatGlobalsPage),
                    Parameter: "allegiance"),
                new("live_hub.live_skulls", Symbol.Emoji, typeof(LiveSkullsPage)),
                new("live_hub.other", Symbol.More, typeof(OtherGameplayPage)),
            ],
        };

        ToolNav.SelectionChanged -= OnToolSelectionChanged;
        ToolNav.MenuItems.Clear();

        NavigationViewItem? firstEnabled = null;
        foreach (ToolDefinition tool in tools)
        {
            bool enabled = tool.Enabled;
            var item = new NavigationViewItem
            {
                Content = L.Get(tool.LabelKey),
                Icon = new SymbolIcon(tool.Icon),
                Tag = new ToolTarget(tool.PageType, tool.Parameter),
                IsEnabled = enabled,
            };
            if (!enabled)
            {
                ToolTipService.SetToolTip(
                    item,
                    L.Get("live_hub.disabled_tooltip"));
            }
            ToolNav.MenuItems.Add(item);
            firstEnabled ??= enabled ? item : null;
        }

        ToolNav.SelectedItem = firstEnabled;
        ToolNav.SelectionChanged += OnToolSelectionChanged;
    }

    private async void OnToolSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: ToolTarget target })
            return;

        await Task.Yield();
        await ShowToolAsync(target);
    }

    private async Task ShowToolAsync(ToolTarget target)
    {
        if (_activeTool?.GetType() == target.PageType &&
            string.Equals(_activeParameter, target.Parameter, StringComparison.Ordinal))
        {
            ActivateTool(_activeTool);
            return;
        }

        bool coldStart = !_toolCache.ContainsKey(target.PageType);
        if (coldStart)
        {
            SetToolLoading(true);
            await Task.Yield();
        }

        try
        {
            if (!_toolCache.TryGetValue(target.PageType, out Page? page))
            {
                page = (Page)Activator.CreateInstance(target.PageType)!;
                page.NavigationCacheMode = NavigationCacheMode.Required;
                _toolCache[target.PageType] = page;
            }

            if (page is CheatGlobalsPage cheats && target.Parameter is not null)
                cheats.ShowSection(target.Parameter);

            if (_activeTool?.GetType() != target.PageType)
                DeactivateTool(_activeTool);

            ToolFrame.Content = page;
            _activeTool = page;
            _activeParameter = target.Parameter;
            ActivateTool(page);
        }
        finally
        {
            if (coldStart)
                SetToolLoading(false);
        }
    }

    private static void ActivateTool(Page? page)
    {
        if (page is IActivatablePage activatable)
            activatable.OnActivated();
    }

    private static void DeactivateTool(Page? page)
    {
        if (page is IActivatablePage activatable)
            activatable.OnDeactivated();
    }

    private void SetToolLoading(bool loading)
    {
        ToolLoadingOverlay.Visibility =
            loading ? Visibility.Visible : Visibility.Collapsed;
        ToolLoadingRing.IsActive = loading;
    }
}
