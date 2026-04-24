table 304000 "NCIL Item"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; Description; Text[100]) { DataClassification = ToBeClassified; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 304000 "NCIL Helper"
{
    /// <summary>
    /// Reproduces issue #1211: AL 'in' operator against a set of Code literals
    /// on a Code-typed record field. BC compiler emits a call that expects
    /// NavCode arguments but the field accessor returns NavValue → CS1503.
    /// </summary>
    procedure IsWhitelistedNo(var Item: Record "NCIL Item"): Boolean
    begin
        if Item."No." in ['ALPHA', 'BETA', 'GAMMA'] then
            exit(true);
        exit(false);
    end;

    /// <summary>
    /// Negated form — mirrors the exact AL snippet from the telemetry.
    /// </summary>
    procedure IsNotWhitelisted(var Item: Record "NCIL Item"): Boolean
    begin
        if not (Item."No." in ['ALPHA', 'BETA']) then
            exit(true);
        exit(false);
    end;
}
