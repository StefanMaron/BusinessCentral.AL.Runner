// TestArtifacts — RED placeholder reproducing the per-class gate as it exists today.
// Replaced by the real implementation in the GREEN step.

namespace AlRunner.Tests;

internal static class TestArtifacts
{
    internal static string? HomeDir() => Environment.GetEnvironmentVariable("HOME");

    internal static bool Present() => PresentIn(HomeDir());

    internal static bool PresentIn(string? home)
    {
        if (string.IsNullOrEmpty(home)) return false;
        return Directory.Exists(Path.Combine(home, ".bcartifacts.cache", "sandbox"));
    }

    internal static string MissingReason(string? home) => "BC artifacts not present";

    internal static void SkipIfMissing()
    {
        // The bug in one line: when the environment cannot support the test we return,
        // and xUnit records the caller as Passed.
    }

    internal static void SkipIfMissingIn(string? home) { }

    internal static void SkipIf(bool condition, string reason) { }
}
