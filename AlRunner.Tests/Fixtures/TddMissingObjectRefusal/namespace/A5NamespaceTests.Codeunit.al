namespace Fixture.Mine;

/// <summary>"Namespaced Elsewhere" exists, in a namespace this file does not use. An empty object of that name would
/// shadow it, so nothing is generated and the test is FAILED naming the unresolved type.</summary>
codeunit 65325 "Missing Object Namespace Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ExistsInANamespaceNotUsed_IsNotShadowed()
    var
        Elsewhere: Codeunit "Namespaced Elsewhere";
        Result: Integer;
    begin
        Result := Elsewhere.Real();
    end;
}
