/// <summary>
/// A plain record used as an INSTANCE var-record field of the interface
/// implementation codeunit (see "Iface Impl Vendor ICS"). The compiler emits
/// the field's allocation in the codeunit's private InitializeComponent(),
/// which runs only from the codeunit's constructor.
/// </summary>
table 60200 "State Rec ICS"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Name"; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
