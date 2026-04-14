codeunit 50769 "NS Dispatch Test"
{
    Subtype = Test;

    // Positive: same-codeunit call to a record-returning method works.
    [Test]
    procedure TestSameCuRecordReturn()
    var
        Factory: Codeunit "NS Record Factory";
        Assert: Codeunit Assert;
    begin
        Assert.AreEqual('Auto', Factory.CreateAndGetName('X1'),
            'Same-codeunit record-return call should work');
    end;

    // Positive: cross-codeunit call to a record-returning method works.
    [Test]
    procedure TestCrossCuRecordReturn()
    var
        Factory: Codeunit "NS Record Factory";
        Item: Record "NS Item";
        Assert: Codeunit Assert;
    begin
        Item := Factory.CreateItem('Y1', 'Cross', 55);
        Assert.AreEqual('Cross', Item.Name, 'Cross-codeunit record-return call should work');
        Assert.AreEqual(55, Item.Amount, 'Amount should be 55');
    end;

    // Negative: ensure logic errors are caught, not silently swallowed.
    [Test]
    procedure TestRecordReturnWrongValue()
    var
        Factory: Codeunit "NS Record Factory";
        Assert: Codeunit Assert;
    begin
        // CreateAndGetName always passes 'Auto' as name.
        asserterror Assert.AreEqual('Wrong', Factory.CreateAndGetName('Z1'),
            'Should not match');
        Assert.ExpectedError('Assert.AreEqual failed');
    end;
}
