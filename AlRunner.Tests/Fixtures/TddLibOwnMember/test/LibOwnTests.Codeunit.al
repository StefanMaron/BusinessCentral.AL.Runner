/// <summary>
/// #5271: "SameModule_Stub" reaches a member missing on a codeunit of the same library the call is in;
/// "OtherLibraryObject_IsNotAnnotated" reaches nothing missing.
/// </summary>
codeunit 71980 "Lib Own Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SameModule_Stub()
    var
        Same: Codeunit "Lib Own Same";
    begin
        if Same.Calc() <> 0 then
            Error('the stub returns the default');
    end;

    [Test]
    procedure OtherLibraryObject_IsNotAnnotated()
    var
        Other: Codeunit "Lib Own Other";
    begin
        if Other.Seven() <> 7 then
            Error('Seven returns 7');
    end;
}
