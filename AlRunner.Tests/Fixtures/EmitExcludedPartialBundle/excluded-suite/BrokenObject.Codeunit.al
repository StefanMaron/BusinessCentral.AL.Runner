/// <summary>
/// References a codeunit that exists nowhere in the module or its dependencies, so it
/// cannot bind. BC's Compilation.Emit is atomic per module, so the runner's retry loop
/// excludes THIS object and recompiles the survivors — the EMIT-EXCLUDED path of #2762.
/// </summary>
codeunit 61320 "Emit Excl Part Broken"
{
    Subtype = Test;

    [Test]
    procedure ExcludedSuiteBroken_NeverRuns()
    var
        Missing: Codeunit "This Partial Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
