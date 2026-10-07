// Named like the dropped codeunit of JobsUnitClaimExcluded, so a run of both bundles has two dropped objects
// of one name at two paths: each belongs to the worker that claims it for ITS bundle.
codeunit 51000 "Jobs Excl Dropped"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Dropped_A()
    var
        Missing: Record "This Table Does Not Exist At All";
    begin
        Missing.Init();
    end;

    [Test]
    procedure Dropped_B()
    begin
    end;
}
