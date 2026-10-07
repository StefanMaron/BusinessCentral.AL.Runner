/// <summary>"Sibling Helper" is a real codeunit of the other bundle of the run, but this app declares no
/// dependency on it, so it is unresolved here (AL0185). An empty codeunit of that name would shadow it.</summary>
codeunit 65360 "Missing Object Undeclared Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SiblingBundleObject_IsNotShadowed()
    var
        Helper: Codeunit "Sibling Helper";
        Result: Integer;
    begin
        Result := Helper.Seven();
    end;
}
