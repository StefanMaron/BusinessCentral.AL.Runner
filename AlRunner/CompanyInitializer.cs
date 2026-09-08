// CompanyInitializer — make the runner's company look like a company that exists.
//
// WHY
//   Install triggers are not what creates a BC company's baseline rows. In real BC that is
//   company CREATION, which runs codeunit 2 "Company-Initialize" — and that is what inserts
//   the Company Information record, the Source Code Setup, the setup No. Series and so on.
//   The runner fired install triggers and nothing else, so those rows simply did not exist.
//
//   That went unnoticed for as long as a failed Record.Get silently succeeded: the caller got
//   a blank record and carried on. Once Get was made to raise (as AL requires when the return
//   value is not consumed), the missing rows surfaced — as a swallowed error five layers up,
//   reported as "report 1306 requires filter information".
//
//   Running BC's own codeunit 2 is the faithful answer rather than fabricating rows here: the
//   rows are then exactly the ones Base App creates. It runs once per bundle, immediately
//   before the install baseline is captured, so its rows are part of the committed baseline
//   every test is restored to.
//
// KNOWN INCOMPLETE — and deliberately not hidden
//   Codeunit 2 does not currently run to completion here: it reaches InitSourceCodeSetup and
//   the OnBeforeSourceCodeSetupInsert subscriber in Manufacturing codeunit 99000790 NREs.
//   Everything it inserted before that point IS committed, and that partial state is worth
//   +5 Pageworks tests over not running it at all (measured both ways). So this deliberately
//   keeps the partial result rather than rolling back — but it reports the failure loudly,
//   because a half-initialized company is a real limitation a developer needs to know about
//   when something downstream reads a setup table that codeunit 2 never got to.
//
//   #3538: the abort is also a RUN-LEVEL RESULT, not only a line on stderr. Real BC never
//   presents a half-initialized company, so a run carrying one is in a state no service tier
//   can produce, and every reporting surface says so — including the exit code. The decision,
//   the BC citation and the exit-code rule are in docs/partial-company-initialization.md.
using System.Reflection;

namespace AlRunner;

internal static class CompanyInitializer
{
    // Codeunit 2 "Company-Initialize" (Base App). Absent in bundles that do not carry Base
    // App, which is not an error: those have no company-setup concept to initialize.
    private const int CompanyInitializeCodeunitId = 2;
    private const string CompanyInitializeCodeunitName = "Company-Initialize";

    // Test-only seam. Whether codeunit 2 aborts is a property of a particular Base Application
    // on a particular BC build — #3054 measured it aborting on five of eight legs, and it
    // aborts on none of them today — so the reporting path below has no other deterministic
    // way to be driven. Same shape as AL_RUNNER_TEST_BARRIER_DIR: an env var no shipped code
    // path sets. Its value becomes the exception message, so a test can assert its own text
    // came through the formatting below rather than a fixed string.
    private const string InjectFailureEnvVar = "AL_RUNNER_TEST_FAIL_COMPANY_INIT";

    private static bool _ranForThisBundle;

    // Run-wide, NOT per app group: ResetForNewBundle runs once per app group and a bucket has
    // several, so a single overwritten field would lose group 1's abort the moment group 2
    // succeeded. Drained where Program.cs builds the bucket's BucketResult.
    private static readonly List<CompanyInitFailure> _failures = new();
    private static readonly object _failuresLock = new();

    internal static void ResetForNewBundle() => _ranForThisBundle = false;

    /// <summary>Everything this run has recorded so far, removed from the accumulator.</summary>
    internal static IReadOnlyList<CompanyInitFailure> DrainFailures()
    {
        lock (_failuresLock)
        {
            if (_failures.Count == 0) return Array.Empty<CompanyInitFailure>();
            var drained = _failures.ToArray();
            _failures.Clear();
            return drained;
        }
    }

    /// <summary>Discard what has been recorded. For tests that reuse one process.</summary>
    internal static void ResetFailuresForTests()
    {
        lock (_failuresLock) _failures.Clear();
        LastRecordedFailure = null;
    }

