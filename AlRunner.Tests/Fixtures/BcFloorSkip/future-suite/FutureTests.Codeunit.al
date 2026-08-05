/// <summary>
/// Declares BC >= 99.0.0.0 — a version no artifact will ever satisfy — so this suite must be
/// skipped on every host, deterministically, with no dependence on which BC is installed.
///
/// The single test fails UNCONDITIONALLY. That is deliberate: it is what makes the assertion
/// non-vacuous. If the floor check regresses, this suite either runs (and the failing test
/// turns the run red) or fails to emit (a suite error, which reaches the exit code since
/// 6fbb6ff1). There is no path where a broken skip still exits 0.
/// </summary>
codeunit 60820 "BC Floor Skip Future"
{
    Subtype = Test;

    [Test]
    procedure BcFloorSkip_FutureSuite_MustNeverExecute()
    begin
        Error('this suite declares BC >= 99.0.0.0 and must never be executed');
    end;
}
