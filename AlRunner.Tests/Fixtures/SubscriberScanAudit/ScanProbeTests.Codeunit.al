/// <summary>
/// One trivial passing test, so the runner invocation this fixture exists for
/// (AlRunner.Tests' EventSubscriberScanEquivalenceTests) has a real test run to complete
/// and exit 0. The AL-observable claim here is not the point of the fixture -- the
/// app.json "application" floor is. See app.json's description and
/// .claude/rules/no-base-app-in-csharp-tests.md.
/// </summary>
codeunit 61201 "SSA Scan Probe Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SSA Assert";

    [Test]
    procedure Insert_ThenGet_ReadsBackTheStoredValue()
    var
        Rec: Record "SSA Scan Probe";
    begin
        // [GIVEN] a fresh record
        Rec.Init();
        Rec."No." := 'A1';
        Rec."Value" := 42;

        // [WHEN] it is inserted
        Rec.Insert(false);

        // [THEN] reading it back returns the stored value
        Rec.Get('A1');
        Assert.AreEqual(42, Rec."Value", 'expected the stored Value to round-trip through Insert/Get');
    end;
}
