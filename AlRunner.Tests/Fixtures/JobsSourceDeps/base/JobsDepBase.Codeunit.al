/// <summary>The bottom of the dependency chain: every other folder reaches this value.</summary>
codeunit 65700 "Jobs Dep Base"
{
    procedure Value(): Integer
    begin
        exit(10);
    end;
}
