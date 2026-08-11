using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1641 (errorKind + stackFrames slice): the streaming <c>runTests</c>
/// <c>test</c> event must carry the two structured-diagnostics fields
/// <c>protocol-v2.schema.json</c> defines — <c>errorKind</c> (the
/// <see cref="ErrorClassifier"/> bucket) and <c>stackFrames</c> (the
/// <see cref="StackFrameMapper"/> parse of the captured AL call stack) — so the
/// VS Code extension can vary its failure UI and offer clickable frames instead
/// of re-parsing the free-form <c>stackTrace</c> string.
///
/// These drive <see cref="ServerProtocol.TestEvent"/> directly (no BC runtime
/// needed), so they run everywhere and pin the exact wire shape.
/// </summary>
public class ServerProtocolTestEventTests
{
    private const string SampleStack =
        "\"Sales-Post\"(CodeUnit 80).OnRun(Trigger) line 42 - Base Application by Microsoft version 26.0\n"
        + "\"My Test CU\"(CodeUnit 60200).PostIt line 7";

    private static JsonElement Event(TestResult t)
        => JsonSerializer.Deserialize<JsonElement>(ServerProtocol.TestEvent(t));

    private static TestResult Failing(Exception ex, string? alStack = SampleStack)
        => new("Codeunit60200", "PostIt", TestOutcome.Fail,
               $"{ex.GetType().Name}: {ex.Message}", ex.ToString(),
               TimeSpan.FromMilliseconds(5), alStack, "My Test CU", ex);

    // ── errorKind ────────────────────────────────────────────────────────────

    [Fact]
    public void PassingTest_HasNoErrorKindAndNoStackFrames()
    {
        var t = new TestResult("Codeunit60200", "Ok", TestOutcome.Pass,
            null, null, TimeSpan.FromMilliseconds(3), null, "My Test CU");

        var e = Event(t);
        Assert.Equal("pass", e.GetProperty("status").GetString());
        // A pass has no error to classify: emitting errorKind:"unknown" here would
        // read as "we failed and don't know why". The field must be absent.
        Assert.False(e.TryGetProperty("errorKind", out _));
        Assert.False(e.TryGetProperty("stackFrames", out _));
    }

    [Fact]
    public void FailureInsideTestProc_IsRuntime()
    {
        var e = Event(Failing(new InvalidOperationException("boom")));
        Assert.Equal("runtime", e.GetProperty("errorKind").GetString());
    }

    [Fact]
    public void CtorFailure_IsSetup()
    {
        // The `<ctor>` result TestExecutor produces when InstantiateCodeunit throws:
        // nothing inside a [Test] proc ever ran, so this is a setup failure, not a
        // test-runtime one.
        var ex = new InvalidOperationException("cannot instantiate");
        var t = new TestResult("Codeunit60200", "<ctor>", TestOutcome.Error,
            ex.Message, ex.ToString(), TimeSpan.Zero, null, null, ex,
            InsideTestProc: false);

        Assert.Equal("setup", Event(t).GetProperty("errorKind").GetString());
    }

    [Fact]
    public void TimedOutTest_IsTimeout()
    {
        // The timeout path carries no caught exception (the runaway thread is
        // abandoned, nothing was thrown back), so the TimedOut flag — not the
        // exception type — is what makes this classifiable.
        var t = new TestResult("Codeunit60200", "Hangs", TestOutcome.Error,
            "Test exceeded 60s timeout.", null, TimeSpan.FromSeconds(60),
            SampleStack, "My Test CU", null, TimedOut: true);

        Assert.Equal("timeout", Event(t).GetProperty("errorKind").GetString());
    }

    [Fact]
    public void ErrorWithoutException_IsUnknown()
    {
        // e.g. the "unsupported test signature" result — an error we produced
        // ourselves with no exception behind it. `unknown` is the honest answer.
        var t = new TestResult("Codeunit60200", "TakesArgs", TestOutcome.Error,
            "unsupported test signature (1 params)", null, TimeSpan.Zero, null, "My Test CU");

        Assert.Equal("unknown", Event(t).GetProperty("errorKind").GetString());
    }

    [Fact]
    public void SkippedTest_HasNoErrorKind()
    {
        var t = new TestResult("Codeunit60200", "Skipped", TestOutcome.Skipped,
            "skipped — declared in known-gaps.json", null, TimeSpan.Zero, null, "My Test CU",
            null, Infrastructure.ExpectationResult.Skipped);

        var e = Event(t);
        Assert.Equal("skipped", e.GetProperty("status").GetString());
        Assert.False(e.TryGetProperty("errorKind", out _));
    }

    // ── stackFrames ──────────────────────────────────────────────────────────

    [Fact]
    public void StackFrames_AreStructuredDeepestFirst_MatchingTheSchema()
    {
        var frames = Event(Failing(new InvalidOperationException("boom")))
            .GetProperty("stackFrames");

        Assert.Equal(2, frames.GetArrayLength());

        var deepest = frames[0];
        Assert.Equal("\"Sales-Post\"(CodeUnit 80).OnRun(Trigger)", deepest.GetProperty("name").GetString());
        Assert.Equal(42, deepest.GetProperty("line").GetInt32());
        Assert.Equal("normal", deepest.GetProperty("presentationHint").GetString());
        // File is unknown for a BC call-stack frame — the schema's `source` object
        // must be omitted rather than filled with an invented path.
        Assert.False(deepest.TryGetProperty("source", out _));
        Assert.False(deepest.TryGetProperty("column", out _));

        var caller = frames[1];
        Assert.Equal("\"My Test CU\"(CodeUnit 60200).PostIt", caller.GetProperty("name").GetString());
        Assert.Equal(7, caller.GetProperty("line").GetInt32());
    }

    [Fact]
    public void StackFrames_OmittedWhenNoAlCallStackWasCaptured()
    {
        // A runner-internal failure has a C# stackTrace but no AL stack. Emitting
        // an empty array would imply "AL stack captured, zero frames deep".
        var e = Event(Failing(new InvalidOperationException("boom"), alStack: null));
        Assert.False(e.TryGetProperty("stackFrames", out _));
        Assert.Contains("boom", e.GetProperty("stackTrace").GetString());
    }

    [Fact]
    public void StackFrames_KeepAnUnparsableLineVerbatimAsName()
    {
        var e = Event(Failing(new InvalidOperationException("boom"), alStack: "not a BC frame at all"));
        var frames = e.GetProperty("stackFrames");
        Assert.Equal(1, frames.GetArrayLength());
        Assert.Equal("not a BC frame at all", frames[0].GetProperty("name").GetString());
        Assert.False(frames[0].TryGetProperty("line", out _));
    }

    // ── the pre-existing fields must not have shifted ────────────────────────

    [Fact]
    public void ExistingTestEventFieldsAreUnchanged()
    {
        var e = Event(Failing(new InvalidOperationException("boom")));
        Assert.Equal("test", e.GetProperty("type").GetString());
        Assert.Equal("Codeunit60200.PostIt", e.GetProperty("name").GetString());
        Assert.Equal("fail", e.GetProperty("status").GetString());
        Assert.Equal(5, e.GetProperty("durationMs").GetInt64());
        Assert.Contains("boom", e.GetProperty("message").GetString());
        Assert.Equal(SampleStack, e.GetProperty("stackTrace").GetString());
    }
}
