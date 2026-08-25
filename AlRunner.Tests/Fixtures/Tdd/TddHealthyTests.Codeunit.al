/// <summary>
/// The healthy half of the fixture: this test binds and runs normally, referencing
/// nothing missing. It exists to prove a --tdd run's OTHER tests still pass while
/// three sibling objects are excluded and reported as synthetic failures.
/// </summary>
codeunit 65020 "Tdd Healthy Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Assert";

    [Test]
    procedure UnrelatedTest_StillPasses()
    begin
        Assert.AreEqual(3, 1 + 2, 'the healthy object must compile and run under --tdd too');
    end;
}
