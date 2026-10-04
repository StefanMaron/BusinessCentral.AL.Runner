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
            "--tdd: 2 test(s) reach generated stubs this run:",
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
        Assert.Equal("reaches generated stub(s): Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer", lines[fail + 2]);

        var pass = lines.FindIndex(l => l.Contains("DoubleIt_OfZero"));
        Assert.StartsWith("PASS", lines[pass]);
        Assert.StartsWith("reaches generated stub(s): Calc: procedure", lines[pass + 1]);

        var unrelated = lines.FindIndex(l => l.Contains("Unrelated_Passes"));
        Assert.DoesNotContain(lines.Skip(unrelated + 1), l => l.StartsWith("reaches generated", StringComparison.Ordinal));
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
        var guideText = System.Text.RegularExpressions.Regex.Replace(tddSection, @"\s+", " ");
        Assert.Contains("helpers, library codeunits, [HandlerFunctions] handlers, event subscribers of a publisher procedure declared in the same app", guideText); // follows calls, not just the [Test] body
        Assert.DoesNotContain("event subscribers are not followed", guideText);
        Assert.Contains("takes its return type from the other Variant argument", guideText); // #5146
        Assert.Contains("names its publisher by a bare object id adds no edge", guideText); // #5161
        Assert.Contains("generates an overload beside it", guideText); // #5228
        Assert.Contains("or through another source folder of the run (a test library between the app and its tests", guideText); // #5161
        Assert.Contains("The call that names the member can sit in a folder between the two, such as a test library", guideText); // #5243
        Assert.Contains("the library is reported as dropped (EMIT-EXCLUDED, with the AL diagnostic) and the run goes on", guideText); // #5243
        Assert.Contains("an event subscriber in one folder of a publisher in an earlier folder is followed back to what raises the publisher there", guideText); // #5264
        Assert.Contains("a publisher of a precompiled .app only when the test calls it itself", guideText); // #5264
        Assert.Contains("naming the object and its AL error rather than advising to provision a package", guideText); // #5266
        Assert.Contains("A member missing on a codeunit of the library that calls it is generated into the library's own compile", guideText); // #5271
        Assert.Contains("the run re-runs at most three times. Members generated by the pass that reaches that limit are never compiled in", guideText); // #5265
        Assert.Contains("A table operation starts what the table declares (#5286)", guideText); // #5286
        Assert.Contains("only when their RunTrigger argument is not omitted or false", guideText);
        Assert.Contains("a RecordRef, a FieldRef or a record that is not a variable (a procedure's return value) names none, so its operation counts for every table", guideText);
        Assert.Contains("Codeunit.Run reaches the OnRun of the codeunit named by Codeunit::Name", guideText);
        Assert.Contains("the triggers and events of a report, query or xmlport, a TestRequestPage, the page an action's RunObject opens, and a table operation, Codeunit.Run or page operation inside a precompiled .app that the test only calls", guideText);
        Assert.DoesNotContain("which is not followed", guideText);
        Assert.Contains("A page's own code is followed too (#5309)", guideText);
        Assert.Contains("OpenView, OpenEdit and OpenNew start OnInit, OnOpenPage, OnFindRecord, OnNextRecord, OnAfterGetRecord, OnAfterGetCurrRecord and OnNewRecord", guideText);
        Assert.Contains("Invoke on an action its OnAction, that action's and no other's", guideText);
        Assert.Contains("Page code that calls CurrPage.Update, SaveRecord or Close starts the row, save or close triggers of its page", guideText);
        Assert.Contains("Page.Run and Page.RunModal of a named page, and Run or RunModal on a Page variable, start every trigger of that page", guideText);
        Assert.Contains("a lookup, drill-down or assist-edit trigger by the Lookup, Drilldown or AssistEdit of a page control on that table", guideText);
        Assert.Contains("A TestPage writes records from the page runtime, so its calls count as table operations too (#5301): OpenNew and New as an Insert", guideText); // #5301
        Assert.Contains("typing into a field as a Validate, Insert, Modify and Rename, always running the triggers and raising the events", guideText);
        Assert.Contains("a control's SetValue, its Value with an argument, and an assignment to its Value (P.Qty.Value := x)", guideText);
        Assert.Contains("A control's Activate counts as an Insert", guideText);
        Assert.Contains("Reading a control (Value with no argument, AsInteger, AssertEquals, Caption, Editable, ...) starts nothing", guideText);
        Assert.Contains("a page or control with no table the compiler can read counts for every table", guideText);
        Assert.Contains("Close and the moves (GoToKey, GoToRecord, Next, First, ...) start nothing of their own", guideText);
        Assert.DoesNotContain("Inserting a record that a subscriber reacts to is such a path", guideText);
        Assert.Contains("its OnBefore and OnAfter Insert, Modify, Delete and Rename, and the OnBeforeValidate and OnAfterValidate of a modify() block, are started by the operation of that name", guideText);
        Assert.Contains("a trigger name this list does not know counts as started by every operation", guideText);
        Assert.DoesNotContain("ran against generated", tddSection); // "reaches": read from the code, not observed
        Assert.DoesNotContain("raises a distinctive error", tddSection);

        var help = new StringWriter();
        ProgramSupport.PrintHelp(help);
        Assert.Contains("empty body returning the default value",
            System.Text.RegularExpressions.Regex.Replace(help.ToString(), @"\s+", " "));

        var serverDoc = File.ReadAllText(Path.Combine(RepoRoot, "docs", "server-mode.md"));
        Assert.Contains("`generatedStubs` (#5147)", serverDoc);
        Assert.DoesNotContain("a test that ran against a stub is not a pass", serverDoc);
        Assert.Contains("a procedure the\n  test calls directly or transitively", serverDoc.Replace("\r\n", "\n"));
        var serverText = System.Text.RegularExpressions.Regex.Replace(serverDoc, @"\s+", " ");
        Assert.Contains("an `[EventSubscriber]` of a publisher procedure declared in the same app", serverText); // #5161
        Assert.DoesNotContain("Event subscribers and procedures in another `sourcePaths` bundle", serverText);
        Assert.Contains("takes its return type from the other `Variant` arguments", serverText); // #5146
        Assert.Contains("is named by an AL0132 (missing member) or AL0126 (missing overload)", serverText); // #5228
        Assert.Contains("names its publisher by a bare object id, not `Codeunit::\"Name\"`, adds no edge", serverText); // #5161
        Assert.Contains("generates an overload beside it", serverText); // #5228
        Assert.Contains("A procedure of another `sourcePaths` bundle is (a test library app compiled separately, #5161)", serverText);
        Assert.DoesNotContain("neither are procedures in another `sourcePaths` bundle", serverText);
        Assert.Contains("An `[EventSubscriber]` in one bundle of a publisher declared in an earlier bundle is followed too (#5264)", serverText);
        Assert.Contains("A table operation starts what the table declares (#5286)", serverText); // #5286
        Assert.Contains("only when their `RunTrigger` argument is not omitted or `false`", serverText);
        Assert.Contains("a `RecordRef`, a `FieldRef` or a record that is not a variable (a procedure's return value) names none, so its operation counts for every table", serverText);
        Assert.Contains("the triggers and events of a report, query or xmlport, a `TestRequestPage`, the page an action's `RunObject` opens, and a table operation, `Codeunit.Run` or page operation inside a precompiled `.app` that the test only calls", serverText);
        Assert.DoesNotContain("which is not followed", serverText);
        Assert.Contains("A page's own code is followed too (#5309)", serverText);
        Assert.Contains("`OpenView`, `OpenEdit` and `OpenNew` start `OnInit`, `OnOpenPage`, `OnFindRecord`, `OnNextRecord`, `OnAfterGetRecord`, `OnAfterGetCurrRecord` and `OnNewRecord`", serverText);
        Assert.Contains("`Invoke` on an action its `OnAction`, that action's and no other's", serverText);
        Assert.Contains("Page code that calls `CurrPage.Update`, `SaveRecord` or `Close` starts the row, save or close triggers of its page", serverText);
        Assert.Contains("`Page.Run` and `Page.RunModal` of a named page, and `Run` or `RunModal` on a `Page` variable, start every trigger of that page", serverText);
        Assert.Contains("a lookup, drill-down or assist-edit trigger by the `Lookup`, `Drilldown` or `AssistEdit` of a page control on that table", serverText);
        Assert.Contains("A `TestPage` writes records from the page runtime, so its calls count as table operations too (#5301): `OpenNew` and `New` as an `Insert`", serverText); // #5301
        Assert.Contains("typing into a field as a `Validate`, `Insert`, `Modify` and `Rename`, which always run the triggers and raise the events", serverText);
        Assert.Contains("a control's `SetValue`, its `Value` with an argument and an assignment to its `Value` (`P.Qty.Value := x`)", serverText);
        Assert.Contains("A control's `Activate` counts as an `Insert`", serverText);
        Assert.Contains("Reading a control (`Value` with no argument, `AsInteger`, `AssertEquals`, `Caption`, `Editable` and the like) starts nothing", serverText);
        Assert.Contains("a page or control with no table the compiler can read counts for every table", serverText);
        Assert.Contains("`Close` and the moves (`GoToKey`, `GoToRecord`, `Next`, `First` and the like) start nothing of their own", serverText);
        Assert.DoesNotContain("carries no `generatedStubs` (#5286)", serverText);
        Assert.Contains("its `OnBefore` and `OnAfter` `Insert`, `Modify`, `Delete` and `Rename`, and the `OnBeforeValidate` and `OnAfterValidate` of a `modify()` block, are started by the operation of that name", serverText);
        Assert.Contains("a trigger name this list does not know counts as started by every operation", serverText);
        Assert.Contains("A publisher of a precompiled dependency is followed only when the test calls the publisher procedure itself", serverText); // #5264
        Assert.Contains("A member missing on a codeunit of the library that calls it is generated into that library's own compile", serverText); // #5271
        Assert.Contains("a request makes at most three: the members generated by the pass that reaches that limit are never compiled in", serverText); // #5265
        Assert.Contains("names the dropped object and its AL error, not provisioning advice (#5266)", serverText);
        Assert.Contains("can sit in a bundle between the two, a test library the tests depend on: the member goes into the app all the same (#5243)", serverText);
        Assert.Contains("the library is reported as dropped (`EMIT-EXCLUDED`, with the AL diagnostic) and a test that reaches it fails where it does", serverText); // #5243

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "protocol-v2.schema.json")));
        Assert.True(schema.RootElement.GetProperty("definitions").GetProperty("TestEvent")
            .GetProperty("properties").TryGetProperty("generatedStubs", out _));
    }
}
