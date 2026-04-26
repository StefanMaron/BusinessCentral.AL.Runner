/// Helper codeunit exercising Guid.ToText() overloads.
codeunit 82100 "GT Src"
{
    /// Returns g.ToText() — default format with braces and hyphens.
    procedure ToTextDefault(g: Guid): Text
    begin
        exit(g.ToText());
    end;

    /// Returns g.ToText(false) — format without hyphens or braces.
    procedure ToTextNoDelimiters(g: Guid): Text
    begin
        exit(g.ToText(false));
    end;

    /// Returns g.ToText(true) — explicit braces-and-hyphens form.
    procedure ToTextWithDelimiters(g: Guid): Text
    begin
        exit(g.ToText(true));
    end;

    /// Returns length of default ToText() result.
    procedure DefaultLength(g: Guid): Integer
    begin
        exit(StrLen(g.ToText()));
    end;

    /// Returns length of ToText(false) result.
    procedure NoDelimLength(g: Guid): Integer
    begin
        exit(StrLen(g.ToText(false)));
    end;
}
