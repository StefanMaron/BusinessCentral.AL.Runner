/// Helper codeunit exercising FldRef.Record().KeyIndex(n).
/// The BC transpiler lowers this chain to FldRef.ALKeyIndex(compilationTarget, n)
/// on the MockFieldRef — this codeunit proves that path works end-to-end.
codeunit 1310002 "FRK Src"
{
    /// Returns KeyRef.FieldCount for the PK obtained via FldRef.Record().KeyIndex(1).
    procedure GetKeyFieldCountViaFieldRef(): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        KRef: KeyRef;
    begin
        RecRef.Open(Database::"FRK Test Entry");
        FldRef := RecRef.Field(1);
        KRef := FldRef.Record().KeyIndex(1);
        exit(KRef.FieldCount);
    end;

    /// Returns the field number of the Nth PK field (1-based) obtained via
    /// FldRef.Record().KeyIndex(1).FieldIndex(fieldIdx).Number().
    procedure GetPkFieldNoViaFieldRef(FieldIdx: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        KRef: KeyRef;
        KFldRef: FieldRef;
    begin
        RecRef.Open(Database::"FRK Test Entry");
        FldRef := RecRef.Field(1);
        KRef := FldRef.Record().KeyIndex(1);
        KFldRef := KRef.FieldIndex(FieldIdx);
        exit(KFldRef.Number());
    end;

    /// Verifies that an out-of-range index raises an error.
    procedure GetKeyIndexOutOfRange()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        KRef: KeyRef;
    begin
        RecRef.Open(Database::"FRK Test Entry");
        FldRef := RecRef.Field(1);
        KRef := FldRef.Record().KeyIndex(99);
    end;
}
