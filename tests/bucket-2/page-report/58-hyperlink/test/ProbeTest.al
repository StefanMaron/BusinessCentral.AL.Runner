codeunit 56581 "HL Probe Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure HyperlinkIsNoOp()
    var
        Probe: Codeunit "HL Probe";
    begin
        // [WHEN] Calling Hyperlink('...') — there's nowhere to open a browser
        // [THEN] The call must return cleanly and execution continues to the sentinel
        Assert.AreEqual(42, Probe.OpenDoc(), 'Hyperlink must not throw');
    end;

    [Test]
    procedure HyperlinkWithBuiltUrl()
    var
        Probe: Codeunit "HL Probe";
    begin
        Assert.AreEqual(99, Probe.OpenDocMessage('hello'), 'Hyperlink with interpolated URL must not throw');
    end;
}
