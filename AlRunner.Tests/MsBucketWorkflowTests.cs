// Issue #2724: .github/workflows/ms-bucket.yml runs one Microsoft BaseApp test bucket with
// --test-data as a MANUAL verification net. A YAML workflow cannot be unit-tested the way
// the C# behind it can, so this file pins the properties that are load-bearing and cheap
// to lose in an edit:
//
//   1. It is manual-dispatch ONLY. A push/pull_request/schedule trigger would put a
//      multi-hour, 9,500-test job on every PR — the issue rules that out explicitly.
//   2. Its BC provisioning is the SAME definition bc-tests.yml uses — a composite action —
//      not a hand-maintained copy. bc-tests.yml's own header records a copy of those steps
//      drifting four times and failing two releases (#1976); ReleaseTestParityTests guards
//      the matrix callers, this guards the provisioning action's two consumers.
//   3. The configuration values recorded from working local runs are present verbatim.
//      Wrong values do not fail the run — they make its number meaningless (the company
//      name with a trailing underscore, both package caches, a raised emit timeout, a
//      private --cache).
//
// Split like ReleaseTestParityTests: WorkflowTriggers.TriggersOf is a pure function proven
// on constructed text; the rest wires it (and the marker checks) to the real files on disk.
using System.Globalization;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

internal static class WorkflowTriggers
{
    /// <summary>
    /// The event names declared under a workflow's top-level <c>on:</c> key, in order. Only
    /// the two-space-indented keys directly under <c>on:</c> count — <c>branches:</c> and
    /// <c>inputs:</c> sit deeper. Empty when there is no <c>on:</c> block.
    /// </summary>
    internal static IReadOnlyList<string> TriggersOf(string workflowText)
    {
        var lines = workflowText.Replace("\r\n", "\n").Split('\n');
        var triggers = new List<string>();
        var inOn = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#') || line.Length == 0) continue;
            if (!line.StartsWith(' '))
            {
                inOn = line.StartsWith("on:", StringComparison.Ordinal);
                continue;
            }
            if (!inOn) continue;
            if (line.StartsWith("  ") && !line.StartsWith("   "))
            {
                var key = line.Trim();
                var colon = key.IndexOf(':');
                if (colon > 0) triggers.Add(key[..colon]);
            }
        }
        return triggers;
    }
}

internal static class WorkflowInputDefaults
{
    /// <summary>
    /// Every <c>default:</c> declared for an input of the given name, one entry per
    /// declaration — a workflow that offers both <c>workflow_call</c> and
    /// <c>workflow_dispatch</c> declares its inputs twice, and an input added to only one of
    /// them resolves to the empty string on the other trigger. Empty when the input is absent,
    /// so callers must assert a COUNT before comparing values.
    /// </summary>
    internal static IReadOnlyList<string> Of(string workflowText, string inputName)
    {
        var lines = workflowText.Replace("\r\n", "\n").Split('\n');
        var found = new List<string>();
        var keyIndent = -1;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;
            var indent = line.Length - trimmed.Length;

            if (keyIndent < 0)
            {
                if (trimmed == inputName + ":") keyIndent = indent;
                continue;
            }
            if (indent <= keyIndent) { keyIndent = trimmed == inputName + ":" ? indent : -1; continue; }
            if (trimmed.StartsWith("default:", StringComparison.Ordinal))
                found.Add(trimmed["default:".Length..].Trim());
        }
        return found;
    }
}

internal static class BashArrayLiteral
{
    /// <summary>
    /// The inside of a bash array literal <c>name=( … )</c>, matched by counting parentheses
    /// rather than by looking for a terminator string. An assertion about what the runner is
    /// invoked WITH has to be scoped to the array it is invoked with — a flag mentioned in a
    /// comment, an <c>echo</c>, or a sibling <c>args+=(</c> guarded by an <c>if</c> is not the
    /// same claim. Empty when the array is absent.
    /// </summary>
    internal static string Of(string script, string name)
    {
        var open = script.IndexOf(name + "=(", StringComparison.Ordinal);
        if (open < 0) return string.Empty;
        var i = open + name.Length + 1;      // at the '('
        var depth = 0;
        for (var j = i; j < script.Length; j++)
        {
            if (script[j] == '(') depth++;
            else if (script[j] == ')' && --depth == 0) return script[(i + 1)..j];
        }
        return string.Empty;                 // unbalanced — the caller's assertion says so
    }
}

