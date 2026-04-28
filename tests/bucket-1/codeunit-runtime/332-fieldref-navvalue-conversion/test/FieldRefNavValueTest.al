codeunit 1320426 "FieldRef NavValue Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "FieldRef NavValue Src";

    [Test]
    procedure StrSubstNo_WithFieldRef_FormatsValue()
    begin
        Assert.AreEqual('FR1', Src.StrSubstNo_WithFieldRef(),
            'FieldRef should format via StrSubstNo when passed as argument');
    end;

    [Test]
    procedure StrSubstNo_WithFieldRef_WrongExpectationFails()
    begin
        asserterror Assert.AreEqual('WRONG', Src.StrSubstNo_WithFieldRef(),
            'FieldRef formatting should not match the wrong value');
        Assert.ExpectedError('AreEqual');
    end;
}
