// PartialCompanyInitAcceptanceTests — issue #3561.
//
// What is being pinned
// --------------------
// #3538 made a run whose company initialization did not complete exit 2 instead of 0. The only
// escape from that was `--no-strict-exit`, which forces 0 for a failing test, a compile failure
// and a lost output file too — a suite that knowingly accepts one abort had to give up its exit
// code entirely. The targeted opt-out is an expectations-manifest entry:
//
//   { "codeunitId": 2, "CodeunitName": "Company-Initialize", "Method": "*",
//     "Mode": "accept-partial-company-init", "Reason": "<why this project accepts it>" }
//
// A matching abort is still printed in the summary (marked `[accepted: <reason>]`), still in
// --out, --output-json and the JUnit comment — ONLY the exit 0 → 2 escalation is suppressed, and
// only for the codeunit the entry names. Exit codes 1/3/4/5 are untouched.
//
// The drift half mirrors expect-oos's "the test passed, remove the entry": an entry whose
// codeunit RAN TO COMPLETION in this run is an exit-code suppression with nothing behind it, and
// the run says so and does not exit 0.
//
// Why two injected seams
// ----------------------
// Whether codeunit 2 aborts is a property of a particular Base Application build (#3054 measured
// five of eight legs; today, none), so the abort arms drive AL_RUNNER_TEST_FAIL_COMPANY_INIT,
// exactly as PartialCompanyInitializationTests does. The drift arm needs the opposite fact —
// codeunit 2 found and COMPLETED — which for real requires the Base Application floor, forbidden
// in a C# fixture (.claude/rules/no-base-app-in-csharp-tests.md) and, at ~70s per invocation,
// unable to guarantee the completion anyway. AL_RUNNER_TEST_COMPANY_INIT_COMPLETED=1 records the
// completion at exactly the line the real one does, so the drift check under test is the real one.
using System.Text.Json;
using Xunit;
using static AlRunner.Tests.CipAcceptanceHarness;

namespace AlRunner.Tests;

// The runner spawn, the manifest writer and the two injected env vars live in
// CipAcceptanceHarness, shared with PartialCompanyInitAcceptanceEscalationTests.
public sealed class PartialCompanyInitAcceptanceTests
{
    /// <summary>
    /// The positive case. An accepted abort keeps every reporting surface it already had — the
    /// summary block, the reason text, the JSON document — and adds the entry's reason to them,
    /// while the process exits 0 because nothing about the AL failed. The escalation line is
    /// absent, because there is no moved exit code for it to explain.
    /// </summary>
    [SkippableFact]
    public void AnAcceptedAbort_IsStillReportedEverywhere_ButDoesNotEscalateTheExitCode()
    {
        TestArtifacts.SkipIfMissing();

        var manifest = ManifestDir("accept", AcceptEntry());
        var run = RunRunner(NewCacheDir(), manifest);

        Assert.True(run.Exit == 0,
            $"an accepted company-init abort must not move the exit code. exit={run.Exit}\n{run.Output}");
        // Recorded, not silenced: the whole point of the entry over --no-strict-exit.
        Assert.Contains("Company initialization: INCOMPLETE", run.Output);
        Assert.Contains(InjectedReason, run.Output);
        Assert.Contains($"[accepted: {AcceptedBecause}]", run.Output);
        Assert.Contains("accept-partial-company-init", run.Output);
        // ...and the tests are still real results.
        Assert.Contains("pass:        1", run.Output);
        // The escalation line only ever explains a MOVED exit code.
        Assert.DoesNotContain("[warn] company-init: 1 company initialization abort(s)", run.Output);

        // --output-json carries the acceptance as a field, so a consumer reading only the
        // document can tell an accepted condition from one nobody has looked at.
        var json = RunRunner(NewCacheDir(), manifest, outputJson: true);
        Assert.Equal(0, json.Exit);
        var doc = JsonDocument.Parse(json.Output[json.Output.IndexOf('{')..]).RootElement;
        Assert.Equal(0, doc.GetProperty("exitCode").GetInt32());
        var failures = doc.GetProperty("companyInitFailures").EnumerateArray().ToList();
        Assert.Single(failures);
        Assert.Equal(AcceptedBecause, failures[0].GetProperty("accepted").GetString());
        Assert.Equal(1, failures[0].GetProperty("count").GetInt32());
        Assert.Equal(InjectedReason, failures[0].GetProperty("message").GetString());
    }

