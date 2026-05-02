/// <summary>
/// Suite 313700 (bucket-1/codeunit-runtime): verify that a codeunit whose DotNet-interop
/// procedures have been stripped by extract-deps (replaced with Error() stubs) compiles
/// and executes normally for its non-DotNet procedures.
///
/// Reproduces issue #1524: extract-deps must strip DotNet-referencing procedure bodies
/// and replace them with Error() stubs so the extracted slice compiles cleanly in the
/// runner. This src codeunit simulates the output format that StripDotNetProcedures
/// produces — a codeunit with both a stripped DotNet procedure and a normal procedure.
/// </summary>
codeunit 1313700 "DotNet Stripped Dep Helper"
{
    /// A normal (non-DotNet) procedure — must work correctly after stripping.
    procedure GetVersion(): Text
    begin
        exit('2.0');
    end;

    /// A procedure whose body was replaced by StripDotNetProcedures.
    /// The exact Error() message format mirrors what extract-deps produces.
    procedure ParseXml(Input: Text): Text
    begin
        Error('AL Runner: ''ParseXml'' uses DotNet interop — not supported in standalone mode. Add this object to your compiled dependency slice.');
    end;
}
