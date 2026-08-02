/// <summary>
/// The broken half: references a codeunit that does not exist anywhere in the
/// module or its dependencies, so it cannot bind. BC's Compilation.Emit is atomic
/// per module, so the runner's retry loop excludes THIS file and recompiles the
/// rest — dropping the object (and any tests it declared) from the run.
/// </summary>
codeunit 60620 "Emit Excl Broken"
{
    Subtype = Test;

    [Test]
    procedure Broken_NeverRuns()
    var
        Missing: Codeunit "This Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
