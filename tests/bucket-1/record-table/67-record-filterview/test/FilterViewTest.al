codeunit 67001 "Filter View Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetFilter_NoFilter_ReturnsEmpty()
    var
        Rec: Record "FV Test Table";
    begin
        Assert.AreEqual('', Rec.GetFilter(Id), 'GetFilter on unfiltered field must be empty');
    end;

    [Test]
    procedure GetFilter_AfterSetRange_ReturnsRangeExpression()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetRange(Id, 1, 5);
        Assert.AreEqual('1..5', Rec.GetFilter(Id), 'GetFilter after SetRange(1,5) must return 1..5');
    end;

    [Test]
    procedure GetFilter_AfterSetFilter_ReturnsExpression()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetFilter(Category, 'A|B');
        Assert.AreEqual('A|B', Rec.GetFilter(Category), 'GetFilter after SetFilter(A|B) must return A|B');
    end;

    [Test]
    procedure GetFilter_AfterReset_ReturnsEmpty()
    var
        Rec: Record "FV Test Table";
    begin
        Rec.SetRange(Id, 1, 10);
        Rec.Reset();
        Assert.AreEqual('', Rec.GetFilter(Id), 'GetFilter after Reset must be empty');
    end;
}
