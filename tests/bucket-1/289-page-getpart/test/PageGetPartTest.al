codeunit 164001 "PGP Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    /// <summary>
    /// Positive: CurrPage.SubPart.Page.Procedure() returns a default value when no
    /// record is loaded (Value field defaults to 0).
    /// This proves that GetPart dispatch works end-to-end — a broken or missing
    /// GetPart would cause a compile error or runtime exception, not return 0.
    /// </summary>
    [Test]
    procedure GetPart_CallSubPageProc_ReturnsDefaultValue()
    var
        CardPage: Page "PGP Card Page";
    begin
        // WHEN we call a procedure on the card page that delegates to the subpage
        // THEN it returns the Integer default (0) since no record is loaded
        Assert.AreEqual(0, CardPage.CallGetSelectedValue(),
            'CurrPage.SubPart.Page.GetSelectedValue() must return 0 (no record loaded)');
    end;

    /// <summary>
    /// Negative: calling CardPage.CallGetSelectedValue() must not raise an error.
    /// If GetPart is not implemented, the runner produces a compile error — not a
    /// runtime error — so the test codeunit itself would fail to load.
    /// This assertion verifies the feature compiles AND executes without throwing.
    /// </summary>
    [Test]
    procedure GetPart_CardPage_DoesNotThrow()
    var
        CardPage: Page "PGP Card Page";
        Result: Integer;
    begin
        // This would fail at compile time (CS1061) if GetPart is missing from the page class.
        // Running successfully is the assertion.
        Result := CardPage.CallGetSelectedValue();
        Assert.IsTrue(Result >= 0, 'Result must be a non-negative integer');
    end;
}
