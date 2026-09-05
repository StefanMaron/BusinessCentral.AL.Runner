// ResumeCarryTests — what a watchdog-resume attempt hands to the next process (#2719).
//
// The point of carrying full results rather than rebuilding them from the JUnit the summary
// already carries is that a JUnit <testcase> has a name and a status and nothing else. These
// tests pin the fields that make that difference — above all Expectation, which is what decides
// whether a failure is a real `fail` or a pass-known-gap / pass-oos / pass-divergence. A carried
// case that lost it would be classified as an UNEXPECTED failure by --out, turning a silently
// missing error into a confidently wrong one.
//
// They also pin what deliberately does NOT cross: a live Exception object, and CapturedValues
// (server `execute` only). Being explicit about the crossing set is the reason this DTO exists
// instead of making TestResult serialisable, where both would have ridden along unnoticed.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeCarryTests : IDisposable
{
    private readonly string _dir = TestScratch.Dir("al-runner-resume-carry-tests");

    public ResumeCarryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => ScratchDirs.Release(_dir);

    private static BucketResult Bucket(params TestResult[] tests)
        => new("/bundle/x", BucketStage.Ran, new[] { "compile note" }, null, tests,
               TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
               RanGroupCount: 7, ProvisionGaps: new[] { "gap" });

    [Fact]
    public void RoundTrip_KeepsTheFieldsAJUnitCaseCannotCarry()
    {
        var path = Path.Combine(_dir, "attempt.json");
        var original = Bucket(
            new TestResult("Codeunit1", "KnownGap", TestOutcome.Fail, "boom", "full-ex",
                TimeSpan.FromMilliseconds(1234), AlCallStack: "al-stack",
                CodeunitDisplayName: "Nice Name", Exception: new InvalidOperationException("live"),
                Expectation: ExpectationResult.PassKnownGap, InsideTestProc: false, TimedOut: true,
                CapturedValues: null, Diagnosis: "no rows in table X"));

        ResumeCarry.Write(path, new[] { original });
        var back = ResumeCarry.Read(new[] { path }, out var unreadable);

        Assert.Equal(0, unreadable);
        var t = Assert.Single(Assert.Single(back).Tests);
        // The three the issue named as unavailable from JUnit:
        Assert.Equal(ExpectationResult.PassKnownGap, t.Expectation);
        Assert.Equal("no rows in table X", t.Diagnosis);
        Assert.Equal("Nice Name", t.CodeunitDisplayName);
        // And the rest of what a consumer reads.
        Assert.Equal("Codeunit1", t.Codeunit);
        Assert.Equal("KnownGap", t.Method);
        Assert.Equal(TestOutcome.Fail, t.Outcome);
        Assert.Equal("boom", t.Message);
        Assert.Equal("full-ex", t.FullException);
        Assert.Equal("al-stack", t.AlCallStack);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), t.Duration);
        Assert.False(t.InsideTestProc);
        Assert.True(t.TimedOut);
    }

    [Fact]
    public void RoundTrip_DoesNotResurrectTheLiveException_NorCapturedValues()
    {
        // FullException already carries the text, which is everything a later process can use.
        // Serialising an exception graph to rebuild an object in another process would be a
        // fiction dressed as fidelity.
        var path = Path.Combine(_dir, "attempt.json");
        ResumeCarry.Write(path, new[] { Bucket(
            new TestResult("Codeunit1", "Boom", TestOutcome.Error, "m", "the text",
                TimeSpan.Zero, Exception: new InvalidOperationException("live"))) });

        var t = Assert.Single(Assert.Single(ResumeCarry.Read(new[] { path }, out _)).Tests);

        Assert.Null(t.Exception);
        Assert.Null(t.CapturedValues);
        Assert.Equal("the text", t.FullException);
    }

    [Fact]
    public void RoundTrip_KeepsBucketLevelFacts()
    {
        // The bucket's own CompileErrors are how a watchdog abort is recorded, and they are what
        // makes a resumed run's exit code describe the run rather than the last slice.
        var path = Path.Combine(_dir, "attempt.json");
        ResumeCarry.Write(path, new[] { Bucket() });

        var b = Assert.Single(ResumeCarry.Read(new[] { path }, out _));

        Assert.Equal("/bundle/x", b.BucketPath);
        Assert.Equal(BucketStage.Ran, b.Stage);
        Assert.Equal(new[] { "compile note" }, b.CompileErrors);
        Assert.Equal(7, b.RanGroupCount);
        Assert.Equal(new[] { "gap" }, b.ProvisionGaps);
        Assert.Equal(TimeSpan.FromSeconds(1), b.EmitTime);
        Assert.Equal(TimeSpan.FromSeconds(2), b.CompileTime);
        Assert.Equal(TimeSpan.FromSeconds(3), b.RunTime);
    }

    [Fact]
    public void ManyFiles_AreConcatenatedInOrder()
    {
        // One file per attempt, so a chain of resumes reads back as the chain — never with an
        // earlier attempt folded in twice, which is the rule the JUnit carry file follows too.
        var a = Path.Combine(_dir, "a.json");
        var b = Path.Combine(_dir, "b.json");
        ResumeCarry.Write(a, new[] { Bucket(new TestResult("C1", "First", TestOutcome.Pass, null, null, TimeSpan.Zero)) });
        ResumeCarry.Write(b, new[] { Bucket(new TestResult("C2", "Second", TestOutcome.Pass, null, null, TimeSpan.Zero)) });

        var back = ResumeCarry.Read(new[] { a, b }, out var unreadable);

        Assert.Equal(0, unreadable);
        Assert.Equal(new[] { "First", "Second" }, back.SelectMany(x => x.Tests).Select(t => t.Method).ToArray());
    }

    [Fact]
    public void AMissingOrCorruptFile_ContributesNothing_AndIsCounted()
    {
        // Counted, not swallowed: the caller prints the count, because the difference between
        // "the run had one error" and "the run was clean" must never turn on a scratch file
        // quietly going missing (#2747 is the lifetime hazard that makes this reachable).
        var good = Path.Combine(_dir, "good.json");
        var corrupt = Path.Combine(_dir, "corrupt.json");
        ResumeCarry.Write(good, new[] { Bucket(new TestResult("C1", "Kept", TestOutcome.Pass, null, null, TimeSpan.Zero)) });
        File.WriteAllText(corrupt, "{ not json at all");

        var back = ResumeCarry.Read(
            new[] { good, corrupt, Path.Combine(_dir, "does-not-exist.json") }, out var unreadable);

        Assert.Equal(2, unreadable);
        Assert.Equal(new[] { "Kept" }, back.SelectMany(x => x.Tests).Select(t => t.Method).ToArray());
    }

    [Fact]
    public void NoFiles_ReadsEmpty_SoANonResumedRunIsUnchanged()
    {
        Assert.Empty(ResumeCarry.Read(Array.Empty<string>(), out var unreadable));
        Assert.Equal(0, unreadable);
    }
}
