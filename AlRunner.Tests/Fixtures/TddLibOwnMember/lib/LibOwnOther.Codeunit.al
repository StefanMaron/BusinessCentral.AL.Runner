/// <summary>#5271: a codeunit of the library the library's own "Lib Own Same" calls a missing member of.</summary>
codeunit 71970 "Lib Own Other"
{
    procedure Seven(): Integer
    begin
        exit(7);
    end;
}
