table 296001 "MaxStrLen Test"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; ShortText; Text[50]) { }
        field(3; MediumCode; Code[20]) { }
        field(4; LongText; Text[250]) { }
    }
    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}
