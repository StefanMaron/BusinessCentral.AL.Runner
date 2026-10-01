// #5147: --tdd keeps each test's own result and annotates the ones whose compile referenced a
// generated member. The in-process half of that contract — the annotation, the closing list and
// the three output shapes carrying it — pinned without a BC service tier; TddModeTests,
// TddTwoFolderTests, TddWatchTests and ServerTddTests drive it end to end.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddResultAnnotationTests
{
    private static readonly TddGeneratedMember CalcStub =
        new("Calc", "procedure", "\"DoubleIt\"(Arg1: Integer): Integer")
        {
            DependentTests = new[] { "Calc Tests.DoubleIt_ReturnsTwice", "Calc Tests.DoubleIt_OfZero" },
        };

    private static readonly TddGeneratedMember FieldStub =
        new("Member", "field", "\"Points\": Integer") { DependentTests = new[] { "Calc Tests.DoubleIt_OfZero" } };

    private static TestResult Result(string method, TestOutcome outcome, string? message = null) =>
        new("Codeunit50101", method, outcome, message, null, TimeSpan.FromMilliseconds(3),
            CodeunitDisplayName: "Calc Tests");

    private static TddDependents Dependents()
    {
        var d = new TddDependents();
        d.Add(new[] { CalcStub, FieldStub });
        return d;
    }

    [Fact]
    public void Apply_KeepsTheTestsOwnOutcomeAndMessage_AndNamesTheStubsItRanAgainst()
    {
        var failed = Result("DoubleIt_ReturnsTwice", TestOutcome.Fail, "DoubleIt returned 0");
        var annotated = Dependents().Apply(failed);

        Assert.Equal(TestOutcome.Fail, annotated.Outcome);
        Assert.Equal("DoubleIt returned 0", annotated.Message);
        Assert.Null(annotated.KnownErrorKind);
        Assert.Equal(new[] { "Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer" }, annotated.GeneratedStubs);

        var passed = Dependents().Apply(Result("DoubleIt_OfZero", TestOutcome.Pass));
        Assert.Equal(TestOutcome.Pass, passed.Outcome);
        Assert.Null(passed.Message);
        Assert.Equal(new[] { "Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer", "Member: field \"Points\": Integer" },
            passed.GeneratedStubs);
    }

    [Fact]
    public void Apply_LeavesATestThatReferencedNoGeneratedMemberUntouched()
    {
        var unrelated = Result("Unrelated_Passes", TestOutcome.Pass);
        Assert.Same(unrelated, Dependents().Apply(unrelated));
    }

    [Fact]
    public void SummaryLines_ListEveryAnnotatedTestWithItsResult_AndNothingWhenNoneIs()
    {
        var d = Dependents();
        var tests = new[]
        {
            d.Apply(Result("DoubleIt_ReturnsTwice", TestOutcome.Fail, "DoubleIt returned 0")),
            d.Apply(Result("DoubleIt_OfZero", TestOutcome.Pass)),
            d.Apply(Result("Unrelated_Passes", TestOutcome.Pass)),
        };

        Assert.Equal(new[]
        {
            "--tdd: 2 test(s) ran against generated stubs this run:",
            "  Calc Tests.DoubleIt_ReturnsTwice (fail): Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer",
            "  Calc Tests.DoubleIt_OfZero (pass): Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer; Member: field \"Points\": Integer",
        }, TddReport.SummaryLines(tests, "run"));

        Assert.Empty(TddReport.SummaryLines(new[] { Result("Unrelated_Passes", TestOutcome.Pass) }, "run"));
    }

    [Fact]
    public void ServerRequest_RecordsEachAnnotatedTestOnce_ForTheStderrSummary()
    {
        var request = new TddServerRequest(_ => { }) { Active = Dependents() };
        var streamed = request.Apply(Result("DoubleIt_OfZero", TestOutcome.Pass));
        request.Apply(Result("DoubleIt_OfZero", TestOutcome.Pass)); // the returned list re-applies it
        request.Apply(Result("Unrelated_Passes", TestOutcome.Pass));

        Assert.NotNull(streamed.GeneratedStubs);
        Assert.Equal("DoubleIt_OfZero", Assert.Single(request.RanAgainstStubs).Method);
    }

    [Fact]
    public void ServerTestLine_CarriesGeneratedStubs_OnlyWhenTheTestRanAgainstOne()
    {
        var annotated = Dependents().Apply(Result("DoubleIt_OfZero", TestOutcome.Pass));
        using (var doc = JsonDocument.Parse(ServerProtocol.TestEvent(annotated)))
        {
            Assert.Equal("pass", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("generatedStubs").GetArrayLength());
            Assert.Equal("Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer",
                doc.RootElement.GetProperty("generatedStubs")[0].GetString());
        }
        using (var doc = JsonDocument.Parse(ServerProtocol.TestEvent(Result("Unrelated_Passes", TestOutcome.Pass))))
            Assert.False(doc.RootElement.TryGetProperty("generatedStubs", out _));
    }

    private static BucketResult Bucket(params TestResult[] tests) =>
        new("/tdd", BucketStage.Ran, Array.Empty<string>(), null, tests,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    [Fact]
    public void OutputJson_CarriesGeneratedStubs_OnlyWhenTheTestRanAgainstOne()
    {
        var d = Dependents();
        var json = Reporter.SerializeJsonOutput(new[]
        {
            Bucket(d.Apply(Result("DoubleIt_ReturnsTwice", TestOutcome.Fail, "DoubleIt returned 0")),
                   d.Apply(Result("Unrelated_Passes", TestOutcome.Pass))),
        }, exitCode: 1);
        using var doc = JsonDocument.Parse(json);
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal("Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer",
            Assert.Single(tests[0].GetProperty("generatedStubs").EnumerateArray()).GetString());
        Assert.Equal("DoubleIt returned 0", tests[0].GetProperty("message").GetString());
        Assert.False(tests[1].TryGetProperty("generatedStubs", out _));
    }

    [Fact]
    public void ConsoleOutput_PrintsTheStubLineUnderAnnotatedTestsOnly()
    {
        var d = Dependents();
        var w = new StringWriter();
        Reporter.PrintPerTest(new[]
        {
            Bucket(d.Apply(Result("DoubleIt_ReturnsTwice", TestOutcome.Fail, "DoubleIt returned 0")),
                   d.Apply(Result("DoubleIt_OfZero", TestOutcome.Pass)),
                   d.Apply(Result("Unrelated_Passes", TestOutcome.Pass))),
        }, w, showPass: true);
        var lines = w.ToString().Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();

        var fail = lines.FindIndex(l => l.Contains("DoubleIt_ReturnsTwice"));
        Assert.Equal("DoubleIt returned 0", lines[fail + 1]);
        Assert.Equal("ran against generated stub(s): Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer", lines[fail + 2]);

        var pass = lines.FindIndex(l => l.Contains("DoubleIt_OfZero"));
        Assert.StartsWith("PASS", lines[pass]);
        Assert.StartsWith("ran against generated stub(s): Calc: procedure", lines[pass + 1]);

        var unrelated = lines.FindIndex(l => l.Contains("Unrelated_Passes"));
        Assert.DoesNotContain(lines.Skip(unrelated + 1), l => l.StartsWith("ran against", StringComparison.Ordinal));
    }

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>The behaviour the docs describe: --guide's TDD MODE, --help's --tdd entry, the
    /// server docs and the protocol schema all name the empty stub and the annotation, and none
    /// still describes the stub that raised an error.</summary>
    [Fact]
    public void Docs_DescribeEmptyStubsAndTheAnnotation()
    {
        var guide = new StringWriter();
        ProgramSupport.PrintGuide(guide);
        var tddSection = guide.ToString();
        tddSection = tddSection[tddSection.IndexOf("TDD MODE (--tdd)", StringComparison.Ordinal)..];
        Assert.Contains("EMPTY body", tddSection);
        Assert.Contains(TddReport.PerTestPrefix.TrimEnd(), tddSection);
        Assert.Contains("generatedStubs", tddSection);
        Assert.DoesNotContain("raises a distinctive error", tddSection);

        var help = new StringWriter();
        ProgramSupport.PrintHelp(help);
        Assert.Contains("empty body returning the default value",
            System.Text.RegularExpressions.Regex.Replace(help.ToString(), @"\s+", " "));

        var serverDoc = File.ReadAllText(Path.Combine(RepoRoot, "docs", "server-mode.md"));
        Assert.Contains("`generatedStubs` (#5147)", serverDoc);
        Assert.DoesNotContain("a test that ran against a stub is not a pass", serverDoc);

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "protocol-v2.schema.json")));
        Assert.True(schema.RootElement.GetProperty("definitions").GetProperty("TestEvent")
            .GetProperty("properties").TryGetProperty("generatedStubs", out _));
    }
}
