/// <summary>#5445: "Sibling Status" is a real enum of the other bundle, which this app does not depend on: an empty one
/// would shadow it, so nothing is generated and the test is FAILED on the AL0185.</summary>
codeunit 65361 "Missing Enum Sibling Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SiblingBundleEnum_IsNotShadowed()
    var
        K: Enum "Sibling Status";
    begin
        K := K::Active;
    end;
}
