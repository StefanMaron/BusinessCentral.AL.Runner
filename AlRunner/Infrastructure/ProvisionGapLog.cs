// ProvisionGapLog — carries a provisioning-gap message from the dependency load that finds it
// up to the run summary, without making it any quieter on the way.
//
// WHY THIS EXISTS (issue #2587)
//   Two messages predict a run's failure exactly, and both were printed once, at the point of
//   discovery, and never mentioned again:
//
//     * DependencyResolver.UnservableDependencies — a dependency no loader tier can serve.
//       Printed in Program.cs right after resolution, where the bundle loop CAN see it.
//     * DependencyLoader's symbol-only platform-app note — a known Microsoft platform runtime
//       app found as a symbol-only package. Reported from inside the dependency load, several
//       layers below the bundle loop, so it has nothing to hand the message to. That is the one
//       this collector exists for.
//
//   Measured on npcore: four such blocks at about 20 seconds in, then 212 seconds of emit and
//   compile, then "The object with ID 0 does not have a member with that ID" — precisely what
//   those blocks predicted — with roughly 2,600 lines of log in between. A caller reading the
//   bottom of the run concludes their AL is broken. It is not; their package cache is
//   unprovisioned, and the runner said so, 2,600 lines earlier.
//
// A COLLECTOR, NOT A REPLACEMENT
//   Report still writes to stderr exactly as before (.claude/rules/loud-failures.md — nothing
//   here gets quieter) and only ALSO records. The summary is a second, findable statement of
//   the same thing, not a relocation of the first.
namespace AlRunner.Infrastructure;

internal static class ProvisionGapLog
{
    private static readonly object _lock = new();
    private static List<string> _gaps = new();

    /// <summary>
    /// Forget the previous bundle's gaps. Called once per bundle: a run walks bundles in
    /// sequence and a --watch session re-runs them forever, so without this the first bundle's
    /// missing package is attributed to every later bundle and every later cycle.
    /// </summary>
    internal static void Reset()
    {
        lock (_lock) _gaps = new List<string>();
    }

    /// <summary>Report one gap: loud on stderr (unchanged), and recorded for the summary.</summary>
    internal static void Report(string message)
    {
        Console.Error.WriteLine(message);
        lock (_lock) _gaps.Add(message);
    }

    /// <summary>
    /// What has been reported since the last <see cref="Reset"/>. A copy, so a caller that has
    /// already read it keeps what it read when the next bundle resets.
    /// </summary>
    internal static IReadOnlyList<string> Collected
    {
        get { lock (_lock) return _gaps.ToList(); }
    }
}
