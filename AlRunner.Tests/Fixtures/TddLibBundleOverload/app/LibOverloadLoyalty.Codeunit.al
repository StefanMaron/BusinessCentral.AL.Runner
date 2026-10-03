/// <summary>
/// The implementing codeunit declares "Missing" with NO parameter. The library calls it with one
/// argument, which names an overload none of them takes (AL0126, not AL0132): --tdd generates the
/// overload beside it.
/// </summary>
codeunit 65550 "Lib Overload Loyalty"
{
    procedure Missing(): Integer
    begin
        exit(0);
    end;
}
