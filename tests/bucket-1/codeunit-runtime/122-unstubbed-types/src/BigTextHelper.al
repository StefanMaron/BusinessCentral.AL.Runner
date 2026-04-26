codeunit 59821 "BigText Helper"
{
    procedure AddAndGetLength(): Integer
    var
        BT: BigText;
    begin
        BT.AddText('Hello ');
        BT.AddText('World');
        exit(BT.Length());
    end;

    procedure AddAndGetSubText(): Text
    var
        BT: BigText;
        T: Text;
    begin
        BT.AddText('Hello World');
        BT.GetSubText(T, 1, 5);
        exit(T);
    end;

    procedure TextPosFound(): Integer
    var
        BT: BigText;
    begin
        BT.AddText('Hello World');
        exit(BT.TextPos('World'));
    end;

    procedure TextPosMissing(): Integer
    var
        BT: BigText;
    begin
        BT.AddText('Hello World');
        exit(BT.TextPos('Xyz'));
    end;

    procedure GetSubTextAcrossBoundary(): Text
    var
        BT: BigText;
        T: Text;
    begin
        BT.AddText('Hello');
        BT.AddText(' World');
        BT.GetSubText(T, 4, 5);
        exit(T);
    end;

    procedure GetSubTextNoLength(): Text
    var
        BT: BigText;
        T: Text;
    begin
        BT.AddText('Hello World');
        BT.GetSubText(T, 7);
        exit(T);
    end;
}
