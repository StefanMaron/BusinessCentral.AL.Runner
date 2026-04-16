codeunit 82001 "TCP Tooltip Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPageWithTooltipCompiles()
    var
        TP: TestPage "TCP Test Card";
    begin
        // [GIVEN] A page with Tooltip properties on controls and actions
        // [WHEN] OpenNew is called
        TP.OpenNew();
        TP.Close();
        // [THEN] The page compiles and runs without error — Tooltip is metadata only
        Assert.IsTrue(true, 'Page with Tooltip properties must compile and open without error');
    end;

    [Test]
    procedure TestPageFieldWithTooltipCanSetValue()
    var
        TP: TestPage "TCP Test Card";
    begin
        // [GIVEN] A page where fields have Tooltip properties
        TP.OpenNew();
        // [WHEN] Field values are set (Tooltip is metadata, not runtime behavior)
        TP.NameField.SetValue('TestName');
        TP.AmountField.SetValue(42);
        // [THEN] Field values reflect the set values
        Assert.AreEqual('TestName', TP.NameField.Value(), 'NameField value must match what was set');
        Assert.AreEqual('42', TP.AmountField.Value(), 'AmountField value must match what was set');
        TP.Close();
    end;

    [Test]
    procedure TestPageGroupWithTooltipFields()
    var
        TP: TestPage "TCP Test Card";
    begin
        // [GIVEN] A page with multiple Tooltip-annotated fields in a group
        TP.OpenNew();
        // [WHEN] Boolean and numeric fields with Tooltip are set
        TP.IdField.SetValue(99);
        TP.ActiveField.SetValue(true);
        // [THEN] Values are correctly set; Tooltip does not interfere
        Assert.AreEqual('99', TP.IdField.Value(), 'IdField must return set value');
        Assert.AreEqual('Yes', TP.ActiveField.Value(), 'ActiveField must return set value');
        TP.Close();
    end;

    [Test]
    procedure TestPageWithTooltipOnActionCompiles()
    var
        TP: TestPage "TCP Test Card";
    begin
        // [GIVEN] A page with Tooltip on an action
        // [WHEN] The page is opened
        TP.OpenNew();
        // [THEN] No compilation or runtime error from action Tooltip
        Assert.IsTrue(true, 'Page with Tooltip on action must compile and run');
        TP.Close();
    end;
}
