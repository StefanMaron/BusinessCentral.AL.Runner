// Written ONLY by "ITS Session Worker", and only when an install trigger's StartSession
// actually dispatches it. Deliberately a different table from "Install Seed": the baseline
// isolation tests next door count that table's rows exactly, and a row added there would
// break them for a reason unrelated to what they pin.
table 60714 "ITS Session Marker"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Value"; Integer) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
