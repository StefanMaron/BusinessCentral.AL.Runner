/// <summary>
/// #5264, #5271: a codeunit of the library itself that declares only a placeholder; "MissingR" is generated
/// into the library's own compile, which no other bundle recompiles.
/// </summary>
codeunit 71951 "Lib Sub Target"
{
    procedure Placeholder()
    begin
    end;
}
