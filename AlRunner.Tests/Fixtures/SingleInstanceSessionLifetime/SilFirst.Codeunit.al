// Runs before 71931: the runner orders test codeunits by object id (#2801).
codeunit 71930 "SIL First"
{
    Subtype = Test;

    [Test]
    procedure FirstCodeunit_StartsClean_ThenLeavesState()
    var
        Single: Codeunit "SIL Single";
    begin
        if Single.GetInstallMarks() <> 0 then
            Error('install-trigger SingleInstance state reached the first test: %1', Single.GetInstallMarks());
        if Single.GetBumps() <> 0 then
            Error('first test codeunit expected Bumps 0, got %1', Single.GetBumps());
        Single.Bump();
        Single.OpenAll();
        Single.InsertRow('A');
        if Single.CountAll() <> '1/1/1/1/1' then
            Error('first test codeunit expected 1/1/1/1/1, got %1', Single.CountAll());
        if Single.CountIndirect() <> '1/1/1/1' then
            Error('first test codeunit expected 1/1/1/1 through interface/variant/list/dictionary, got %1', Single.CountIndirect());
    end;
}
