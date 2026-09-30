/// <summary>
/// #5044: the enum value is written namespace-qualified at the call site (no `using`), so
/// --tdd must generate CalcRenewal(Arg1: Enum TddEnumArgs.Membership."Member Status"): Integer.
/// The file declares a namespace because the AL compiler resolves a qualified name in an
/// expression only from inside one (from the global namespace it reports AL0118).
/// </summary>
namespace TddEnumArgs.Tests;

codeunit 65117 "Tdd Qualified Enum Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure QualifiedEnumValueArg_GeneratesQualifiedEnumParameter()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Result: Integer;
    begin
        Result := Target.CalcRenewal(TddEnumArgs.Membership."Member Status"::Lapsed);
    end;
}
