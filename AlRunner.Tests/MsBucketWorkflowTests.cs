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
