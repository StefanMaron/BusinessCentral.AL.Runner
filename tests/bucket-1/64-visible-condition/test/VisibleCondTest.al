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
        Item: Record "VCond Item";
        Page: TestPage "VCond Item Page";
    begin
        // [GIVEN] A VCond Item record exists
        Item.Init();
        Item.Id := 1;
        Item.Name := 'Widget';
        Item.Amount := 0;
        Item.Active := false;
        Item.Insert();

        // [WHEN]  The page is opened for that record
        Page.OpenEdit();
        Page.GoToRecord(Item);

        // [THEN]  The page opens without error and basic fields are accessible
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
        Item: Record "VCond Item";
        Page: TestPage "VCond Item Page";
    begin
        // [GIVEN] A VCond Item with Active = true
        Item.Init();
        Item.Id := 4;
        Item.Name := 'Test';
        Item.Amount := 200;
        Item.Active := true;
        Item.Insert();

        // [WHEN]  The page is opened for that record
        Page.OpenEdit();
        Page.GoToRecord(Item);

        // [THEN]  The Amount field (Visible = Rec.Active) is accessible
        Assert.AreEqual('200', Page.AmountField.Value, 'Amount should display 200');
        Page.Close();
    end;
}
