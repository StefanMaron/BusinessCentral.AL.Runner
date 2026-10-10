namespace Fixture.Mine;

/// <summary>#5445: "Namespaced Elsewhere Status" exists, in a namespace this file does not use. An empty enum of that
/// name would shadow it, so nothing is generated and the test is FAILED on the AL0185.</summary>
codeunit 65362 "Missing Enum Namespace Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NamespacedEnum_IsNotShadowed()
    var
        K: Enum "Namespaced Elsewhere Status";
    begin
        K := K::Active;
    end;
}
