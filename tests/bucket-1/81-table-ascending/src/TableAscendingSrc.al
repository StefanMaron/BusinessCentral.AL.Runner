/// Helper table and codeunit for Table.Ascending get/set tests.
table 81100 "TA Item"
{
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Amount; Decimal) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
        key(AmountKey; Amount, Code) { }
    }
}

codeunit 81101 "TA Helper"
{
    /// Returns the current Ascending() direction (no-arg getter).
    procedure GetAscending(): Boolean
    var
        Rec: Record "TA Item";
    begin
        exit(Rec.Ascending());
    end;

    /// Sets Ascending(false), then reads it back.
    procedure SetDescendingThenGet(): Boolean
    var
        Rec: Record "TA Item";
    begin
        Rec.Ascending(false);
        exit(Rec.Ascending());
    end;

    /// Sets Ascending(true) explicitly, then reads it back.
    procedure SetAscendingThenGet(): Boolean
    var
        Rec: Record "TA Item";
    begin
        Rec.Ascending(true);
        exit(Rec.Ascending());
    end;

    /// Seeds table with 3 records.
    procedure Seed()
    var
        Rec: Record "TA Item";
    begin
        Rec.DeleteAll();
        Rec.Init(); Rec.Code := 'A'; Rec.Amount := 30; Rec.Insert();
        Rec.Init(); Rec.Code := 'B'; Rec.Amount := 10; Rec.Insert();
        Rec.Init(); Rec.Code := 'C'; Rec.Amount := 20; Rec.Insert();
    end;

    /// Iterates with Ascending(false) — PK descending — returns concatenated codes.
    procedure IterateDescending(): Text
    var
        Rec: Record "TA Item";
        Result: Text;
    begin
        Seed();
        Rec.Ascending(false);
        if Rec.FindSet() then
            repeat
                Result += Rec.Code;
            until Rec.Next() = 0;
        exit(Result);
    end;

    /// Iterates with default (Ascending = true) — PK ascending — returns concatenated codes.
    procedure IterateAscending(): Text
    var
        Rec: Record "TA Item";
        Result: Text;
    begin
        Seed();
        if Rec.FindSet() then
            repeat
                Result += Rec.Code;
            until Rec.Next() = 0;
        exit(Result);
    end;

    /// Sets Ascending(false), resets, then gets — should return true after Reset.
    procedure ResetRestoresAscending(): Boolean
    var
        Rec: Record "TA Item";
    begin
        Rec.Ascending(false);
        Rec.Reset();
        exit(Rec.Ascending());
    end;
}
