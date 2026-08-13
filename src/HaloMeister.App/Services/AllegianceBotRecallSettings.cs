using System.Globalization;
using System.Text.Json;

namespace HaloMeister.App.Services;

/// <summary>
/// Persistable BOT recall preferences for Allegiance Demo.
/// Recall is manual only (button / hotkey). HotkeyVirtualKey is a
/// Win32/VirtualKey code (default V = 0x56).
/// </summary>
public sealed class AllegianceBotRecallSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaloMeister",
        "AllegianceDemo",
        "bot-recall-settings.json");

    /// <summary>Virtual-key code for V.</summary>
    public const int DefaultHotkeyVirtualKey = 0x56;

    public bool IncludeHostiles { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public int HotkeyVirtualKey { get; set; } = DefaultHotkeyVirtualKey;

    public static AllegianceBotRecallSettings Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new AllegianceBotRecallSettings();
            AllegianceBotRecallSettings? loaded =
                JsonSerializer.Deserialize<AllegianceBotRecallSettings>(
                    File.ReadAllText(StorePath),
                    JsonOptions);
            return Clamp(loaded ?? new AllegianceBotRecallSettings());
        }
        catch
        {
            return new AllegianceBotRecallSettings();
        }
    }

    public void Save()
    {
        try
        {
            AllegianceBotRecallSettings clamped = Clamp(this);
            IncludeHostiles = clamped.IncludeHostiles;
            HotkeyEnabled = clamped.HotkeyEnabled;
            HotkeyVirtualKey = clamped.HotkeyVirtualKey;
            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                StorePath,
                JsonSerializer.Serialize(clamped, JsonOptions));
        }
        catch
        {
            // Preference persistence must never break the page.
        }
    }

    public static AllegianceBotRecallSettings Clamp(AllegianceBotRecallSettings settings)
    {
        int key = settings.HotkeyVirtualKey;
        if (key is < 0x08 or > 0xFE)
            key = DefaultHotkeyVirtualKey;
        return new AllegianceBotRecallSettings
        {
            IncludeHostiles = settings.IncludeHostiles,
            HotkeyEnabled = settings.HotkeyEnabled,
            HotkeyVirtualKey = key,
        };
    }

    public string HotkeyLabel => FormatHotkey(HotkeyVirtualKey);

    public static string FormatHotkey(int virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x7B)
            return "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture);
        return virtualKey switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture),
        };
    }
}
