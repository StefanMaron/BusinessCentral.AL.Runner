/// One row for the card page to sit on. Flag is here so a control can carry a property whose
/// expression is an ordinary field read -- the shape #3730 measured -- next to the procedure-call
/// shape this suite is about.
table 65911 "Ppb Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Flag; Boolean) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
