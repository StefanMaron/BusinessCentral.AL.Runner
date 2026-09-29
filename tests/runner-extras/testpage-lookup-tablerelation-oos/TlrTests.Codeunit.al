// Issue #2775 — runner-specific half of the TestPage OnLookup contract.
//
// AL spells OnLookup two unrelated ways: `trigger OnLookup(var Text: Text): Boolean` on a page
// control, and a parameterless `trigger OnLookup()` on a table field that writes into Rec
// itself. Real BC tries the control first, then the table field, and then falls back to the
// field's TableRelation, which opens the related table's list page.
//
// All three run here since #3518 — the third through BC's own lookup-mode RunModal, which needs
// no client (see tests/runner-extras/testpage-lookup-tablerelation-served, the positive side).
//
// What this bundle now pins is the REMAINING boundary (#4403). "Tlr Row" declares no
// LookupPageId, so a relation pointing at it resolves and has no page. With no handler bound,
// the runner raises BC's own "Unhandled UI: ModalPage" -- plain BC behaviour, pinned upstream by
// corpus codeunit 60569. With a [ModalPageHandler] bound, real BC hands that handler a form it
// never registered and fails with a NullReferenceException; the runner refuses that by name
// with reason `testpage-lookup` instead of imitating the crash, and that refusal -- a
// runner-specific claim -- is what the first test here asserts.
//
// Everything that is plain BC behaviour — that BC runs the table field's trigger, and that a
// control trigger wins over it — is proven upstream in the al-language corpus against a real
// service tier. What is asserted here is the runner's own refusal, from inside AL through
// asserterror and GetLastErrorText, which is the surface a consumer meets.
codeunit 65563 "Tlr Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Tlr Assert";
        HandlerRan: Boolean;

    local procedure OpenOn(var Card: TestPage "Tlr Card")
    var
        Row: Record "Tlr Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := 'R1';
        Row.Insert();
        Card.OpenEdit();
        Card.GoToRecord(Row);
    end;

    [Test]
    [HandlerFunctions('AnyModalHandler')]
    procedure Lookup_TableRelationToTableWithoutLookupPage_HandlerBound_IsRefusedByName()
    var
        Card: TestPage "Tlr Card";
    begin
        // Neither the control nor the table field declares an OnLookup, so the lookup comes from
        // the TableRelation, which points at "Tlr Row", a table declaring no LookupPageId. A
        // ModalPage handler IS bound, which is the one shape the runner still refuses.
        OpenOn(Card);
        HandlerRan := false;

        asserterror Card."Relation Only".Lookup();

        // Separate fragments, each a different part of the contract: that it is a scope
        // refusal, which one, what resolved, and why BC itself has no answer to imitate.
        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('testpage-lookup');
        Assert.ExpectedError('comes from its TableRelation to table 65561');
        Assert.ExpectedError('declares no LookupPageId or DrillDownPageId');
        Assert.ExpectedError('NullReferenceException inside NavTestExecution.ShowLookupForm');
        // BC's FindHandler returns the handler without ever invoking it; so does the runner.
        Assert.AreEqual(false, HandlerRan, 'the bound handler must not run for a page that was never opened');

        Card.Close();
    end;

    [Test]
    procedure Lookup_TableFieldTrigger_Runs()
    var
        Card: TestPage "Tlr Card";
    begin
        // Scoping control: the refusal is keyed on there being NO trigger, not on the control
        // declaring none. Without this a runner that refused every control without its own
        // OnLookup would pass the test above and still be wrong — which is exactly what #2549
        // reported.
        OpenOn(Card);

        Card."Table Trigger".Lookup();

        Assert.AreEqual('FROM-TABLE', Card."Table Trigger".Value,
            'the table field''s OnLookup must run when the page control declares none');

        Card.Close();
    end;

    [Test]
    procedure Lookup_ControlTrigger_Runs()
    var
        Card: TestPage "Tlr Card";
    begin
        // The other scoping control, and the reason the three fields sit on one page: a fix
        // that turned the refusal above into a no-op would pass this and the test above, so
        // all three have to run together for any of them to mean anything.
        OpenOn(Card);

        Card."Control Trigger".Lookup();

        Assert.AreEqual('FROM-CONTROL', Card."Control Trigger".Value,
            'the page control''s OnLookup must run and write its text back to the field');

        Card.Close();
    end;

    [ModalPageHandler]
    procedure AnyModalHandler(var Modal: TestPage "Tlr Card")
    begin
        HandlerRan := true;
    end;
}
