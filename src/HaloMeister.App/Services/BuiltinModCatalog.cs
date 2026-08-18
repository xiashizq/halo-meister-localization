namespace HaloMeister.App.Services;

/// <summary>
/// Shipping built-in overlay packages. Campaign extras and character AI are
/// independent IoStore triplets so they can be installed or removed separately.
/// </summary>
public static class BuiltinModCatalog
{
    public const string CampaignId = "campaign";
    public const string CharactersId = "characters";

    public static BuiltinModDefinition Campaign { get; } = new(
        Id: CampaignId,
        Stem: FullPalettesOverlayService.OverlayStem,
        ExpectedFingerprint: FullPalettesOverlayService.ExpectedBundledFingerprint,
        LegacyStems:
        [
            "HM_FullPalettes_P",
            "ZZ_HM_DemoSquads_P",
            "HM_DemoSquads_P",
        ],
        TitleKey: "builtin_mod.campaign.title",
        DescriptionKey: "builtin_mod.campaign.description",
        NoteKeys:
        [
            "builtin_mod.campaign.optional",
            "builtin_mod.campaign.required",
        ]);

    public static BuiltinModDefinition Characters { get; } = new(
        Id: CharactersId,
        Stem: FullPalettesOverlayService.CharacterOverlayStem,
        ExpectedFingerprint: FullPalettesOverlayService.ExpectedCharacterFingerprint,
        LegacyStems: [],
        TitleKey: "builtin_mod.characters.title",
        DescriptionKey: "builtin_mod.characters.description",
        NoteKeys: ["builtin_mod.characters.note"]);

    public static IReadOnlyList<BuiltinModDefinition> All { get; } =
        [Campaign, Characters];
}

public sealed record BuiltinModDefinition(
    string Id,
    string Stem,
    string ExpectedFingerprint,
    IReadOnlyList<string> LegacyStems,
    string TitleKey,
    string DescriptionKey,
    IReadOnlyList<string> NoteKeys);
