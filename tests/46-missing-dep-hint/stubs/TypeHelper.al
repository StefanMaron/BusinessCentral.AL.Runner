// Deliberately in the WRONG namespace — the consumer expects
// System.Reflection, not System.Utilities.
namespace System.Utilities;

codeunit 10 "Type Helper"
{
    procedure IsText(Value: Variant): Boolean
    begin
        exit(true);
    end;
}