    /// <summary>
    /// The failure recorded for the app group that just ran, or null. The dependency-baseline
    /// cache stores this next to the snapshot it captured, so an app group reusing that
    /// snapshot re-reports the abort instead of inheriting a silent partial company.
    /// </summary>
    internal static CompanyInitFailure? LastRecordedFailure { get; private set; }

    /// <summary>
    /// Re-report a failure carried on a cached dependency baseline. The snapshot restored on a
    /// cache HIT IS the partial company, so the run is in exactly the state the original abort
    /// left behind and has to say so. The emit-exclusion path had the mirror-image bug — a warm
    /// run of a module missing objects came back byte-identical to a clean one (#3476).
    /// </summary>
    internal static void ReportCachedFailure(CompanyInitFailure failure) => Report(failure);

    /// <summary>
    /// Run BC's own company initialization once per bundle. Call after install triggers and
    /// before CaptureInstallBaseline so the rows it creates are part of the restored baseline.
    /// </summary>
    internal static void EnsureCompanyInitialized()
    {
        if (_ranForThisBundle) return;
        _ranForThisBundle = true;
        LastRecordedFailure = null;

        var injected = Environment.GetEnvironmentVariable(InjectFailureEnvVar);
        // The exists-check is gated by the seam deliberately: the seam stands in for "codeunit
        // 2 was found and threw", and whether Base App is loaded at all is upstream of
        // everything this path reports. Gating it the other way would force every test of the
        // reporting path to load the Base Application floor, which costs ~70s per runner
        // invocation and proves nothing further about the reporting
        // (.claude/rules/no-base-app-in-csharp-tests.md).
        if (string.IsNullOrEmpty(injected)
            && BcRuntime.FindCodeunitTypePublic(CompanyInitializeCodeunitId) == null)
            return; // no Base App in this bundle — nothing to initialize.

        try
        {
            if (!string.IsNullOrEmpty(injected))
                throw new InvalidOperationException(injected);
            BcRuntime.NavCodeunit_RunCodeunit(
                Microsoft.Dynamics.Nav.Types.DataError.ThrowError, CompanyInitializeCodeunitId, null);
            PerfTrace.Log("CompanyInitializer: ran codeunit 2 Company-Initialize");
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            var failure = new CompanyInitFailure(
                CompanyInitializeCodeunitId, CompanyInitializeCodeunitName,
                inner.GetType().Name, inner.Message);
            LastRecordedFailure = failure;
            Report(failure);
        }
    }

    private static void Report(CompanyInitFailure failure)
    {
        lock (_failuresLock) _failures.Add(failure);
        // #3068: tagged `[warn]`, NOT `[CompanyInitializer]`. Log.cs drops any line starting
        // with a `[Component]` tag at default verbosity, so for as long as this said
        // `[CompanyInitializer]` the comment below was false — the line only reached the
        // user under --verbose. Measured on BC 27.3: company initialisation aborted on every
        // run and the runner said nothing, so the reader saw only the downstream
        // "<Setup Table> does not exist" failures and a `[test-data]` hint pointing at the
        // wrong remedy. `[warn]` is exempt from that filter by design (Log.cs: "a severity
        // tag is never an internal diagnostic"), and the exemption list does not have to
        // grow a component at a time. Do not re-tag this with a component name.
        // Loud, never silent — see the KNOWN INCOMPLETE note above. Not fatal at this point:
        // the rows it did insert stay and the tests still run, but the run records the
        // condition and will not exit 0 (#3538).
        Console.Error.WriteLine(
            $"[warn] CompanyInitializer: codeunit {failure.CodeunitId} \"{failure.CodeunitName}\" "
            + $"did not complete: {failure.ExceptionType}: {failure.Message} — the company is "
            + "PARTIALLY initialized; setup tables it had not reached yet are missing, and AL "
            + "that reads them will fail.");
    }
}
