using System.Globalization;
using System.Text.RegularExpressions;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HaloMeister.App.Pages;

public sealed partial class ConfigPage : Page, IActivatablePage
{
    private readonly MeteoriteConfigStore _store = new();
    private readonly ScriptingBridgeService _scriptingBridge = ScriptingBridgeService.Current;
    private List<ConfigDocument> _documents = [];
    private readonly Dictionary<ConfigDocument, DocumentViewState> _documentViews = [];
    private readonly Dictionary<ConfigDocument, DocumentListItemState> _documentListItems = [];
    private ConfigDocument? _selectedDocument;
    private bool _initialized;

    public ConfigPage()
    {
        InitializeComponent();
    }

    public void OnActivated()
    {
        if (!_initialized)
        {
            _initialized = true;
            ReloadDocuments();
            return;
        }

        // Refresh from disk only when the editor has no unsaved edits.
        if (!_documents.Any(document => document.IsDirty))
            ReloadDocuments();
    }

    private void ReloadDocuments()
    {
        try
        {
            string? previouslySelected = _selectedDocument?.RelativePath;
            _documents = _store.LoadDocuments().ToList();
            _selectedDocument = null;
            _documentViews.Clear();
            _documentListItems.Clear();
            DocumentList.Items.Clear();
            EditorContent.Content = null;

            foreach (ConfigDocument document in _documents)
            {
                _documentViews.Add(document, CreateDocumentView(document));
                ListViewItem item = CreateDocumentListItem(document);
                _documentListItems.Add(document, new DocumentListItemState(
                    item,
                    (TextBlock)((StackPanel)((Grid)item.Content).Children[1]).Children[0],
                    (Border)((Grid)item.Content).Children[2]));
                DocumentList.Items.Add(item);
            }

            bool hasDocuments = _documents.Count > 0;
            EmptyState.Visibility = hasDocuments ? Visibility.Collapsed : Visibility.Visible;
            SearchSettings.IsEnabled = hasDocuments;
            SelectedDocumentTitle.Text = hasDocuments
                ? L.Get("config.select_a_config_file")
                : L.Get("config.meteorite_config_unavailable");
            SelectedDocumentPath.Text = hasDocuments
                ? string.Empty
                : $"{_store.ConfigRoot}  •  {_store.ImGuiRoot}";
            SettingCountText.Text = string.Empty;

            if (hasDocuments)
            {
                ConfigDocument selected = _documents.FirstOrDefault(document =>
                    string.Equals(document.RelativePath, previouslySelected, StringComparison.OrdinalIgnoreCase))
                    ?? _documents[0];
                DocumentList.SelectedItem = _documentListItems[selected].Item;
            }

            RefreshDocumentChrome();
            RefreshBackups();
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private DocumentViewState CreateDocumentView(ConfigDocument document)
    {
        IniDocumentModel ini = IniDocumentModel.Parse(document);
        var sectionRows = new Dictionary<Expander, List<(FrameworkElement Row, string SearchText)>>();
        var sections = new StackPanel { Spacing = 10, Padding = new Thickness(24, 0, 18, 24) };
        foreach (IGrouping<string, IniSetting> group in ini.Settings.GroupBy(setting => GetCategory(document, setting)))
        {
            var rows = new StackPanel { Spacing = 8, Padding = new Thickness(0, 2, 0, 4) };
            var expanderHeader = new Grid { Padding = new Thickness(2, 5, 4, 5) };
            expanderHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            expanderHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            expanderHeader.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var countBadge = new Border
            {
                Padding = new Thickness(8, 2, 8, 2),
                Background = Application.Current.Resources["ControlFillColorSecondaryBrush"] as Brush,
                CornerRadius = new CornerRadius(10),
                Child = new TextBlock { Text = group.Count().ToString(CultureInfo.InvariantCulture), FontSize = 12 },
            };
            Grid.SetColumn(countBadge, 1);
            expanderHeader.Children.Add(countBadge);
            var expander = new Expander
            {
                Header = expanderHeader,
                IsExpanded = group.Count() <= 8,
                Content = rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };

            var trackedRows = new List<(FrameworkElement Row, string SearchText)>();
            foreach (IniSetting setting in group)
            {
                FrameworkElement row = CreateSettingRow(setting, () =>
                {
                    RefreshDocumentChrome(document);
                });
                rows.Children.Add(row);
                trackedRows.Add((row, $"{group.Key} {setting.Key} {LocalizeFieldName(setting.Key)}"));
            }

            sectionRows.Add(expander, trackedRows);
            sections.Children.Add(expander);
        }

        var scroll = new ScrollViewer
        {
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return new DocumentViewState(scroll, sectionRows, ini.Settings.Count);
    }

    private static ListViewItem CreateDocumentListItem(ConfigDocument document)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(2, 5, 2, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon
        {
            Glyph = "\uE8A5",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var labels = new StackPanel { Spacing = 1 };
        Grid.SetColumn(labels, 1);
        labels.Children.Add(new TextBlock
        {
            Text = document.TabTitle,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        labels.Children.Add(new TextBlock
        {
            Text = GetDocumentLocationLabel(document),
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        grid.Children.Add(labels);

        var dirtyDot = new Border
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Application.Current.Resources["SystemFillColorCautionBrush"] as Brush,
            CornerRadius = new CornerRadius(4),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(dirtyDot, 2);
        grid.Children.Add(dirtyDot);

        var item = new ListViewItem
        {
            Content = grid,
            Tag = document,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(item, document.RelativePath);
        return item;
    }

    private static string GetDocumentLocationLabel(ConfigDocument document)
    {
        string? directory = Path.GetDirectoryName(document.RelativePath);
        if (string.IsNullOrWhiteSpace(directory))
            return L.Get("config.meteorite");
        return directory.Replace("\\", " › ", StringComparison.Ordinal);
    }

    private static string LocalizeChoiceValue(string value)
    {
        if (value.StartsWith("Percent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value["Percent".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
        {
            return L.Format("config.enum_percent", percent);
        }

        if (value.StartsWith("LookSensitivity", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value["LookSensitivity".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lookSensitivity))
        {
            return L.Format("config.enum_look_sensitivity_n", lookSensitivity);
        }

        if (value.StartsWith("LookAcceleration", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value["LookAcceleration".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lookAcceleration))
        {
            return L.Format("config.enum_look_acceleration_n", lookAcceleration);
        }

        if (value.Length > 1 &&
            value.EndsWith('s') &&
            int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) &&
            value.AsSpan(0, value.Length - 1).IndexOfAnyExcept("0123456789") < 0)
        {
            return L.Format("config.enum_seconds_n", seconds);
        }

        if (value.EndsWith("Seconds", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[..^"Seconds".Length], NumberStyles.Integer, CultureInfo.InvariantCulture, out int namedSeconds))
        {
            return L.Format("config.enum_seconds_n", namedSeconds);
        }

        string? mapped = value switch
        {
            "Low" => L.Get("config.quality_low"),
            "Medium" => L.Get("config.quality_medium"),
            "High" => L.Get("config.quality_high"),
            "Epic" => L.Get("config.quality_epic"),
            "Cinematic" => L.Get("config.quality_cinematic"),
            "Ultra" => L.Get("config.quality_ultra"),
            "Custom" => L.Get("config.quality_custom"),
            "SystemDefault" => L.Get("config.visual_language_system_default"),
            "English" => L.Get("config.visual_language_english"),
            "Standard" => L.Get("config.dynamic_range_standard"),
            "Compressed" => L.Get("config.dynamic_range_compressed"),
            "Wide" => L.Get("config.dynamic_range_wide"),
            "Default" => L.Get("config.text_size_default"),
            "UseGlobal" => L.Get("config.text_size_use_global"),
            "Minimum" => L.Get("config.text_size_minimum"),
            "Maximum" => L.Get("config.text_size_maximum"),
            "None" => L.Get("config.enum_none"),
            "Off" => L.Get("config.enum_off"),
            "On" => L.Get("config.enum_on"),
            "Poor" => L.Get("config.enum_poor"),
            "Good" => L.Get("config.enum_good"),
            "Invulnerable" => L.Get("config.enum_invulnerable"),
            "Fatality" => L.Get("config.enum_fatality"),
            "BottomlessClip" => L.Get("config.enum_bottomless_clip"),
            _ => null,
        };
        if (mapped is not null)
            return mapped;

        string enumKey = $"config.enum_{ToConfigLocSuffix(value)}";
        return LocalizationService.Current.Has(enumKey)
            ? L.Get(enumKey)
            : Humanize(value);
    }

    private static string LocalizeFieldName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return L.Get("config.value");

        if (name.StartsWith("PlayerTraits", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name["PlayerTraits".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return Humanize(name);
        }

        string fieldKey = $"config.field_{ToConfigLocSuffix(name)}";
        return LocalizationService.Current.Has(fieldKey)
            ? L.Get(fieldKey)
            : Humanize(name);
    }

    private static string LocalizeDocumentTitle(string tabTitle)
    {
        string stem = Path.GetFileNameWithoutExtension(tabTitle);
        string documentKey = $"config.document_{ToConfigLocSuffix(stem)}";
        return LocalizationService.Current.Has(documentKey)
            ? L.Get(documentKey)
            : Humanize(stem);
    }

    private static string LocalizeSectionName(string section)
    {
        string cleaned = section.Replace("/Script/", string.Empty, StringComparison.Ordinal);
        string sectionKey = $"config.section_{ToConfigLocSuffix(cleaned)}";
        return LocalizationService.Current.Has(sectionKey)
            ? L.Get(sectionKey)
            : Humanize(cleaned);
    }

    private static string ToConfigLocSuffix(string name)
    {
        string text = name.Trim();
        if (text.StartsWith('b') && text.Length > 1 && char.IsUpper(text[1]))
            text = text[1..];

        text = text.Replace("sg.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("/Script/", string.Empty, StringComparison.Ordinal)
            .Replace('-', '_')
            .Replace('.', '_')
            .Replace(' ', '_');
        text = Regex.Replace(text, "([A-Z]+)([A-Z][a-z])", "$1_$2");
        text = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1_$2");
        text = Regex.Replace(text, "([A-Za-z])([0-9])", "$1_$2");
        text = Regex.Replace(text, "_+", "_").Trim('_');
        return text.ToLowerInvariant();
    }

    private void OnDocumentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentList.SelectedItem is not ListViewItem { Tag: ConfigDocument document } ||
            !_documentViews.TryGetValue(document, out DocumentViewState? view))
            return;

        _selectedDocument = document;
        EditorContent.Content = view.Content;
        EmptyState.Visibility = Visibility.Collapsed;
        SelectedDocumentTitle.Text = LocalizeDocumentTitle(document.TabTitle);
        SelectedDocumentPath.Text = document.RelativePath;
        SettingCountText.Text = L.Format("config.settings_count", view.SettingCount);
        SearchSettings.Text = string.Empty;
        RefreshDocumentChrome(document);
    }

    private void OnSearchSettingsChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedDocument is null ||
            !_documentViews.TryGetValue(_selectedDocument, out DocumentViewState? view))
            return;

        string query = SearchSettings.Text.Trim();
        int visibleCount = 0;
        foreach ((Expander expander, List<(FrameworkElement Row, string SearchText)> trackedRows) in view.SectionRows)
        {
            bool anyVisible = false;
            foreach ((FrameworkElement row, string searchText) in trackedRows)
            {
                bool visible = query.Length == 0 ||
                               searchText.Contains(query, StringComparison.OrdinalIgnoreCase);
                row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                anyVisible |= visible;
                if (visible)
                    visibleCount++;
            }

            expander.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
            if (query.Length > 0 && anyVisible)
                expander.IsExpanded = true;
        }

        SettingCountText.Text = query.Length == 0
            ? L.Format("config.settings_count", view.SettingCount)
            : L.Format("config.settings_filtered_count", visibleCount, view.SettingCount);
    }

    private static FrameworkElement CreateSettingRow(IniSetting setting, Action changed)
    {
        var labels = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 520,
        };
        labels.Children.Add(new TextBlock
        {
            Text = LocalizeFieldName(setting.Key),
            Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap,
        });
        labels.Children.Add(new TextBlock
        {
            Text = SettingDescription(setting),
            Opacity = 0.62,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        FrameworkElement editor = CreateSettingEditor(setting, changed);
        FrameworkElement layout;
        if (IsCompoundValue(setting.Value))
        {
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            var vertical = new StackPanel { Spacing = 10 };
            vertical.Children.Add(labels);
            vertical.Children.Add(editor);
            layout = vertical;
        }
        else
        {
            var grid = new Grid { ColumnSpacing = 20 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(labels);
            editor.HorizontalAlignment = HorizontalAlignment.Right;
            editor.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            layout = grid;
        }

        return new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(8),
            Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
            Child = layout,
        };
    }

    private static FrameworkElement CreateSettingEditor(IniSetting setting, Action changed)
    {
        if (UnrealConfigValue.TryParse(setting.Value, out UnrealConfigValue? parsed) &&
            parsed is UnrealContainerValue container)
        {
            return setting.Key.StartsWith("PlayerTraits", StringComparison.OrdinalIgnoreCase)
                ? CreatePlayerTraitsEditor(setting, container, changed)
                : CreateCompoundEditor(setting, container, changed);
        }

        if (setting.TryGetBoolean(out bool booleanValue))
        {
            var toggle = new ToggleSwitch
            {
                IsOn = booleanValue,
                OnContent = L.Get("cheat_globals.on"),
                OffContent = L.Get("cheat_globals.off"),
                MinWidth = 110,
            };
            toggle.Toggled += (_, _) =>
            {
                setting.SetBoolean(toggle.IsOn);
                changed();
            };
            return toggle;
        }

        if (TryCreateChoiceEditor(setting, changed, out ComboBox choiceEditor))
            return choiceEditor;

        if (setting.IsVector2)
            return CreateVectorEditor(setting, changed);

        if ((setting.IsInteger || setting.IsFloatingPoint) && !IsIdentifier(setting.Key))
            return CreateNumberEditor(setting, changed);

        var text = new TextBox
        {
            Text = setting.Value,
            MinWidth = 280,
            MaxWidth = 520,
            TextWrapping = setting.Value.Length > 100 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = setting.Value.Length > 100,
            MinHeight = setting.Value.Length > 100 ? 88 : 0,
        };
        text.TextChanged += (_, _) =>
        {
            setting.SetValue(text.Text);
            changed();
        };
        return text;
    }

    private static FrameworkElement CreatePlayerTraitsEditor(
        IniSetting setting,
        UnrealContainerValue root,
        Action changed)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (TraitGroup definition in TraitGroups)
        {
            UnrealConfigEntry? groupEntry = root.Entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (groupEntry?.Value is not UnrealContainerValue group)
                continue;

            var fields = new StackPanel { Spacing = 6 };
            foreach (TraitField field in definition.Fields)
                fields.Children.Add(CreateTraitFieldRow(field, group, root, setting, changed));

            panel.Children.Add(new Expander
            {
                Header = L.Get(definition.Label),
                IsExpanded = true,
                Content = fields,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }

        return panel;
    }

    private static FrameworkElement CreateTraitFieldRow(
        TraitField field,
        UnrealContainerValue group,
        UnrealContainerValue root,
        IniSetting setting,
        Action changed)
    {
        UnrealConfigEntry? existing = group.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, field.Name, StringComparison.OrdinalIgnoreCase));
        string current = (existing?.Value as UnrealScalarValue)?.Value ?? string.Empty;

        List<ConfigChoice> choices = field.Values
            .Append(current)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => new ConfigChoice(LocalizeChoiceValue(value), value))
            .Prepend(new ConfigChoice(L.Get("config.game_default"), string.Empty))
            .ToList();

        var combo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(ConfigChoice.Label),
            MinWidth = 260,
            SelectedItem = choices.First(choice =>
                choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase)),
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ConfigChoice selected)
                return;
            string value = selected.Value;
            if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
                return;

            UnrealConfigEntry? entry = group.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (value.Length == 0)
            {
                if (entry is not null)
                    group.Entries.Remove(entry);
            }
            else if (entry?.Value is UnrealScalarValue scalar)
            {
                scalar.Value = value;
            }
            else
            {
                group.Entries.Add(new UnrealConfigEntry(field.Name, new UnrealScalarValue(value, isQuoted: false)));
            }

            setting.SetValue(root.Serialize());
            current = value;
            changed();
        };
        return CreateNestedRow(L.Get(field.Label), field.Name, combo);
    }

    private static FrameworkElement CreateCompoundEditor(
        IniSetting setting,
        UnrealContainerValue root,
        Action changed)
    {
        Dictionary<string, List<string>> options = CollectScalarOptions(root);
        return CreateContainerPanel(root, root, setting, options, changed, depth: 0);
    }

    private static StackPanel CreateContainerPanel(
        UnrealContainerValue container,
        UnrealContainerValue root,
        IniSetting setting,
        Dictionary<string, List<string>> options,
        Action changed,
        int depth)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (container.Entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = L.Get("config.no_overrides_configured"),
                Opacity = 0.62,
                Margin = new Thickness(8),
            });
            return panel;
        }

        if (container.IsList)
        {
            int itemNumber = 0;
            foreach (UnrealConfigEntry entry in container.Entries)
            {
                itemNumber++;
                if (entry.Value is UnrealContainerValue item)
                {
                    panel.Children.Add(new Expander
                    {
                        Header = GetCompoundItemTitle(item, itemNumber),
                        IsExpanded = false,
                        Content = CreateContainerPanel(item, root, setting, options, changed, depth + 1),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    });
                }
                else if (entry.Value is UnrealScalarValue scalar)
                {
                    panel.Children.Add(CreateNestedScalarRow(
                        L.Format("config.item_n", itemNumber), null, scalar, root, setting, options, changed));
                }
            }
            return panel;
        }

        foreach (UnrealConfigEntry entry in container.Entries)
        {
            string label = LocalizeFieldName(entry.Name);
            if (entry.Value is UnrealContainerValue child)
            {
                string count = child.IsList ? $" ({child.Entries.Count})" : string.Empty;
                panel.Children.Add(new Expander
                {
                    Header = label + count,
                    IsExpanded = depth == 0 && child.Entries.Count <= 8,
                    Content = CreateContainerPanel(child, root, setting, options, changed, depth + 1),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                });
            }
            else if (entry.Value is UnrealScalarValue scalar)
            {
                panel.Children.Add(CreateNestedScalarRow(
                    label, entry.Name, scalar, root, setting, options, changed));
            }
        }
        return panel;
    }

    private static FrameworkElement CreateNestedScalarRow(
        string label,
        string? fieldName,
        UnrealScalarValue scalar,
        UnrealContainerValue root,
        IniSetting setting,
        Dictionary<string, List<string>> options,
        Action changed)
    {
        void Commit()
        {
            setting.SetValue(root.Serialize());
            changed();
        }

        FrameworkElement editor;
        if (bool.TryParse(scalar.Value, out bool boolean))
        {
            var toggle = new ToggleSwitch { IsOn = boolean, MinWidth = 100 };
            toggle.Toggled += (_, _) =>
            {
                scalar.Value = toggle.IsOn ? "True" : "False";
                Commit();
            };
            editor = toggle;
        }
        else if (fieldName is not null &&
                 options.TryGetValue(fieldName, out List<string>? fieldOptions) &&
                 fieldOptions.Count is > 1 and <= 60)
        {
            List<ConfigChoice> choices = fieldOptions
                .Append(scalar.Value)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => new ConfigChoice(LocalizeChoiceValue(value), value))
                .ToList();

            ConfigChoice? currentChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.Value, scalar.Value, StringComparison.OrdinalIgnoreCase));
            var combo = new ComboBox
            {
                ItemsSource = choices,
                DisplayMemberPath = nameof(ConfigChoice.Label),
                SelectedItem = currentChoice,
                IsEditable = true,
                MinWidth = 230,
            };
            if (currentChoice is null)
                combo.Text = scalar.Value;

            void Apply()
            {
                string next = combo.SelectedItem is ConfigChoice selected
                    ? selected.Value
                    : combo.Text?.Trim() ?? string.Empty;
                ConfigChoice? byLabel = choices.FirstOrDefault(choice =>
                    string.Equals(choice.Label, next, StringComparison.OrdinalIgnoreCase));
                if (byLabel is not null)
                    next = byLabel.Value;
                if (string.Equals(next, scalar.Value, StringComparison.OrdinalIgnoreCase))
                    return;
                scalar.Value = next;
                Commit();
            }
            combo.SelectionChanged += (_, _) => Apply();
            combo.LostFocus += (_, _) => Apply();
            editor = combo;
        }
        else
        {
            var text = new TextBox { Text = scalar.Value, MinWidth = 230, MaxWidth = 460 };
            text.TextChanged += (_, _) =>
            {
                scalar.Value = text.Text;
                Commit();
            };
            editor = text;
        }

        return CreateNestedRow(label, fieldName, editor);
    }

