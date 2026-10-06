// #4920: a control's AutoFormatExpression and CaptionClass expression are evaluated when a row is
// populated, so an error they raise fails the page open (or tears the page down on a move onto the
// row) instead of surfacing at the read of that control. The BC half, measured on every cloud leg, is
// corpus codeunits "AFT Expression Timing Tests" and "AFS Resolver Failure Tests" (autoformat/).
// These pin the runner's own mechanism: which source-expression keys count, that the open-time
// snapshot no longer evaluates them (and so cannot absorb their failure), and that the failure reaches
// the test as itself.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class RowExpressionOpenFailureTests
{
    private sealed class CountingExpression(Func<NavValue> get)
    {
        public int Gets;
        public NavValue Get() { Gets++; return get(); }
        public void Set(NavValue _) { }
    }

    private static RunnerPageInstance BuildPage(Dictionary<string, object?> expressions)
    {
        var ctor = typeof(RunnerPageInstance).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(object), typeof(object), typeof(NavRecord), typeof(int), typeof(System.Collections.IDictionary) },
            modifiers: null)
            ?? throw new InvalidOperationException("RunnerPageInstance private ctor not found.");
        return (RunnerPageInstance)ctor.Invoke(new object?[] { new object(), new object(), null, 4920001, expressions });
    }

    [Theory]
    [InlineData("Control12_Format", true)]
    [InlineData("Control7_DynamicCaption", true)]
    [InlineData("Control12_Visible", false)]
    [InlineData("Control12_Editable", false)]
    [InlineData("Control_Format", false)]       // no control id
    [InlineData("Format", false)]
    [InlineData("Control12_FormatX", false)]
    [InlineData("MyControl12_Format", false)]
    public void IsRowExpressionKey_AcceptsOnlyBcsFormatAndDynamicCaptionKeys(string key, bool expected)
        => Assert.Equal(expected, RunnerPageInstance.IsRowExpressionKey(key));

    [Fact]
    public void RowExpressionKeys_AreBcsOwnKeyShapes()
    {
        // The two builders are what every control lookup uses; the predicate must accept exactly them.
        Assert.True(RunnerPageInstance.IsRowExpressionKey(RunnerPageInstance.FormatExpressionKey(31)));
        Assert.True(RunnerPageInstance.IsRowExpressionKey(RunnerPageInstance.DynamicCaptionExpressionKey(31)));
    }

    [Fact]
    public void Construction_DoesNotEvaluateARowExpression_AndEvaluateRowExpressionsDoes()
    {
        var format = new CountingExpression(() => new NavText("<Precision,3:3>"));
        var caption = new CountingExpression(() => new NavText("Caption"));
        var visible = new CountingExpression(() => new NavText("x"));
        var page = BuildPage(new Dictionary<string, object?>
        {
            ["Control1_Format"] = format,
            ["Control2_DynamicCaption"] = caption,
            ["Control3_Visible"] = visible,
        });

        // The construction-time snapshot is for Visible; a row expression it evaluated would run page AL
        // before OnOpenPage and with a blank record, and absorb whatever it raised.
        Assert.Equal(0, format.Gets);
        Assert.Equal(0, caption.Gets);
        Assert.Equal(1, visible.Gets);

        page.EvaluateRowExpressions();

        Assert.Equal(1, format.Gets);
        Assert.Equal(1, caption.Gets);
        Assert.Equal(1, visible.Gets);   // not a row expression: left alone
    }

    [Fact]
    public void EvaluateRowExpressions_RethrowsTheExpressionsOwnError_FromEitherKey()
    {
        foreach (var key in new[] { "Control1_Format", "Control1_DynamicCaption" })
        {
            var page = BuildPage(new Dictionary<string, object?>
            {
                [key] = new CountingExpression(() => throw new InvalidOperationException("RXO row expression failed")),
            });
            var ex = Assert.Throws<InvalidOperationException>(() => page.EvaluateRowExpressions());
            Assert.Equal("RXO row expression failed", ex.Message);
        }
    }

    [Fact]
    public void Snapshot_StillAbsorbsAFailingNonRowExpression()
    {
        // The catch the issue asked about stays for what BC was not measured to raise at open: a
        // Visible/Editable/Enabled expression that cannot be read at open must not cost the page its
        // snapshot.
        var snapshot = RunnerPageInstance.SnapshotExpressionValues(new System.Collections.Hashtable
        {
            ["Control1_Visible"] = new CountingExpression(() => throw new InvalidOperationException("RXO visible failed")),
        });
        Assert.Empty(snapshot);
    }

    private const string Al = """
        table 63470 "Rxo Row"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Amount; Decimal) { }
                field(3; Boom; Boolean) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 63470 "Rxo Failing Card"
        {
            PageType = Card;
            ApplicationArea = All;
            SourceTable = "Rxo Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { ApplicationArea = All; }
                    field(FailingCtl; Rec.Amount)
                    {
                        ApplicationArea = All;
                        AutoFormatType = 10;
                        AutoFormatExpression = FailingFormat();
                    }
                }
            }

            local procedure FailingFormat(): Text
            begin
                Error('RXO format expression failed');
            end;
        }

        page 63471 "Rxo Row Card"
        {
            PageType = Card;
            ApplicationArea = All;
            SourceTable = "Rxo Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { ApplicationArea = All; }
                    field(RowCtl; Rec.Amount)
                    {
                        ApplicationArea = All;
                        AutoFormatType = 10;
                        AutoFormatExpression = RowFormat();
                    }
                }
            }

            local procedure RowFormat(): Text
            begin
                if Rec.Boom then
                    Error('RXO row format failed for %1', Rec."No.");
                exit('<Precision,3:3><Standard Format,0>');
            end;
        }

        page 63472 "Rxo Caption Card"
        {
            PageType = Card;
            ApplicationArea = All;
            SourceTable = "Rxo Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { ApplicationArea = All; }
                    field(FailingCaptionCtl; Rec.Amount) { ApplicationArea = All; CaptionClass = FailingCaption(); }
                }
            }

            local procedure FailingCaption(): Text
            begin
                Error('RXO caption class expression failed');
            end;
        }

        page 63473 "Rxo Plain Card"
        {
            PageType = Card;
            ApplicationArea = All;
            SourceTable = "Rxo Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { ApplicationArea = All; }
                    field(TypeOnlyCtl; Rec.Amount) { ApplicationArea = All; AutoFormatType = 10; }
                }
            }
        }

        codeunit 63470 "Rxo Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Row: Record "Rxo Row";
            begin
                Row.DeleteAll();
                Row."No." := 'A';
                Row.Amount := 7;
                Row.Insert();
                Row."No." := 'B';
                Row.Amount := 8;
                Row.Boom := true;
                Row.Insert();
            end;

            local procedure Expect(Actual: Text; Wanted: Text)
            begin
                if Actual <> Wanted then
                    Error('WRONG: wanted [%1], got [%2]', Wanted, Actual);
            end;

            [Test]
            procedure FailingFormat_FailsTheOpen_AndLeavesThePageNotOpen()
            var
                P: TestPage "Rxo Failing Card";
                Shown: Text;
            begin
                Seed();
                asserterror P.OpenView();
                Expect(GetLastErrorText(), 'RXO format expression failed');
                asserterror Shown := P.NoCtl.Value();
                Expect(GetLastErrorText(), 'The TestPage is not open.');
            end;

            [Test]
            procedure FailingFormat_OnAnEmptyView_StillFailsTheOpen()
            var
                P: TestPage "Rxo Failing Card";
                Row: Record "Rxo Row";
            begin
                Row.DeleteAll();
                asserterror P.OpenView();
                Expect(GetLastErrorText(), 'RXO format expression failed');
            end;

            [Test]
            procedure FailingFormat_OpenNew_FailsTheOpen()
            var
                P: TestPage "Rxo Failing Card";
            begin
                Seed();
                asserterror P.OpenNew();
                Expect(GetLastErrorText(), 'RXO format expression failed');
            end;

            [Test]
            procedure FailingCaptionClass_FailsTheOpen()
            var
                P: TestPage "Rxo Caption Card";
            begin
                Seed();
                asserterror P.OpenView();
                Expect(GetLastErrorText(), 'RXO caption class expression failed');
            end;

            [Test]
            procedure RowFormat_OpenAtTheFailingRow_FailsTheOpen()
            var
                P: TestPage "Rxo Row Card";
                Row: Record "Rxo Row";
            begin
                Seed();
                Row.Get('A');
                Row.Delete();
                asserterror P.OpenView();
                Expect(GetLastErrorText(), 'RXO row format failed for B');
            end;

            [Test]
            procedure RowFormat_MovingOntoTheFailingRow_ReadsAsTheTestPageNotOpen()
            var
                P: TestPage "Rxo Row Card";
                Shown: Text;
            begin
                Seed();
                P.OpenView();
                asserterror P.GoToKey('B');
                Expect(GetLastErrorText(), 'The TestPage is not open.');
                asserterror Shown := P.NoCtl.Value();
                Expect(GetLastErrorText(), 'The TestPage is not open.');
            end;

            [Test]
            procedure RowFormat_OpeningOnARowThatDoesNotFail_Reads()
            var
                P: TestPage "Rxo Row Card";
                Shown: Text;
            begin
                Seed();
                P.OpenView();
                Shown := P.NoCtl.Value();
                P.Close();
                Expect(Shown, 'A');
            end;

            [Test]
            procedure TypeWithoutExpression_Opens()
            var
                P: TestPage "Rxo Plain Card";
                Shown: Text;
            begin
                Seed();
                P.OpenView();
                Shown := P.TypeOnlyCtl.Value();
                P.Close();
                Expect(Shown, '7.00');
            end;
        }
        """;

    [SkippableFact]
    public async Task ARaisingExpression_FailsTheOpen_AsItsOwnError()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-4920-row-expression-open-failure");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "Rxo", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "idRanges": [ { "from": 63470, "to": 63479 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Rxo.al"), Al);

        var r = await SuiteServer.RunViaServer(root);
        Assert.False(r.Tests.Any(t => t.Message.Contains("WRONG:")), r.ToString());
        Assert.True(r.Total == 8 && r.Passed == 8 && r.Failed == 0, r.ToString());
        Assert.Equal(0, r.ExitCode);
    }
}
