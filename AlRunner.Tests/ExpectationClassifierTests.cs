// ExpectationClassifierTests — the matcher semantics of tests/expectations/.
//
// Issue #1743: expect-oos matched on the EXCEPTION TYPE only, so no Cecil-injected
// out-of-scope surface (HTTP egress, RDLC rendering, …) could ever be declared —
// injected IL cannot construct our typed RunnerOutOfScopeException, it carries the
// documented `out-of-scope: <api> — <reason> — see docs/scope.md#<anchor>` message
// instead. expect-oos now recognises both shapes.
//
// Issue #1741: nothing in the manifest could say "the runner INTENDS to answer
// differently from BC here" (docs/scope.md §3.6 task scheduler), so an intended,
// permanent divergence was filed as expect-fail-known-gap against a CLOSED issue.
// expect-divergence says it honestly.
//
// The manifest is test infrastructure, so the load-bearing assertions here are the
// NEGATIVE ones: a wrong reason, a near-miss reason, a non-OOS exception and a
// still-passing test must all keep failing the run. Widening the matcher must not
// turn it into something that says yes to everything.

using AlRunner.Infrastructure;
using Xunit;

// AlRunner.TestOutcome (the pass/fail enum) shadows the classifier's input record of
// the same name; alias it so the tests read the way the classifier signature does.
using ObservedOutcome = AlRunner.Infrastructure.TestOutcome;

namespace AlRunner.Tests;

public class ExpectationClassifierTests
{
    private const string File = "oos-fixture.json";

    private static ExpectationEntry Oos(string reason) => new(
        60000, "Cu", "M", ExpectationMode.ExpectOos, reason, null, null, null, File);

    private static ExpectationEntry Divergence(string reason) => new(
        60000, "Cu", "M", ExpectationMode.ExpectDivergence, reason, null,
        "docs/scope.md#jobs", null, File);

    private static ObservedOutcome Failed(Exception ex) => new("Cu", "M", Passed: false, ex);
    private static ObservedOutcome Passed() => new("Cu", "M", Passed: true, null);

    // The exact strings the runner emits today, so a throw-site edit that breaks the
    // convention breaks these tests rather than silently un-declaring a surface.
    private const string HttpMsg =
        "out-of-scope: HttpClient.Get — external-http — see docs/scope.md#external-http";
    private const string RdlcMsg =
        "out-of-scope: ReportResultSetProcessorFactory.GetRdlcResultSetProcessor — "
        + "report-rendering-external — RDLC layout processing requires an external renderer "
        + "— see docs/scope.md#report-rendering";

    // ── message-convention parsing ────────────────────────────────────────────

    [Fact]
    public void Parse_HttpEgressMessage_SplitsApiFromReason()
    {
        Assert.True(OutOfScopeMessage.TryParse(HttpMsg, out var s));
        Assert.Equal("HttpClient.Get", s.Api);
        Assert.Equal("external-http", s.Reason);
        Assert.False(s.Typed);
    }

    [Fact]
    public void Parse_RdlcMessage_KeepsAnchorFirstAndDropsTheScopeLink()
    {
        Assert.True(OutOfScopeMessage.TryParse(RdlcMsg, out var s));
        Assert.Equal("ReportResultSetProcessorFactory.GetRdlcResultSetProcessor", s.Api);
        Assert.Equal(
            "report-rendering-external — RDLC layout processing requires an external renderer",
            s.Reason);
    }

