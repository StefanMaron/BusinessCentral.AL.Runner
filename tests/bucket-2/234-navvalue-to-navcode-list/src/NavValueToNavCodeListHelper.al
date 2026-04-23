table 234000 "NVL Item"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; "Category"; Code[20]) { DataClassification = ToBeClassified; }
        field(3; Description; Text[100]) { DataClassification = ToBeClassified; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 234000 "NVL Helper"
{
    /// <summary>
    /// Collects Code field values from a record into a List of [Code[20]].
    /// BC emits GetFieldValueSafe with a (NavCode) cast; but in some contexts
    /// the NavValue is not cast — this tests that Code fields round-trip through lists.
    /// </summary>
    procedure CollectCategoryCodes(var Rec: Record "NVL Item"): List of [Code[20]]
    var
        Codes: List of [Code[20]];
    begin
        if Rec.FindSet() then
            repeat
                Codes.Add(Rec."Category");
            until Rec.Next() = 0;
        exit(Codes);
    end;

    /// <summary>
    /// Returns a list built from Code parameters (baseline).
    /// </summary>
    procedure BuildCodeList(Code1: Code[20]; Code2: Code[20]): List of [Code[20]]
    var
        Codes: List of [Code[20]];
    begin
        Codes.Add(Code1);
        Codes.Add(Code2);
        exit(Codes);
    end;

    /// <summary>
    /// Tests List.Contains with a Code search parameter.
    /// </summary>
    procedure ListContainsCode(var Rec: Record "NVL Item"; SearchCode: Code[20]): Boolean
    var
        Codes: List of [Code[20]];
    begin
        if Rec.FindSet() then
            repeat
                Codes.Add(Rec."Category");
            until Rec.Next() = 0;
        exit(Codes.Contains(SearchCode));
    end;

    /// <summary>
    /// Tests List.IndexOf with a Code parameter.
    /// </summary>
    procedure IndexOfCode(var Rec: Record "NVL Item"; SearchCode: Code[20]): Integer
    var
        Codes: List of [Code[20]];
    begin
        if Rec.FindSet() then
            repeat
                Codes.Add(Rec."Category");
            until Rec.Next() = 0;
        exit(Codes.IndexOf(SearchCode));
    end;
}
