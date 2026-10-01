// TestPageMinMaxValueDispatchTests — issue #2495.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR OWN
// wiring — LiveNavTestField.Value's setter in MockTestPage.cs calling TestPageMinMaxValue.Check,
// fed by RecordPatches.TryGetParsedFieldMinMax reading the AL-declared MinValue/MaxValue field
// properties — enforces a bounded field's range on a TestPage control write, and specifically
// does NOT enforce it on Rec.Validate or a plain field assignment (measured against real BC
// 28.1/28.4, see issue #2490's arm A2/D2).
//
// The BEHAVIORAL claim (what real BC does on each of these four surfaces, and the exact error
// text) is the corpus's to adjudicate — see StefanMaron/BusinessCentral.AL.Language.Tests, per
// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR OWN
// dispatch mechanism fails loudly here, in seconds, without needing the corpus's al-language
// submodule pin bumped first.
//
// RED/GREEN proof: before RecordPatches.TryGetParsedFieldMinMax and TestPageMinMaxValue.Check
// existed, SetValue_BelowMin_Decimal_RaisesFromTestPageOnly below failed with
// "An error was expected inside an ASSERTERROR statement." (SetValue let -1 through silently),
// while the negative tests (Validate/assignment) already passed.

using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageMinMaxValueDispatchTests : IDisposable
{

    private readonly string _root;

    public TestPageMinMaxValueDispatchTests()
    {
        _root = TestScratch.Dir("al-runner-minmax-dispatch");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "d1e2f3a4-5b6c-4890-9a1b-6c7d8e9f0b2d",
          "name": "Runner Mechanism - TestPage MinValue MaxValue Dispatch",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62400, "to": 62409 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MmvRow.Table.al"), """
        table 62400 "Mmv Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; PK; Code[10]) { }
                field(2; Completion; Decimal)
                {
                    MinValue = 0;
                    MaxValue = 100;
                }
            }

            keys
            {
                key(K; PK) { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MmvCard.Page.al"), """
        page 62401 "Mmv Card"
        {
            PageType = Card;
            SourceTable = "Mmv Row";
            ApplicationArea = All;
            UsageCategory = Administration;

            layout
            {
                area(Content)
                {
                    field(RecCompletion; Rec.Completion) { ApplicationArea = All; }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "MmvTests.Codeunit.al"), """
        codeunit 62402 "Mmv Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Seed()
            var
                Row: Record "Mmv Row";
            begin
                Row.DeleteAll();
                Row.Init();
                Row.PK := 'R1';
                Row.Insert();
                // Commit the seed explicitly. SetValue_BelowMin_Decimal_RaisesFromTestPageOnly
                // reads R1 back AFTER an asserterror, and an AL error unwinds the database to
                // the last commit — so without this the seed is part of what gets rolled back
                // and the read fails on a row that never should have been the subject of this
                // test. It used to survive by accident: opening a TestPage marked a commit
                // point in the runner, which real BC does not do (issue #2400). This test is
                // about MinValue/MaxValue dispatch, so it says where its own commit boundary
                // is instead of inheriting one from the page.
                Commit();
            end;

            [Test]
            procedure SetValue_BelowMin_Decimal_RaisesFromTestPageOnly()
            var
                Row: Record "Mmv Row";
                Card: TestPage "Mmv Card";
            begin
                Seed();

                Card.OpenEdit();
                Card.First();
                asserterror Card.RecCompletion.SetValue(-1);
                if StrPos(GetLastErrorText(), 'greater than or equal to') = 0 then
                    Error('expected a MinValue-shaped error, got: %1', GetLastErrorText());
                Card.Close();

                Row.Get('R1');
                if Row.Completion <> 0 then
                    Error('a rejected SetValue must not have persisted, got %1', Row.Completion);
            end;

            [Test]
            procedure SetValue_AboveMax_Decimal_Raises()
            var
                Card: TestPage "Mmv Card";
            begin
                Seed();

                Card.OpenEdit();
                Card.First();
                asserterror Card.RecCompletion.SetValue(101);
                if StrPos(GetLastErrorText(), 'less than or equal to') = 0 then
                    Error('expected a MaxValue-shaped error, got: %1', GetLastErrorText());
                Card.Close();
            end;

            [Test]
            procedure SetValue_WithinBounds_Succeeds()
            var
                Card: TestPage "Mmv Card";
            begin
                Seed();

                Card.OpenEdit();
                Card.First();
                Card.RecCompletion.SetValue(50);
                if Card.RecCompletion.AsDecimal() <> 50 then
                    Error('expected 50, got %1', Card.RecCompletion.AsDecimal());
                Card.Close();
            end;

            [Test]
            procedure Validate_BelowMin_DoesNotRaise()
            var
                Row: Record "Mmv Row";
            begin
                Row.Init();
                Row.PK := 'R2';
                Row.Validate(Completion, -1);
                Row.Insert();
                if Row.Completion <> -1 then
                    Error('expected -1 to be stored (Validate does not enforce MinValue), got %1', Row.Completion);
            end;

            [Test]
            procedure DirectAssignment_BelowMin_DoesNotRaise()
            var
                Row: Record "Mmv Row";
            begin
                Row.Init();
                Row.PK := 'R3';
                Row.Completion := -1;
                Row.Insert();
                if Row.Completion <> -1 then
                    Error('expected -1 to be stored (plain assignment does not enforce MinValue), got %1', Row.Completion);
            end;
        }
        """);
    }

    /// <summary>
    /// Positive (SetValue enforces, on both the below-min and above-max sides, and lets an
    /// in-range write through) and negative (Rec.Validate and plain assignment both stay
    /// unenforced) in one bundle — the shape #2495 depends on the runner getting each of these
    /// right simultaneously, since the fix must not "leak" enforcement into Validate.
    /// </summary>
    [SkippableFact]
    public async Task MinMaxValue_EnforcedOnlyOnTestPageSetValue()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var r = await SuiteServer.RunViaServer(_root);

        Assert.True(r.ExitCode == 0, $"Expected the bundle to pass; exit={r.ExitCode}\n{r}");
        r.AssertPassed("Codeunit62402.SetValue_BelowMin_Decimal_RaisesFromTestPageOnly");
        r.AssertPassed("Codeunit62402.SetValue_AboveMax_Decimal_Raises");
        r.AssertPassed("Codeunit62402.SetValue_WithinBounds_Succeeds");
        r.AssertPassed("Codeunit62402.Validate_BelowMin_DoesNotRaise");
        r.AssertPassed("Codeunit62402.DirectAssignment_BelowMin_DoesNotRaise");
        r.AssertNoFailures();
    }
}
