// AffectedSelectionState — the per-bundle memory affected-test selection keeps between runs, and
// what one selecting run returns. One instance per `--server` process and one per `--watch
// --affected` process; both drive it through the same Program.RunTestsWithSelection
// (docs/server-mode.md#affectedonly-and-previously-failing-tests and the sections after it).
using AlRunner.Patches;

namespace AlRunner.Infrastructure;

internal sealed class AffectedSelectionState
{
    /// <param name="logTag">The bracketed tag its stderr lines carry: <c>server</c> or <c>watch</c>.</param>
    public AffectedSelectionState(string logTag) => LogTag = logTag;

    public string LogTag { get; }

    // #2441: per bundle, the previous run's per-test object coverage (object-key strings), the tests
    // that were unknown on that run, and the runtime environment key the coverage was recorded under.
    public Dictionary<string, Dictionary<string, HashSet<string>>> CoverageByBundle { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> UnknownTestsByBundle { get; } = new(StringComparer.Ordinal);
    // #4978: tests whose last recorded result was not a pass but whose coverage is stored; a subset
    // of the coverage keys, never of the unknown set.
    public Dictionary<string, HashSet<string>> FailingTestsByBundle { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EnvironmentKeyByBundle { get; } = new(StringComparer.Ordinal);
    // #5028: per bundle, the apps of the environment the coverage was recorded in, which a later
    // run in another environment diffs per object. Null when the records cannot vouch for one
    // environment: loaded from an older baseline, or carried through an approximate run.
    public Dictionary<string, EnvironmentSnapshot?> EnvironmentByBundle { get; } = new(StringComparer.Ordinal);
    // #4971: per bundle, every request module's change-model baseline generation at the moment its
    // coverage was recorded. Selection trusts changedObjects only when each module's baseline at the
    // start of the next run is that same generation.
    public Dictionary<string, Dictionary<string, long?>> BaselineGenerationsByBundle { get; } = new(StringComparer.Ordinal);
    // #4988: per bundle, the events each covered test raised, and the subscriber bindings and event
    // observability its tests ran with. Written and dropped together with the coverage.
    public Dictionary<string, Dictionary<string, HashSet<string>>> EventsByBundle { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<SubscriberBinding>> BindingsByBundle { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EventObservability> ObservabilityByBundle { get; } = new(StringComparer.Ordinal);
    // #4979: bundles whose state above was loaded from the persisted baseline and not re-recorded in
    // this process, with the module snapshots it was recorded on. Selection compares those with the
    // change model's baselines by file content in place of the generation check.
    public Dictionary<string, Dictionary<string, AffectedModuleSnapshot>> PersistedModulesByBundle { get; } = new(StringComparer.Ordinal);
}

/// <summary>What one run through Program.RunTestsWithSelection produced.</summary>
/// <param name="SelectedTestsByBundle">Per bundle path, the tests selection ran; null for a bundle
/// that was not narrowed (every discovered test was selected).</param>
internal sealed record AffectedRunOutcome(
    IReadOnlyList<ServerRunResult> Runs,
    IReadOnlyList<TestResult> AllTests,
    IReadOnlyList<CompilationErrorGroup> CompileErrors,
    int ExitCode,
    bool Cached,
    bool Cancelled,
    IReadOnlyList<AlCoverageTracker.AlStatementRecord>? StatementTable,
    IReadOnlyDictionary<string, List<AlCoverageTracker.AlStatementRecord>>? PerTestStatementTable,
    IReadOnlyList<SourceScanFailure>? ScanFailures,
    ServerSelection? Selection,
    IReadOnlyDictionary<string, HashSet<string>> DiscoveredTestsByBundle,
    IReadOnlyDictionary<string, HashSet<string>?> SelectedTestsByBundle);
