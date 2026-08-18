namespace HaloMeister.App.Models;

public sealed class RuntimeTagModDocument
{
    public const string CurrentFormat = "halo-meister.runtime-tag-mod";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = CurrentFormat;
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    /// <summary>
    /// Game-build profile id this mod was exported against. Missing or mismatched
    /// values mean the pack is expired after a Campaign Evolved update.
    /// </summary>
    public string? GameBuildId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<RuntimeTagModTag> Tags { get; set; } = [];
}

public sealed class RuntimeTagModTag
{
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public List<RuntimeTagModPatch> Patches { get; set; } = [];
}

public sealed class RuntimeTagModPatch
{
    public string Field { get; set; } = "";
    public string Type { get; set; } = "";
    public int Offset { get; set; }
    public int Size { get; set; }
    public List<RuntimeTagModBlockStep> Blocks { get; set; } = [];
    public string? Data { get; set; }
    public string? ReferenceGroup { get; set; }
    public string? ReferenceName { get; set; }
    public string? StringIdName { get; set; }
    public bool ClearReference { get; set; }
}

public sealed class RuntimeTagModBlockStep
{
    public int Offset { get; set; }
    public string Definition { get; set; } = "";
    public int Element { get; set; }
    public int ElementSize { get; set; }
}

public sealed record RuntimeTagModApplyResult(
    int TagCount,
    int PatchCount,
    IReadOnlyList<string> MissingTags);
