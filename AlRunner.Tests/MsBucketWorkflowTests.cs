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
    /// The inside of EVERY bash array literal <c>name=( … )</c> in the script, newline-joined
    /// and parenthesis-matched rather than terminated by a string. An assertion about what the
    /// runner is invoked WITH has to be scoped to the arrays it is invoked with — a flag named
    /// in a comment, an <c>echo</c>, or a sibling <c>args+=(</c> guarded by an <c>if</c> is not
    /// the same claim. Empty when there is no such literal.
    ///
    /// It reads every literal, not the first, because a NEGATIVE reading is load-bearing here:
    /// "the flag must not be in the unconditional array" is what keeps the input's <c>false</c>
    /// default from being decorative. A first-match reader supports a positive claim perfectly
    /// well — #3443 asserted <c>--test-timeout</c> IS present and was right — but scoped to a
    /// subset it silently PERMITS what a negative claim forbids everywhere else. Reviewed on
    /// #3453: keeping the conditional append exactly as shipped and adding a second literal,
    /// <c>args=( "${args[@]}" --test-data-normalize-company )</c>, turns the flag on for every
    /// caller including the nightly and left the whole pinned suite green.
    /// </summary>
    internal static string Of(string script, string name)
    {
        var marker = name + "=(";
        var bodies = new List<string>();
        for (var open = script.IndexOf(marker, StringComparison.Ordinal); open >= 0;
             open = script.IndexOf(marker, open + marker.Length, StringComparison.Ordinal))
        {
            // `MY_args=(` is a different array from `args=(`, and matching it would let a
            // negative assertion fail on text it does not govern.
            if (open > 0 && (char.IsLetterOrDigit(script[open - 1]) || script[open - 1] == '_'))
                continue;
            var i = open + name.Length + 1;      // at the '('
            var depth = 0;
            for (var j = i; j < script.Length; j++)
            {
                if (script[j] == '(') depth++;
                else if (script[j] == ')' && --depth == 0) { bodies.Add(script[(i + 1)..j]); break; }
            }
            // An unbalanced literal contributes nothing — the caller's assertion says so.
        }
        return string.Join('\n', bodies);
    }
}

internal static class BashIfBlock
{
    /// <summary>
    /// The body of <c>if &lt;condition&gt;; then … fi</c>, up to the <c>fi</c> that closes it
    /// rather than the first one seen, so a nested <c>if</c> does not end the block early.
    /// Empty when no such block exists.
    ///
    /// An index comparison is NOT this claim. <c>Assert.True(inner &gt; outer)</c> is satisfied
    /// by text sitting after the block's closing <c>fi</c>, so it pins ORDER and reads like it
    /// pins NESTING — reviewed on #3453, where moving the append out of the block with its
    /// guard and body unchanged left the suite green.
    /// </summary>
    internal static string BodyOf(string script, string condition)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var opener = "if " + condition + "; then";
        var start = -1;
        var depth = 0;
        var body = new List<string>();
        for (var k = 0; k < lines.Length; k++)
        {
            var line = lines[k].Trim();
            if (start < 0)
            {
                if (line == opener) { start = k; depth = 1; }
                continue;
            }
            if (line.StartsWith("if ", StringComparison.Ordinal)) depth++;
            else if (line == "fi" && --depth == 0) return string.Join('\n', body);
            body.Add(lines[k]);
        }
        return string.Empty;                 // absent, or never closed
    }
}

