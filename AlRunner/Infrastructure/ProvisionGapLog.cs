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
// PRINTED ONCE, AT THE END (#4560)
//   Every gap reaches the "Action needed" block right before the Result line, once per app —
//   printing each at discovery too repeated it per dependency edge (17 blocks for 7 apps).
//   --verbose still prints it at discovery. Nothing is dropped: every bucket, including one
//   that failed to compile or execute, carries its gaps to that block, and an abort out of the
//   bundle loop prints them first (Reporter.PrintActionNeededOnAbort, #4636). A run that prints no
//   such block (--output-json, --server) keeps the discovery write: see DeferToActionNeeded.
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
        lock (_lock)
        {
            _gaps = new List<string>();
            _unservableApps = new List<(string App, Func<IEnumerable<int>> CodeunitIds)>();
            _codeunitOwner = null;
        }
    }

    /// <summary>How many lines of a gap the closing "Action needed" block prints (#4600).</summary>
    internal const int SummaryLineCount = 3;

    /// <summary>
    /// The Action needed entry for one gap: its first <see cref="SummaryLineCount"/> lines. So
    /// every gap message puts what a user acts on — the app and what is wrong, where, and the
    /// fix — in those lines, and its diagnosis after them (printed only where the gap is found).
    /// </summary>
    internal static IReadOnlyList<string> SummaryLines(string gap, out bool shortened)
    {
        var lines = gap.Replace("\r\n", "\n").Split('\n');
        shortened = lines.Length > SummaryLineCount;
        return lines.Take(SummaryLineCount).ToList();
    }

    private static List<(string App, Func<IEnumerable<int>> CodeunitIds)> _unservableApps = new();
    private static Dictionary<int, string>? _codeunitOwner;

    /// <summary>
    /// Record an app with no implementation (<c>Publisher/Name</c>) and how to list its codeunit
    /// ids, so a failing call into one of them can name the app (#4600). Read lazily: only a
    /// run that fails that way pays for the symbol read.
    /// </summary>
    internal static void RegisterUnservableApp(string app, Func<IEnumerable<int>> codeunitIds)
    {
        lock (_lock)
        {
            _unservableApps.Add((app, codeunitIds));
            _codeunitOwner = null;
        }
    }

    /// <summary>The codeunits of the package at <paramref name="appPath"/>, read from its symbols.</summary>
    internal static void RegisterUnservableApp(string app, string appPath) =>
        // A vanished or unreadable package throws here, lazily, and UnservableAppDeclaringCodeunit
        // says so on stderr and attributes nothing (the failure then prints its full remedy).
        RegisterUnservableApp(app, () => AlRunner.Patches.BcAppSymbolCache.Get(appPath).Objects
            .Where(o => string.Equals(o.Kind.Replace(" ", ""), "Codeunit", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Id));

    /// <summary>
    /// The registered app with no implementation that declares codeunit <paramref name="id"/>,
    /// or null. An app whose symbols cannot be read attributes nothing: the caller then prints
    /// the full message, which names the remedy without the app.
    /// </summary>
    internal static string? UnservableAppDeclaringCodeunit(int id)
    {
        lock (_lock)
        {
            if (_codeunitOwner == null)
            {
                _codeunitOwner = new Dictionary<int, string>();
                foreach (var (app, ids) in _unservableApps)
                {
                    IEnumerable<int> list;
                    try { list = ids().ToList(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[provision-gap] could not read the codeunits of {app}, so a failure "
                            + $"calling into it cannot name it: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }
                    foreach (var cu in list) _codeunitOwner.TryAdd(cu, app);
                }
            }
            return _codeunitOwner.TryGetValue(id, out var owner) ? owner : null;
        }
    }

    /// <summary>
    /// Set only by a run whose closing "Action needed" block will print the collected gaps
    /// (#4560). Left false — --output-json, --server — every gap is written to stderr at
    /// discovery, because nothing else would ever print it (loud-failures.md).
    /// </summary>
    internal static bool DeferToActionNeeded { get; set; }

    /// <summary>Whether a gap is written at the moment it is found.</summary>
    internal static bool WriteAtDiscovery => Log.Verbose || !DeferToActionNeeded;

    /// <summary>
    /// Report one gap: recorded for the run's closing "Action needed" block, which prints it
    /// once per app (#4560); written at discovery too when <see cref="WriteAtDiscovery"/>.
    /// </summary>
    internal static void Report(string message)
    {
        if (WriteAtDiscovery) Console.Error.WriteLine(message);
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
