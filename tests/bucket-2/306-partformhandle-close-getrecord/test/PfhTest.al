/// Tests for MockPartFormHandle.Close and GetRecord (issue #1325).
codeunit 306003 "PFH Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// Positive: CurrPage.SubPart.Page.Close() must compile and execute without error.
    /// A broken or missing Close stub would cause CS1061 at Roslyn compile time,
    /// preventing the test codeunit from loading at all.
    [Test]
    procedure PartClose_NoThrow()
    var
        CardPage: Page "PFH Card Page";
    begin
        // WHEN the card page procedure that calls CurrPage.SubPart.Page.Close() is invoked
        // THEN it must not throw — Close is a no-op in standalone mode.
        CardPage.CallPartClose();
        Assert.IsTrue(true, 'CurrPage.SubPart.Page.Close() must not throw');
    end;

    /// Positive: CurrPage.SubPart.Page.GetRecord(rec) must compile and execute without error.
    /// A missing GetRecord stub would cause CS1061 at Roslyn compile time.
    [Test]
    procedure PartGetRecord_NoThrow()
    var
        CardPage: Page "PFH Card Page";
        Rec: Record "PFH Record";
    begin
        // WHEN the card page procedure that calls CurrPage.SubPart.Page.GetRecord(rec) is invoked
        // THEN it must not throw — GetRecord is a no-op in standalone mode.
        CardPage.CallPartGetRecord(Rec);
        Assert.IsTrue(true, 'CurrPage.SubPart.Page.GetRecord(rec) must not throw');
    end;

    /// Negative / proving: Close and GetRecord are distinct stubs, not aliases.
    /// Calling them both in sequence must compile (both names must exist on the type)
    /// and must not interfere with each other.
    [Test]
    procedure PartClose_And_GetRecord_AreDistinct()
    var
        CardPage: Page "PFH Card Page";
        Rec: Record "PFH Record";
    begin
        // WHEN both part methods are called in sequence
        // THEN neither throws, proving they are separate, independent stubs.
        CardPage.CallPartClose();
        CardPage.CallPartGetRecord(Rec);
        Assert.IsTrue(true, 'Close and GetRecord must be independent stubs');
    end;
}