internal static class BashConditionalAppend
{
    /// <summary>
    /// Every <c>name+=( … )</c> in a script, paired with the condition of the nearest
    /// <c>if …; then</c> above it — parenthesis-matched for the same reason
    /// <see cref="BashArrayLiteral.Of"/> is: a flag named in a comment, an <c>echo</c> or an
    /// input's own description is not the claim "the runner is invoked with it".
    ///
    /// A SWITCH cannot be asserted with <see cref="BashArrayLiteral.Of"/> at all. It carries no
    /// value, so it has to be appended under a condition rather than sit in the unconditional
    /// literal — and that literal is precisely what <c>Of</c> reads and a conditional append is
    /// precisely what it excludes. Pairing the append with its guard is what makes "turning the
    /// input on is what puts the flag on the command line" a falsifiable claim rather than a
    /// substring search that a comment would satisfy.
    /// </summary>
    internal static IReadOnlyList<(string Condition, string Body)> Of(string script, string name)
    {
        var found = new List<(string, string)>();
        var marker = name + "+=(";
        for (var open = script.IndexOf(marker, StringComparison.Ordinal); open >= 0;
             open = script.IndexOf(marker, open + marker.Length, StringComparison.Ordinal))
        {
            var i = open + marker.Length - 1;    // at the '('
            var depth = 0;
            var body = string.Empty;
            for (var j = i; j < script.Length; j++)
            {
                if (script[j] == '(') depth++;
                else if (script[j] == ')' && --depth == 0) { body = script[(i + 1)..j]; break; }
            }
            found.Add((ConditionAbove(script, open), body.Trim()));
        }
        return found;
    }

