/// <summary>
/// Suite 312800 (bucket-1/codeunit-runtime): case-insensitive SubType property discovery.
///
/// Reproduces issue #1520: the gate check at Pipeline.cs:796 used a case-sensitive
/// string comparison, which caused codeunits written with the camelCase spelling
/// (SubType = Test) or all-lowercase spelling (subtype = test) to be silently
/// skipped. The runner exited 0 with "No test codeunits found" instead of running.
///
/// This src codeunit is a plain helper so there is always a compilable src target.
/// The actual test codeunits (using camelCase and lowercase spellings) live in test/.
/// </summary>

codeunit 1312800 "SCD Helper"
{
    procedure Add(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;
}
