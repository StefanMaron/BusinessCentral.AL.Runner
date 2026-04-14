codeunit 53800 "Test CurrPage"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtWithCurrPageDoesNotCascade()
    var
        Rec: Record "CurrPage Table";
        Logic: Codeunit "CurrPage Logic";
    begin
        // Positive: page extension using CurrPage doesn't cascade exclude the table
        Rec.Init();
        Rec."No." := 'CP1';
        Rec."Status" := 'Active';
        Rec.Insert(true);

        Rec.Get('CP1');
        Assert.AreEqual('Active', Logic.GetStatus(Rec), 'Status should be Active');
    end;

    [Test]
    procedure PageExtWithCurrPageNegative()
    var
        Rec: Record "CurrPage Table";
        Logic: Codeunit "CurrPage Logic";
    begin
        // Negative: verify actual value, not default
        Rec.Init();
        Rec."No." := 'CP2';
        Rec."Status" := 'Closed';
        Rec.Insert(true);

        Rec.Get('CP2');
        Assert.AreNotEqual('Active', Logic.GetStatus(Rec), 'Status should not be Active');
    end;
}
