/// Helpers for null-option-default tests.
/// When an enum/option field on a record is read before it has been
/// explicitly written the underlying NavOption variable is null.
/// CloneTaggedOption must not crash in that case.
table 29201 "NullOpt Record"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; Id; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(2; Status; Enum "NullOpt Status")
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

enum 29201 "NullOpt Status"
{
    Extensible = false;

    value(0; Initial) { }
    value(1; Active)  { }
    value(2; Closed)  { }
}

codeunit 29201 "NullOpt Helper"
{
    /// Read the Status field from a record that was inserted with Init()
    /// only (no explicit Status assignment). The NavOption backing the
    /// field is null at this point — CloneTaggedOption must return the
    /// default ordinal (0) rather than crashing.
    procedure ReadUninitializedStatus(Rec: Record "NullOpt Record"): Integer
    var
        LocalStatus: Enum "NullOpt Status";
    begin
        // Re-assign from the record field — this triggers CloneTaggedOption
        // with a null `existing` argument.
        LocalStatus := Rec.Status;
        exit(LocalStatus.AsInteger());
    end;

    /// Assign a non-default value and compare it using the enum literal.
    /// The comparison `rec.Status = rec.Status::Closed` triggers
    /// CloneTaggedOption(rec.Status.getFieldValue, 2).
    /// Before the fix, if rec.Status was null this crashed.
    procedure IsStatusClosed(Rec: Record "NullOpt Record"): Boolean
    begin
        exit(Rec.Status = Rec.Status::Closed);
    end;

    /// Assign a non-default value then re-assign to a local — CloneTaggedOption
    /// with a non-null `existing` should still work correctly.
    procedure ReadInitializedStatus(Rec: Record "NullOpt Record"; NewStatus: Enum "NullOpt Status"): Integer
    var
        LocalStatus: Enum "NullOpt Status";
    begin
        Rec.Status := NewStatus;
        LocalStatus := Rec.Status;
        exit(LocalStatus.AsInteger());
    end;

    /// Sets and reads a status via the record-field enum path, verifying
    /// that the CloneTaggedOption round-trip (set → get → clone) gives
    /// back the correct ordinal.
    procedure SetThenReadStatus(var Rec: Record "NullOpt Record"; NewStatus: Enum "NullOpt Status"): Integer
    begin
        Rec.Status := NewStatus;
        exit(Rec.Status.AsInteger());
    end;
}
