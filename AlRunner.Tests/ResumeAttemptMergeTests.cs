// ResumeAttemptMergeTests — ResumeCarry.MergeAttempts, the one bucket per bundle a resumed run's
// --output-json, --out and --count-out read (#5273).
//
// Every attempt reports each bundle. What the merge keeps is the DISTINCT things: a suite error two
// attempts both report is one, an app group every attempt enters is one, and nothing only one attempt
// saw is lost. So each test that shows something collapsing has a control beside it showing the
// neighbouring thing that must not. docs/watchdog-resume-reporting.md has the rule and the reasons.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ResumeAttemptMergeTests : IDisposable
{
    private readonly string _dir = TestScratch.Dir("al-runner-resume-attempt-merge-tests");

    public ResumeAttemptMergeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => ScratchDirs.Release(_dir);

    private static BucketResult B(string path, int groups = 1, BucketStage stage = BucketStage.Ran,
        string[]? errors = null, TestResult[]? tests = null, double seconds = 1,
        string[]? gaps = null, string? processError = null, CompanyInitFailure[]? init = null)
        => new(path, stage, errors ?? Array.Empty<string>(), processError, tests ?? Array.Empty<TestResult>(),
            TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds),
            groups, gaps, init);

    private static TestResult T(string codeunit, string method, TestOutcome o = TestOutcome.Pass)
        => new(codeunit, method, o, null, null, TimeSpan.Zero, null, null, null);

    private static List<BucketResult> Merge(params BucketResult[][] attempts)
        => ResumeCarry.MergeAttempts(attempts.Select(a => (IReadOnlyList<BucketResult>)a).ToList());

    [Fact]
    public void ARunThatNeverResumed_IsReturnedUnchanged()
    {
        var b = B("/x/a", groups: 2, errors: new[] { "E", "E" }, tests: new[] { T("C", "M") }, gaps: new[] { "g" });
        var merged = Merge(new[] { b });
        Assert.Same(b, Assert.Single(merged));
    }

    [Fact]
    public void ASuiteErrorEveryAttemptReports_IsOne_AndOneOnlyAnAttemptReports_IsKept()
    {
        var merged = Merge(
            new[] { B("/x/a", errors: new[] { "drop", "abort-1" }) },
            new[] { B("/x/a", errors: new[] { "drop", "abort-2" }) },
            new[] { B("/x/a", errors: new[] { "drop" }) });
        // In order of first appearance: the first attempt's own two, then what only the second added.
        Assert.Equal(new[] { "drop", "abort-1", "abort-2" }, Assert.Single(merged).CompileErrors);
    }

    [Fact]
    public void ABundleThatFailsInEveryAttemptForDifferentReasons_KeepsEveryReason()
    {
        var merged = Merge(
            new[] { B("/x/a", stage: BucketStage.CompileFailed, errors: new[] { "COMPILE-FAIL: CS0246" }) },
            new[] { B("/x/a", stage: BucketStage.CompileFailed, errors: new[] { "COMPILE-FAIL: CS0103" }) });
        var b = Assert.Single(merged);
        Assert.Equal(BucketStage.CompileFailed, b.Stage);
        Assert.Equal(new[] { "COMPILE-FAIL: CS0246", "COMPILE-FAIL: CS0103" }, b.CompileErrors);
    }

    [Fact]
    public void ABundleThatOnlyFailsInTheLaterAttempt_KeepsItsError()
    {
        var merged = Merge(
            new[] { B("/x/a") },
            new[] { B("/x/a", errors: new[] { "abort-2" }) });
        Assert.Equal(new[] { "abort-2" }, Assert.Single(merged).CompileErrors);
    }

    [Fact]
    public void ARepeatWithinOneAttempt_StaysRepeated_WhicheverAttemptHasMore()
    {
        // The count is the most one attempt reported: neither the first's, the last's, their sum nor a set.
        Assert.Equal(new[] { "E", "E" }, Merge(new[] { B("/x/a", errors: new[] { "E", "E" }) },
            new[] { B("/x/a", errors: new[] { "E" }) }).Single().CompileErrors);
        Assert.Equal(new[] { "E", "E" }, Merge(new[] { B("/x/a", errors: new[] { "E" }) },
            new[] { B("/x/a", errors: new[] { "E", "E" }) }).Single().CompileErrors);
        Assert.Equal(new[] { "E", "E", "E" }, Merge(new[] { B("/x/a", errors: new[] { "E" }) },
            new[] { B("/x/a", errors: new[] { "E", "E", "E" }) }, new[] { B("/x/a", errors: new[] { "E", "E" }) })
            .Single().CompileErrors);
    }

    [Fact]
    public void AppGroups_AreTheMostAnAttemptEntered_NotTheSumTheFirstOrTheLast()
    {
        Assert.Equal(3, Merge(new[] { B("/x/a", groups: 2) }, new[] { B("/x/a", groups: 3) },
            new[] { B("/x/a", groups: 1) }).Single().RanGroupCount);
        Assert.Equal(2, Merge(new[] { B("/x/a", groups: 2) }, new[] { B("/x/a", groups: 1) }).Single().RanGroupCount);
        Assert.Equal(2, Merge(new[] { B("/x/a", groups: 1) }, new[] { B("/x/a", groups: 2) }).Single().RanGroupCount);
    }

    [Fact]
    public void TwoBundlesSharingALabel_AreTwoBundles_AndTheirGroupsAdd()
    {
        var merged = Merge(
            new[] { B("/one/tests", groups: 1, errors: new[] { "E" }), B("/two/tests", groups: 2, errors: new[] { "E" }) },
            new[] { B("/one/tests", groups: 1, errors: new[] { "E" }), B("/two/tests", groups: 2, errors: new[] { "E" }) });
        Assert.Equal(new[] { "/one/tests", "/two/tests" }, merged.Select(b => b.BucketPath));
        Assert.All(merged, b => Assert.Single(b.CompileErrors));
        // --count-out keys a suite by its directory name and adds buckets of one name: 1 + 2, not 2 (one bundle) or 6.
        var tally = CountOut.Tally(merged.Select(b => (b.BucketPath, b.Tests.Count, b.RanGroupCount)));
        Assert.Equal(3, tally["tests"].AppGroups);
    }

    [Fact]
    public void TheSameBundleNamedTwice_StaysTwoBuckets_EachMergedWithItsOwnCounterpart()
    {
        var merged = Merge(
            new[] { B("/x/a", errors: new[] { "E1" }), B("/x/a", errors: new[] { "E2" }) },
            new[] { B("/x/a", errors: new[] { "E1" }), B("/x/a", errors: new[] { "E2" }) });
        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { "E1" }, merged[0].CompileErrors);
        Assert.Equal(new[] { "E2" }, merged[1].CompileErrors);
    }

    [Fact]
    public void AttemptsThatDisagreeAboutTheStage_StayApart_SoNeitherIsReadAsTheOther()
    {
        var ran = B("/x/a", errors: new[] { "E" }, tests: new[] { T("C", "M") });
        var failed = B("/x/a", stage: BucketStage.CompileFailed, errors: new[] { "E" });
        var merged = Merge(new[] { ran }, new[] { failed });
        Assert.Equal(2, merged.Count);
        // The Ran bucket keeps its tests: a non-Ran stage would have stopped --output-json listing them.
        Assert.Equal(new[] { BucketStage.Ran, BucketStage.CompileFailed }, merged.Select(b => b.Stage));
        Assert.Single(merged[0].Tests);
    }

    [Fact]
    public void TheTestsOfEveryAttempt_AreKept_InAttemptOrder()
    {
        var merged = Merge(
            new[] { B("/x/a", tests: new[] { T("C1", "A"), T("C1", "B", TestOutcome.Fail) }) },
            new[] { B("/x/a", tests: new[] { T("C2", "A", TestOutcome.Error) }) });
        Assert.Equal(new[] { "C1.A", "C1.B", "C2.A" }, Assert.Single(merged).Tests.Select(t => $"{t.Codeunit}.{t.Method}"));
    }

    [Fact]
    public void TheTimesOfEveryAttempt_Add()
    {
        var b = Merge(new[] { B("/x/a", seconds: 1) }, new[] { B("/x/a", seconds: 2) }).Single();
        Assert.Equal(TimeSpan.FromSeconds(3), b.EmitTime);
        Assert.Equal(TimeSpan.FromSeconds(3), b.CompileTime);
        Assert.Equal(TimeSpan.FromSeconds(3), b.RunTime);
    }

    [Fact]
    public void ProvisionGaps_AreTheUnion_AndStayAbsentWhenNoAttemptHadAny()
    {
        Assert.Equal(new[] { "g1", "g2" }, Merge(new[] { B("/x/a", gaps: new[] { "g1" }) },
            new[] { B("/x/a", gaps: new[] { "g1", "g2" }) }).Single().ProvisionGaps);
        Assert.Null(Merge(new[] { B("/x/a") }, new[] { B("/x/a") }).Single().ProvisionGaps);
    }

    [Fact]
    public void ProcessErrors_AreKeptDistinct()
    {
        Assert.Equal("boom; bang", Merge(new[] { B("/x/a", processError: "boom") },
            new[] { B("/x/a", processError: "boom") }, new[] { B("/x/a", processError: "bang") }).Single().ProcessError);
        Assert.Null(Merge(new[] { B("/x/a") }, new[] { B("/x/a") }).Single().ProcessError);
    }

    [Fact]
    public void ACompanyInitAbortEveryAttemptReports_IsOne_WithTheMostAppGroupsAnAttemptGaveIt()
    {
        var abort = new CompanyInitFailure(2, "Company-Initialize", "NullReferenceException", "boom", Count: 2);
        var other = new CompanyInitFailure(3, "Other", "InvalidOperationException", "bang");
        var merged = Merge(
            new[] { B("/x/a", init: new[] { abort }) },
            new[] { B("/x/a", init: new[] { abort with { Count = 3, AcceptedReason = "known" }, other }) }).Single();
        Assert.Equal(new[] { (2, 3, "known"), (3, 1, (string?)null) },
            merged.CompanyInitFailures!.Select(f => (f.CodeunitId, f.Count, f.AcceptedReason)));
        Assert.Null(Merge(new[] { B("/x/a") }, new[] { B("/x/a") }).Single().CompanyInitFailures);
    }

    [Fact]
    public void ReadAttempts_GivesOneListPerReadableFile_AndAMissingFileContributesNone()
    {
        var one = Path.Combine(_dir, "one.json");
        var two = Path.Combine(_dir, "two.json");
        ResumeCarry.Write(one, new[] { B("/x/a", tests: new[] { T("C", "A") }), B("/x/b") });
        ResumeCarry.Write(two, new[] { B("/x/a", tests: new[] { T("C", "B") }) });

        var attempts = ResumeCarry.ReadAttempts(new[] { one, Path.Combine(_dir, "missing.json"), two }, out var unreadable);
        Assert.Equal(1, unreadable);
        Assert.Equal(new[] { 2, 1 }, attempts.Select(a => a.Count));
        // Read is the same buckets flattened, as every earlier caller saw them.
        var flat = ResumeCarry.Read(new[] { one, two }, out _);
        Assert.Equal(new[] { "/x/a", "/x/b", "/x/a" }, flat.Select(b => b.BucketPath));

        var merged = ResumeCarry.MergeAttempts(attempts.Select(a => (IReadOnlyList<BucketResult>)a).ToList());
        Assert.Equal(new[] { "C.A", "C.B" }, merged[0].Tests.Select(t => $"{t.Codeunit}.{t.Method}"));
    }
}
