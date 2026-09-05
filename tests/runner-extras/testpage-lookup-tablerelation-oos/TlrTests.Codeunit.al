// Issue #2775 — runner-specific half of the TestPage OnLookup contract.
//
// AL spells OnLookup two unrelated ways: `trigger OnLookup(var Text: Text): Boolean` on a page
// control, and a parameterless `trigger OnLookup()` on a table field that writes into Rec
// itself. Real BC tries the control first, then the table field, and then falls back to the
// field's TableRelation, which opens the related table's list page.
//
// The first two run here. The third cannot: standing up a list page needs a client the runner
// does not have. So it refuses by name with RunnerOutOfScopeException and reason
// `testpage-lookup` (docs/scope.md), instead of doing nothing — doing nothing is what let a
// test invoke a lookup, observe no change, and compare two empty strings successfully, which is
// the failure mode .claude/rules/loud-failures.md exists for.
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
    procedure Lookup_TableRelationOnly_IsRefusedByName()
    var
        Card: TestPage "Tlr Card";
    begin
        // The subject. Neither the control nor the table field declares an OnLookup, so the
        // only lookup this field has is its TableRelation's list page.
        OpenOn(Card);

        asserterror Card."Relation Only".Lookup();

        // Each fragment is a separate assertion because each carries a different part of the
        // contract, and a message change that dropped any one of them would leave a consumer
        // without it. Naming them individually also keeps this from being a bare asserterror,
        // which would pass on any error at all — including the runner failing to open the page.
        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('testpage-lookup');
        Assert.ExpectedError('neither the control nor its source table field declares an');
        Assert.ExpectedError('OnLookup trigger');
        // The reason the refusal exists, and the part a reader needs in order to know this is
        // a scope boundary and not a bug in their AL.
        Assert.ExpectedError('would open the related table''s list page');

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
