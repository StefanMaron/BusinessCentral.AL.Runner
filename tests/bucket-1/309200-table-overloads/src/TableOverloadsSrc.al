/// Test table for Table overload tests (Insert/FindSet/TransferFields/FullyQualifiedName).
table 309200 "Table Overloads"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Counter; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    trigger OnInsert()
    begin
        Counter += 1;
    end;
}

/// Helper codeunit wrapping the calls so AL compiles each overload explicitly.
codeunit 309201 "Table Overloads Helper"
{
    /// Insert(Boolean) — explicit runTrigger
    procedure InsertWithTriggerFlag(var Rec: Record "Table Overloads"; RunTrigger: Boolean)
    begin
        Rec.Insert(RunTrigger);
    end;

    /// Insert(Boolean, Boolean) — runTrigger + belowXRec
    procedure InsertWithTriggerAndBelowXRec(var Rec: Record "Table Overloads"; RunTrigger: Boolean; BelowXRec: Boolean)
    begin
        Rec.Insert(RunTrigger, BelowXRec);
    end;

    /// FindSet(Boolean, Boolean) — forUpdate + updateKey
    procedure FindSetForUpdateAndKey(var Rec: Record "Table Overloads"; ForUpdate: Boolean; UpdateKey: Boolean): Boolean
    begin
        exit(Rec.FindSet(ForUpdate, UpdateKey));
    end;

    /// TransferFields(source, Boolean, Boolean) — 3-arg overload
    procedure TransferFieldsThreeArgs(var Target: Record "Table Overloads"; Source: Record "Table Overloads"; InitPK: Boolean; InitSystem: Boolean)
    begin
        Target.TransferFields(Source, InitPK, InitSystem);
    end;

    /// FullyQualifiedName() — returns company$tableName
    procedure GetFullyQualifiedName(var Rec: Record "Table Overloads"): Text
    begin
        exit(Rec.FullyQualifiedName());
    end;
}
