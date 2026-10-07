// Issue #5349. A record on another company holds its rows in a store the runner keeps beside the
// session company's one (RecordPatches.CompanyStores.cs). That store must not outlive the test
// codeunit that wrote it: the Company row that made the company reachable does not, and a later
// codeunit that inserts a company of the same name must find no rows in it.
//
// The two codeunits below do the same thing and run one after the other. Each asserts the other
// company is empty BEFORE it writes, so whichever runs second is the one that would see a leak.
// BC's own behaviour (rows apart per company, rollback, FlowFields) is measured upstream, in
// corpus codeunits 69970 to 69975.
codeunit 66801 "CSI Tests One"
{
    Subtype = Test;

    var
        Lib: Codeunit "CSI Lib";

    [Test]
    procedure CsiOtherCompanyStartsEmptyThenIsWritten()
    begin
        Lib.StartEmptyThenWrite();
    end;
}

codeunit 66802 "CSI Tests Two"
{
    Subtype = Test;

    var
        Lib: Codeunit "CSI Lib";

    [Test]
    procedure CsiOtherCompanyStartsEmptyThenIsWrittenAgain()
    begin
        Lib.StartEmptyThenWrite();
    end;
}

codeunit 66803 "CSI Lib"
{
    procedure StartEmptyThenWrite()
    var
        Company: Record Company;
        Home: Record "CSI Row";
        Other: Record "CSI Row";
    begin
        Company.Init();
        Company.Name := 'CSI OTHER';
        if not Company.Insert() then
            Error('the company row of an earlier codeunit is still there');

        if not Other.ChangeCompany('CSI OTHER') then
            Error('ChangeCompany to the inserted company answered false');
        if Other.Count() <> 0 then
            Error('the other company starts with %1 row(s) an earlier codeunit left there', Other.Count());

        Other.Init();
        Other."Entry No." := 1;
        Other.Value := 42;
        Other.Insert();
        if Other.Count() <> 1 then
            Error('the other company holds %1 row(s) after its own insert', Other.Count());

        if Home.Count() <> 0 then
            Error('the session company holds %1 row(s), the other company''s insert reached it', Home.Count());
    end;
}
