/// <summary>
/// Pins the AllObj system virtual table (2000000038).
///
/// The AllObj metatable is parsed out of the System Application package so the
/// table exists, but nothing provides rows for it — so AllObj.Get(...) returns
/// false for every object id, including objects the runner compiled itself
/// moments earlier. Real BC answers truthfully.
///
/// AL that gates on object existence through AllObj is a normal, legitimate
/// pattern, and it silently takes the not-found branch here. Pageworks raises
/// 'reportNotFound: Report N does not exist or you do not have permission to
/// read it' for 13 tests on that basis, 12 of them for its OWN source-compiled
/// reports — the failure looks like a report problem and is actually an empty
/// system table.
///
/// The negative tests matter as much as the positive ones: a provider that just
/// answers true, or that ignores Object Type, would satisfy the positive cases
/// alone. Those are pinned explicitly below.
/// </summary>
codeunit 61862 "AOV Tests"
{
    Subtype = Test;

    [Test]
    procedure AllObjGet_FindsAReportCompiledInThisApp()
    var
        AllObjRec: Record AllObj;
    begin
        if not AllObjRec.Get(AllObjRec."Object Type"::Report, 61860) then
            Error('AllObj.Get(Report, 61860) returned false, but report 61860 is defined in this app and was just compiled.');

        if AllObjRec."Object Name" <> 'AOV Probe Report' then
            Error('Expected Object Name ''AOV Probe Report'' for report 61860, got ''%1''', AllObjRec."Object Name");
    end;

    [Test]
    procedure AllObjGet_FindsATableCompiledInThisApp()
    var
        AllObjRec: Record AllObj;
    begin
        if not AllObjRec.Get(AllObjRec."Object Type"::Table, 61860) then
            Error('AllObj.Get(Table, 61860) returned false, but table 61860 is defined in this app.');

        if AllObjRec."Object Name" <> 'AOV Row' then
            Error('Expected Object Name ''AOV Row'' for table 61860, got ''%1''', AllObjRec."Object Name");
    end;

    [Test]
    procedure AllObjGet_FindsACodeunitCompiledInThisApp()
    var
        AllObjRec: Record AllObj;
    begin
        if not AllObjRec.Get(AllObjRec."Object Type"::Codeunit, 61861) then
            Error('AllObj.Get(Codeunit, 61861) returned false, but codeunit 61861 is defined in this app.');
    end;

    [Test]
    procedure AllObjGet_DistinguishesObjectType()
    var
        AllObjRec: Record AllObj;
    begin
        // Report 61860 and table 61860 share an id. A provider that ignored
        // Object Type, or that answered true for any id it had seen, would pass
        // the positive tests above and fail here: there is no CODEUNIT 61860.
        if AllObjRec.Get(AllObjRec."Object Type"::Codeunit, 61860) then
            Error('AllObj.Get(Codeunit, 61860) returned TRUE, but only table and report 61860 exist.');
    end;

    [Test]
    procedure AllObjGet_ReturnsFalseForAnIdThatDoesNotExist()
    var
        AllObjRec: Record AllObj;
    begin
        // 61869 is inside this app's declared range but no object claims it, so
        // "everything in my range exists" is also not a valid implementation.
        if AllObjRec.Get(AllObjRec."Object Type"::Report, 61869) then
            Error('AllObj.Get(Report, 61869) returned TRUE, but report 61869 is not defined anywhere.');

        if AllObjRec.Get(AllObjRec."Object Type"::Table, 61869) then
            Error('AllObj.Get(Table, 61869) returned TRUE, but table 61869 is not defined anywhere.');
    end;

    [Test]
    procedure AllObj_IterationYieldsThisAppsReport()
    begin
        // Get() could be special-cased; a filtered iteration proves the rows are
        // really in the table.
        if CountReportsWithId(61860) <> 1 then
            Error('Expected exactly 1 AllObj row for report 61860 when iterating with a filter, got %1',
                CountReportsWithId(61860));
    end;

    local procedure CountReportsWithId(ObjectId: Integer): Integer
    var
        AllObjRec: Record AllObj;
    begin
        AllObjRec.SetRange("Object Type", AllObjRec."Object Type"::Report);
        AllObjRec.SetRange("Object ID", ObjectId);
        exit(AllObjRec.Count());
    end;
}
