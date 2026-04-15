codeunit 50130 "BigText Helper"
{
    procedure AddAndRead(InputText: Text): Text
    var
        BT: BigText;
        Result: Text;
    begin
        BT.AddText(InputText);
        BT.GetSubText(Result, 1);
        exit(Result);
    end;

    procedure GetLength(InputText: Text): Integer
    var
        BT: BigText;
    begin
        BT.AddText(InputText);
        exit(BT.Length);
    end;

    procedure FindPosition(Haystack: Text; Needle: Text): Integer
    var
        BT: BigText;
    begin
        BT.AddText(Haystack);
        exit(BT.TextPos(Needle));
    end;

    procedure GetSubstring(InputText: Text; FromPos: Integer; Length: Integer): Text
    var
        BT: BigText;
        Result: Text;
    begin
        BT.AddText(InputText);
        BT.GetSubText(Result, FromPos, Length);
        exit(Result);
    end;

    procedure ConcatenateTexts(Text1: Text; Text2: Text): Text
    var
        BT: BigText;
        Result: Text;
    begin
        BT.AddText(Text1);
        BT.AddText(Text2);
        BT.GetSubText(Result, 1);
        exit(Result);
    end;

    procedure RequireText(InputText: Text; Expected: Text)
    var
        BT: BigText;
        Result: Text;
    begin
        BT.AddText(InputText);
        BT.GetSubText(Result, 1);
        if Result <> Expected then
            Error('BigText content "%1" does not match expected "%2"', Result, Expected);
    end;
}
