/// <summary>
/// References CountOpen, a procedure "Tdd Target Cu" does not declare yet, and asserts the
/// value an EMPTY generated stub returns (0). With --tdd (#5147) this test passes, and its
/// result names the generated member it ran against.
/// </summary>
codeunit 65016 "Tdd Default Assert Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Assert";

    [Test]
    procedure DefaultReturn_PassesAgainstEmptyStub()
    var
        Target: Codeunit "Tdd Target Cu";
    begin
        Assert.AreEqual(0, Target.CountOpen(7), 'an empty generated stub returns 0');
    end;
}
