// Extension A: extends Test Item Card with a field from App A
pageextension 56301 "ItemCardExt" extends "Test Item Card 130"
{
    layout
    {
        addafter(Description)
        {
            field(AppAField; AppAValue) { }
        }
    }

    var
        AppAValue: Text[50];
}

codeunit 56300 "AppA Helper"
{
    procedure GetAppAValue(): Text
    begin
        exit('Alpha');
    end;
}
