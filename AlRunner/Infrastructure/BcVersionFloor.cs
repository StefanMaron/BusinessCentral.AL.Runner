namespace AlRunner.Infrastructure;

/// <summary>
/// The minimum BC version a run's projects declare in app.json (<c>application</c> /
/// <c>platform</c>), applied to BC version SELECTION (#4590): the default picks the newest
/// version at or above it and never one below; an explicit <c>--bc-version</c> below it runs,
/// with a warning. Per-suite skipping of a nested suite whose floor exceeds the running BC is
/// <see cref="AlRunner.BcFloorGate"/>'s job, not this one's.
/// </summary>
public static class BcVersionFloor
{
    /// <summary>
    /// True unless <paramref name="versionOrPrefix"/> is certainly below
    /// <paramref name="floor"/>. A prefix ("28.4", "28") is compared on the components it has,
    /// so "28.4" meets 28.4.0.0 — the prefix covers builds that do. Unparseable input meets it:
    /// this answers "is it known to be below", never "is it known to be above".
    /// </summary>
    public static bool Meets(string versionOrPrefix, Version floor)
    {
        var parts = versionOrPrefix.Trim().Split('.');
        var floorParts = new[] { floor.Major, floor.Minor, Math.Max(floor.Build, 0), Math.Max(floor.Revision, 0) };
        for (var i = 0; i < parts.Length && i < 4; i++)
        {
            if (!int.TryParse(parts[i], out var p)) return true;
            if (p != floorParts[i]) return p > floorParts[i];
        }
        return true;
    }

    /// <summary>The loud refusal when no version this install can run meets the floor.</summary>
    public static string DescribeUnmet(Version floor, string supported) =>
        $"BC version selection failed: the project's app.json declares a minimum of BC {floor} " +
        $"(application/platform), and this install ships no engine at or above it " +
        $"(supported BC versions: {supported}). Update al-runner to a release that supports BC " +
        $"{floor.Major}.{floor.Minor}, or pass --bc-version to run an older BC deliberately.";

    /// <summary>The warning for an explicit selection below the floor. It runs anyway: the user
    /// asked for it by name.</summary>
    public static string DescribeExplicitBelow(string requested, Version floor) =>
        $"[bc] warning: --bc-version {requested} is below the minimum BC {floor} this project's " +
        $"app.json declares (application/platform). Running it because it was requested explicitly; " +
        $"omit --bc-version to select a version that meets the minimum.";
}
