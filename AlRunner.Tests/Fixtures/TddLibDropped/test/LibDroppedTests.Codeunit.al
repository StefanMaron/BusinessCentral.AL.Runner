/// <summary>
/// #5266: "RefusedShape_FailsNamingTheDroppedObject" reaches a library codeunit that was dropped for a call
/// --tdd refuses; "OtherLibraryObject_StillRuns" reaches the library codeunit that compiled.
/// </summary>
codeunit 72100 "Lib Dropped Tests"
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
