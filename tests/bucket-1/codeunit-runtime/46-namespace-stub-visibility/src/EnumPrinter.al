// Issue #1589: namespace-aware consumers must be able to reference
// an enum that lives in a specific namespace.
// This src codeunit is in namespace MySales.Document and exposes
// the enum for use by consumers that import that namespace.

namespace MySales.Document;

enum 50540 "Print Option"
{
    Extensible = false;
    value(0; None) { Caption = 'None'; }
    value(1; Draft) { Caption = 'Draft'; }
    value(2; Final) { Caption = 'Final'; }
}

codeunit 50541 "Print Option Helper"
{
    procedure GetDefault(): Enum "Print Option"
    var
        Opt: Enum "Print Option";
    begin
        exit(Opt);
    end;

    procedure GetFinal(): Enum "Print Option"
    begin
        exit(Enum::"Print Option"::Final);
    end;

    procedure GetOrdinal(Opt: Enum "Print Option"): Integer
    begin
        exit(Opt.AsInteger());
    end;
}
