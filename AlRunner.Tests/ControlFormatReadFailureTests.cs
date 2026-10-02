// #3479: TryGetControlFormat evaluates the control's Control<id>_Format expression, which runs page
// AL (AutoFormatExpression). A failure there used to be swallowed into "no format declared". It
// now propagates, as TryGetControlCaptionClass's does, and GetValue unwraps the reflection
// wrapper so the AL error itself reaches the test. The BC half is corpus codeunit 67644.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class ControlFormatReadFailureTests
{
    private sealed class ThrowingExpression
    {
        public NavValue Get() => throw new InvalidOperationException("CFR getter failed");
    }

    [Fact]
    public void GetValue_RethrowsTheGettersOwnException_NotTheReflectionWrapper()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunnerPageInstance.GetValue(new ThrowingExpression()));
        Assert.Equal("CFR getter failed", ex.Message);
    }

    private const string Al = """
        table 63460 "Cfr Row"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Amount; Decimal) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 63460 "Cfr Card"
        {
            PageType = Card;
            SourceTable = "Cfr Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { }
                    field(PlainCtl; Rec.Amount) { }
                    field(FailingCtl; Rec.Amount)
                    {
                        AutoFormatType = 10;
                        AutoFormatExpression = FailingFormat();
                    }
                }
            }

            local procedure FailingFormat(): Text
            begin
                Error('CFR format expression failed');
            end;
        }

        codeunit 63461 "Cfr Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Row: Record "Cfr Row";
            begin
                Row.DeleteAll();
                Row."No." := 'CFR';
                Row.Amount := 7;
                Row.Insert();
            end;

            local procedure ReadFailing(): Text
            var
                Card: TestPage "Cfr Card";
            begin
                Card.OpenView();
                exit(Card.FailingCtl.Value());
            end;

            [Test]
            procedure FailingFormatExpression_ReachesTheTest()
            begin
                Seed();
                asserterror ReadFailing();
                if StrPos(GetLastErrorText(), 'CFR format expression failed') = 0 then
                    Error('WRONG: the format expression''s error did not reach the test; got [%1]', GetLastErrorText());
            end;

            [Test]
            procedure ControlWithoutFormatExpression_ReadsItsValue()
            var
                Card: TestPage "Cfr Card";
                Shown: Decimal;
            begin
                Seed();
                Card.OpenView();
                Evaluate(Shown, Card.PlainCtl.Value());
                Card.Close();
                if Shown <> 7 then
                    Error('WRONG: the plain control read %1', Shown);
            end;
        }
        """;

    [SkippableFact]
    public async Task FailingFormatExpression_ErrorReachesTheTest()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-3479-control-format-failure");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "Cfr", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "idRanges": [ { "from": 63460, "to": 63469 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Cfr.al"), Al);

        var r = await SuiteServer.RunViaServer(root);
        Assert.False(r.Tests.Any(t => t.Message.Contains("WRONG:")), r.ToString());
        Assert.True(r.Total == 2 && r.Passed == 2 && r.Failed == 0, r.ToString());
        Assert.Equal(0, r.ExitCode);
    }
}
