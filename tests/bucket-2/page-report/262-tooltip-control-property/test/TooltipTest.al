codeunit 80951 "TTP Tooltip Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Positive: page with Tooltip properties on controls compiles and the
    // backing table is fully usable.
    // -----------------------------------------------------------------------

    [Test]
    procedure TooltipControl_PageWithTooltips_DoesNotBlockCompilation()
    var
        Rec: Record "TTP Test Record";
    begin
        // Positive: inserting and reading back a record works when the page
        // that uses this table has Tooltip on every field control.
        Rec.Init();
        Rec.Id := 1;
        Rec.Name := 'Widget';
        Rec.Amount := 9.99;
        Rec.Active := true;
        Rec.Insert();

        Rec.Get(1);
        Assert.AreEqual(1, Rec.Id, 'Id must match inserted value');
        Assert.AreEqual('Widget', Rec.Name, 'Name must match inserted value');
        Assert.AreEqual(9.99, Rec.Amount, 'Amount must match inserted value');
        Assert.IsTrue(Rec.Active, 'Active must be true');
    end;

    [Test]
    procedure TooltipControl_MultipleControlTypes_AllFieldsAccessible()
    var
        Rec: Record "TTP Test Record";
    begin
        // Positive: Integer, Text, Decimal and Boolean fields with Tooltip all
        // remain accessible after compilation.
        Rec.Init();
        Rec.Id := 2;
        Rec.Name := 'Gadget';
        Rec.Amount := 0;
        Rec.Active := false;
        Rec.Insert();

        Rec.Get(2);
        Assert.AreEqual('Gadget', Rec.Name, 'Text field accessible when page has Tooltip');
        Assert.AreEqual(0, Rec.Amount, 'Decimal field accessible when page has Tooltip');
        Assert.IsFalse(Rec.Active, 'Boolean field accessible when page has Tooltip');
    end;

    // -----------------------------------------------------------------------
    // Negative: error paths still work when compiled alongside a page with
    // Tooltip properties.
    // -----------------------------------------------------------------------

    [Test]
    procedure TooltipControl_GetNonExistent_RaisesError()
    var
        Rec: Record "TTP Test Record";
    begin
        // Negative: record-not-found error still propagates correctly.
        asserterror Rec.Get(9999);
        Assert.ExpectedError('');
    end;
}
