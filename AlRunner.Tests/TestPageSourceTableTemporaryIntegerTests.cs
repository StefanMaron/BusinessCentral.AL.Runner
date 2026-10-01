// TestPageSourceTableTemporaryIntegerTests — issue #2516.
//
// A page (or ListPart) declared SourceTable = Integer; SourceTableTemporary = true; must
// bind to its OWN empty temporary rowset when driven through a TestPage, exactly like a
// temporary source table over any other table. TestPageFactory.TryBuild used to hardcode
// isTemporary: false for every TestPage-driven page, so a temporary-Integer page's Rec
// fell through to GetDataAccessForTableCore's virtual-table population branch (the
// materialised [-1000..100000] window) instead of GetDataAccessForTableCore's `if
// (isTemporary) return _mCreateTempDataAccess...` short-circuit, which never reaches that
// branch at all. See AlRunner/Patches/TestPageFactory.cs and
// AlRunner/Patches/RecordPatches.cs (NavDataAccessSource_GetDataAccessForTable).
//
// This is a RUNNER-MECHANISM test: it exercises TestPageFactory.TryBuild's isTemporary
// resolution end to end via a real bundle run, not a claim about what real BC does. The
// BEHAVIORAL claim ("SourceTableTemporary = true opens empty in Cloud") is proven upstream
// against a live BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests PR
// adding TestTempIntegerListPart.al (60622-60630), per
// .claude/rules/bc-behavior-tests-go-upstream.md.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageSourceTableTemporaryIntegerTests : IDisposable
{

    private readonly string _root;

    public TestPageSourceTableTemporaryIntegerTests()
    {
        _root = TestScratch.Dir("al-runner-temp-integer-testpage");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteFixture()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "6ffcc9c3-2516-4e2c-a2a5-2516251625160001",
          "name": "TempIntTestPage Repro",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 90200, "to": 90209 } ],
          "runtime": "17.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Part.Page.al"), """
        page 90200 "TIT SelfLoad Part"
        {
            PageType = ListPart;
            SourceTable = Integer;
            SourceTableTemporary = true;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Group)
                    {
                        field(Number; Rec.Number) { ApplicationArea = All; }
                        field(ValueAtNumber; Values[Rec.Number]) { ApplicationArea = All; }
                    }
                }
            }

            var
                Values: array[20] of Decimal;

            trigger OnOpenPage()
            var
                i: Integer;
            begin
                for i := 1 to 3 do begin
                    Values[i] := i * 10;
                    Rec.Init();
                    Rec.Number := i;
                    Rec.Insert();
                end;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "EmptyPart.Page.al"), """
        page 90201 "TIT Empty Part"
        {
            PageType = ListPart;
            SourceTable = Integer;
            SourceTableTemporary = true;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Group)
                    {
                        field(Number; Rec.Number) { ApplicationArea = All; }
                    }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Test.Codeunit.al"), """
        codeunit 90202 "TIT Test"
        {
            Subtype = Test;

            [Test]
            procedure DirectOpen_StartsEmpty()
            var
                Part: TestPage "TIT Empty Part";
            begin
                Part.OpenView();
                if Part.First() then
                    Error('never-populated temporary-Integer part must start empty, First() returned true');
                Part.Close();
            end;

            [Test]
            procedure SelfLoaded_FirstRowIsNumberOne()
            var
                Part: TestPage "TIT SelfLoad Part";
            begin
                Part.OpenView();
                if not Part.First() then
                    Error('part must have rows after its own OnOpenPage runs');
                if Part.Number.Value() <> '1' then
                    Error('first row Number expected 1, got %1', Part.Number.Value());
                if Part.ValueAtNumber.Value() <> '10.00' then
                    Error('first row Values[Number] expected 10.00, got %1', Part.ValueAtNumber.Value());
                Part.Close();
            end;
        }
        """);
    }

    [SkippableFact]
    public async Task TempIntegerListPart_TestPage_BindsOwnEmptyRowset_NotVirtualTable()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture();
        var r = await SuiteServer.RunViaServer(_root);

        Assert.True(r.ExitCode == 0,
            $"expected both tests to pass (isTemporary must route to an empty rowset, not the Integer virtual table); exit={r.ExitCode}\n{r}");
        Assert.True(r.Passed == 2, $"expected 2 passed\n{r}");
        r.AssertNoFailures();
    }
}
