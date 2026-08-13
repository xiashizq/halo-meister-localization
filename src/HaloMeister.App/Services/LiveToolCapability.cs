namespace HaloMeister.App.Services;

/// <summary>
/// A separately releasable live-tools feature. A supported DLL fingerprint does
/// not imply every native operation has been validated for that build.
/// </summary>
public enum LiveToolCapability
{
    RuntimeTags,
    ObjectSpawn,
    WeaponLoad,
    ObjectAppearance,
    BipedPossession,
    AiPlacement,
    GameplayCheats,
    SoftCeilings,
    RuntimeBoundaries,
    PlayerTools,
    PlayerAllegiance,
    Machinima,
    SavedFilm,
}

/// <summary>
/// Evidence required before a capability is exposed to a user.
/// </summary>
public enum CapabilityValidationLevel
{
    Unsupported = 0,
    Cataloged = 1,
    StaticVerified = 2,
    Integrated = 3,
    LiveValidated = 4,
}

public static class LiveToolCapabilityCatalog
{
    public static LiveToolCapability? For(ScriptLanguage language)
        => language switch
        {
            ScriptLanguage.BlamSpawn or ScriptLanguage.BlamTagAssetLoad =>
                LiveToolCapability.ObjectSpawn,
            ScriptLanguage.BlamWeaponLoad or ScriptLanguage.BlamWeaponVariant =>
                LiveToolCapability.WeaponLoad,
            ScriptLanguage.BlamObjectVariant or ScriptLanguage.BlamObjectColors =>
                LiveToolCapability.ObjectAppearance,
            ScriptLanguage.BlamBipedSpawn or ScriptLanguage.BlamBipedVariantSpawn or
                ScriptLanguage.BlamBipedPossess or ScriptLanguage.BlamBumpPossessionOff =>
                LiveToolCapability.BipedPossession,
            ScriptLanguage.BlamAiSpawn or ScriptLanguage.BlamAiTeamSpawn =>
                LiveToolCapability.AiPlacement,
            ScriptLanguage.BlamCheatGlobalsRead or ScriptLanguage.BlamCheatGlobalWrite or
                ScriptLanguage.BlamSkullsRead or ScriptLanguage.BlamSkullWrite =>
                LiveToolCapability.GameplayCheats,
            ScriptLanguage.BlamSoftCeilingRead or ScriptLanguage.BlamSoftCeilingWrite =>
                LiveToolCapability.SoftCeilings,
            ScriptLanguage.BlamBoundariesRead or ScriptLanguage.BlamBoundariesDisable or
                ScriptLanguage.BlamBoundariesRestore =>
                LiveToolCapability.RuntimeBoundaries,
            ScriptLanguage.PlayerTeleport or ScriptLanguage.PlayerNoClip or
                ScriptLanguage.PlayerPosition or ScriptLanguage.PlayerInput or
                ScriptLanguage.PlayerUnitTagRead or
                ScriptLanguage.ObjectPosition or ScriptLanguage.ObjectTeleport =>
                LiveToolCapability.PlayerTools,
            ScriptLanguage.PlayerTeam or ScriptLanguage.ObjectTeam =>
                LiveToolCapability.PlayerAllegiance,
            ScriptLanguage.BlamMachinima or ScriptLanguage.MachinimaState or
                ScriptLanguage.MachinimaNodes or ScriptLanguage.MachinimaEnable or
                ScriptLanguage.MachinimaDisable or ScriptLanguage.MachinimaCameraTeleport =>
                LiveToolCapability.Machinima,
            _ => null,
        };
}
