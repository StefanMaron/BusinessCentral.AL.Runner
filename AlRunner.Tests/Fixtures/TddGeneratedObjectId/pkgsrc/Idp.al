// One object of every kind --tdd generates, each on the id 65400, which is also the first id of the test app's idRanges.
codeunit 65400 "Idp Calc"
{
    procedure Existing(): Integer
    begin
        exit(1);
    end;
}

table 65400 "Idp Entry"
{
    fields
    {
        field(1; "Id"; Integer) { }
    }
    keys
    {
        key(PK; "Id") { Clustered = true; }
    }
}

enum 65400 "Idp Tier"
{
    Extensible = true;
    value(0; Bronze) { }
}

tableextension 65400 "Idp Entry Ext" extends "Idp Entry"
{
    fields
    {
        field(65400; "Idp Ext Field"; Integer) { }
    }
}

enumextension 65400 "Idp Tier Ext" extends "Idp Tier"
{
    value(65400; "Idp Ext Value") { }
}
