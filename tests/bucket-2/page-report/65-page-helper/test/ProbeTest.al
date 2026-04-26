codeunit 56651 "PH Helper Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure FormatLineBreaksViaPageVariable()
    var
        P: Page "PH Helper Page";
    begin
        Assert.AreEqual('a<br />b', P.FormatLineBreaksForHTML('a\b'), 'Page helper should be callable from a test');
    end;

    [Test]
    procedure IsYesViaPageVariable()
    var
        P: Page "PH Helper Page";
    begin
        Assert.IsTrue(P.IsYes('yes'), 'Second helper should dispatch too');
        Assert.IsFalse(P.IsYes('no'), 'Second helper must return false for non-matching input');
    end;
}
