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

// Renumbered from 56301 to avoid collision in new bucket layout (#1385).
codeunit 1056301 "AppB Helper"
{
    procedure GetAppBValue(): Text
    begin
        exit('Beta');
    end;
}
