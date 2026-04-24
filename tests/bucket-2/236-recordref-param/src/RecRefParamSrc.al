// Suite 236: RecordRef as a non-var procedure parameter, with a Record variable
// passed to it in the same codeunit.
// Reproduces CS1503: cannot convert from 'AlRunner.Runtime.MockRecordHandle'
// to 'Microsoft.Dynamics.Nav.Runtime.NavRecord'.
// Root cause: BC emits ALCompiler.ToRecordRef(scope, rec.Target) when a Record
// variable is passed by value to a RecordRef parameter; the rewriter did not
// handle ALCompiler.ToRecordRef, so the raw BC method call survived into Roslyn
// compilation where MockRecordHandle (from rec.Target rewrite) is incompatible
// with the NavRecord parameter of the real ALCompiler.ToRecordRef.

table 56580 "RecRef Param Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
}
