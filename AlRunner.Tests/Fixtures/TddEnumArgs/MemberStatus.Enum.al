/// <summary>
/// Declared in a namespace the implementing codeunit ("Tdd Loyalty Cu", global namespace)
/// does not import, so a generated parameter naming it must be namespace-qualified.
/// </summary>
namespace TddEnumArgs.Membership;

enum 65101 "Member Status"
{
    Extensible = true;

    value(0; Active) { }
    value(1; Lapsed) { }
}
