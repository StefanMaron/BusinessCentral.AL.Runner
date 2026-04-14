codeunit 56662 "ES Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure SubscribersFireOnceEach()
    var
        Publisher: Codeunit "ES Publisher";
        C: Record "ES Counter";
    begin
        Publisher.DoIt();
        Assert.IsTrue(C.Get(1), 'Subscriber should have inserted counter row');
        Assert.AreEqual(1, C.Before, 'OnBefore should have fired once');
        Assert.AreEqual(1, C.After, 'OnAfter should have fired once');
    end;

    [Test]
    procedure SubscribersFireOnEachCall()
    var
        Publisher: Codeunit "ES Publisher";
        C: Record "ES Counter";
    begin
        Publisher.DoIt();
        Publisher.DoIt();
        Publisher.DoIt();
        Assert.IsTrue(C.Get(1), 'Counter row should exist');
        Assert.AreEqual(3, C.Before, 'OnBefore should have fired 3 times');
        Assert.AreEqual(3, C.After, 'OnAfter should have fired 3 times');
    end;
}
