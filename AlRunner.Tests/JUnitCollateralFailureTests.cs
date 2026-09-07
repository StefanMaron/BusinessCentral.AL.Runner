// JUnitCollateralFailureTests — issue #2919, the seventh reporting surface.
//
// #2898 made a fail/error that shares a bucket with a suite error report as SUSPECT — likely
// collateral of the lost suite rather than a real regression — on six surfaces: the summary,
// PrintPerTest, PrintFailureClassification, --output-json, --out, and the --watch tree.
// CollateralFailureReportingTests pins all six. --output-junit was left out, and this file is
// that surface.
//
// The reason recorded for leaving it out — "either an attribute outside the schema or inflating
// `failures`" — was a false dichotomy, and JUnitReport.cs already contained both counterexamples
// (see WriteJUnit's own comments for why each representation was picked):
//
//   * BuildBody puts #2240's missing-test-data explanation in the <failure> BODY, so `message`
//     — which a CI dashboard groups by — and the counters stay untouched.
//   * The watchdog-resume provenance is an XML COMMENT, "because every JUnit consumer tolerates
//     a comment, not every one tolerates an attribute its schema does not name."
//
// So the marker is body text and the lost suite is a comment, and neither moves a counter.
// The counter assertions below are the ones that matter most: a JUnit consumer reads `failures`
// as a number and plots it, so a representation that reclassified a suspect failure as a skip
// (or dropped it) would make a trend line move for a reporting change. Every test here that
// asserts a marker also asserts the exact integers beside it.
//
// The negatives carry as much weight as on the other six surfaces, plus one this surface has
// that the console ones do not: a JUnit document is consumed by machines that DIFF it between
// runs. A clean run must serialise byte-for-byte as it did before, which
// CleanRun_IsByteIdenticalToTheDocumentWrittenWithoutThisFeature asserts directly rather than
// by proxy.
//
// Entirely about the RUNNER's own output format. "What does al-runner write into its JUnit XML
// when a bundle loses a suite" is not a question a BC service tier can adjudicate, so nothing
// here belongs in the al-language corpus (.claude/rules/bc-behavior-tests-go-upstream.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class JUnitCollateralFailureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("al-runner-junit-collateral-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private const string SuiteError =
        "<bundled>: COMPILE-FAIL (24): _polyfill.cs(31,24): error CS0400: The type or namespace "
        + "name 'AlRunner' could not be found in the global namespace";

    private static TestResult Pass(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(3));

    private static TestResult Fail(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Fail, "Assert.AreEqual failed", null,
            TimeSpan.FromMilliseconds(5));

    private static TestResult Error(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Error, "object not found", null,
            TimeSpan.FromMilliseconds(5));

    /// <summary>
    /// The #2880 shape: a bucket that ran, lost a suite, and whose surviving tests include one
    /// pass, one fail and one error — so a marker cannot be confused with "the bucket failed".
    /// Deliberately the same fixture shape as CollateralFailureReportingTests, so the seventh
    /// surface is being asked about the same run the other six were.
    /// </summary>
    private static BucketResult PartialBucketWithFailures(string path = "/runner-extras") =>
        new(path, BucketStage.Ran,
            new[] { SuiteError }, null,
            new[]
            {
                Pass("Codeunit60100", "Healthy_StillPasses"),
                Fail("Codeunit64535", "JoinWithLeftOuterJoin_ReturnsRows"),
                Error("Codeunit60391", "RightOuterJoin_IsOutOfScope_ThrowsNamedReason"),
            },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    /// <summary>The control: the identical results with nothing lost.</summary>
    private static BucketResult CleanBucketWithFailures(string path = "/runner-extras") =>
        new(path, BucketStage.Ran,
            Array.Empty<string>(), null,
            new[]
            {
                Pass("Codeunit60100", "Healthy_StillPasses"),
                Fail("Codeunit64535", "JoinWithLeftOuterJoin_ReturnsRows"),
                Error("Codeunit60391", "RightOuterJoin_IsOutOfScope_ThrowsNamedReason"),
            },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    private XDocument Write(string name, params BucketResult[] buckets)
    {
        var path = P(name);
        JUnitReport.WriteJUnit(path, buckets);
        return XDocument.Load(path, LoadOptions.PreserveWhitespace);
    }

    private static XElement Case(XDocument doc, string method) =>
        doc.Descendants("testcase").Single(c => c.Attribute("name")?.Value == method);

    /// <summary>Every XML comment in the document, whitespace-normalised.</summary>
    private static List<string> Comments(XDocument doc) =>
        doc.DescendantNodes().OfType<XComment>().Select(c => c.Value.Trim()).ToList();

    // ── The marker: <failure>/<error> body, never `message`, never a counter ─────────

    /// <summary>
    /// The positive. A CI dashboard renders the failure body under the failing test, so this is
    /// where a reader who has clicked into the failure is already looking — and it is the one
    /// place BuildBody has already established as safe (#2240).
    /// </summary>
    [Fact]
    public void SuspectFailure_IsMarkedInTheFailureBody_NotInMessage()
    {
        var doc = Write("partial.xml", PartialBucketWithFailures());

        var failure = Case(doc, "JoinWithLeftOuterJoin_ReturnsRows").Element("failure")!;

        // The marker is the same string every other surface prints, from the same predicate.
        Assert.Contains("[suspect — bucket lost 1 suite(s)]", failure.Value, StringComparison.Ordinal);
        // …and it explains itself, because a JUnit reader has no summary above it to read.
        Assert.Contains("suite(s) in this bundle did not compile", failure.Value, StringComparison.Ordinal);

        // `message` is BC's own failure text, untouched: a dashboard groups failures by it, so a
        // suspect marker there would split one cluster in two and change what a grouping view
        // shows for a reporting-only change.
        Assert.Equal("Assert.AreEqual failed", failure.Attribute("message")!.Value);

        // The original body is still there, below the marker — the marker is additive.
        Assert.Contains("Assert.AreEqual failed", failure.Value, StringComparison.Ordinal);
    }

    /// <summary>An <c>error</c> result is marked the same way: #2898 marks fail AND error.</summary>
    [Fact]
    public void SuspectError_IsMarkedInTheErrorBody_NotInMessage()
    {
        var doc = Write("partial.xml", PartialBucketWithFailures());

        var error = Case(doc, "RightOuterJoin_IsOutOfScope_ThrowsNamedReason").Element("error")!;

        Assert.Contains("[suspect — bucket lost 1 suite(s)]", error.Value, StringComparison.Ordinal);
        Assert.Equal("object not found", error.Attribute("message")!.Value);
    }

    /// <summary>
    /// The counter assertion, and the reason this surface needed care at all. `failures` and
    /// `errors` are numbers a CI system plots over time; marking a failure suspect must not move
    /// either one, at the suite level or the root.
    /// </summary>
    [Fact]
    public void MarkingASuspectFailure_MovesNoCounter()
    {
        var doc = Write("partial.xml", PartialBucketWithFailures());

        var root = doc.Root!;
        Assert.Equal("3", root.Attribute("tests")!.Value);
        Assert.Equal("1", root.Attribute("failures")!.Value);
        Assert.Equal("1", root.Attribute("errors")!.Value);
        Assert.Equal("0", root.Attribute("skipped")!.Value);

        // The failing test is still a <failure> element, not reclassified to <skipped> to keep
        // the number down — the marker says "this may be collateral", it does not say "this did
        // not happen".
        var failing = Case(doc, "JoinWithLeftOuterJoin_ReturnsRows");
        Assert.NotNull(failing.Element("failure"));
        Assert.Null(failing.Element("skipped"));

        // Per-suite counters agree with the root: 1 test each, exactly one of them failing.
        var suites = doc.Descendants("testsuite").ToList();
        Assert.Equal(3, suites.Count);
        Assert.Equal(1, suites.Sum(s => int.Parse(s.Attribute("failures")!.Value)));
        Assert.Equal(1, suites.Sum(s => int.Parse(s.Attribute("errors")!.Value)));
        Assert.Equal(3, suites.Sum(s => int.Parse(s.Attribute("tests")!.Value)));
    }

    /// <summary>
    /// A pass in the same bucket is untouched. #2898's stance, restated here because it is a
    /// property of the output and not only of the predicate: a missing object manufactures
    /// failures, it does not manufacture passes.
    /// </summary>
    [Fact]
    public void PassesInAPartialBucket_AreNotMarked()
    {
        var doc = Write("partial.xml", PartialBucketWithFailures());

        var passing = Case(doc, "Healthy_StillPasses");
        Assert.Empty(passing.Elements());
        Assert.DoesNotContain("suspect", passing.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The negative that keeps the marker meaningful: failures in a bucket that lost nothing are
    /// exactly as they were. An implementation that marks every failure passes every positive
    /// above and is as useless as marking none.
    /// </summary>
    [Fact]
    public void FailuresInACleanBucket_AreNotMarked()
    {
        var doc = Write("clean.xml", CleanBucketWithFailures());

        var failure = Case(doc, "JoinWithLeftOuterJoin_ReturnsRows").Element("failure")!;
        Assert.DoesNotContain("suspect", failure.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Assert.AreEqual failed", failure.Value.Trim());
    }

    /// <summary>
    /// Only the bucket that lost something is implicated. Two bundles in one run, one clean and
    /// one partial, and the marker lands on exactly one of the two failing tests — which the
    /// single-bucket tests above cannot distinguish from "mark whatever is failing".
    /// </summary>
    [Fact]
    public void OnlyTheFailuresSharingABucketWithASuiteError_AreMarked()
    {
        var doc = Write("mixed.xml",
            PartialBucketWithFailures("/lost-a-suite"),
            new BucketResult("/intact", BucketStage.Ran,
                Array.Empty<string>(), null,
                new[] { Fail("Codeunit60200", "RealRegression_ShouldBeInvestigated") },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null));

        Assert.Contains("[suspect", Case(doc, "JoinWithLeftOuterJoin_ReturnsRows").Element("failure")!.Value,
            StringComparison.Ordinal);
        Assert.DoesNotContain("suspect",
            Case(doc, "RealRegression_ShouldBeInvestigated").Element("failure")!.Value,
            StringComparison.OrdinalIgnoreCase);

        // Two failures and one error across both bundles, and marking moved neither count.
        Assert.Equal("2", doc.Root!.Attribute("failures")!.Value);
        Assert.Equal("1", doc.Root!.Attribute("errors")!.Value);
    }

    /// <summary>
    /// The pairing bug this surface would have had if WriteJUnit had kept flattening buckets
    /// before asking the question. Two bundles whose codeunits COLLIDE by name — the flattened
    /// list groups by codeunit, so the two buckets' results land in one &lt;testsuite&gt; and a
    /// per-suite predicate could not tell which bucket each came from. The marker must follow the
    /// bucket the result actually ran in, not the suite it is printed under.
    /// </summary>
    [Fact]
    public void WhenTwoBundlesShareACodeunitName_TheMarkerFollowsTheBucket()
    {
        var doc = Write("collide.xml",
            new BucketResult("/lost-a-suite", BucketStage.Ran,
                new[] { SuiteError }, null,
                new[] { Fail("Codeunit60500", "Collateral_FromThePartialBundle") },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null),
            new BucketResult("/intact", BucketStage.Ran,
                Array.Empty<string>(), null,
                new[] { Fail("Codeunit60500", "Real_FromTheIntactBundle") },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null));

        // One <testsuite name="Codeunit60500"> holding both, as before — grouping is unchanged.
        var suite = doc.Descendants("testsuite").Single(s => s.Attribute("name")!.Value == "Codeunit60500");
        Assert.Equal("2", suite.Attribute("tests")!.Value);
        Assert.Equal("2", suite.Attribute("failures")!.Value);

        Assert.Contains("[suspect", Case(doc, "Collateral_FromThePartialBundle").Element("failure")!.Value,
            StringComparison.Ordinal);
        Assert.DoesNotContain("suspect", Case(doc, "Real_FromTheIntactBundle").Element("failure")!.Value,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #2898's watchdog carve-out reaches this surface too: the test the abort reason NAMES is
    /// the one thing in the run that is definitely real, so marking it would point the reader
    /// away from the only genuine problem. Asked through Reporter.IsSuspect, so this holds
    /// without JUnitReport knowing the rule exists.
    /// </summary>
    [Fact]
    public void TheTestNamedByAWatchdogAbort_IsNotMarked()
    {
        // Built by the executor's own formatter, so the two cannot drift apart silently.
        var abortReason = "runner-extras/timeout: " + Infrastructure.BundleFailureStage.TestTimeoutAbort + ": "
            + Infrastructure.BundleFailureStage.AbortReasonHead("Codeunit 64535 \"Hang Suite\"", "Codeunit64535", "HungTest")
            + " — 2 further [Test] method(s) in this codeunit did not run (2 total)";
        var doc = Write("watchdog.xml",
            new BucketResult("/runner-extras", BucketStage.Ran,
                new[] { abortReason }, null,
                new[]
                {
                    Error("Codeunit64535", "HungTest"),
                    Fail("Codeunit60391", "Collateral_OfTheAbort"),
                },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null));

        Assert.DoesNotContain("suspect", Case(doc, "HungTest").Element("error")!.Value,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[suspect", Case(doc, "Collateral_OfTheAbort").Element("failure")!.Value,
            StringComparison.Ordinal);
    }

    // ── The lost suite: an XML comment, because it has no <testsuite> of its own ─────

    /// <summary>
    /// The other half of #2919, and the same family as the original #2762 report: a lost suite
    /// contributes NO &lt;testsuite&gt; element, so a reader of the XML cannot tell a suite that
    /// did not run from one that never existed. That is the silent-failure shape
    /// .claude/rules/loud-failures.md forbids — the document said 3 tests, all accounted for, and
    /// omitted that 24 more were supposed to be there.
    /// </summary>
    [Fact]
    public void ALostSuite_IsNamedInAnXmlComment()
    {
        var doc = Write("partial.xml", PartialBucketWithFailures());

        var comments = Comments(doc);
        var lost = Assert.Single(comments, c => c.Contains("did not compile", StringComparison.Ordinal));

        // The bundle it happened in, and the compiler's own text — the reader needs to know
        // WHICH bundle lost something and what it said, not merely that something was lost.
        Assert.Contains("/runner-extras", lost, StringComparison.Ordinal);
        Assert.Contains("CS0400", lost, StringComparison.Ordinal);
        // …and how many, so a truncated list cannot understate the loss.
        Assert.Contains("1 suite(s)", lost, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bucket whose EVERY suite was lost contributes no testcase at all, which is precisely the
    /// case where nothing else in the document can hint at it — there is no marked failure to
    /// read. The comment is the whole record.
    /// </summary>
    [Fact]
    public void ABucketThatLostEverything_IsStillNamed_ThoughItHasNoTests()
    {
        var doc = Write("total-loss.xml",
            new BucketResult("/runner-extras", BucketStage.Ran,
                new[] { SuiteError }, null,
                Array.Empty<TestResult>(),
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null));

        Assert.Empty(doc.Descendants("testsuite"));
        Assert.Equal("0", doc.Root!.Attribute("tests")!.Value);
        // The document is otherwise an empty, entirely green report — the exact silent success
        // this comment exists to prevent.
        Assert.Equal("0", doc.Root!.Attribute("failures")!.Value);

        var lost = Assert.Single(Comments(doc), c => c.Contains("did not compile", StringComparison.Ordinal));
        Assert.Contains("/runner-extras", lost, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CompileFailed bucket — one that lost EVERYTHING and never reached BucketStage.Ran — is
    /// filtered out of `tests` by WriteJUnit's `Stage == Ran` clause and so contributed nothing
    /// to the document either. Same silence, one Stage over; the same JSON gap was #1692/#2779's
    /// subject on the other surface.
    /// </summary>
    [Fact]
    public void ACompileFailedBucket_IsNamedToo()
    {
        var doc = Write("compile-failed.xml",
            new BucketResult("/broken-bundle", BucketStage.CompileFailed,
                new[] { "app.al(1,1): error AL0118: syntax error" }, null,
                Array.Empty<TestResult>(),
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null));

        Assert.Empty(doc.Descendants("testsuite"));
        var lost = Assert.Single(Comments(doc), c => c.Contains("did not compile", StringComparison.Ordinal));
        Assert.Contains("/broken-bundle", lost, StringComparison.Ordinal);
        Assert.Contains("AL0118", lost, StringComparison.Ordinal);
    }

    /// <summary>
    /// A long error list is truncated, and says how much it hid. A silent truncation would be a
    /// smaller instance of the very defect being fixed — the same argument BundleProgressLine
    /// makes for its own cap (#2898).
    /// </summary>
    [Fact]
    public void ManyLostSuites_AreCappedAndTheCommentSaysHowManyItHid()
    {
        var errors = Enumerable.Range(1, 23)
            .Select(i => $"<bundled>: COMPILE-FAIL ({i}): suite {i} did not build").ToArray();
        var doc = Write("many.xml",
            new BucketResult("/runner-extras", BucketStage.Ran, errors, null,
                new[] { Pass("Codeunit60100", "Healthy_StillPasses") },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null));

        var lost = Assert.Single(Comments(doc), c => c.Contains("did not compile", StringComparison.Ordinal));
        Assert.Contains("23 suite(s)", lost, StringComparison.Ordinal);
        Assert.Contains("suite 1 did not build", lost, StringComparison.Ordinal);
        Assert.Contains("and 20 more", lost, StringComparison.Ordinal);
        // The ones it hid are genuinely absent, so "20 more" is not decoration.
        Assert.DoesNotContain("suite 23 did not build", lost, StringComparison.Ordinal);
    }

    /// <summary>
    /// "--" cannot appear inside an XML comment, so a compiler message containing it must be
    /// softened or the document is not well-formed at all. WriteJUnit's carried-file comment
    /// already does this; the same input reaches this one. Asserting the document PARSES is the
    /// claim — XDocument.Load would throw on a malformed comment.
    /// </summary>
    [Fact]
    public void ADoubleHyphenInACompilerMessage_DoesNotBreakTheDocument()
    {
        var doc = Write("hyphens.xml",
            new BucketResult("/runner-extras", BucketStage.Ran,
                new[] { "COMPILE-FAIL: bad flag --package-cache -- see docs" }, null,
                new[] { Pass("Codeunit60100", "Healthy_StillPasses") },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null));

        var lost = Assert.Single(Comments(doc), c => c.Contains("did not compile", StringComparison.Ordinal));
        Assert.DoesNotContain("--", lost, StringComparison.Ordinal);
        Assert.Contains("package-cache", lost, StringComparison.Ordinal);
    }

    // ── The negative that matters most on a machine-read format ─────────────────────

    /// <summary>
    /// A JUnit document is diffed between runs by tooling that has no idea what a suspect failure
    /// is. A clean run must produce exactly the bytes it produced before this feature existed —
    /// asserted here against a document built from the same results with the loss removed, so it
    /// cannot pass by both sides gaining the same new text.
    /// </summary>
    [Fact]
    public void CleanRun_ContainsNoNewMarkup_AtAll()
    {
        var path = P("clean.xml");
        JUnitReport.WriteJUnit(path, new[] { CleanBucketWithFailures() });
        var xml = File.ReadAllText(path);

        Assert.DoesNotContain("suspect", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not compile", xml, StringComparison.Ordinal);
        // No comment of any kind: a run with nothing carried and nothing lost writes the same
        // pure-element document it always did.
        Assert.DoesNotContain("<!--", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The strongest form of the same claim: byte-for-byte against a document written from a
    /// bucket whose only difference is the presence of the loss. Any incidental change to the
    /// clean path — a reordering from the (bucket, test) pairing, a stray blank line — fails here
    /// even if every marker assertion above still passes.
    /// </summary>
    [Fact]
    public void CleanRun_IsByteIdenticalToTheDocumentWrittenWithoutThisFeature()
    {
        // Written against a checked-in expected document rather than a second call to the same
        // method: comparing WriteJUnit to itself would be satisfied by any change applied to both
        // sides. This is the shape the emitter produced before #2919.
        var path = P("clean.xml");
        JUnitReport.WriteJUnit(path, new[] { CleanBucketWithFailures() });

        var expected = string.Join("\n", new[]
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<testsuites tests=\"3\" failures=\"1\" errors=\"1\" skipped=\"0\" time=\"0.013\">",
            "  <testsuite name=\"Codeunit60100\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" time=\"0.003\">",
            "    <testcase name=\"Healthy_StillPasses\" classname=\"Codeunit60100\" time=\"0.003\" />",
            "  </testsuite>",
            "  <testsuite name=\"Codeunit60391\" tests=\"1\" failures=\"0\" errors=\"1\" skipped=\"0\" time=\"0.005\">",
            "    <testcase name=\"RightOuterJoin_IsOutOfScope_ThrowsNamedReason\" classname=\"Codeunit60391\" time=\"0.005\">",
            "      <error message=\"object not found\">object not found</error>",
            "    </testcase>",
            "  </testsuite>",
            "  <testsuite name=\"Codeunit64535\" tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\" time=\"0.005\">",
            "    <testcase name=\"JoinWithLeftOuterJoin_ReturnsRows\" classname=\"Codeunit64535\" time=\"0.005\">",
            "      <failure message=\"Assert.AreEqual failed\">Assert.AreEqual failed</failure>",
            "    </testcase>",
            "  </testsuite>",
            "</testsuites>",
        });

        Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    /// <summary>
    /// A partial run's document is still valid JUnit that JUnitCounts can read back — the
    /// --jobs aggregation path (#2280) sums `testsuite` attributes out of exactly this file, so a
    /// representation that broke parsing, or that hid a failure from the sum, would silently
    /// shrink a parallel run's reported totals.
    /// </summary>
    [Fact]
    public void APartialRunsDocument_StillReadsBackWithTheSameTotals()
    {
        var path = P("partial.xml");
        JUnitReport.WriteJUnit(path, new[] { PartialBucketWithFailures() });

        var totals = Infrastructure.JUnitCounts.Read(path);

        Assert.Equal(3, totals.Tests);
        Assert.Equal(1, totals.Failures);
        Assert.Equal(1, totals.Errors);
        Assert.Equal(0, totals.Skipped);
    }
}
