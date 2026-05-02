codeunit 1900008 "Preproc Helper Test"
{
    Subtype = Test;

    var
        Helper: Codeunit "Preproc Helper";
        Assert: Codeunit Assert;

    // Positive: the always-present procedure returns the expected value.
    // This verifies the runner handles #if/#endif directives in src/ without
    // crashing and that un-gated code still executes correctly (issue #1525).
    [Test]
    procedure AlwaysPresent_Returns10()
    begin
        Assert.AreEqual(10, Helper.AlwaysPresent(), 'AlwaysPresent must return 10');
    end;
}
