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
    private static readonly string GithubDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".github"));

    private const string Workflow = "ms-bucket.yml";
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

    [Fact]
    public void MsBucketWorkflow_IsManualDispatchOnly()
    {
        var triggers = WorkflowTriggers.TriggersOf(Read(Path.Combine("workflows", Workflow)));

        Assert.Equal(new[] { "workflow_dispatch" }, triggers);
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
