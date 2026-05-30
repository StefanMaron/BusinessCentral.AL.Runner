/// <summary>
/// Regression proof that a var Rec mutation inside an OnAfterValidateEvent
/// subscriber propagates to the record being validated.
///
/// The subscriber (Mut Subscriber ESM) receives the record by reference and
/// sets "Target Field". After Validate("Trigger Field", …) returns, the live
/// record must show that write. If the runner passes the subscriber a
/// throwaway record (e.g. a freshly materialised handle target) instead of the
/// record being validated, the mutation is lost and "Target Field" stays blank.
/// </summary>
codeunit 60210 "Mut Probe Tests ESM"
{
    Subtype = Test;

    var
        Assert: Codeunit "Mut Assert ESM";

    [Test]
    procedure Validate_OnAfterValidateSubscriberMutation_Propagates()
    var
        Rec: Record "Mut Probe ESM";
    begin
        // [GIVEN] a record with a blank target field
        Rec.Init();
        Rec."No." := 'A1';
        Rec."Target Field" := '';

        // [WHEN] the trigger field is validated, firing the OnAfterValidate subscriber
        Rec.Validate("Trigger Field", 'X');

        // [THEN] the subscriber's var Rec mutation is visible on the validated record
        Assert.AreEqual('MUTATED:X', Rec."Target Field",
            'OnAfterValidate subscriber mutation of var Rec must propagate to the validated record');
    end;

    [Test]
    procedure Validate_ThenModify_SubscriberMutationPersistsToDb()
    var
        Rec: Record "Mut Probe ESM";
        Reread: Record "Mut Probe ESM";
    begin
        // Mirrors the RS integration pattern: Validate fires the subscriber that
        // mutates "Target Field" on the live var Rec, then Modify must persist
        // that mutated in-memory value to the DB. Re-reading the row must show
        // the subscriber's write — not blank (the symptom the RS posting test hit:
        // posted doc "Posting Description" came back as <>).
        Rec.Init();
        Rec."No." := 'C1';
        Rec."Target Field" := '';
        Rec.Insert(true);

        // [WHEN] validate fires the subscriber (mutates live Rec) and Modify persists
        Rec.Validate("Trigger Field", 'Z');
        Rec.Modify(true);

        // [THEN] a fresh read from the DB carries the subscriber's mutation
        Reread.Get('C1');
        Assert.AreEqual('MUTATED:Z', Reread."Target Field",
            'Subscriber mutation made during Validate must persist through Modify to the DB');
    end;

    [Test]
    procedure Validate_NoSubscriberMutation_LeavesFieldUntouched()
    var
        Rec: Record "Mut Probe ESM";
    begin
        // Negative direction: a field the subscriber does NOT touch stays as set.
        Rec.Init();
        Rec."No." := 'B1';
        Rec."Target Field" := 'KEEP';

        // Validate a field with no subscriber mutating "Target Field" beyond the
        // 'Trigger Field' subscriber (which only writes the MUTATED: prefix when
        // it fires). Here we validate "No." which has no subscriber.
        Rec.Validate("No.", 'B1');

        Assert.AreEqual('KEEP', Rec."Target Field",
            'A validate with no subscriber touching Target Field must leave it unchanged');
    end;
}
