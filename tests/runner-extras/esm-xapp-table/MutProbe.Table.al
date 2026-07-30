/// <summary>
/// Publisher table (lives in its own app). A sibling app subscribes to this
/// table's field OnAfterValidateEvent and mutates the record by reference —
/// the cross-app analogue of an ISV subscribing to BaseApp "Purchase Header".
/// </summary>
table 63300 "Mut Probe XESM"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Trigger Field"; Code[20]) { }
        field(3; "Target Field"; Code[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
