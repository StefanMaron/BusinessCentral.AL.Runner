codeunit 60051 "CLR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "CLR Src";

    [Test]
    procedure Clear_Text_ResetsToEmpty()
    begin
        Assert.AreEqual('', Src.ClearTextAndReturn('hello'),
            'Clear(Text) must reset to empty string');
    end;

    [Test]
    procedure Clear_Text_DoesNotPreserveInput_NegativeTrap()
    begin
        // Negative: if Clear were a no-op the call would return the input unchanged.
        Assert.AreNotEqual('hello', Src.ClearTextAndReturn('hello'),
            'Clear(Text) must not leave input intact');
    end;

    [Test]
    procedure Clear_Integer_ResetsToZero()
    begin
        Assert.AreEqual(0, Src.ClearIntAndReturn(42),
            'Clear(Integer) must reset to 0');
    end;

    [Test]
    procedure Clear_Decimal_ResetsToZero()
    begin
        Assert.AreEqual(0, Src.ClearDecimalAndReturn(3.14),
            'Clear(Decimal) must reset to 0');
    end;

    [Test]
    procedure Clear_Boolean_ResetsToFalse()
    begin
        Assert.IsFalse(Src.ClearBooleanAndReturn(true),
            'Clear(Boolean) must reset to false');
    end;

    [Test]
    procedure Clear_Date_ResetsToZero()
    begin
        Assert.AreEqual(0D, Src.ClearDateAndReturn(Today()),
            'Clear(Date) must reset to 0D');
    end;

    [Test]
    procedure Clear_Record_ResetsAllFields()
    begin
        Assert.IsTrue(Src.ClearRecordReturnsEmptyFields(),
            'Clear(Record) must reset all fields to defaults');
    end;

    [Test]
    procedure Clear_List_ResetsToEmpty()
    begin
        Assert.AreEqual(0, Src.ClearListAndReturnCount(),
            'Clear(List) must result in an empty list');
    end;

    [Test]
    procedure ClearAll_ResetsGlobals()
    begin
        Assert.IsTrue(Src.ClearAllClearsBothGlobals(),
            'ClearAll() must reset all globals on the codeunit');
    end;
}
