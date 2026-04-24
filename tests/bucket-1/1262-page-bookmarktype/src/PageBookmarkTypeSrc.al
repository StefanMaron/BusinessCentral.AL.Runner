/// Source fixtures for BookmarkType / CheckType / SetRecord page stub tests.
/// BC emits BookmarkType, CheckType, and SetRecord on generated Page classes;
/// without stubs the Roslyn compilation fails with CS1061.

table 1262001 "BkmType Test Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

page 1262002 "BkmType Test Page"
{
    PageType = List;
    SourceTable = "BkmType Test Table";
    SourceTableTemporary = true;
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field("Entry No."; Rec."Entry No.") { }
                field(Description; Rec.Description) { }
            }
        }
    }

    procedure GetPageId(): Integer
    begin
        exit(1262002);
    end;

    procedure SetRecordOnPage(var TestRec: Record "BkmType Test Table")
    begin
        // Exercises CurrPage.SetRecord which BC lowers to this.SetRecord(rec.Target).
        CurrPage.SetRecord(TestRec);
    end;
}
