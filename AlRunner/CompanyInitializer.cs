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
using System.Linq;
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

    // Test-only seam, #3561, and the same justification as the one above: the drift half of
    // accept-partial-company-init fires when the named codeunit RAN TO COMPLETION, and the only
    // way to complete codeunit 2 for real is to load the Base Application floor — which costs
    // ~70s per invocation and is forbidden in C# test fixtures
    // (.claude/rules/no-base-app-in-csharp-tests.md). Set to 1, this stands in for "codeunit 2
    // was found and returned normally": the completion is recorded exactly where the real one
    // is, so the drift check under test is the real one. No shipped code path sets it.
    //
    // Note the DIRECTION, which is the opposite of its two siblings and the reason to be careful
    // with it: this one HIDES. It takes an early return that skips the real codeunit 2 call
    // entirely, so set outside a test it leaves the company uninitialized AND suppresses the
    // 0 → 2 escalation that would have said so. Gating is the family's — a bare env-var read,
    // exactly as AL_RUNNER_TEST_FAIL_COMPANY_INIT and AL_RUNNER_TEST_BARRIER_DIR are, with no
    // build guard anywhere in the family; setting it is as deliberate as passing --no-strict-exit.
    private const string InjectCompletedEnvVar = "AL_RUNNER_TEST_COMPANY_INIT_COMPLETED";

    private static bool _ranForThisBundle;

    // Run-wide, NOT per app group: ResetForNewBundle runs once per app group and a bucket has
    // several, so a single overwritten field would lose group 1's abort the moment group 2
    // succeeded. Drained where Program.cs builds the bucket's BucketResult.
    private static readonly List<CompanyInitFailure> _failures = new();
    private static readonly object _failuresLock = new();

    internal static void ResetForNewBundle() => _ranForThisBundle = false;

    // #3561: the initialization codeunits that ran to COMPLETION in this process, which is what
    // makes an accept-partial-company-init entry drift rather than merely unused. Never drained:
    // an entry is judged against the whole run, and under --server against the whole session,
    // where "this codeunit initialized cleanly" stays true for every later request.
    private static readonly HashSet<(int Id, string Name)> _completed = new();

    /// <summary>Initialization codeunits that ran to completion in this process (#3561).</summary>
    internal static IReadOnlyCollection<(int Id, string Name)> CompletedInitializations
    {
        get { lock (_failuresLock) return _completed.ToArray(); }
    }

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
        lock (_failuresLock) { _failures.Clear(); _completed.Clear(); }
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
        // #3561: stands in for a codeunit 2 that completed — see InjectCompletedEnvVar. Ignored
        // when an abort is injected too, because a run cannot both abort and complete and the
        // abort is the more specific instruction.
        if (string.IsNullOrEmpty(injected)
            && Environment.GetEnvironmentVariable(InjectCompletedEnvVar) == "1")
        {
            NoteCompleted();
            return;
        }
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
            NoteCompleted();
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

    private static void NoteCompleted()
    {
        lock (_failuresLock) _completed.Add((CompanyInitializeCodeunitId, CompanyInitializeCodeunitName));
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
