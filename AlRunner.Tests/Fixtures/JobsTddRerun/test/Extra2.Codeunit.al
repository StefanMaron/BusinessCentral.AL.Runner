// A real test codeunit of the shared bundle (#5318): a worker that runs this bundle and only compiles the app
// folder must report it once, even though --tdd compiles the bundle a second time. It is also what the planner
// weighs: a bundle is shared between workers by its AL file count, and this one needs more than twice the app
// folder's to be shared by two.
codeunit 51123 "Jobs Rerun Extra 2"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Extra2_Runs()
    begin
    end;
}
