codeunit 71931 "SIL Second"
{
    Subtype = Test;

    [Test]
    procedure SecondCodeunit_SeesTheFirstCodeunitsSingleInstanceState()
    var
        Single: Codeunit "SIL Single";
    begin
        if Single.GetBumps() <> 1 then
            Error('SingleInstance state did not survive the test-codeunit boundary: expected Bumps 1, got %1', Single.GetBumps());
    end;

    [Test]
    procedure SecondCodeunit_SingleInstanceRecordsReadTheRolledBackStore()
    var
        Single: Codeunit "SIL Single";
        Row: Record "SIL Row";
    begin
        if Row.Count() <> 0 then
            Error('the first codeunit''s row survived the boundary: plain record counts %1', Row.Count());
        if Single.CountAll() <> '0/0/0/0/0' then
            Error('SingleInstance records must read the rolled-back store: expected 0/0/0/0/0, got %1', Single.CountAll());
        if Single.GetByKey('A') then
            Error('Get through a SingleInstance record found a row the boundary rolled back');
        if Single.BlobHasValue() then
            Error('CalcFields through a SingleInstance record read a BLOB the boundary rolled back');
        if Single.CountIndirect() <> '0/0/0/0' then
            Error('records held through interface/variant/list/dictionary must read the rolled-back store: expected 0/0/0/0, got %1', Single.CountIndirect());
        if Single.Filters() <> 'Key: <>ZZZ|Val: 0' then
            Error('SingleInstance records must keep their filters across the boundary, got %1', Single.Filters());
    end;
}
