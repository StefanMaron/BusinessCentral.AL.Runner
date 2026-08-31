/// <summary>
/// The second half of issue #2262's boundary claim, and it has to be a SEPARATE codeunit: the
/// runner restores the install baseline at each codeunit boundary, and the tests inside one
/// codeunit share a database, so a single codeunit cannot observe the restore at all.
///
/// Identical body to codeunit 64404, deliberately. Each codeunit asserts the pristine state
/// and then dirties it, so whichever of the two the runner happens to run SECOND is asserting
/// after a boundary restore of the other's real damage — a modified row, a deleted row and an
/// inserted row. See "TDF Lazy Load Steps" for the full argument, including the measured
/// codeunit order that ruled out the obvious dirty-then-check shape.
///
/// NOT RUN BY CI — see README.md in this directory.
/// </summary>
codeunit 64405 "Test Data Lazy Load Boundary"
{
    Subtype = Test;

    var
        Steps: Codeunit "TDF Lazy Load Steps";

    [Test]
    procedure LazilyLoadedRowsSurviveTheCodeunitBoundaryRestore()
    begin
        Steps.AssertPristineThenDirty();
    end;
}
