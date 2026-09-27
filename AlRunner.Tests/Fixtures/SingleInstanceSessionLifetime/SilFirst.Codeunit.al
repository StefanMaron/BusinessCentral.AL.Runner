// Runs before 71931: the runner orders test codeunits by object id (#2801).
codeunit 71930 "SIL First"
{
    Subtype = Test;

    [Test]
    procedure FirstCodeunit_StartsClean_ThenLeavesState()
    var
        Single: Codeunit "SIL Single";
        Probe: Record "SIL Row";
        Out: OutStream;
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
        if not Single.GetByKey('A') then
            Error('first test codeunit: Get on the inserted row must succeed');
        // The SingleInstance record loads the row while it has no BLOB; the BLOB is written
        // afterwards through another record, so only the store holds it. The runner keeps a
        // buffered BLOB when a row is gone, boundary or not, so it must not be in the buffer.
        Single.LoadBlobRecord('A');
        Probe.Get('A');
        Probe.Pic.CreateOutStream(Out);
        Out.WriteText('picture');
        Probe.Modify();
        Probe.Get('A');
        Probe.CalcFields(Pic);
        if not Probe.Pic.HasValue() then
            Error('first test codeunit: the BLOB written through another record must be stored');
        if Single.CountIndirect() <> '1/1/1/1' then
            Error('first test codeunit expected 1/1/1/1 through interface/variant/list/dictionary, got %1', Single.CountIndirect());
    end;
}
