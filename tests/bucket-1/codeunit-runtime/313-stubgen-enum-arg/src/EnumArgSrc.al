/// Regression fixture for issue #1419.
///
/// In real-world usage the "Enum Arg Helper" codeunit comes from an .app package
/// and is auto-stubbed by the runner.  The stub generator must emit the Enum
/// parameter type as  "Enum ""..."""  (or "Option" for legacy Option params), NOT
/// as "Code[20]" / "Integer" / "Variant".  A wrong type causes a NavOption→NavCode
/// (or similar) cast error the moment the caller passes an Enum literal.
///
/// This fixture compiles the callee codeunit from source so the test suite can run
/// without a package; the companion C# test (AutoStubEnumParamTests.cs) covers the
/// actual auto-stub path end-to-end when alc.exe is available.
enum 1313001 "Enum Arg Status"
{
    Extensible = false;

    value(0; " ") { Caption = ' '; }
    value(1; Active) { Caption = 'Active'; }
    value(2; Inactive) { Caption = 'Inactive'; }
    value(3; Pending) { Caption = 'Pending'; }
}

/// Simulates the codeunit whose methods have Enum/Option-typed parameters.
/// In production use this would come from a missing library package (auto-stubbed).
codeunit 1313002 "Enum Arg Helper"
{
    /// Method with a named Enum parameter — the canonical #1419 failure pattern.
    /// Returns the ordinal of the passed enum value so the test can assert a
    /// non-default value (proves the value was conveyed, not swallowed by a no-op stub).
    procedure GetStatusOrdinal(Status: Enum "Enum Arg Status"): Integer
    begin
        exit(Status.AsInteger());
    end;

    /// Method that accepts two Enum parameters and returns the one with higher ordinal.
    /// Exercises multi-Enum-param dispatch.
    procedure MaxStatus(A: Enum "Enum Arg Status"; B: Enum "Enum Arg Status"): Enum "Enum Arg Status"
    var
        Result: Enum "Enum Arg Status";
    begin
        if A.AsInteger() >= B.AsInteger() then
            Result := A
        else
            Result := B;
        exit(Result);
    end;

    /// Legacy Option-typed parameter (not Enum "…") — #1419 second failure pattern.
    /// Returns the passed integer so the test can assert a concrete value.
    procedure GetOptionValue(DocType: Option): Integer
    begin
        exit(DocType);
    end;
}
