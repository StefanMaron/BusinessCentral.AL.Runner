codeunit 73001 "SA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageWithSystemAction_CompilesAndRunsHelper()
    var
        Helper: Codeunit "SA Helper";
    begin
        // Positive: codeunit in same compilation unit as a page with systemaction
        // declarations must compile and be callable.
        Assert.AreEqual('ok', Helper.GetValue(),
            'Helper in systemaction compilation unit must return ok');
    end;

    [Test]
    procedure PageWithSystemAction_HelperLogicIsCorrect()
    var
        Helper: Codeunit "SA Helper";
    begin
        // Prove the helper performs real work — not a no-op stub.
        Assert.AreEqual(12, Helper.Multiply(3, 4), 'Multiply(3,4) must return 12');
    end;
}
