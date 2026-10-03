/// <summary>
/// #5264, #5271: a codeunit of the library itself that declares only a placeholder; "MissingR" is generated
/// into the library's own compile, so no other bundle's recompile (and no second pass) is involved.
/// </summary>
codeunit 72210 "Round Target"
{
    procedure Placeholder()
    begin
    end;
}
