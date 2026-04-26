/// Tests for MockPartFormHandle.PageCaption (issue #1440).
codeunit 311702 "PFH Caption Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// Positive: PageCaption getter must return a non-null string without throwing.
    /// A missing PageCaption property would cause CS1061 at Roslyn compile time,
    /// preventing the test codeunit from loading at all.
    [Test]
    procedure PartPageCaption_Get_NoThrow()
    var
        CardPage: Page "PFH Caption Card Page";
        Result: Text;
    begin
        // WHEN the card page getter that reads CurrPage.SubPart.Page.Caption is called
        // THEN it must compile and return without throwing
        Result := CardPage.GetPartCaption();
        Assert.IsTrue(true, 'CurrPage.SubPart.Page.Caption (get) must not throw');
    end;

    /// Positive: PageCaption setter must accept a value without throwing.
    /// A missing PageCaption setter would cause CS1061 at Roslyn compile time.
    [Test]
    procedure PartPageCaption_Set_NoThrow()
    var
        CardPage: Page "PFH Caption Card Page";
    begin
        // WHEN the card page setter that writes CurrPage.SubPart.Page.Caption is called
        // THEN it must compile and not throw
        CardPage.SetPartCaption('My Caption');
        Assert.IsTrue(true, 'CurrPage.SubPart.Page.Caption (set) must not throw');
    end;

    /// Positive (proving): set then get must return the assigned value.
    /// A no-op getter that always returns '' would fail this test.
    [Test]
    procedure PartPageCaption_SetThenGet_ReturnsAssignedValue()
    var
        CardPage: Page "PFH Caption Card Page";
        Result: Text;
    begin
        // WHEN a specific caption is assigned via the part handle
        // THEN reading it back must return that exact value — not empty or a default
        Result := CardPage.SetThenGetPartCaption('Expected Caption Value');
        Assert.AreEqual('Expected Caption Value', Result, 'PageCaption round-trip must return the assigned value');
    end;

    /// Positive (proving): distinct values must be distinguishable.
    /// Two successive sets with different captions must both be observable.
    [Test]
    procedure PartPageCaption_SetTwice_ReturnsSecondValue()
    var
        CardPage: Page "PFH Caption Card Page";
        First: Text;
        Second: Text;
    begin
        // WHEN caption is set to two different values in sequence
        // THEN each read must return the value that was last set
        First := CardPage.SetThenGetPartCaption('Caption One');
        Second := CardPage.SetThenGetPartCaption('Caption Two');
        Assert.AreEqual('Caption One', First, 'First SetThenGet must return Caption One');
        Assert.AreEqual('Caption Two', Second, 'Second SetThenGet must return Caption Two');
    end;
}
