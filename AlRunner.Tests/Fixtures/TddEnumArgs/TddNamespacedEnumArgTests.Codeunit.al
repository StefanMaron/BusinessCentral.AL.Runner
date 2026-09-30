namespace TddEnumArgs.Tests;

using TddEnumArgs.Membership;

codeunit 65113 "Tdd Namespaced Enum Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NamespacedEnumValueArg_GeneratesQualifiedEnumParameter()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Result: Integer;
    begin
        Result := Target.CalcStatus("Member Status"::Lapsed);
    end;
}
