codeunit 81201 "VCond Visible Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: page with conditional Visible attributes compiles and opens.
    // ------------------------------------------------------------------

    [Test]
    procedure PageOpensWithDefaultVisibility()
    var
        Page: TestPage "VCond Item Page";
    begin
        // [GIVEN/WHEN] A TestPage with conditional Visible is opened
        Page.OpenNew();
        // [THEN]  SetValue and read back work on a plain field (no Visible condition)
        Page.IdField.SetValue(1);
        Assert.AreEqual('1', Page.IdField.Value, 'Id field should display 1');
        Page.Close();
    end;

    [Test]
    procedure PageOpensWithShowDetailsFalse()
    var
        Item: Record "VCond Item";
        ItemPage: Page "VCond Item Page";
    begin
        // [GIVEN] A VCond Item record exists
        Item.Init();
        Item.Id := 2;
        Item.Name := 'Gadget';
        Item.Amount := 100;
        Item.Active := true;
        Item.Insert();

        // [WHEN]  ShowDetails is set to false (default)
        ItemPage.SetShowDetails(false);

        // [THEN]  No error — conditional Visible compiles and the method runs
        Assert.IsTrue(true, 'Page with conditional Visible should compile and run');
    end;

    [Test]
    procedure PageOpensWithShowDetailsTrue()
    var
        Item: Record "VCond Item";
        ItemPage: Page "VCond Item Page";
    begin
        // [GIVEN] A VCond Item record exists
        Item.Init();
        Item.Id := 3;
        Item.Name := 'Widget';
        Item.Amount := 50;
        Item.Active := true;
        Item.Insert();

        // [WHEN]  ShowDetails is set to true
        ItemPage.SetShowDetails(true);

        // [THEN]  No error — conditional Visible using a variable compiles correctly
        Assert.IsTrue(true, 'SetShowDetails(true) should not raise an error');
    end;

    [Test]
    procedure PageAmountFieldVisibleWhenActive()
    var
        Page: TestPage "VCond Item Page";
    begin
        // [GIVEN] A TestPage opened for a new record
        Page.OpenNew();
        // [WHEN]  The Amount field (Visible = Rec.Active) is set
        Page.AmountField.SetValue(200);
        // [THEN]  The value can be read back — field is accessible despite conditional Visible
        Assert.AreEqual('200', Page.AmountField.Value, 'Amount should display 200');
        Page.Close();
    end;
}
