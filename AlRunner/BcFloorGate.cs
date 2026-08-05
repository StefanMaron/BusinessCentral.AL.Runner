namespace AlRunner;

/// <summary>
/// Honors the minimum BC version a suite declares in its app.json (`application` / `platform`).
///
/// Those fields are MINIMA in AL, not pins: `"application": "28.0.0.0"` means "needs BC 28.0 or
/// newer". Running such a suite against an older BC cannot work — the Microsoft symbols it asks
/// for do not exist at that version — and until now the runner ignored the declaration and let
/// it fail instead, in one of two opaque ways:
///
///   • the bundle-level dependency union inherits the suite's unmet Microsoft dependency and the
///     WHOLE bundle aborts before a single test runs (this is what made every BC 27.x matrix leg
///     red: one suite needing Microsoft/Application Test Library, an app that first ships in
///     BC 28.0, took all 24 sibling suites down with it), or
///   • the dependency quietly fails to resolve, the module is dropped from emit, and the
///     exclusion carries no AL diagnostic pointing at the cause.
///
/// Neither names the real reason, and both look like runner bugs rather than what they are: a
/// suite declaring, correctly, that it needs a newer BC than the one under test.
///
/// This is not the silent no-op that .claude/rules/loud-failures.md forbids. Nothing is faked and
/// no default is returned — the suite states its own floor, the runner honors that statement, and
/// every skip is reported on stdout with the suite name, the floor, and the running version.
/// The tradeoff worth knowing: this trusts a DECLARATION rather than probing what the artifact
/// actually contains, so a suite declaring a floor it does not really need loses coverage on
/// older BC instead of failing loudly. The printed lines are what keep that visible.
/// </summary>
public static class BcFloorGate
{
    // Both the bundle-level dependency union and the per-app grouping pass consult this gate,
    // and for a multi-suite bundle they see the same suite. Report each one once per process so
    // a skip reads as one fact rather than an echo.
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="appJsonPath"/> declares a minimum BC version above the one
    /// selected for this run. A suite with no declared floor never gates.
    /// </summary>
    public static bool DeclaresNewerBcThanRunning(string appJsonPath, out Version? floor)
    {
        floor = AlRunner.Infrastructure.InProcessAppPackager.ReadMinimumBcVersion(appJsonPath);
        return floor != null && floor > AlRunner.Infrastructure.BcArtifacts.SelectedVersion;
    }

    /// <summary>Print the skip once, naming the suite, its floor, and the running version.</summary>
    public static void ReportSkip(string appJsonPath, string suiteName, Version floor)
    {
        lock (Reported)
            if (!Reported.Add(appJsonPath)) return;

        Console.WriteLine(
            $"  [skip] {suiteName}: declares BC >= {floor}, running "
            + $"{AlRunner.Infrastructure.BcArtifacts.SelectedVersion} (app.json application/platform)");
    }

    /// <summary>Suite name from an app.json, falling back to its directory name.</summary>
    public static string SuiteNameOf(string appJsonPath)
        => AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(appJsonPath)?.Name
           ?? Path.GetFileName(Path.GetDirectoryName(appJsonPath)) ?? appJsonPath;

    /// <summary>Reset the report ledger — for tests that drive several runs in one process.</summary>
    public static void ResetForTests()
    {
        lock (Reported) Reported.Clear();
    }
}
