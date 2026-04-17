/// Minimal table for RecordRef / NavIndirectValueToNavValue coverage.
table 97900 "NVI Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// Source helpers for RecordRef-in-Variant coverage (issue #983).
/// Exercises patterns that the BC compiler lowers to
/// NavIndirectValueToNavValue<NavRecordRef>(…), which the runner must
/// translate without requiring MockRecordRef : NavValue.
codeunit 97901 "NVI Src"
{
    /// Assign a RecordRef to a Variant, then recover it from the Variant.
    /// The BC compiler emits NavIndirectValueToNavValue<NavRecordRef> for the
    /// Variant → RecordRef assignment.
    procedure RoundtripRecordRefViaVariant(rr: RecordRef): Integer
    var
        v: Variant;
        rr2: RecordRef;
    begin
        v := rr;
        rr2 := v;
        exit(rr2.Number);
    end;

    /// Read the first text field of a record through a RecordRef / FieldRef,
    /// then format its Variant value.  This pattern is the canonical
    /// real-world trigger (see issue #983 reproduction).
    procedure GetNameViaRecordRef(rr: RecordRef): Text
    var
        fref: FieldRef;
    begin
        fref := rr.Field(2);
        exit(Format(fref.Value));
    end;
}
