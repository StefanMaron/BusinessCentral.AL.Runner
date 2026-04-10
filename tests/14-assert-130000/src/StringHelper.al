codeunit 50114 "String Helper"
{
    procedure Reverse(Input: Text): Text
    var
        Result: Text;
        I: Integer;
    begin
        for I := StrLen(Input) downto 1 do
            Result += CopyStr(Input, I, 1);
        exit(Result);
    end;

    procedure IsPalindrome(Input: Text): Boolean
    begin
        exit(Input = Reverse(Input));
    end;
}
