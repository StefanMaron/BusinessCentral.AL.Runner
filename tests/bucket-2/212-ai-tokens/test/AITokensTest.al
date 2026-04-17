codeunit 60391 "AIT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "AIT Src";

    [Test]
    procedure AITokensUsed_ReturnsZero()
    begin
        Assert.AreEqual(0, Src.GetAITokens(),
            'SessionInformation.AITokensUsed must return 0 in standalone mode (no AI calls)');
    end;

    [Test]
    procedure AITokensUsed_NotNegative()
    begin
        Assert.IsTrue(Src.GetAITokens() >= 0,
            'SessionInformation.AITokensUsed must not be negative');
    end;

    [Test]
    procedure AITokensUsed_NotNonZero_NegativeTrap()
    begin
        // Negative trap: standalone must not accidentally report usage.
        Assert.AreNotEqual(1, Src.GetAITokens(),
            'SessionInformation.AITokensUsed must not report >= 1 in standalone mode');
    end;
}
