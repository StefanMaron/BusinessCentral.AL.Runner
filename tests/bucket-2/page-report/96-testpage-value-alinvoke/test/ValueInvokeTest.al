codeunit 1298002 "Value Invoke Tests"
{
    Subtype = Test;

    [Test]
    procedure ValueUsedAsArgText()
    var
        TestCard: TestPage "Value Invoke Card";
    begin
        // Open a new page and set a non-default value
        TestCard.OpenNew();
        TestCard.NameField.SetValue('Contoso Ltd');

        // Using .Value as a method argument — BC may emit .ALInvoke() on the
        // string result.  This is the exact pattern from issue #1298:
        //   Assert.AreEqual(VendorName, VendorCard.Name.Value(), '...');
        Assert.AreEqual('Contoso Ltd', TestCard.NameField.Value, 'Name field value should match set value');
        TestCard.Close();
    end;

    [Test]
    procedure ValueUsedAsArgDecimal()
    var
        TestCard: TestPage "Value Invoke Card";
    begin
        TestCard.OpenNew();
        TestCard.AmountField.SetValue(99.75);

        // Decimal field Value used as argument
        Assert.AreEqual('99.75', TestCard.AmountField.Value, 'Amount field value should match');
        TestCard.Close();
    end;

    [Test]
    procedure ValueMismatchNegative()
    var
        TestCard: TestPage "Value Invoke Card";
    begin
        TestCard.OpenNew();
        TestCard.NameField.SetValue('Fabrikam Inc');

        // Negative: wrong value must fail
        Assert.AreNotEqual('Contoso Ltd', TestCard.NameField.Value, 'Should not match a different name');
        TestCard.Close();
    end;

    var
        Assert: Codeunit "Library Assert";
}
