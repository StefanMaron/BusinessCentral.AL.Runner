codeunit 72001 "AR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageWithActionRef_CompilesAndRunsHelper()
    var
        Helper: Codeunit "AR Helper";
    begin
        // Positive: codeunit in the same compilation unit as a page with actionref
        // sections must compile and be callable.
        Assert.AreEqual('ok', Helper.GetValue(), 'Helper in actionref compilation unit must return ok');
    end;

    [Test]
    procedure PageWithActionRef_HelperLogicIsCorrect()
    var
        Helper: Codeunit "AR Helper";
    begin
        // Prove the helper performs real work (not a no-op stub).
        Assert.AreEqual(7, Helper.Add(3, 4), 'Add(3,4) must return 7');
    end;
}
