using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

internal sealed record GameBuildProfile(
    string Id,
    string Sha256,
    int PeTimestamp,
    int ImageSize,
    string? NativeLayout,
    long TagTablePointerOffset,
    long ArenaTableOffset,
    long StringIdStorageRva,
    long StringIdStorageUsedRva,
    long StringIdStringsRva,
    long StringIdCountRva,
    long StringIdBuiltinTableRva,
    IReadOnlyDictionary<LiveToolCapability, CapabilityValidationLevel> Capabilities);

internal static class GameBuildProfileCatalog
{
    private const string RelativeCatalogPath = "Assets/GameBuildProfiles.json";
    private static readonly ConcurrentDictionary<string, GameBuildProfile> ResolvedProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    public static GameBuildProfile Resolve(string modulePath)
    {
        string catalogPath = Path.Combine(
            AppContext.BaseDirectory,
            RelativeCatalogPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                L.Get("build_profile.catalog_missing"),
                catalogPath);
        }

        FileInfo module = new(modulePath);
        string cacheKey = $"{module.FullName}|{module.Length}|{module.LastWriteTimeUtc.Ticks}";
        if (ResolvedProfiles.TryGetValue(cacheKey, out GameBuildProfile? cached))
            return cached;

        string hash;
        int timestamp;
        int imageSize;
        using (FileStream stream = File.OpenRead(modulePath))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            timestamp = pe.PEHeaders.CoffHeader.TimeDateStamp;
            imageSize = pe.PEHeaders.PEHeader?.SizeOfImage
                ?? throw new InvalidDataException(
                    L.Get("build_profile.missing_pe_header"));
        }

        IReadOnlyList<GameBuildProfile> profiles = Load(catalogPath);
        foreach (GameBuildProfile profile in profiles)
        {
            if (profile.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) &&
                profile.PeTimestamp == timestamp &&
                profile.ImageSize == imageSize)
            {
                ResolvedProfiles[cacheKey] = profile;
                return profile;
            }
        }

        throw new NotSupportedException(
            L.Format(
                ClassifyUnsupportedBuildKey(profiles, timestamp),
                hash,
                $"0x{timestamp:X8}",
                $"0x{imageSize:X8}"));
    }

    private static string ClassifyUnsupportedBuildKey(
        IReadOnlyList<GameBuildProfile> profiles,
        int timestamp)
    {
        if (profiles.Count == 0)
            return "build_profile.unsupported_dll";

        int oldestSupported = profiles.Min(profile => profile.PeTimestamp);
        int newestSupported = profiles.Max(profile => profile.PeTimestamp);
        if (timestamp < oldestSupported)
            return "build_profile.unsupported_dll_outdated";
        if (timestamp > newestSupported)
            return "build_profile.unsupported_dll_newer";
        return "build_profile.unsupported_dll";
    }

    public static CapabilityValidationLevel GetCapability(
        GameBuildProfile profile,
        LiveToolCapability capability)
    {
        if (profile.Capabilities.TryGetValue(capability, out CapabilityValidationLevel level))
            return level;

        if (string.IsNullOrWhiteSpace(profile.NativeLayout))
            return CapabilityValidationLevel.Unsupported;

        string catalogPath = Path.Combine(
            AppContext.BaseDirectory,
            RelativeCatalogPath.Replace('/', Path.DirectorySeparatorChar));
        GameBuildProfile? layout = Load(catalogPath).FirstOrDefault(candidate =>
            candidate.Id.Equals(profile.NativeLayout, StringComparison.OrdinalIgnoreCase));
        return layout?.Capabilities.TryGetValue(capability, out level) == true
            ? level
            : CapabilityValidationLevel.Unsupported;
    }

    private static IReadOnlyList<GameBuildProfile> Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        var profiles = new List<GameBuildProfile>();
        foreach (JsonElement item in document.RootElement
                     .GetProperty("profiles")
                     .EnumerateArray())
        {
            JsonElement runtimeTags = item.GetProperty("runtimeTags");
            if (!item.TryGetProperty("researchAnchors", out JsonElement anchors))
            {
                throw new InvalidDataException(
                    $"Build profile '{item.GetProperty("id").GetString()}' is missing researchAnchors.");
            }

            var capabilities = new Dictionary<LiveToolCapability, CapabilityValidationLevel>();
            if (item.TryGetProperty("capabilities", out JsonElement capabilityElement))
            {
                foreach (JsonProperty capability in capabilityElement.EnumerateObject())
                {
                    if (!Enum.TryParse(capability.Name, ignoreCase: true,
                            out LiveToolCapability parsedCapability) ||
                        !Enum.TryParse(capability.Value.GetString(), ignoreCase: true,
                            out CapabilityValidationLevel parsedLevel))
                    {
                        throw new InvalidDataException(
                            $"Build profile '{item.GetProperty("id").GetString()}' has an invalid capability declaration.");
                    }
                    capabilities.Add(parsedCapability, parsedLevel);
                }
            }

            profiles.Add(new GameBuildProfile(
                item.GetProperty("id").GetString()
                    ?? throw new InvalidDataException("A build profile has no id."),
                item.GetProperty("sha256").GetString()
                    ?? throw new InvalidDataException("A build profile has no SHA-256."),
                checked((int)ParseHex(item.GetProperty("peTimestamp"))),
                checked((int)ParseHex(item.GetProperty("imageSize"))),
                item.TryGetProperty("nativeLayout", out JsonElement nativeLayout)
                    ? nativeLayout.GetString()
                    : null,
                checked((long)ParseHex(runtimeTags.GetProperty("tagTablePointer"))),
                checked((long)ParseHex(runtimeTags.GetProperty("arenaTable"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdStorage"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdStorageUsed"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdStrings"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdCount"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdBuiltinTable"))),
                capabilities));
        }
        return profiles;
    }

    private static ulong ParseHex(JsonElement value)
    {
        string text = value.GetString()
            ?? throw new InvalidDataException("A build-profile address is not a string.");
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(
                text.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong parsed))
        {
            throw new InvalidDataException(
                $"Invalid hexadecimal value '{text}' in the build-profile catalog.");
        }
        return parsed;
    }
}
