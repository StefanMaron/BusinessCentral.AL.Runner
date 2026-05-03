/// Helper codeunit used by fail-on-stub tests.
/// This codeunit is compiled from source (not auto-stubbed), so calling it
/// must always succeed regardless of --fail-on-stub.
codeunit 1321001 "FOS Real Helper"
{
    procedure GetValue(): Integer
    begin
        exit(42);
    end;

    procedure DoWork()
    begin
        // Real implementation — not a stub.
    end;

    procedure CallCommit()
    begin
        // Commit() is a no-op in the runner. Without --fail-on-stub it silently
        // passes; with --fail-on-stub it throws RunnerGapException.
        // This helper lets the AL test call Commit() indirectly.
        Commit();
    end;
}
