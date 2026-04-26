/// <summary>
/// Suite 312700 (bucket-1/codeunit-runtime): [NavTest] attribute-only discovery.
///
/// Reproduces issue #1420: the executor has a "StartsWith('Test')" fallback that
/// picks up ANY procedure named Test* as a test, including local procedures on
/// tables, non-test codeunits, and pages. Those procedures crash with NRE when
/// run without context.
///
/// This table and non-test codeunit each define a procedure whose name starts
/// with "Test". Neither bears [Test]. They must NOT be discovered as test cases.
/// </summary>

// -------------------------------------------------------------------
// A table with a local procedure called TestSomething — reproduces the
// exact shape from the bug report (NALICF Component Line, etc.).
// -------------------------------------------------------------------
table 1312700 "NAD Demo Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[50]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }

    trigger OnInsert()
    begin
        TestSomething();
    end;

    local procedure TestSomething()
    begin
        // Local procedure prefixed with "Test" — must NEVER be discovered as [Test].
        // Called only from OnInsert with a valid Rec context.
        if "No." = '' then
            Error('NAD Demo Table: No. must not be empty on insert.');
    end;
}

// -------------------------------------------------------------------
// A plain (non-Test) codeunit with a public Test* procedure — another
// shape that caused false positives.
// -------------------------------------------------------------------
codeunit 1312701 "NAD Helper"
{
    /// <summary>
    /// A public helper procedure whose name starts with "Test".
    /// It is NOT tagged [Test] and the codeunit has no Subtype = Test.
    /// Must NOT be discovered as a test by the executor.
    /// </summary>
    procedure TestComputeSum(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;
}