    /// <summary>
    /// The negative control, and what makes the acceptance targeted rather than a blanket
    /// switch: an entry naming a DIFFERENT initialization codeunit accepts nothing here. Same
    /// manifest machinery, same run, exit 2 — so a project cannot suppress an abort it has not
    /// actually declared, which is the failure mode `--no-strict-exit` has by design.
    /// </summary>
    [SkippableFact]
    public void AnEntryNamingAnotherCodeunit_DoesNotAcceptThisAbort()
    {
        TestArtifacts.SkipIfMissing();

        var manifest = ManifestDir("other",
            AcceptEntry(codeunitId: 9999, codeunitName: "Some Other Initialize"));
        var run = RunRunner(NewCacheDir(), manifest);

        Assert.True(run.Exit == 2,
            $"an abort no entry names must still exit 2. exit={run.Exit}\n{run.Output}");
        Assert.Contains("[warn] company-init:", run.Output);
        Assert.DoesNotContain("[accepted:", run.Output);
    }

    /// <summary>
    /// The drift half. The entry says "this codeunit aborts here"; once it completes, the entry
    /// is a suppression nobody is watching, and the run refuses to be clean until it is removed
    /// — the same loudness expect-oos has for a test that started passing.
    /// </summary>
    [SkippableFact]
    public void AnEntryWhoseCodeunitCompleted_IsDrift_AndTheRunSaysSo()
    {
        TestArtifacts.SkipIfMissing();

        var manifest = ManifestDir("stale", AcceptEntry());
        var run = RunRunner(NewCacheDir(), manifest, injectAbort: false, injectCompleted: true);

        Assert.True(run.Exit == 2,
            $"a stale acceptance entry must not leave the run reporting clean. exit={run.Exit}\n{run.Output}");
        Assert.Contains("declares accept-partial-company-init for codeunit 2", run.Output);
        Assert.Contains("ran to completion in this run", run.Output);
        Assert.Contains("Remove the entry", run.Output);
        Assert.Contains("accept-company-init.json", run.Output);
        // The company DID initialize, so none of the abort reporting appears.
        Assert.DoesNotContain("Company initialization: INCOMPLETE", run.Output);
    }

    /// <summary>
    /// The same entry in a run that never attempted the codeunit is INERT, not drift. The
    /// manifest directory is auto-probed and shared by every invocation in a project — the
    /// reason `--expectations-require-match` is opt-in — so a bundle carrying no Base App must
    /// not fail because another bundle's declaration is in the same directory.
    /// </summary>
    [SkippableFact]
    public void AnEntryWhoseCodeunitWasNeverAttempted_IsInert()
    {
        TestArtifacts.SkipIfMissing();

        var manifest = ManifestDir("inert", AcceptEntry());
        var run = RunRunner(NewCacheDir(), manifest, injectAbort: false);

        Assert.True(run.Exit == 0,
            $"an entry for a codeunit this run never ran must not fail it. exit={run.Exit}\n{run.Output}");
        Assert.DoesNotContain("Remove the entry", run.Output);
        Assert.DoesNotContain("Company initialization: INCOMPLETE", run.Output);
    }

    /// <summary>
    /// The reason is what a reviewer reads, so a marker with nothing behind it is refused at
    /// load time — before a single test runs, like every other schema violation. Same fixed
    /// token list as .github/scripts/check_corpus_linkage.sh's Corpus-NA reason.
    /// </summary>
    [SkippableTheory]
    [InlineData("n/a")]
    [InlineData("TBD")]
    [InlineData("-")]
    public void APlaceholderReason_IsRefusedAtLoadTime(string placeholder)
    {
        TestArtifacts.SkipIfMissing();

        var manifest = ManifestDir("placeholder-" + placeholder.Replace("/", ""),
            AcceptEntry(reason: placeholder));
        var run = RunRunner(NewCacheDir(), manifest);

        Assert.Equal(2, run.Exit);
        Assert.Contains("is a placeholder", run.Output);
        Assert.Contains("expectations manifest", run.Output);
        // Refused BEFORE the run, so no test result exists to read.
        Assert.DoesNotContain("pass:        1", run.Output);
    }

    /// <summary>
    /// The entry names an initialization codeunit, not a test, so a Method other than "*" is a
    /// misunderstanding worth refusing rather than silently ignoring — nothing would ever look
    /// such an entry up, and it would read as an accepted abort that never accepts anything.
    /// </summary>
    [SkippableFact]
    public void AMethodOtherThanWildcard_IsRefusedAtLoadTime()
    {
        TestArtifacts.SkipIfMissing();

        var manifest = ManifestDir("method", AcceptEntry(method: "SomeTestMethod"));
        var run = RunRunner(NewCacheDir(), manifest);

        Assert.Equal(2, run.Exit);
        Assert.Contains("'Method' must be", run.Output);
    }
}