    private static FrameworkElement CreateNestedRow(string label, string? rawName, FrameworkElement editor)
    {
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(rawName))
            labels.Children.Add(new TextBlock { Text = rawName, FontSize = 11, Opacity = 0.52 });

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(labels);
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        editor.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(editor);

        return new Border
        {
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(6),
            Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
            Child = grid,
        };
    }

    private static Dictionary<string, List<string>> CollectScalarOptions(UnrealContainerValue root)
    {
        var values = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Visit(UnrealContainerValue container)
        {
            foreach (UnrealConfigEntry entry in container.Entries)
            {
                if (entry.Value is UnrealContainerValue child)
                    Visit(child);
                else if (entry.Name is not null && entry.Value is UnrealScalarValue scalar &&
                         scalar.Value.Length > 0)
                {
                    if (!values.TryGetValue(entry.Name, out HashSet<string>? choices))
                        values[entry.Name] = choices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    choices.Add(scalar.Value);
                }
            }
        }

        Visit(root);
        return values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string GetCompoundItemTitle(UnrealContainerValue item, int itemNumber)
    {
        foreach (string preferredName in new[] { "Action", "TagName", "Key", "BasePresetName" })
        {
            UnrealConfigEntry? entry = item.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (entry?.Value is UnrealScalarValue { Value.Length: > 0 } scalar)
                return preferredName == "Action"
                    ? L.Format("config.binding_suffix", Humanize(scalar.Value))
                    : scalar.Value;
        }
        return item.Entries.Count == 0
            ? L.Format("config.unassigned_item_n", itemNumber)
            : L.Format("config.item_n", itemNumber);
    }

    private static bool IsCompoundValue(string value)
        => value.StartsWith('(') && UnrealConfigValue.TryParse(value, out UnrealConfigValue? parsed) &&
           parsed is UnrealContainerValue;

    private static bool TryCreateChoiceEditor(IniSetting setting, Action changed, out ComboBox editor)
    {
        editor = null!;

        if (setting.Key.Contains("FullscreenMode", StringComparison.OrdinalIgnoreCase))
        {
            var choices = new[]
            {
                new ConfigChoice(L.Get("config.fullscreen"), "0"),
                new ConfigChoice(L.Get("config.borderless_fullscreen"), "1"),
                new ConfigChoice(L.Get("config.windowed"), "2"),
            };
            editor = CreateChoiceBox(setting, choices, changed);
            return true;
        }

        if (setting.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) &&
            setting.Key.EndsWith("Quality", StringComparison.OrdinalIgnoreCase) &&
            !setting.Key.Contains("Resolution", StringComparison.OrdinalIgnoreCase))
        {
            var choices = new[]
            {
                new ConfigChoice(L.Get("config.quality_low"), "0"),
                new ConfigChoice(L.Get("config.quality_medium"), "1"),
                new ConfigChoice(L.Get("config.quality_high"), "2"),
                new ConfigChoice(L.Get("config.quality_epic"), "3"),
                new ConfigChoice(L.Get("config.quality_cinematic"), "4"),
            };
            editor = CreateChoiceBox(setting, choices, changed);
            return true;
        }

        string[]? values = setting.Key switch
        {
            "QualityPreset" or "TextureQuality" or "GeometryQuality" or
            "ReflectionsQuality" or "GlobalIlluminationQuality" or "LightingQuality" or
            "EffectsQuality" or "AtmosphericsQuality" or "PostprocessingQuality"
                => ["Low", "Medium", "High", "Ultra", "Custom"],
            "VisualLanguage" or "AudioLanguage"
                => ["SystemDefault", "English", "TraditionalChinese", "SimplifiedChinese", "Japanese", "Korean"],
            "DynamicRange" => ["Standard", "Compressed", "Wide"],
            "GlobalTextSize" or "HudTextSize" or "SubtitleTextSize" or
            "SplitscreenHudTextSize" or "VoiceChatTextSize"
                => ["Default", "UseGlobal", "Minimum", "Maximum"],
            "DistanceUnits" => ["Meters", "Feet"],
            "Upscaler" => ["None", "DLSS", "FSR", "XeSS", "TSR"],
            "UpscalingQuality" => ["Low", "Medium", "High", "Ultra", "Custom"],
            "LowLatencyMode" => ["Default", "On", "Off"],
            "VoiceChatMode" => ["pushtotalk", "openmic", "None"],
            "ShowingHUDBanners" or "ShowingMenuToasts" => ["5s", "15s", "permanent", "Off"],
            "ShowingHUDObjectives" => ["permanent", "5s", "15s", "Off"],
            "ControllerThumbstickLayout" => ["Standard", "Southpaw", "Legacy", "LegacySouthpaw"],
            "ControllerWarthogDrivingMode" or "MouseKeyboardWarthogDrivingMode"
                => ["AimBased", "DriverBased"],
            "HUDAnchoring" => ["Center", "Left", "Right"],
            "ColorCorrectionFilter" => ["NormalVision", "Deuteranopia", "Protanopia", "Tritanopia"],
            "FireteamSettings" => ["FriendsCanJoin", "InviteOnly", "Open"],
            "SubtitleFontWeight" => ["Regular", "Bold"],
            "SubtitleTextCaps" => ["Speaker", "All", "None"],
            "SubtitleBackingColor" => ["Black", "None"],
            _ => null,
        };

        if (values is null)
            return false;

        IEnumerable<ConfigChoice> choicesWithCurrent = values
            .Append(setting.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => new ConfigChoice(LocalizeChoiceValue(value), value));
        editor = CreateChoiceBox(setting, choicesWithCurrent, changed);
        return true;
    }

    private static ComboBox CreateChoiceBox(
        IniSetting setting,
        IEnumerable<ConfigChoice> choices,
        Action changed)
    {
        List<ConfigChoice> choiceList = choices.ToList();
        var combo = new ComboBox
        {
            ItemsSource = choiceList,
            DisplayMemberPath = nameof(ConfigChoice.Label),
            SelectedItem = choiceList.FirstOrDefault(choice =>
                string.Equals(choice.Value, setting.Value, StringComparison.OrdinalIgnoreCase)),
            MinWidth = 220,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ConfigChoice selected)
                return;
            setting.SetValue(selected.Value);
            changed();
        };
        return combo;
    }

    private static FrameworkElement CreateVectorEditor(IniSetting setting, Action changed)
    {
        string[] parts = setting.Value.Split(',');
        var x = new NumberBox
        {
            Header = L.Get("config.axis_x"),
            Value = double.Parse(parts[0], CultureInfo.InvariantCulture),
            Width = 112,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var y = new NumberBox
        {
            Header = L.Get("config.axis_y"),
            Value = double.Parse(parts[1], CultureInfo.InvariantCulture),
            Width = 112,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        void Update()
        {
            if (double.IsNaN(x.Value) || double.IsNaN(y.Value))
                return;
            setting.SetValue(
                FormatNumberLike(x.Value, parts[0]) + "," +
                FormatNumberLike(y.Value, parts[1]));
            changed();
        }

        x.ValueChanged += (_, _) => Update();
        y.ValueChanged += (_, _) => Update();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { x, y } };
    }

    private static NumberBox CreateNumberEditor(IniSetting setting, Action changed)
    {
        string original = setting.Value;
        var number = new NumberBox
        {
            Value = double.Parse(original, CultureInfo.InvariantCulture),
            Width = 220,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        number.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(number.Value))
                return;
            setting.SetValue(FormatNumberLike(number.Value, original));
            changed();
        };
        return number;
    }

    private static string FormatNumberLike(double value, string original)
    {
        int decimalPlaces = original.Contains('.')
            ? original.Length - original.IndexOf('.') - 1
            : 0;
        return decimalPlaces == 0
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
    }

    private static string GetCategory(ConfigDocument document, IniSetting setting)
    {
        if (document.RelativePath.StartsWith("ImGui", StringComparison.OrdinalIgnoreCase))
            return L.Get("config.category_window_layout");

        if (!setting.Section.Equals("HaloUserSettings", StringComparison.OrdinalIgnoreCase))
            return LocalizeSectionName(setting.Section);

        string key = setting.Key;
        if (ContainsAny(key, "Subtitle")) return L.Get("config.category_subtitles");
        if (ContainsAny(key, "ScreenReader", "TextSize", "ColorCorrection", "Flashing", "ScreenShake", "MotionBlur", "Tutorial", "ObjectiveHint"))
            return L.Get("config.category_accessibility");
        if (ContainsAny(key, "HUD", "Crosshair", "MotionTracker", "DamageScreen", "DirectionalDamage", "NavigationPoint", "TeammateMarker", "Showing", "Backer"))
            return L.Get("config.category_hud");
        if (ContainsAny(key, "Modifier", "PlayerTraits", "FriendlyFire", "AllowInCoop"))
            return L.Get("config.category_gameplay_modifiers");
        if (ContainsAny(key, "Mouse", "KBM")) return L.Get("config.category_keyboard_mouse");
        if (ContainsAny(key, "Controller", "Gamepad")) return L.Get("config.category_controller");
        if (ContainsAny(key, "Volume", "Audio", "Voice", "TTS", "DynamicRange")) return L.Get("config.category_audio_voice");
        if (ContainsAny(key, "Quality", "Resolution", "Upscal", "Frame", "VSync", "HDR", "Monitor", "Brightness", "Contrast", "AsyncCompute", "Latency", "Chromatic", "FilmGrain"))
            return L.Get("config.category_video");
        if (ContainsAny(key, "FieldOfView", "Offset", "Gore", "HitMarker", "ItemHighlight"))
            return L.Get("config.category_gameplay_camera");
        return L.Get("config.category_other");
    }

    private static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsIdentifier(string key)
        => ContainsAny(key, "Token", "Version", "ClientID");

    private static string SettingDescription(IniSetting setting)
        => setting.Key switch
        {
            "PlayerTraits1" => L.Get("config.desc_player_traits_1"),
            "PlayerTraits2" => L.Get("config.desc_player_traits_2"),
            "PlayerTraits3" => L.Get("config.desc_player_traits_3"),
            "PlayerTraits4" => L.Get("config.desc_player_traits_4"),
            "CustomInputMappingGamepad" => L.Get("config.desc_custom_input_mapping_gamepad"),
            "CustomInputMappingKBM" => L.Get("config.desc_custom_input_mapping_kbm"),
            "ObjectCustomizationNames" => L.Get("config.desc_object_customization_names"),
            "Pos" => L.Get("config.desc_pos"),
            "Size" => L.Get("config.desc_size"),
            "Collapsed" => L.Get("config.desc_collapsed"),
            "FieldOfView" => L.Get("config.desc_field_of_view"),
            "FieldOfView3rdPerson" => L.Get("config.desc_field_of_view_3rd_person"),
            "ResolutionSizeX" => L.Get("config.desc_resolution_size_x"),
            "ResolutionSizeY" => L.Get("config.desc_resolution_size_y"),
            "FrameRateLimit" or "MaximumFrameRate" => L.Get("config.desc_frame_rate_limit"),
            "ResolutionScale" => L.Get("config.desc_resolution_scale"),
            _ => setting.Key,
        };

    private static string Humanize(string value)
    {
        if (value.StartsWith("PlayerTraits", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value["PlayerTraits".Length..], out int presetNumber))
            return L.Format("config.custom_modifier_preset_n", presetNumber);

        string text = value;
        if (text.StartsWith('b') && text.Length > 1 && char.IsUpper(text[1]))
            text = text[1..];
        text = text.Replace("sg.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ')
            .Replace('.', ' ');
        text = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");
        text = Regex.Replace(text, "([A-Za-z])([0-9])", "$1 $2");
        return text.Trim();
    }

    private void RefreshDocumentChrome(ConfigDocument? changedDocument = null)
    {
        IEnumerable<ConfigDocument> documents = changedDocument is null
            ? _documents
            : [changedDocument];
        foreach (ConfigDocument document in documents)
        {
            if (!_documentListItems.TryGetValue(document, out DocumentListItemState? state))
                continue;
            state.Title.Text = document.TabTitle;
            state.Title.FontWeight = document.IsDirty
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            state.DirtyDot.Visibility = document.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        }

        bool hasChanges = _documents.Any(document => document.IsDirty);
        SaveButton.IsEnabled = hasChanges;
        UnsavedBadge.Visibility = _selectedDocument?.IsDirty == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static readonly IReadOnlyList<TraitGroup> TraitGroups =
    [
        new("VitalityTraits", "config.trait_vitality",
        [
            new("DamageResistancePercentageSetting", "config.trait_damage_resistance",
                ["Percent10", "Percent50", "Percent90", "Percent100", "Percent110", "Percent150", "Percent200", "Percent300", "Invulnerable"]),
            new("ShieldRechargeRatePercentageSetting", "config.trait_shield_recharge_rate",
                ["Percent0", "Percent10", "Percent25", "Percent50", "Percent75", "Percent90", "Percent100", "Percent110", "Percent150", "Percent200"]),
        ]),
        new("WeaponTraits", "config.trait_weapons",
        [
            new("DamageModifierPercentageSetting", "config.trait_weapon_damage",
                ["Percent0", "Percent10", "Percent25", "Percent50", "Percent75", "Percent90", "Percent100", "Percent110", "Percent125", "Percent150", "Percent200", "Percent300", "Fatality"]),
            new("MeleeDamageModifierPercentageSetting", "config.trait_melee_damage",
                ["Percent0", "Percent10", "Percent25", "Percent50", "Percent75", "Percent90", "Percent100", "Percent110", "Percent125", "Percent150", "Percent200", "Percent300", "Fatality"]),
            new("InfiniteAmmoSetting", "config.trait_infinite_ammo", ["Off", "On", "BottomlessClip"]),
        ]),
        new("MovementTraits", "config.trait_movement",
        [
            new("SpeedSetting", "config.trait_movement_speed",
                ["Percent25", "Percent50", "Percent75", "Percent90", "Percent100", "Percent110", "Percent120", "Percent130", "Percent150", "Percent200", "Percent300"]),
            new("GravitySetting", "config.trait_gravity",
                ["Percent50", "Percent75", "Percent100", "Percent150", "Percent200"]),
        ]),
        new("AppearanceTraits", "config.trait_appearance",
        [
            new("ActiveCamoSetting", "config.trait_active_camo", ["Off", "Poor", "Good"]),
        ]),
    ];

    private sealed record ConfigChoice(string Label, string Value);
    private sealed record TraitGroup(string Name, string Label, IReadOnlyList<TraitField> Fields);
    private sealed record TraitField(string Name, string Label, IReadOnlyList<string> Values);

    private void RefreshBackups(string? selectedPath = null)
    {
        IReadOnlyList<ConfigBackup> backups = _store.GetBackups();
        BackupPicker.ItemsSource = backups;
        BackupPicker.SelectedItem = backups.FirstOrDefault(backup =>
            string.Equals(backup.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
            ?? backups.FirstOrDefault();
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_documents.Any(document => document.IsDirty))
        {
            _ = ConfirmReloadAsync();
            return;
        }

        ReloadDocuments();
        Report(L.Get("config.config_files_reloaded"), InfoBarSeverity.Success);
    }

    private async Task ConfirmReloadAsync()
    {
        if (!await ConfirmAsync(
                L.Get("config.discard_unsaved_changes_title"),
                L.Get("config.discard_unsaved_changes_message"),
                L.Get("config.discard_and_reload")))
            return;

        ReloadDocuments();
        Report(L.Get("config.unsaved_discarded_reloaded"), InfoBarSeverity.Success);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        BusyRing.IsActive = true;
        SaveButton.IsEnabled = false;
        try
        {
            List<ConfigDocument> changedDocuments =
                _documents.Where(document => document.IsDirty).ToList();
            int changedCount = changedDocuments.Count;
            bool changedHaloSettings = changedDocuments.Any(IsHaloGlobalSettings);
            ConfigBackup backup = await _store.SaveAsync(_documents);
            RefreshDocumentChrome();
            RefreshBackups(backup.Path);

            if (!changedHaloSettings)
            {
                Report(L.Format("config.saved_config_files", changedCount, backup.Path),
                    InfoBarSeverity.Success);
                return;
            }

            ScriptingBridgeStatus status = _scriptingBridge.GetStatus();
            if (!status.IsRuntimeReady || status.IsStale)
            {
                Report(
                    L.Format("config.saved_not_updated", changedCount, status.Summary, backup.Path),
                    InfoBarSeverity.Warning);
                return;
            }

            ScriptExecutionResult result;
            try
            {
                result = await _scriptingBridge.ExecuteAsync(
                    ScriptLanguage.Lua,
                    LiveHaloSettingsReloadScript);
            }
            catch (Exception ex)
            {
                Report(
                    L.Format("config.saved_live_reload_failed", ex.Message),
                    InfoBarSeverity.Warning);
                return;
            }
            if (result.Outcome == ScriptOutcome.Confirmed)
            {
                Report(
                    L.Format("config.saved_and_reloaded_halo", changedCount),
                    InfoBarSeverity.Success);
            }
            else
            {
                Report(
                    L.Format("config.saved_reload_not_confirmed", result.Message),
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Report(ex.Message, ex is InvalidOperationException ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }
        finally
        {
            BusyRing.IsActive = false;
            RefreshDocumentChrome();
        }
    }

    private static bool IsHaloGlobalSettings(ConfigDocument document)
        => string.Equals(
            Path.GetFileName(document.FullPath),
            "HaloGlobalGameUserSettings.ini",
            StringComparison.OrdinalIgnoreCase);

    private const string LiveHaloSettingsReloadScript =
        """
        local settings = FindFirstOf("HaloGlobalGameUserSettings")
            or FindFirstOf("HaloUserSettings")

        if not settings or not settings:IsValid() then
            local engine = FindFirstOf("Engine")
            if engine and engine:IsValid() then
                local ok, value = pcall(function()
                    return engine.GameUserSettings
                end)
                if ok then settings = value end
            end
        end

        if not settings or not settings:IsValid() then
            error("The active Halo game-user-settings object was not found.")
        end

        local reloaded = false
        local failures = {}

        local reload_ok, reload_error = pcall(function()
            settings:ReloadConfig()
        end)
        if reload_ok then
            reloaded = true
        else
            table.insert(failures, "ReloadConfig: " .. tostring(reload_error))
        end

        local load_ok, load_error = pcall(function()
            settings:LoadSettings(true)
        end)
        if load_ok then
            reloaded = true
        else
            table.insert(failures, "LoadSettings: " .. tostring(load_error))
        end

        if not reloaded then
            error("Could not reload settings from disk.\n" .. table.concat(failures, "\n"))
        end

        pcall(function()
            settings:ApplySettings(false)
        end)

        return settings:GetFullName()
        """;

    private void OnCreateBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigBackup backup = _store.CreateBackup();
            RefreshBackups(backup.Path);
            Report(L.Format("config.backed_up_config_files", backup.FileCount, backup.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (BackupPicker.SelectedItem is not ConfigBackup backup)
        {
            Report(L.Get("config.create_or_select_backup"), InfoBarSeverity.Warning);
            return;
        }

        if (!await ConfirmAsync(
                L.Get("config.restore_backup_title"),
                L.Format("config.restore_backup_message", backup.DisplayName),
                L.Get("config.restore")))
            return;

        try
        {
            int restored = _store.Restore(backup);
            ReloadDocuments();
            RefreshBackups(backup.Path);
            Report(L.Format("config.restored_config_files", restored),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.SavedRoot);
            await Launcher.LaunchFolderPathAsync(_store.SavedRoot);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnOpenBackupFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.BackupRoot);
            await Launcher.LaunchFolderPathAsync(_store.BackupRoot);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Report(string message, InfoBarSeverity severity)
    {
        PageStatus.Title = severity switch
        {
            InfoBarSeverity.Error => L.Get("common.something_went_wrong"),
            InfoBarSeverity.Warning => L.Get("common.careful"),
            InfoBarSeverity.Success => L.Get("common.done"),
            _ => L.Get("common.info"),
        };
        PageStatus.Message = message;
        PageStatus.Severity = severity;
        PageStatus.IsOpen = true;
    }

    private sealed record DocumentViewState(
        FrameworkElement Content,
        Dictionary<Expander, List<(FrameworkElement Row, string SearchText)>> SectionRows,
        int SettingCount);

    private sealed record DocumentListItemState(ListViewItem Item, TextBlock Title, Border DirtyDot);
}