public sealed class MsBucketWorkflowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string GithubDir = Path.Combine(RepoRoot, ".github");

    private const string Workflow = "ms-bucket.yml";
    private const string Nightly = "ms-bucket-nightly.yml";
    private const string SharedMatrix = "bc-tests.yml";
    private const string ProvisionAction = "actions/provision-bc/action.yml";
    private const string ProvisionMarker = "uses: ./.github/actions/provision-bc";

    /// <summary>
    /// The per-test watchdog these workflows run with, in seconds (#3431). A constant, so
    /// changing the number is a deliberate edit here with a reason, rather than a value that
    /// drifted. 300 covers the hosted runner's slowdown and still catches the unbounded-loop
    /// class the watchdog exists for: #3374's MaxIteration loops finished at no timeout at all.
    /// </summary>
    private const int WorkflowTestTimeoutSeconds = 300;

    private static string Read(string relative)
    {
        var path = Path.Combine(GithubDir, relative);
        Assert.True(File.Exists(path), $"expected {relative} at {path}");
        return File.ReadAllText(path);
    }

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    // ---- the pure function, proven on constructed text ------------------------------

    [Fact]
    public void TriggersOf_ListsEveryTopLevelEventAndNothingDeeper()
    {
        const string wf = """
            name: X
            on:
              push:
                branches: [main]
              pull_request:
                branches: [main]
              workflow_dispatch:
                inputs:
                  bucket:
                    default: Tests-ERM
            jobs:
              run:
                runs-on: ubuntu-latest
            """;

        Assert.Equal(new[] { "push", "pull_request", "workflow_dispatch" }, WorkflowTriggers.TriggersOf(wf));
    }

    [Fact]
    public void TriggersOf_IsEmptyWithoutAnOnBlock_AndIgnoresComments()
    {
        Assert.Empty(WorkflowTriggers.TriggersOf("name: X\njobs:\n  run:\n    runs-on: ubuntu-latest\n"));
        Assert.Equal(new[] { "workflow_dispatch" },
            WorkflowTriggers.TriggersOf("on:\n  # push: would be wrong here\n  workflow_dispatch:\n"));
    }

    [Fact]
    public void InputDefaults_FindsEveryDeclarationOfOneInput_AndNothingElses()
    {
        const string wf = """
            on:
              workflow_call:
                inputs:
                  test-timeout:
                    type: number
                    default: 300
                  test-data:
                    type: boolean
                    default: true
              workflow_dispatch:
                inputs:
                  test-timeout:
                    description: >-
                      Two declarations of one input is normal, and they can disagree.
                    type: number
                    default: 120
            """;

        Assert.Equal(new[] { "300", "120" }, WorkflowInputDefaults.Of(wf, "test-timeout"));
        Assert.Equal(new[] { "true" }, WorkflowInputDefaults.Of(wf, "test-data"));
        Assert.Empty(WorkflowInputDefaults.Of(wf, "bc-version"));
    }

    [Fact]
    public void BashArrayLiteral_ReadsOnlyTheArraysOwnContents()
    {
        const string script = """
            echo "--test-timeout is not set here"
            args=( "$BUNDLE"
                   --cache "$C"
                   --test-timeout "$T" )
            if [ "$X" = "true" ]; then
              args+=( --test-data-company "CRONUS International Ltd_" )
            fi
            """;

        var array = BashArrayLiteral.Of(script, "args");
        Assert.Contains("--test-timeout \"$T\"", array, StringComparison.Ordinal);
        // The echo above it and the conditional append below it are NOT the array.
        Assert.DoesNotContain("echo", array, StringComparison.Ordinal);
        Assert.DoesNotContain("--test-data-company", array, StringComparison.Ordinal);
        Assert.Equal(string.Empty, BashArrayLiteral.Of(script, "nosuch"));
    }

    // ---- wired to the real files ------------------------------------------------------

    /// <summary>
    /// The bucket workflow never runs itself on a code change. `workflow_call` was added so the
    /// nightly can REUSE these steps instead of re-spelling them, and it is safe here for the
    /// same reason `workflow_dispatch` is: neither fires on a push or a pull request, so a
    /// multi-hour 9,500-test job still cannot land on a PR. The forbidden set is asserted by
    /// name rather than by pinning an exact allowed list, so adding another deliberate manual
    /// trigger does not fail this, while push/pull_request/schedule always do.
    ///
    /// `schedule` is forbidden HERE specifically: a scheduled run of this file would arrive
    /// with no inputs, so `bucket` would be empty. The schedule belongs to the nightly, which
    /// supplies one.
    /// </summary>
    [Fact]
    public void MsBucketWorkflow_NeverRunsItselfOnACodeChange()
    {
        var triggers = WorkflowTriggers.TriggersOf(Read(Path.Combine("workflows", Workflow)));

        Assert.Contains("workflow_dispatch", triggers);
        Assert.Contains("workflow_call", triggers);
        foreach (var forbidden in new[] { "push", "pull_request", "pull_request_target", "schedule" })
            Assert.DoesNotContain(forbidden, triggers);
    }

    /// <summary>
    /// The nightly is a SCHEDULE ON TOP of the bucket workflow, not a second copy of it. It must
    /// call the shared file — a hand-copied run step is how bc-tests.yml's provisioning drifted
    /// four times and failed two releases (#1976) — and it must not gate anything, which means
    /// no push/pull_request trigger.
    /// </summary>
    [Fact]
    public void NightlyWorkflow_IsScheduledOnly_AndReusesTheBucketWorkflow()
    {
        var text = Read(Path.Combine("workflows", Nightly));
        var triggers = WorkflowTriggers.TriggersOf(text);
        var code = CodeOnly(text);

        Assert.Contains("schedule", triggers);
        Assert.Contains("workflow_dispatch", triggers);
        foreach (var forbidden in new[] { "push", "pull_request", "pull_request_target" })
            Assert.DoesNotContain(forbidden, triggers);

        // Reuse, not re-spell: the mechanics come from the shared workflow.
        Assert.Contains("uses: ./.github/workflows/ms-bucket.yml", code, StringComparison.Ordinal);
        // And therefore NOT a second copy of the configuration the shared file owns.
        Assert.DoesNotContain("--package-cache", code, StringComparison.Ordinal);
        Assert.DoesNotContain("READER_TAG", code, StringComparison.Ordinal);
        Assert.DoesNotContain("al-runner", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The nightly must run on the NEWEST BC version, which ms-bucket.yml resolves when
    /// `bc-version` is absent. Stefan asked for this explicitly and it is the whole point: a
    /// nightly pinned to an older BC to make it green would measure a version nobody ships.
    /// It may well be red — it runs Microsoft's own tests, which the runner does not pass yet —
    /// and the summary script's annotations are what say which kind of red it is.
    /// </summary>
    [Fact]
    public void NightlyWorkflow_DoesNotPinABcVersion_SoItTracksTheNewest()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Nightly)));

        Assert.DoesNotContain("bc-version:", code, StringComparison.Ordinal);
        // --test-data is mandatory for a number that means anything (running-ms-test-buckets).
        Assert.Contains("test-data: true", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The nightly's header is 90 lines of rationale and NOTHING checks it, because every other
    /// assertion here runs through <see cref="CodeOnly"/>, which strips comments. That is not a
    /// theoretical gap: the same blind spot let "BusinessCentral.BakReader" survive in
    /// ms-bucket-summary.py's blocker detail after #2863 renamed the repository everywhere a
    /// test could see, and it left this header asserting #2780 as pending work after #2780 was
    /// closed as completed and READER_TAG moved to reader v0.1.2.
    ///
    /// This reads the file WITH its comments and pins the one property that goes stale on its
    /// own: the header must not present the closed blocker as outstanding. A workflow header
    /// telling the next reader to wait for something that already shipped is worse than no
    /// header — it sends them to look for work that does not exist.
    ///
    /// Deliberately phrase-based rather than "must not mention #2780": naming the issue to say
    /// it is CLOSED is correct and useful, and a test that forbade the number outright would
    /// push the next author into deleting the history instead of updating it.
    /// </summary>
    [Fact]
    public void NightlyHeader_DoesNotPresentTheClosedReaderBlockerAsPendingWork()
    {
        var header = Read(Path.Combine("workflows", Nightly));

        // Each of these asserted a fact that stopped being true when #2863 merged the reader
        // bump: the reader CAN open a 28.2+ backup now, so there is nothing left to "land".
        foreach (var stale in new[]
                 {
                     "has never been able to open a 28.2+ W1 backup",
                     "the day #2780 lands",
                     "once #2780 lands",
                     "until #2780 lands",
                 })
        {
            Assert.False(header.Contains(stale, StringComparison.OrdinalIgnoreCase),
                $"ms-bucket-nightly.yml still says \"{stale}\". #2780 is closed as completed and "
                + "READER_TAG pins reader v0.1.2, which reads BC 28.2, 28.3 and 28.4 — so this "
                + "presents finished work as pending. Update the header rather than the test.");
        }

        // The positive half, so the test cannot be satisfied by deleting the section wholesale:
        // the header must still explain how a red run says which kind of red it is, because that
        // is the property that makes a scheduled job people actually read.
        Assert.Contains("Known blocker", header, StringComparison.Ordinal);
        Assert.Contains("does NOT recognise", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// A knowingly-red nightly that says nothing trains everyone to ignore it. The summary
    /// script must recognise the reader refusal (#2780) by its signature and name the issue, and
    /// must emit a workflow annotation so the reason reaches the run list and the
    /// scheduled-failure notification rather than only the log.
    /// </summary>
    [Fact]
    public void SummaryScript_NamesTheKnownBlocker_AndAnnotatesAnyRunThatProducedNoNumber()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "ms-bucket-summary.py"));

        Assert.Contains("neither mapped by the derived extent list nor padding filler", script, StringComparison.Ordinal);
        Assert.Contains("#2780", script, StringComparison.Ordinal);
        Assert.Contains("::error title=Known blocker", script, StringComparison.Ordinal);
        // The other half: a failure that is NOT the known blocker must say so, or an unexplained
        // red would read like the expected one.
        Assert.Contains("::error title=No measurement", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsBucketWorkflow_CarriesTheRecordedConfigurationVerbatim()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Workflow)));

        // The SQL-form company name, trailing underscore — the run fails loudly with a period.
        Assert.Contains("CRONUS International Ltd_", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CRONUS International Ltd.", code, StringComparison.Ordinal);
        // Both package caches, repeatable flag.
        Assert.Contains("--package-cache \"$HOME/.al-runner/platform-apps\"", code, StringComparison.Ordinal);
        Assert.Contains("--package-cache \"$HOME/.al-runner/test-apps\"", code, StringComparison.Ordinal);
        // The 120 s default emit timeout is far too short for a 9,500-test bucket.
        Assert.Contains("AL_RUNNER_EMIT_TIMEOUT_SEC", code, StringComparison.Ordinal);
        // A private cache root, not the shared default.
        Assert.Contains("--cache \"", code, StringComparison.Ordinal);
        // The pieces the issue lists as missing: bucket sources, the backup, the reader.
        Assert.Contains("test-sources", code, StringComparison.Ordinal);
        Assert.Contains("--test-data=", code, StringComparison.Ordinal);
        Assert.Contains("--test-data-company", code, StringComparison.Ordinal);
        // StefanMaron/BusinessCentral.DbReader. This assertion previously named
        // BusinessCentral.BakReader, which is the repository's OLD name and resolves only
        // through GitHub's rename redirect -- so the test was pinning a name that is not the
        // repository's, and it went red the moment the workflow was corrected. A redirect is
        // not a contract: it lasts at GitHub's discretion and ends if anyone claims the freed
        // old name. The DoesNotContain arm is the point of the pair -- without it, a revert to
        // the redirect name passes again in silence.
        Assert.Contains("StefanMaron/BusinessCentral.DbReader", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BusinessCentral.BakReader", code, StringComparison.Ordinal);
        Assert.Contains(".cache/al-runner/bcbak/bcbak", code, StringComparison.Ordinal);
        // The deliverable: a job summary plus the JUnit for clustering.
        Assert.Contains("GITHUB_STEP_SUMMARY", code, StringComparison.Ordinal);
        Assert.Contains("--output-junit", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// #2779: the backup reader is PINNED to a tag, not resolved to whatever is newest at run
    /// time. `gh release view … --jq .tagName` made this workflow's result depend on when a
    /// different repository last published — the measurement could change with no change here,
    /// and the reader's identity keys the install-baseline cache, so a reader upgrade changes
    /// decoded values. Both halves are asserted: the pin is present, and the run-time
    /// resolution is gone.
    /// </summary>
    [Fact]
    public void MsBucketWorkflow_PinsTheBackupReaderRelease_RatherThanResolvingLatest()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Workflow)));

        Assert.Contains("READER_TAG: v", code, StringComparison.Ordinal);
        Assert.Contains("tag=\"$READER_TAG\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release view", code, StringComparison.Ordinal);
        // The download still uses the tag, so pinning cannot silently stop pinning.
        Assert.Contains("gh release download \"$tag\"", code, StringComparison.Ordinal);
    }

    // ---- the per-test watchdog (#3431) -------------------------------------------------

    /// <summary>
    /// #3431: the workflow set no timeout at all, so the runner's 60 s wall-clock default
    /// applied on a hosted runner and aborted tests that finish comfortably inside it locally.
    ///
    /// Three links, asserted separately, because checking any one of them alone passes on the
    /// defect this test exists to catch:
    ///
    ///   1. the INPUT reaches a shell variable. Bound to a literal instead, the input would be
    ///      decorative and a caller's override would go nowhere.
    ///   2. that variable is INSIDE the runner's argument array — not in a comment, an
    ///      <c>echo</c>, or the conditional <c>args+=( … )</c> that a false <c>test-data</c>
    ///      skips. This is the link that fails when the flag is dropped while the input stays,
    ///      which is precisely the "a check that cannot fail" shape: an input that exists and
    ///      reaches nothing.
    ///   3. that array is what the runner is invoked with.
    ///
    /// The shell variable is deliberately NOT called <c>AL_RUNNER_TEST_TIMEOUT_SEC</c>, which
    /// the runner reads on its own: under that name link 2 could be deleted and the timeout
    /// would still apply, so nothing here could tell the difference. That the flag itself
    /// works is <c>TestTimeoutFlagTests</c>'s claim, proven against the runner; this file's
    /// claim is only that the workflow supplies it.
    /// </summary>
    [Fact]
    public void MsBucketWorkflow_PutsThePerTestTimeoutOnTheRunnersArgumentArray()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Workflow)));
        var runStep = WorkflowStep.BodyOf(code, "Run the bucket(s)");

        Assert.Contains("TEST_TIMEOUT_SEC: ${{ inputs.test-timeout }}", runStep, StringComparison.Ordinal);

        var args = BashArrayLiteral.Of(runStep, "args");
        Assert.False(string.IsNullOrWhiteSpace(args),
            "the runner's `args=( … )` array is gone from the \"Run the bucket(s)\" step");
        Assert.Contains("--test-timeout \"$TEST_TIMEOUT_SEC\"", args, StringComparison.Ordinal);

        Assert.Contains("/al-runner\" \"${args[@]}\"", runStep, StringComparison.Ordinal);

        // The guard fails the job once, up front. The runner rejects a bad value itself, but
        // only after provisioning and each bucket's setup have been paid, once per
        // invocation. Both halves: the guard is there, and it runs BEFORE the bucket loop.
        var guard = runStep.IndexOf("::error::test-timeout must be a positive whole number",
            StringComparison.Ordinal);
        Assert.True(guard >= 0, "the empty/non-numeric test-timeout guard is gone");
        Assert.True(guard < runStep.IndexOf("for bucket in \"${BUCKET_LIST[@]}\"", StringComparison.Ordinal),
            "the test-timeout guard must run before the bucket loop, not inside it");
    }

    /// <summary>
    /// The default has to be declared on BOTH trigger blocks and has to be the same number.
    /// On <c>workflow_call</c> only, a manual dispatch resolves <c>inputs.test-timeout</c> to
    /// the empty string and the guard above fails the job; on <c>workflow_dispatch</c> only,
    /// ms-surface.yml and the nightly get nothing. Either way the run is wrong, and a PR check
    /// is a far cheaper place to find that than a three-hour dispatch.
    ///
    /// It is compared against the runner's OWN default rather than a second copy of 60, so the
    /// relationship stays true if that default ever moves.
    /// </summary>
    [Fact]
    public void MsBucketWorkflow_DefaultsThePerTestTimeoutAboveTheRunnersOwn()
    {
        var defaults = WorkflowInputDefaults.Of(Read(Path.Combine("workflows", Workflow)), "test-timeout");

        Assert.Equal(2, defaults.Count);
        Assert.Single(defaults.Distinct(StringComparer.Ordinal));

        Assert.True(int.TryParse(defaults[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds),
            $"test-timeout's default must be a whole number of seconds, not '{defaults[0]}'");
        Assert.Equal(WorkflowTestTimeoutSeconds, seconds);
        Assert.True(seconds > ParallelFanOut.DefaultTestTimeoutSec,
            $"the workflow's per-test timeout ({seconds}s) must exceed the runner's own default "
            + $"({ParallelFanOut.DefaultTestTimeoutSec}s) — at or below it the input changes nothing "
            + "and #3431 is back");
    }

    /// <summary>
    /// The nightly runs on the same hosted runner as every other caller, so it wants the same
    /// watchdog — and gets it by passing nothing and inheriting ms-bucket.yml's default. A
    /// second spelling of the number here is how the nightly's configuration and the surface's
    /// would come to differ, which makes their numbers incomparable: the same failure mode as
    /// the provisioning copy that drifted four times (#1976), one input down.
    /// </summary>
    [Fact]
    public void NightlyWorkflow_InheritsThePerTestTimeout_RatherThanSpellingItAgain()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Nightly)));

        Assert.DoesNotContain("test-timeout", code, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/ms-bucket.yml", code, StringComparison.Ordinal);
        // And the thing it inherits is real: ms-bucket.yml declares the input with a default,
        // so "passes nothing" means 300 s, not nothing.
        Assert.NotEmpty(WorkflowInputDefaults.Of(Read(Path.Combine("workflows", Workflow)), "test-timeout"));
    }

    [Fact]
    public void ProvisioningAction_CarriesTheBuildAndBothAppDownloads()
    {
        var code = CodeOnly(Read(ProvisionAction));

        Assert.Contains("AllowBcArtifactDownload=true", code, StringComparison.Ordinal);
        Assert.Contains("platform-apps ${{", code, StringComparison.Ordinal);
        Assert.Contains("test-apps ${{", code, StringComparison.Ordinal);
        Assert.Contains("$HOME/.al-runner/platform-apps", code, StringComparison.Ordinal);
        Assert.Contains("$HOME/.al-runner/test-apps", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workflows/" + Workflow)]
    [InlineData("workflows/" + SharedMatrix)]
    public void ProvisioningConsumers_UseTheSharedAction_AndInlineNeitherDownload(string relative)
    {
        var code = CodeOnly(Read(relative));

        Assert.Contains(ProvisionMarker, code, StringComparison.Ordinal);
        Assert.DoesNotContain("platform-apps ${{", code, StringComparison.Ordinal);
        Assert.DoesNotContain("test-apps ${{", code, StringComparison.Ordinal);
    }
}
