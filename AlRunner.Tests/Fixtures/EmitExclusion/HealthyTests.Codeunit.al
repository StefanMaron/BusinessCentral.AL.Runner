/// <summary>
/// The healthy half of the fixture: these tests bind and run normally. They exist
/// so the run still produces PASSING tests while a sibling object is excluded —
/// which is exactly the shape that made the silent drop invisible.
/// </summary>
codeunit 60610 "Emit Excl Healthy Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Emit Excl Assert";

    [Test]
    procedure Healthy_Addition_StillRuns()
    begin
        Assert.AreEqual(3, 1 + 2, 'the healthy object must compile and run');
    end;
}
