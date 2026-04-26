codeunit 59703 "EP Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure SubscriberReceivesAndModifiesVarParams()
    var
        Publisher: Codeunit "EP Publisher";
        Result: Integer;
    begin
        // Positive: subscriber receives var params and modifies them.
        // Subscriber adds 100 to Amount and sets IsHandled=true,
        // so CalcWithEvent returns Amount+100 instead of Amount*2.
        Result := Publisher.CalcWithEvent(10);
        Assert.AreEqual(110, Result, 'Subscriber should have added 100 and set IsHandled');
    end;

    [Test]
    procedure SubscriberParamsNotForwardedMeansDoubling()
    var
        Publisher: Codeunit "EP Publisher";
        Result: Integer;
    begin
        // Negative: if subscriber params were NOT forwarded (the old bug),
        // IsHandled stays false and result would be 10*2=20.
        // With params forwarded, result is 110.
        // This test verifies the params are actually forwarded.
        Result := Publisher.CalcWithEvent(5);
        Assert.AreEqual(105, Result, 'Subscriber should have modified var params; got doubling instead');
    end;
}
