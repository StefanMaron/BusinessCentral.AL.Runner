codeunit 56302 "Cross Ext Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        HelperA: Codeunit "AppA Helper";
        HelperB: Codeunit "AppB Helper";

    [Test]
    procedure BothExtensionsCompileSuccessfully()
    begin
        // Positive: both codeunits from different extensions are callable
        // despite their pageextensions sharing the name "ItemCardExt"
        Assert.AreEqual('Alpha', HelperA.GetAppAValue(), 'AppA helper should return Alpha');
        Assert.AreEqual('Beta', HelperB.GetAppBValue(), 'AppB helper should return Beta');
    end;

    [Test]
    procedure CrossExtCompilationNotFalsePositive()
    begin
        // Negative: verify the helpers return their distinct values
        Assert.AreNotEqual(HelperA.GetAppAValue(), HelperB.GetAppBValue(), 'AppA and AppB should return different values');
    end;
}
