/// <summary>
/// A codeunit whose OnRun deterministically raises an error. Used to probe the
/// runner's guarded vs unguarded Codeunit.Run semantics.
/// </summary>
codeunit 60401 "Run Guard Erroring"
{
    trigger OnRun()
    begin
        Error('BOOM-FROM-ONRUN');
    end;
}
