/// <summary>
/// References a codeunit that exists nowhere in the module or its dependencies, so it
/// cannot bind. BC's Compilation.Emit is atomic per module, so the runner's retry loop
/// excludes this object — and Program.cs then empties this module's sources entirely,
/// because a module missing objects must not run.
///
/// The point of this fixture is what happens NEXT in a multi-suite bundle: this suite
/// contributes zero tests, but the healthy sibling suite still contributes a passing
/// one, so the bundle is not empty. The guard that reports the exclusion through the
/// exit code used to require an EMPTY bundle, so it stayed silent here.
/// </summary>
codeunit 60720 "Emit Excl Bundle Broken"
{
    Subtype = Test;

    [Test]
    procedure BundleBroken_NeverRuns()
    var
        Missing: Codeunit "This Bundle Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
