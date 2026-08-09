namespace HaloMeister.App;

internal static class BuildPolicy
{
#if RETAIL
    public static bool IsRetail { get; } = true;
#else
    public static bool IsRetail { get; } = false;
#endif

    // Model / cosmetic variant lists stay fully available in all builds.
    public static bool EnforceCustomizationOwnership => false;
}
