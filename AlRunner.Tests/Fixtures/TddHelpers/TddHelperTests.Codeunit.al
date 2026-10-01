/// <summary>
/// Reaches generated procedures only through other procedures (#5147): CountClosed through a
/// local helper in this object, CountPending through "Tdd Helper Library". The AL0132 sits in
/// the called procedure, not in the [Test] body, so each test must still be annotated with the
/// stub it reaches.
/// </summary>
codeunit 65051 "Tdd Helper Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Helper Assert";

    [Test]
    procedure ViaLocalHelper_RunsAgainstGeneratedStub()
    begin
        Assert.AreEqual(0, CountClosedViaHelper(), 'an empty generated stub returns 0');
    end;

    [Test]
    procedure ViaLibraryCodeunit_RunsAgainstGeneratedStub()
    var
        Library: Codeunit "Tdd Helper Library";
    begin
        Assert.AreEqual(0, Library.CountPending(), 'an empty generated stub returns 0');
    end;

    local procedure CountClosedViaHelper(): Integer
    var
        Target: Codeunit "Tdd Helper Target Cu";
        Result: Integer;
    begin
        Result := Target.CountClosed(3);
        exit(Result);
    end;
}
