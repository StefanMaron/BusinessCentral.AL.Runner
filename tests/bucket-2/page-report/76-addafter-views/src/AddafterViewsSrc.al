table 59700 "AV Customer"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Active; Boolean) { }
        field(4; Balance; Decimal) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// List page with a base view — used as the target for pageextension addafter(views).
page 59700 "AV Customer List"
{
    PageType = List;
    SourceTable = "AV Customer";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(Code; Rec.Code) { ApplicationArea = All; }
                field(Name; Rec.Name) { ApplicationArea = All; }
                field(Active; Rec.Active) { ApplicationArea = All; }
                field(Balance; Rec.Balance) { ApplicationArea = All; }
            }
        }
    }

    views
    {
        view(AllRecords)
        {
            Caption = 'All Records';
        }
    }
}

/// Page extension that uses addafter() in the views area — proves the runner
/// compiles view modifications without errors. Views have no runtime effect
/// in unit-test context; this proves the declaration does not block compilation.
pageextension 59700 "AV Customer List Ext" extends "AV Customer List"
{
    views
    {
        addafter(AllRecords)
        {
            view(ActiveOnly)
            {
                Caption = 'Active Only';
                Filters = where(Active = const(true));
            }
            view(HighBalance)
            {
                Caption = 'High Balance';
                Filters = where(Balance = filter('>1000'));
            }
        }
    }
}

/// Business logic helper — proves the compilation unit containing a
/// pageextension with addafter(views) compiles and executes logic correctly.
codeunit 59700 "AV Customer Helper"
{
    procedure IsHighBalance(Balance: Decimal): Boolean
    begin
        exit(Balance > 1000);
    end;

    procedure FormatStatus(Active: Boolean): Text
    begin
        if Active then
            exit('Active');
        exit('Inactive');
    end;

    procedure CalcCategory(Balance: Decimal): Text
    begin
        if Balance > 5000 then
            exit('Premium');
        if Balance > 1000 then
            exit('Standard');
        exit('Basic');
    end;
}
