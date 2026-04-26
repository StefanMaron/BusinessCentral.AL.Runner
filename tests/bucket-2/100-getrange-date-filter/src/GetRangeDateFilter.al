table 308400 "GetRange Filter Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Profile Date"; Date) { }
        field(3; "Description"; Text[100]) { }
        field(4; "Code Field"; Code[20]) { }
        field(5; "Count"; Integer) { }
        field(6; "Active"; Boolean) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 308401 "GetRange Filter Helper"
{
    procedure GetMinDate(var Rec: Record "GetRange Filter Table"): Date
    var
        MinDate: Date;
    begin
        MinDate := Rec.GetRangeMin("Profile Date");
        exit(MinDate);
    end;

    procedure GetMaxDate(var Rec: Record "GetRange Filter Table"): Date
    var
        MaxDate: Date;
    begin
        MaxDate := Rec.GetRangeMax("Profile Date");
        exit(MaxDate);
    end;

    procedure GetMinText(var Rec: Record "GetRange Filter Table"): Text[100]
    var
        MinText: Text[100];
    begin
        MinText := Rec.GetRangeMin("Description");
        exit(MinText);
    end;

    procedure GetMaxText(var Rec: Record "GetRange Filter Table"): Text[100]
    var
        MaxText: Text[100];
    begin
        MaxText := Rec.GetRangeMax("Description");
        exit(MaxText);
    end;

    procedure GetMinCode(var Rec: Record "GetRange Filter Table"): Code[20]
    var
        MinCode: Code[20];
    begin
        MinCode := Rec.GetRangeMin("Code Field");
        exit(MinCode);
    end;

    procedure GetMaxCode(var Rec: Record "GetRange Filter Table"): Code[20]
    var
        MaxCode: Code[20];
    begin
        MaxCode := Rec.GetRangeMax("Code Field");
        exit(MaxCode);
    end;

    procedure GetMinBool(var Rec: Record "GetRange Filter Table"): Boolean
    var
        MinBool: Boolean;
    begin
        MinBool := Rec.GetRangeMin("Active");
        exit(MinBool);
    end;

    procedure GetMaxBool(var Rec: Record "GetRange Filter Table"): Boolean
    var
        MaxBool: Boolean;
    begin
        MaxBool := Rec.GetRangeMax("Active");
        exit(MaxBool);
    end;
}
