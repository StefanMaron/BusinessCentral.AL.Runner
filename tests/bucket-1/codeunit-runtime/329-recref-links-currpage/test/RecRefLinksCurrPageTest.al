codeunit 1320415 "RR Links CurrPage Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RR Links CurrPage Src";

    [Test]
    procedure RecordRef_HasLinks_AfterAdd()
    begin
        Assert.IsTrue(Src.RecordRefHasLinksAfterAdd(),
            'RecordRef.HasLinks should return true after AddLink');
    end;

    [Test]
    procedure RecordRef_HasLinks_WrongExpectationFails()
    begin
        asserterror Assert.AreEqual(false, Src.RecordRefHasLinksAfterAdd(),
            'RecordRef.HasLinks should not return false after AddLink');
        Assert.ExpectedError('AreEqual');
    end;

    [Test]
    procedure RecordRef_DeleteLinks_Clears()
    begin
        Assert.IsFalse(Src.RecordRefDeleteLinksClears(),
            'RecordRef.HasLinks should be false after DeleteLinks');
    end;

    [Test]
    procedure RecordRef_DeleteLinks_WrongExpectationFails()
    begin
        asserterror Assert.AreEqual(true, Src.RecordRefDeleteLinksClears(),
            'RecordRef.HasLinks should not return true after DeleteLinks');
        Assert.ExpectedError('AreEqual');
    end;

    [Test]
    procedure CurrPage_GetRecord_And_Part_SetRecord_NoOp()
    begin
        Assert.IsTrue(Src.PageCurrPageCalls(),
            'CurrPage.GetRecord and CurrPage.Part.Page.SetRecord should compile and not change record');
    end;

    [Test]
    procedure CurrPage_GetRecord_And_Part_SetRecord_WrongExpectationFails()
    begin
        asserterror Assert.AreEqual(false, Src.PageCurrPageCalls(),
            'CurrPage.GetRecord and CurrPage.Part.Page.SetRecord should not return false');
        Assert.ExpectedError('AreEqual');
    end;
}
