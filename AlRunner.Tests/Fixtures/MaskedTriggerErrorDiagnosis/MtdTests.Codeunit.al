// Three arms for issue #3189, two of which fail BY CONSTRUCTION — the diagnosis this fixture is
// about only exists on a reported failure, so a fixture where everything passes could not
// observe it at all. MaskedTriggerErrorDiagnosisTests asserts on each arm's reported block.
//
//   MaskedPartTriggerError_IsReportedWithTheCauseNamed   fails; must carry BC's message AND the
//                                                        converted cause, and NO [test-data] note
//   MaskedPartTriggerError_AlStillSeesOnlyBcsOwnMessage  passes; pins that the cause stays OUT
//                                                        of what AL can read
//   MaskedSetupRecordError_CarriesBothExplanations       fails; must carry the converted cause
//                                                        AND the [test-data] note for the empty
//                                                        table behind it
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
    procedure MaskedSetupRecordError_CarriesBothExplanations()
    var
        Card: TestPage "MTD Setup Card";
    begin
        SeedOneHeaderWithOneLine();
        // Fails by construction, like the arm above, but on a MISSING SETUP RECORD rather than a
        // bare Error(). "MTD Setup" is never given a row, so the converted exception carries the
        // typed evidence MissingTestDataDiagnosis needs (the AL table id) behind the TestPage
        // mask — which is the only way to observe that its walk follows the link (#3189).
        Card.OpenEdit();
    end;

    [Test]
    procedure PlainFailure_IsReportedWithNoTestPageDiagnosis()
    begin
        // No page, nothing converted, so nothing to name. Fails by construction; the assertion
        // is that its reported block carries no diagnosis at all.
        Error('MTD-PLAIN-70545 a failure with no page involved');
    end;
}
