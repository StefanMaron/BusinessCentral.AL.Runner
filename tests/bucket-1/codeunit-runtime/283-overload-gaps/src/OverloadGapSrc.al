/// Source codeunit exercising missing method overloads — issue #979.
table 131000 "OGap Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

codeunit 131001 "OGap Source"
{
    // ── FindSet 2-arg (ForUpdate, ForceNewQuery) ─────────────────────────────
    // BC 26+ emits ALFindSet(DataError, bool, bool) for FindSet(ForUpdate, ForceNewQuery).
    // AL deprecation warning for the ForUpdate param is expected.

    procedure FindSet_TwoArg(var Rec: Record "OGap Table"): Boolean
    begin
        exit(Rec.FindSet(true, false));
    end;

    // ── Insert 2-arg (RunTrigger, CheckMandatoryFields) ──────────────────────
    // BC emits ALInsert(DataError, bool, bool) for Insert(RunTrigger, CheckMandatoryFields).

    procedure Insert_TwoArg(var Rec: Record "OGap Table")
    begin
        Rec.Insert(true, true);
    end;

    // ── SecretText.Unwrap via parameter passing ───────────────────────────────
    // AL allows passing Text literals to SecretText parameters (implicit conversion).
    // BC emits NavSecretText; the runner needs NavSecretText.ALUnwrap() to work.

    procedure SecretText_Unwrap(S: SecretText): Text
    begin
        exit(S.Unwrap());
    end;

    procedure SecretText_GetFromParam(S: SecretText): Text
    begin
        // Mirror the unwrap result through the function
        exit(S.Unwrap());
    end;
}
