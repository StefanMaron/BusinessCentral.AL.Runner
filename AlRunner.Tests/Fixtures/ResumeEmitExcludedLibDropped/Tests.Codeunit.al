/// <summary>
/// #5292: "RefusedShape_FailsNamingTheDroppedObject" reaches a library codeunit that --tdd dropped; it runs in the
/// resumed attempt, which has to know the dropped codeunit again. "OtherLibraryObject_StillRuns" reaches the library
/// codeunit that compiled.
/// </summary>
codeunit 72510 "Resume Lib Dropped Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RefusedShape_FailsNamingTheDroppedObject()
    var
        Refused: Codeunit "Lib Dropped Refused";
    begin
        if Refused.Calc() <> 0 then
            Error('not reached: the codeunit was dropped');
    end;

    [Test]
    procedure OtherLibraryObject_StillRuns()
    var
        Other: Codeunit "Lib Dropped Other";
    begin
        if Other.Seven() <> 7 then
            Error('Seven returns 7');
    end;
}
