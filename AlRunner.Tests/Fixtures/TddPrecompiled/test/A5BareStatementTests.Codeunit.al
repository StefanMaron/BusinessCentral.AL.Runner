/// <summary>
/// A bare statement call: a void procedure and a discarded return value look the same, so --tdd refuses
/// to guess a return type. Alone in its file, so nothing else drops this object.
/// </summary>
codeunit 65327 "Precompiled Bare Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BareStatement_IsRefused()
    var
        Points: Codeunit "Precompiled Points";
    begin
        Points.Fire();
    end;
}
