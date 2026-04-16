codeunit 73001 "Sep Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageWithSeparator_CompilesAndRunsHelper()
    var
        Helper: Codeunit "Sep Helper";
    begin
        // Positive: codeunit in the same compilation unit as a page with separator()
        // actions must compile and be callable.
        Assert.AreEqual('ok', Helper.GetValue(), 'Helper in separator-action compilation unit must return ok');
    end;

    [Test]
    procedure PageWithSeparator_HelperLogicIsCorrect()
    var
        Helper: Codeunit "Sep Helper";
    begin
        // Prove the helper performs real work (not a no-op stub).
        Assert.AreEqual(12, Helper.Multiply(3, 4), 'Multiply(3,4) must return 12');
    end;
}
