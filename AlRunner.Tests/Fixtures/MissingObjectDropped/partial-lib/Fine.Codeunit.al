/// <summary>#5339: compiles, so the library emits something and the two objects below are a PARTIAL drop.</summary>
codeunit 72211 "MOD Fine"
{
    procedure Seven(): Integer
    begin
        exit(7);
    end;
}
