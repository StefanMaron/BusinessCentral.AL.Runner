/// <summary>
/// A table whose OnInsert trigger reads xRec. The AL compiler emits the xRec
/// accessor as a cast to the concrete record type, e.g.
///   xRec => (Record60100)((NavRecord)this).OldRecord
/// so reading xRec forces the runtime to materialise the before-image
/// (NavRecord.OldRecord) and cast it to the concrete Record60100 type. If the
/// runner builds OldRecord as a base NavRecord, that cast throws
/// InvalidCastException — which is exactly the gap this app guards against.
/// </summary>
table 60100 "xRec Probe RXT"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Counter"; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }

    trigger OnInsert()
    begin
        // Read xRec (the before-image). For an insert it is the cleared record,
        // so xRec."Counter" = 0 and Counter becomes 1 — observable proof that
        // the trigger ran AND xRec was accessible as the concrete record type.
        "Counter" := xRec."Counter" + 1;
    end;
}
