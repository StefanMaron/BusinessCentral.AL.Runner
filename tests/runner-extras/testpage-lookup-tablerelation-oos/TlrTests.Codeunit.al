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
// What this bundle now pins is the REMAINING boundary. "Tlr Row" declares no LookupPageId, so a
// relation pointing at it resolves and still has no page to open, and that must stay a refusal
// by name with reason `testpage-lookup` (docs/scope.md) rather than doing nothing — doing
// nothing is what let a test invoke a lookup, observe no change, and compare two empty strings
// successfully, which is the failure mode .claude/rules/loud-failures.md exists for.
//
// The message changed with the boundary, deliberately. It used to assert "neither the control
// nor its source table field declares an OnLookup trigger, so the lookup comes from a
// TableRelation" — true, and no longer the REASON, since a TableRelation is now served. The
// reason here is narrower and is what a reader has to act on: the related table has no lookup
// page.
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
    procedure Lookup_TableRelationToTableWithoutLookupPage_IsRefusedByName()
    var
        Card: TestPage "Tlr Card";
    begin
        // The subject, and the boundary #3518 left standing. Neither the control nor the table
        // field declares an OnLookup, so the lookup comes from the TableRelation — which points
        // at "Tlr Row", a table declaring no LookupPageId. There is nothing to open.
        OpenOn(Card);

        asserterror Card."Relation Only".Lookup();

        // Each fragment is a separate assertion because each carries a different part of the
        // contract, and a message change that dropped any one of them would leave a consumer
        // without it. Naming them individually also keeps this from being a bare asserterror,
        // which would pass on any error at all — including the runner failing to open the page.
        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('testpage-lookup');
        // The reason the refusal exists, and the part a reader needs in order to know this is
        // a scope boundary and not a bug in their AL. It names the RELATION as resolved and the
        // missing page as the cause — if it named the absence of a trigger instead, a reader
        // would go looking for AL to add rather than a LookupPageId to declare.
        Assert.ExpectedError('comes from its TableRelation to table 65561');
        Assert.ExpectedError('declares no LookupPageId or DrillDownPageId');

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
}
