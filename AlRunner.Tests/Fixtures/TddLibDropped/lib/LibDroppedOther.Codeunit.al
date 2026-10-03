/// <summary>#5266: the library codeunit that compiles, and the one "Lib Dropped Refused" calls a missing member of.</summary>
codeunit 71990 "Lib Dropped Other"
{
    procedure Seven(): Integer
    begin
        exit(7);
    end;
}
