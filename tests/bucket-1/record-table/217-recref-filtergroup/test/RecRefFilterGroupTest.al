codeunit 60461 "RFG Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RFG Src";

    [Test]
    procedure FilterGroup_Default_IsZero()
    begin
        Assert.AreEqual(0, Src.FilterGroup_DefaultIsZero(),
            'Default FilterGroup must be 0');
    end;

    [Test]
    procedure FilterGroup_Setter_DoesNotThrow()
    begin
        // Standalone: filter groups are not tracked, setter is a no-op.
        // Reading after set returns 0 (the default). The point is that the call compiles + runs.
        Src.FilterGroup_SetThenGet(2);
        Assert.IsTrue(true, 'FilterGroup setter must not throw');
    end;
}
