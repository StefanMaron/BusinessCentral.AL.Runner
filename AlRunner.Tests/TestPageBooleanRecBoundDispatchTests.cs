// TestPageBooleanRecBoundDispatchTests — issue #1870, the Rec-bound half of #1837/#1869.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR
// OWN wiring — LiveNavTestField.Value's setter in MockTestPage.cs, the Rec-bound sibling of
// PageVariableTestField.ToBoundValue that #1869 already fixed — routes a Boolean-typed
// control's TestPage.SetValue(<Boolean>) through TestPageBooleanValue.Resolve when the source
// table field is Boolean, instead of falling through to ALCompiler.ToNavValue(value) (which
// always produces a NavText and made NavTestField.ALSetValue's own ALValidateAsync reject it
// with "The value \"True\" can't be evaluated into type Boolean").
//
// The BEHAVIORAL claim ("SetValue(true)/SetValue(false) on a Rec-bound Boolean control sets
// and persists the underlying table field, round-tripping both directions, and a text that is
// not a valid Boolean spelling is rejected and not persisted") is proven upstream against a
// live BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests, codeunit 60997
// "TP Boolean Rec Bound Tests" — per .claude/rules/bc-behavior-tests-go-upstream.md. This test
// exists so a regression in OUR OWN dispatch mechanism fails loudly here, in seconds, without
// needing the corpus's al-language submodule pin bumped (deliberately deferred to a separate
// centralized PR, same reasoning as TestPageDrillDownDispatchTests).
//
// RED/GREEN proof: reverting LiveNavTestField.Value's setter to the pre-fix
// `CurrentOption() is { } option ? ... : ALCompiler.ToNavValue(value)` (dropping the
// `FieldType == NavType.Boolean` branch) makes the positive test below fail with exactly the
// NavNCLEvaluateException the issue reported, reproduced against a real BC engine here (not a
// mock of one).

using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageBooleanRecBoundDispatchTests : IDisposable
{

    private readonly string _root;

    public TestPageBooleanRecBoundDispatchTests()
    {
        _root = TestScratch.Dir("al-runner-bool-recbound-dispatch");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Mirrors the issue's own minimal reproducer: a Boolean table field bound directly to a
    /// card control (field(RecFlag; Rec.Flag)), as opposed to a page-variable-bound one.
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "c9d1e3a5-4b6f-4890-9a1b-6c7d8e9f0a2c",
          "name": "Runner Mechanism - TestPage Boolean Rec Bound Dispatch",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62390, "to": 62399 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "BrbRow.Table.al"), """
        table 62390 "Brb Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; PK; Code[10]) { }
                field(2; Value; Text[30]) { }
                field(3; Flag; Boolean) { }
            }

            keys
            {
                key(K; PK) { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "BrbCard.Page.al"), """
        page 62391 "Brb Card"
        {
            PageType = Card;
            SourceTable = "Brb Row";
            ApplicationArea = All;
            UsageCategory = Administration;

            layout
            {
                area(Content)
                {
                    field(RecValue; Rec.Value) { ApplicationArea = All; }
                    field(RecFlag; Rec.Flag) { ApplicationArea = All; }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "BrbTests.Codeunit.al"), """
        codeunit 62392 "Brb Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Seed(Flag: Boolean)
            var
                Row: Record "Brb Row";
            begin
                Row.DeleteAll();
                Row.Init();
                Row.PK := 'R1';
                Row.Value := 'V';
                Row.Flag := Flag;
                Row.Insert();
            end;

            [Test]
            procedure SetValueTrue_RecBoundBooleanControl_PersistsToTheRow()
            var
                Row: Record "Brb Row";
                Card: TestPage "Brb Card";
            begin
                Seed(false);

                Card.OpenEdit();
                Card.First();
                Card.RecFlag.SetValue(true);
                if Card.RecFlag.AsBoolean() <> true then
                    Error('the control must read back true immediately after SetValue(true)');
                Card.Close();

                Row.Get('R1');
                if Row.Flag <> true then
                    Error('SetValue(true) must persist Flag = true to the row, got %1', Row.Flag);
            end;

            [Test]
            procedure SetValueFalse_RecBoundBooleanControl_ClearsAPreviouslyTrueField()
            var
                Row: Record "Brb Row";
                Card: TestPage "Brb Card";
            begin
                Seed(true);

                Card.OpenEdit();
                Card.First();
                Card.RecFlag.SetValue(false);
                if Card.RecFlag.AsBoolean() <> false then
                    Error('the control must read back false immediately after SetValue(false)');
                Card.Close();

                Row.Get('R1');
                if Row.Flag <> false then
                    Error('SetValue(false) must persist Flag = false to the row, got %1', Row.Flag);
            end;

            [Test]
            procedure SetValue_NotABooleanSpelling_IsRejectedAndNotPersisted()
            var
                Row: Record "Brb Row";
                Card: TestPage "Brb Card";
            begin
                Seed(false);
                Commit();

                Card.OpenEdit();
                Card.First();
                asserterror Card.RecFlag.SetValue('Maybe');
                if StrPos(GetLastErrorText(), 'Maybe') = 0 then
                    Error('expected the rejected value named in the error, got: %1', GetLastErrorText());
                Card.Close();

                Row.Get('R1');
                if Row.Flag <> false then
                    Error('a rejected value must not have been persisted to the row');
            end;
        }
        """);
    }

    /// <summary>
    /// Both round-trip directions plus the negative: a Rec-bound Boolean control's
    /// SetValue must actually reach the underlying table field (not just the control's own
    /// in-memory copy), in both directions, and must reject text that is not a Boolean
    /// spelling rather than silently coercing or crashing with an unrelated exception.
    /// </summary>
    [SkippableFact]
    public async Task SetValue_RecBoundBooleanControl_RoundTripsAndRejectsInvalidText()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var r = await SuiteServer.RunViaServer(_root);

        Assert.True(r.ExitCode == 0, $"Expected the bundle to pass; exit={r.ExitCode}\n{r}");
        r.AssertPassed("Codeunit62392.SetValueTrue_RecBoundBooleanControl_PersistsToTheRow");
        r.AssertPassed("Codeunit62392.SetValueFalse_RecBoundBooleanControl_ClearsAPreviouslyTrueField");
        r.AssertPassed("Codeunit62392.SetValue_NotABooleanSpelling_IsRejectedAndNotPersisted");
        r.AssertNoFailures();
    }
}
