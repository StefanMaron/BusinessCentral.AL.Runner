// Not a test: weight only. The planner shares a bundle between workers by its AL file count, and this
// bundle needs more than twice the app folder's to be shared by two. (A test codeunit here would also be
// lost to #5318, which is not what this fixture is for.)
codeunit 51122 "Jobs Rerun Filler 1"
{
    procedure Value(): Integer
    begin
        exit(1);
    end;
}
