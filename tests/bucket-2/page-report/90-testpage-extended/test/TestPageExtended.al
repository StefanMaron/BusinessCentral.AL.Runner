// Renumbered from 90001 to avoid collision in new bucket layout (#1385).
codeunit 1090001 "TPX TestPage Extended Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPageGoToRecordReturnsTrue()
    var
        TP: TestPage "TPX Test Card";
        Rec: Record "TPX Test Record";
    begin
        // [GIVEN] A record and an open TestPage
        Rec.Id := 1;
        Rec.Name := 'Test';
        Rec.Insert(false);
        TP.OpenEdit();

        // [WHEN] GoToRecord is called
        // [THEN] It returns true (stub behavior)
        Assert.IsTrue(TP.GoToRecord(Rec), 'GoToRecord should return true');
        TP.Close();
    end;

    [Test]
    procedure TestPageNextReturnsFalse()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] An open TestPage
        TP.OpenEdit();

        // [WHEN] Next is called
        // [THEN] It returns false (no rows in standalone mode)
        Assert.IsFalse(TP.Next(), 'Next() should return false in standalone mode');
        TP.Close();
    end;

    [Test]
    procedure TestPageNewDoesNotCrash()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] An open TestPage
        TP.OpenEdit();

        // [WHEN] New is called
        TP.New();

        // [THEN] No crash
        Assert.IsTrue(true, 'New() should compile and not crash');
        TP.Close();
    end;

    [Test]
    procedure TestPageGetPartReturnsHandle()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] An open TestPage with a part
        TP.OpenEdit();

        // [WHEN] A field is set on the parent and a different value on the part
        TP.NameField.SetValue('Parent');
        TP.LinesPart.NameField.SetValue('PartValue');

        // [THEN] Parent and part fields are isolated — they don't interfere
        Assert.AreEqual('Parent', TP.NameField.Value, 'Parent field should retain its value');
        Assert.AreEqual('PartValue', TP.LinesPart.NameField.Value, 'Part field should have its own value');
        Assert.AreNotEqual(Format(TP.NameField.Value), Format(TP.LinesPart.NameField.Value), 'Parent and part values should be independent');
        TP.Close();
    end;

    [Test]
    procedure TestPageFilterSetAndGet()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] An open TestPage
        TP.OpenEdit();

        // [WHEN] A filter is set
        TP.Filter.SetFilter(Name, 'Hello*');

        // [THEN] GetFilter returns the same value
        Assert.AreEqual('Hello*', TP.Filter.GetFilter(Name), 'GetFilter should return the filter that was set');
        TP.Close();
    end;

    [Test]
    procedure TestPageFilterGetFilterEmptyByDefault()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] An open TestPage with no filters set
        TP.OpenEdit();

        // [THEN] GetFilter returns empty string
        Assert.AreEqual('', TP.Filter.GetFilter(Name), 'GetFilter should return empty when no filter set');
        TP.Close();
    end;

    [Test]
    procedure TestPageFieldAsDecimal()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] A TestPage with a decimal field set
        TP.OpenNew();
        TP.AmountField.SetValue(42.5);

        // [WHEN] AsDecimal is called
        // [THEN] The decimal value is returned correctly
        Assert.AreEqual(42.5, TP.AmountField.AsDecimal(), 'AsDecimal should return the set decimal value');
        TP.Close();
    end;

    [Test]
    procedure TestPageFieldEnabled()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] A TestPage with a field
        TP.OpenNew();

        // [WHEN] Enabled is called
        // [THEN] It returns true (stub behavior)
        Assert.IsTrue(TP.NameField.Enabled(), 'Enabled should return true');
        TP.Close();
    end;

    [Test]
    procedure TestPageFieldValueAssignable()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [GIVEN] A TestPage field
        TP.OpenNew();

        // [WHEN] Value is set via SetValue and read back
        TP.NameField.SetValue('Direct');

        // [THEN] The value is readable
        Assert.AreEqual('Direct', TP.NameField.Value, 'Value should be directly assignable and readable');
        TP.Close();
    end;

    [Test]
    procedure TestPageFieldAsDecimalNegative()
    var
        TP: TestPage "TPX Test Card";
    begin
        // [NEGATIVE] AsDecimal should not match a wrong value
        TP.OpenNew();
        TP.AmountField.SetValue(10.0);
        Assert.AreNotEqual(20.0, TP.AmountField.AsDecimal(), 'AsDecimal should not return wrong value');
        TP.Close();
    end;
}
