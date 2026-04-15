// Extension B: extends same page with a different field — same name "ItemCardExt"
pageextension 56302 "ItemCardExt" extends "Test Item Card 130"
{
    layout
    {
        addafter(Description)
        {
            field(AppBField; AppBValue) { }
        }
    }

    var
        AppBValue: Text[50];
}

codeunit 56301 "AppB Helper"
{
    procedure GetAppBValue(): Text
    begin
        exit('Beta');
    end;
}