    [Fact]
    public void Parse_PlainMessage_IsNotAnOutOfScopeSignal()
    {
        Assert.False(OutOfScopeMessage.TryParse("boom", out _));
        Assert.False(OutOfScopeMessage.TryParse("scope: HttpClient.Get — external-http", out _));
        Assert.Null(OutOfScopeMessage.FromException(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Parse_TypedExceptionWins_AndCarriesTheTypedFlag()
    {
        var typed = new RunnerOutOfScopeException("NavEmail.Send", "email-smtp", "email");
        var wrapped = new InvalidOperationException("wrapper", typed);
        var s = OutOfScopeMessage.FromException(wrapped);
        Assert.NotNull(s);
        Assert.Equal("NavEmail.Send", s!.Value.Api);
        Assert.Equal("email-smtp", s.Value.Reason);
        Assert.True(s.Value.Typed);
    }

    [Fact]
    public void Parse_MessageConventionIsFoundThroughAWrappingException()
    {
        var s = OutOfScopeMessage.FromException(
            new Exception("AL error", new InvalidOperationException(HttpMsg)));
        Assert.NotNull(s);
        Assert.Equal("external-http", s!.Value.Reason);
    }

    // ── expect-oos: the widened positive ──────────────────────────────────────

    [Fact]
    public void ExpectOos_CecilInjectedMessage_MatchingReason_IsPassOos()
    {
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(HttpMsg)), Oos("external-http"));
        Assert.Equal(ExpectationResult.PassOos, c.Result);
        Assert.Null(c.Diagnostic);
    }

    [Fact]
    public void ExpectOos_CecilInjectedMessage_WithFreeTextAfterTheAnchor_IsPassOos()
    {
        // Throw sites may append detail after a further em-dash; the entry holds
        // only the anchor.
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(RdlcMsg)), Oos("report-rendering-external"));
        Assert.Equal(ExpectationResult.PassOos, c.Result);
    }

    [Fact]
    public void ExpectOos_TypedException_MatchingReason_StillPassOos()
    {
        var c = ExpectationClassifier.Classify(
            Failed(new RunnerOutOfScopeException("NavEmail.Send", "email-smtp", "email")),
            Oos("email-smtp"));
        Assert.Equal(ExpectationResult.PassOos, c.Result);
    }

    // ── expect-oos: the negatives that keep the widening honest ───────────────

    [Fact]
    public void ExpectOos_WrongReason_StillFails()
    {
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(HttpMsg)), Oos("email-smtp"));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("Expected OOS reason 'email-smtp'", c.Diagnostic);
        Assert.Contains("runner threw reason 'external-http'", c.Diagnostic);
    }

    [Theory]
    // A prefix of the real anchor must not match…
    [InlineData("external-htt")]
    // …nor must the real anchor be a prefix of the entry's…
    [InlineData("external-https")]
    // …nor a substring anywhere in it.
    [InlineData("http")]
    [InlineData("EXTERNAL-HTTP")]
    public void ExpectOos_NearMissReason_DoesNotMatch(string nearMiss)
    {
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(HttpMsg)), Oos(nearMiss));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains($"Expected OOS reason '{nearMiss}'", c.Diagnostic);
    }

    [Fact]
    public void ExpectOos_PlainInvalidOperationException_IsNotAbsorbedAsOos()
    {
        // The whole risk of matching on a message prefix: an ordinary
        // InvalidOperationException must NOT be read as an out-of-scope throw just
        // because expect-oos now accepts an untyped exception.
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException("Sequence contains no elements")),
            Oos("external-http"));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("no out-of-scope signal", c.Diagnostic);
        Assert.Contains("InvalidOperationException", c.Diagnostic);
    }

    [Fact]
    public void ExpectOos_MessageWithoutAReasonSlot_CannotMatchAnyEntry()
    {
        // "out-of-scope: <api>" with no reason is still recognised as OOS (so the
        // no-entry path says "add an entry") but can never satisfy an entry —
        // the throw site has to name a docs/scope.md anchor first.
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException("out-of-scope: NavReport.RunRequestPage")),
            Oos("report-rendering"));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("Expected OOS reason 'report-rendering'", c.Diagnostic);
    }

    [Fact]
    public void ExpectOos_TestNowPasses_IsDrift()
    {
        var c = ExpectationClassifier.Classify(Passed(), Oos("external-http"));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("runner now supports this surface", c.Diagnostic);
    }

    [Fact]
    public void NoEntry_CecilInjectedOos_DemandsAnEntry()
    {
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(HttpMsg)), null);
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("Add an expect-oos entry", c.Diagnostic);
    }

    [Fact]
    public void NoEntry_PlainFailure_StaysAPlainFail()
    {
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException("boom")), null);
        Assert.Equal(ExpectationResult.Fail, c.Result);
        Assert.Null(c.Diagnostic);
    }

    // ── expect-divergence (#1741) ─────────────────────────────────────────────

    [Fact]
    public void ExpectDivergence_TestFails_IsPassDivergence()
    {
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(
                "You do not have permission to create or run scheduled tasks.")),
            Divergence("task-scheduler-create-task"));
        Assert.Equal(ExpectationResult.PassDivergence, c.Result);
        Assert.Null(c.Diagnostic);
    }

    [Fact]
    public void ExpectDivergence_TestNowPasses_IsDrift()
    {
        // Same drift rule as every other mode: the entry claims a standing
        // divergence, so the test agreeing with BC again means the entry is a lie.
        var c = ExpectationClassifier.Classify(Passed(), Divergence("task-scheduler-create-task"));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("Remove the entry", c.Diagnostic);
        Assert.Contains("no longer diverges from BC", c.Diagnostic);
    }

    [Fact]
    public void ExpectDivergence_OutOfScopeThrow_MustBeDeclaredExpectOosInstead()
    {
        // Divergence must not become a catch-all that quietly absorbs new OOS
        // surfaces expect-oos is supposed to declare.
        var c = ExpectationClassifier.Classify(
            Failed(new InvalidOperationException(HttpMsg)),
            Divergence("task-scheduler-create-task"));
        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("Declare it expect-oos", c.Diagnostic);
    }

    // ── a corrupt dependency package may never be declared expected (#3241) ───

    private static BcAppSymbolReadException SymbolRead(Exception inner) =>
        new("/artifacts/Some.Publisher_Some App_1.0.0.0.app", "table symbols", inner);

    [Fact]
    public void ExpectOos_SymbolReadFailureWrappingAMatchingOosReason_IsDriftNotPassOos()
    {
        // THE RED, and why this type needed its own branch rather than riding on the shape
        // gap's structural unabsorbability: BcAppSymbolReadException wraps whatever failed the
        // read, and OutOfScopeMessage.FromException walks that chain to depth 16. So a
        // corrupt package whose inner failure happened to be an out-of-scope refusal with the
        // entry's own anchor classified as PassOos — the run went green over a dependency the
        // runner could not read at all.
        var c = ExpectationClassifier.Classify(
            Failed(SymbolRead(new RunnerOutOfScopeException(
                "HttpClient.Get", "external-http", "docs/scope.md#external-http"))),
            Oos("external-http"));

        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("could not read", c.Diagnostic);
        Assert.Contains("Some.Publisher_Some App_1.0.0.0.app", c.Diagnostic);
        Assert.Contains("table symbols", c.Diagnostic);
    }

    [Fact]
    public void ExpectOos_SymbolReadFailure_DiagnosticDoesNotAdviseRaisingAnOosRefusal()
    {
        // With an ordinary inner failure the entry already could not absorb it — what was
        // wrong was the ADVICE. The no-signal branch tells the author to make the throw site
        // raise RunnerOutOfScopeException, which is exactly wrong for a package the runner
        // cannot read: nothing about the throw site is at fault.
        var c = ExpectationClassifier.Classify(
            Failed(SymbolRead(new InvalidOperationException(
                "The requested operation requires an element of type 'String'."))),
            Oos("external-http"));

        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.DoesNotContain("raise RunnerOutOfScopeException", c.Diagnostic);
        Assert.Contains("Repair or re-provision", c.Diagnostic);
    }

    [Fact]
    public void ExpectDivergence_SymbolReadFailure_IsDriftNotPassDivergence()
    {
        var c = ExpectationClassifier.Classify(
            Failed(SymbolRead(new InvalidOperationException("unreadable"))),
            Divergence("task-scheduler-create-task"));

        Assert.Equal(ExpectationResult.FailManifestDrift, c.Result);
        Assert.Contains("no answer at all", c.Diagnostic);
    }

    [Fact]
    public void ExpectFailKnownGap_SymbolReadFailure_StillAbsorbs()
    {
        // The control on the two refusals above: expect-fail-known-gap means "must fail, and
        // this open issue tracks it", so it is unaffected — a fix that refused the type
        // everywhere would fail here.
        var known = new ExpectationEntry(
            60000, "Cu", "M", ExpectationMode.ExpectFailKnownGap, null, "#3241", null, null, File);
        var c = ExpectationClassifier.Classify(
            Failed(SymbolRead(new InvalidOperationException("unreadable"))), known);

        Assert.Equal(ExpectationResult.PassKnownGap, c.Result);
    }

    [Fact]
    public void NoEntry_SymbolReadFailureWrappingAnOosRefusal_IsAPlainFailNotUndeclaredOos()
    {
        // The undeclared-OOS branch would tell a reviewer to add an expect-oos entry for a
        // surface that was never touched.
        var c = ExpectationClassifier.Classify(
            Failed(SymbolRead(new RunnerOutOfScopeException(
                "HttpClient.Get", "external-http", "docs/scope.md#external-http"))),
            null);

        Assert.Equal(ExpectationResult.Fail, c.Result);
        Assert.Null(c.Diagnostic);
    }
}
