/// <summary>
/// #5266: calls a member of "Lib Dropped Other" with the call as the argument of exit(), a shape --tdd
/// refuses (nothing says what the value is for), so this codeunit is dropped while the library's other one
/// compiles.
/// </summary>
codeunit 71991 "Lib Dropped Refused"
{
    procedure Calc(): Integer
    var
        Other: Codeunit "Lib Dropped Other";
    begin
        exit(Other.RefusedMissing(1));
    end;
}
