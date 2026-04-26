/// Helper codeunit for integer-to-text conversion tests (issue #1426).
///
/// BC 26+ introduced an implicit Integer→Text coercion: passing an Integer variable
/// to a Text parameter is accepted by the BC 26+ AL compiler.  When such AL is
/// transpiled to C# the compiler emits a plain 'int' argument to a 'string' parameter,
/// causing CS1503 "cannot convert from 'int' to 'string'".  CS1503Fixer wraps the
/// argument with AlCompat.Format(value) to match BC's implicit conversion.
///
/// These AL tests prove that AlCompat.Format correctly converts integers to their
/// text representation — the exact same operation the CS1503 fixer injects.
/// (BC 17's AL compiler rejects direct Integer-to-Text without Format(), so the
/// explicit Format() call here mirrors what the fixer synthesises at the C# level.)
codeunit 313000 "Int To Text Src"
{
    /// Converts an Integer to its text representation using AL Format().
    /// Mirrors the AlCompat.Format(int) call that CS1503Fixer injects around
    /// every int argument passed to a string/NavText parameter.
    procedure IntToText(Value: Integer): Text
    begin
        exit(Format(Value));
    end;

    /// Accepts a Text parameter and returns it unchanged.
    /// In BC 26+, calling this with an Integer literal is valid AL; in BC 17 the
    /// caller must use Format() explicitly.  This procedure lets tests verify that
    /// the Text value produced by Format() round-trips correctly.
    procedure AcceptText(Txt: Text): Text
    begin
        exit(Txt);
    end;

    /// Calls AcceptText with the result of Format(Value).
    /// This is the pattern CS1503Fixer synthesises: AlCompat.Format(intArg) is
    /// substituted as the argument wherever an int was passed to a string parameter.
    procedure FormatThenAccept(Value: Integer): Text
    begin
        exit(AcceptText(Format(Value)));
    end;
}