    /// <summary>The nearest <c>if …; then</c> line above <paramref name="index"/>, stripped to
    /// its condition. Empty when there is none, so an unconditional append is distinguishable
    /// from a guarded one instead of both reading the same.</summary>
    private static string ConditionAbove(string script, int index)
    {
        var lines = script[..index].Replace("\r\n", "\n").Split('\n');
        for (var k = lines.Length - 1; k >= 0; k--)
        {
            var line = lines[k].Trim();
            if (!line.StartsWith("if ", StringComparison.Ordinal)) continue;
            var then = line.LastIndexOf("; then", StringComparison.Ordinal);
            return (then < 0 ? line[3..] : line[3..then]).Trim();
        }
        return string.Empty;
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

    /// <summary>
    /// The property a NEGATIVE assertion needs and a first-match reader cannot give it: every
    /// literal is read, so "this flag is in no unconditional array" covers all of them. The
    /// second literal below is the reviewer's break on #3453 — the conditional append left
    /// exactly as shipped, plus one more <c>args=( … )</c> that turns the flag on for everyone.
    /// </summary>
    [Fact]
    public void BashArrayLiteral_ReadsEveryLiteral_NotOnlyTheFirst()
    {
        const string script = """
            args=( "$BUNDLE" --cache "$C" )
            if [ "$NORMALIZE_COMPANY" = "true" ]; then
              args+=( --test-data-normalize-company )
            fi
            args=( "${args[@]}" --test-data-normalize-company )
            """;

        var all = BashArrayLiteral.Of(script, "args");
        Assert.Contains("--cache \"$C\"", all, StringComparison.Ordinal);
        Assert.Contains("--test-data-normalize-company", all, StringComparison.Ordinal);
        // The conditional append is still NOT a literal — that separation is what makes the
        // shipped workflow's own assertion mean something.
        Assert.Single(BashConditionalAppend.Of(script, "args"));

        // A different array whose name merely ends in the one asked for is not this array.
        Assert.Equal("--quiet", BashArrayLiteral.Of("MY_args=( --loud )\nargs=( --quiet )", "args").Trim());
    }

    /// <summary>
    /// <see cref="BashIfBlock.BodyOf"/> ends at the <c>fi</c> that closes the block it was
    /// asked for, not the first one it meets, and text after that <c>fi</c> is outside. That
    /// second half is the whole point: an index comparison would call it inside.
    /// </summary>
    [Fact]
    public void BashIfBlock_EndsAtItsOwnFi_AndExcludesWhatFollows()
    {
        const string script = """
            if [ "$WITH_TEST_DATA" = "true" ]; then
              args+=( --test-data-company "CRONUS International Ltd_" )
              if [ "$NORMALIZE_COMPANY" = "true" ]; then
                args+=( --test-data-normalize-company )
              fi
            fi
            args+=( --after-the-block )
            """;

        var body = BashIfBlock.BodyOf(script, "[ \"$WITH_TEST_DATA\" = \"true\" ]");
        Assert.Contains("--test-data-normalize-company", body, StringComparison.Ordinal);
        Assert.Contains("--test-data-company", body, StringComparison.Ordinal);
        Assert.DoesNotContain("--after-the-block", body, StringComparison.Ordinal);

        // The inner block is addressable on its own terms, and an absent condition is empty
        // rather than "everything from here on".
        Assert.Equal("args+=( --test-data-normalize-company )",
            BashIfBlock.BodyOf(script, "[ \"$NORMALIZE_COMPANY\" = \"true\" ]").Trim());
        Assert.Equal(string.Empty, BashIfBlock.BodyOf(script, "[ \"$NOPE\" = \"true\" ]"));
    }

    [Fact]
    public void BashConditionalAppend_PairsEachAppendWithItsGuard_AndIgnoresProseNamingTheFlag()
    {
        const string script = """
            # --test-data-normalize-company belongs in a comment and must not count as an append.
            echo "would run with --test-data-normalize-company"
            args=( "$BUNDLE" --cache "$C" )
            if [ "$WITH_TEST_DATA" = "true" ]; then
              args+=( "--test-data=$bak" --test-data-company "CRONUS International Ltd_" )
              if [ "$NORMALIZE_COMPANY" = "true" ]; then
                args+=( --test-data-normalize-company )
              fi
            fi
            """;

        var appends = BashConditionalAppend.Of(script, "args");
        Assert.Equal(2, appends.Count);
        Assert.Equal("[ \"$WITH_TEST_DATA\" = \"true\" ]", appends[0].Condition);
        Assert.Contains("--test-data-company", appends[0].Body, StringComparison.Ordinal);
        Assert.Equal("[ \"$NORMALIZE_COMPANY\" = \"true\" ]", appends[1].Condition);
        Assert.Equal("--test-data-normalize-company", appends[1].Body);

        // The comment and the echo name the same flag and are not appends; the unconditional
        // literal is a different claim again and holds neither flag.
        Assert.DoesNotContain(appends, a => a.Body.Contains("echo", StringComparison.Ordinal));
        Assert.DoesNotContain("--test-data-normalize-company", BashArrayLiteral.Of(script, "args"),
            StringComparison.Ordinal);
        Assert.Empty(BashConditionalAppend.Of(script, "nosuch"));

        // An append with no `if` above it reports an empty condition rather than borrowing one.
        Assert.Equal(string.Empty,
            Assert.Single(BashConditionalAppend.Of("args+=( --quiet )", "args")).Condition);
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

    // ---- company normalization (#3450, the flag from #2730) ----------------------------

    /// <summary>
    /// #3450: <c>--test-data-normalize-company</c> shipped on the runner (#2730) with no way
    /// for any workflow to pass it, so the comparison it was built for could not be run in CI.
    ///
    /// The claim is that the flag ARRIVES, which is four separate links — checking any one of
    /// them alone passes on the defect this exists to catch:
    ///
    ///   1. the INPUT reaches a shell variable, so a caller's override goes somewhere;
    ///   2. the variable GUARDS an append, so the input is what decides;
    ///   3. the APPENDED text is the flag the runner's own parser accepts, spelled from
    ///      <see cref="TestDataNormalization.FlagName"/> rather than typed a second time, and
    ///      carries no value — <c>--test-data-normalize-company false</c> would leave
    ///      <c>false</c> as a positional argument, which Program.cs adds to the bundle list;
    ///   4. that array is what the runner is invoked with.
    ///
    /// And the negative that keeps every recorded number valid: the flag is NOT in the
    /// unconditional <c>args=( … )</c> literal. There it would be on for every caller,
    /// including the nightly, and the input's <c>false</c> default would be decorative.
    /// </summary>
    [Fact]
    public void MsBucketWorkflow_PutsCompanyNormalizationOnTheRunnersArgumentArray_OnlyWhenAskedTo()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Workflow)));
        var runStep = WorkflowStep.BodyOf(code, "Run the bucket(s)");

        Assert.Contains("NORMALIZE_COMPANY: ${{ inputs.normalize-company }}", runStep,
            StringComparison.Ordinal);

        var appends = BashConditionalAppend.Of(runStep, "args");
        var normalize = Assert.Single(appends.Where(
            a => a.Body.Contains(TestDataNormalization.FlagName, StringComparison.Ordinal)));

        Assert.Equal(TestDataNormalization.FlagName, normalize.Body);
        Assert.Equal("[ \"$NORMALIZE_COMPANY\" = \"true\" ]", normalize.Condition);

        // In NO unconditional literal — BashArrayLiteral.Of reads every `args=( … )` in the
        // step, because this negative claim is what keeps the input's false default from being
        // decorative and a claim scoped to one literal permits the flag in the next one.
        Assert.DoesNotContain(TestDataNormalization.FlagName,
            BashArrayLiteral.Of(runStep, "args"), StringComparison.Ordinal);

        Assert.Contains("/al-runner\" \"${args[@]}\"", runStep, StringComparison.Ordinal);

        // Nested INSIDE the --test-data block, which is a containment claim and not an
        // ordering one: the rule rewrites rows on their way out of the backup, so outside the
        // block the flag changes nothing while the runner still prints "company normalization
        // ON" — a line that reads like data was prepared when none was restored.
        var withTestData = BashIfBlock.BodyOf(runStep, "[ \"$WITH_TEST_DATA\" = \"true\" ]");
        Assert.False(string.IsNullOrWhiteSpace(withTestData),
            "the --test-data block is gone from the \"Run the bucket(s)\" step");
        Assert.Contains(TestDataNormalization.FlagName, withTestData, StringComparison.Ordinal);
        Assert.Equal(TestDataNormalization.FlagName,
            Assert.Single(BashConditionalAppend.Of(withTestData, "args")
                .Where(a => a.Body.Contains(TestDataNormalization.FlagName, StringComparison.Ordinal))).Body);

        Assert.True(runStep.IndexOf("if [ \"$WITH_TEST_DATA\" = \"true\" ]; then", StringComparison.Ordinal)
                < runStep.IndexOf("\"${args[@]}\"", StringComparison.Ordinal),
            "the switch must be appended before the runner is invoked");
    }

    /// <summary>
    /// The default is OFF on BOTH trigger blocks, and it is the same word on each. Every
    /// pass/fail number recorded in this repository — the running-ms-test-buckets skill's
    /// 259/595 for Tests-SMB, #3416's corpus counts, the full-surface runs — was measured
    /// against the un-normalized restore. A default of <c>true</c> would make all of them
    /// uncomparable and nothing would announce it; a default on only one trigger would make
    /// the two spellings disagree, which is the same failure one dispatch at a time.
    /// </summary>
    [Fact]
    public void MsBucketWorkflow_DefaultsCompanyNormalizationOff_OnBothTriggers()
    {
        var defaults = WorkflowInputDefaults.Of(
            Read(Path.Combine("workflows", Workflow)), "normalize-company");

        Assert.Equal(2, defaults.Count);
        Assert.Equal(new[] { "false", "false" }, defaults);
    }

    /// <summary>
    /// The nightly inherits the OFF default by passing nothing. It is the trend line, and every
    /// point on it so far was measured un-normalized — a second spelling here is both how two
    /// configurations drift apart (#1976, one input down) and how a series quietly stops being
    /// comparable with itself.
    /// </summary>
    [Fact]
    public void NightlyWorkflow_InheritsCompanyNormalization_RatherThanSpellingItAgain()
    {
        var code = CodeOnly(Read(Path.Combine("workflows", Nightly)));

        Assert.DoesNotContain("normalize-company", code, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/ms-bucket.yml", code, StringComparison.Ordinal);
        // And what it inherits is real: ms-bucket.yml declares the input with a default, so
        // "passes nothing" means OFF rather than an empty string.
        Assert.NotEmpty(WorkflowInputDefaults.Of(
            Read(Path.Combine("workflows", Workflow)), "normalize-company"));
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
