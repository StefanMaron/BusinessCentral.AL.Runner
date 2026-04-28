// Regression test for issue #1528:
// A user-defined codeunit method named ToText() must NOT be rewritten
// to AlCompat.Format() by the Roslyn rewriter. BC runtime .ToText() calls
// always pass a session argument (null! or a real session) — user-defined
// ToText() methods have 0 arguments and must be left untouched.

codeunit 1319001 "ToText User Method Src"
{
    /// Returns a fixed string. Used to verify that calling a user-defined
    /// ToText() method (0 args, returns Text) does not trigger the
    /// `.ToText() → AlCompat.Format()` rewriter rule.
    procedure ToText(): Text
    begin
        exit('hello from user ToText');
    end;

    /// Calls ToText() from within the same codeunit and returns the result.
    /// This is the pattern that triggered CS0029 before the fix:
    ///   this.result = base.Parent.ToText()
    /// was wrongly rewritten to:
    ///   this.result = AlCompat.Format(_parent)
    /// making `string` appear where `NavText` is expected.
    procedure CallsOwnToText(): Text
    var
        result: Text;
    begin
        result := ToText();
        exit(result);
    end;

    /// Takes a Text parameter named like a ToText call target to ensure
    /// the fix does not break parameter passing.
    procedure FormatValue(v: Variant): Text
    begin
        exit(Format(v));
    end;
}
