// Three arms for issue #3189, two of which fail BY CONSTRUCTION — the diagnosis this fixture is
// about only exists on a reported failure, so a fixture where everything passes could not
// observe it at all. MaskedTriggerErrorDiagnosisTests asserts on each arm's reported block.
//
//   MaskedPartTriggerError_IsReportedWithTheCauseNamed   fails; must carry BC's message AND the
//                                                        converted cause
//   MaskedPartTriggerError_AlStillSeesOnlyBcsOwnMessage  passes; pins that the cause stays OUT
//                                                        of what AL can read
//   PlainFailure_IsReportedWithNoTestPageDiagnosis       fails; must carry NO diagnosis
//
// The middle one is the guard that stops the fix from becoming "leak the original into AL".
// Real BC surfaces "The TestPage is not open." and nothing else here (#2656), so a diagnosis
// that reached GetLastErrorText would be a runner-invented AL error.
codeunit 70545 "MTD Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "MTD Assert";
        NotOpenTxt: Label 'The TestPage is not open.', Locked = true;

    local procedure SeedOneHeaderWithOneLine()
    var
        Header: Record "MTD Header";
        Line: Record "MTD Line";
    begin
        Line.DeleteAll();
        Header.DeleteAll();

        Header.Init();
        Header."No." := 'H1';
        Header.Insert();

        // The part must FIND a row: LiveNavTestPage.Loaded only runs OnAfterGetRecord for a row
        // it found, so without this line the part's trigger never raises and the arm would pass
        // for a reason that has nothing to do with the fix.
        Line.Init();
        Line."No." := 'H1';
        Line."Line No." := 10000;
        Line.Insert();
    end;

    [Test]
    procedure MaskedPartTriggerError_IsReportedWithTheCauseNamed()
    var
        Card: TestPage "MTD Card";
    begin
        SeedOneHeaderWithOneLine();
        // Fails by construction: the linked part's OnAfterGetRecord raises while the host page
        // is opening. What the runner REPORTS for this failure is the subject.
        Card.OpenEdit();
    end;

    [Test]
    procedure MaskedPartTriggerError_AlStillSeesOnlyBcsOwnMessage()
    var
        Card: TestPage "MTD Card";
    begin
        SeedOneHeaderWithOneLine();

        asserterror Card.OpenEdit();

        Assert.AreEqual(NotOpenTxt, GetLastErrorText(),
            'AL must see BC''s own message and nothing else — the converted cause is diagnostic ' +
            'output, not an AL-visible error (issue #3189)');
    end;

    [Test]
    procedure PlainFailure_IsReportedWithNoTestPageDiagnosis()
    begin
        // No page, nothing converted, so nothing to name. Fails by construction; the assertion
        // is that its reported block carries no diagnosis at all.
        Error('MTD-PLAIN-70545 a failure with no page involved');
    end;
}
