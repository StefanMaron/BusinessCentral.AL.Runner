// Fixtures for the AllObjWithCaption (2000000058) virtual-table tests.
//
// 61970 declares a Caption that is DELIBERATELY different from its object name, so a
// provider that filled Object Caption from Object Name fails on it. 61971 declares no
// Caption at all — AL's own default is then the object name, which is what a real tier
// reports, so it pins the fallback rather than an empty string.
table 61970 "AOWC Header"
{
    Caption = 'AOWC Header Caption';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

table 61971 "AOWC NoCaption"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

report 61970 "AOWC Doc Report"
{
    Caption = 'AOWC Document Report';
    UsageCategory = None;

    dataset
    {
        dataitem(Header; "AOWC Header")
        {
            column(No; Header."No.") { }
        }
    }
}
