codeunit 100011 "Case Validate Test"
{
    Subtype = Test;

    [Test]
    procedure CaseStatement_InCodeunit_Works()
    var
        Helper: Codeunit "Case Helper";
        Assert: Codeunit "Library Assert";
    begin
        // Test case statement in a normal codeunit (not through trigger dispatch)
        Assert.AreEqual(1, Helper.GetPosition('22'), 'Rate 22 -> position 1');
        Assert.AreEqual(2, Helper.GetPosition('8'), 'Rate 8 -> position 2');
        Assert.AreEqual(3, Helper.GetPosition('5'), 'Rate 5 -> position 3');
        Assert.AreEqual(0, Helper.GetPosition('unknown'), 'Unknown -> position 0');
    end;

    [Test]
    procedure Validate_RateCode22_Position1()
    var
        Rec: Record "Case Validate Table";
        Assert: Codeunit "Library Assert";
    begin
        Rec.PK := 'T1';
        Rec.Insert(false);
        Rec.Validate("Rate Code", '22');
        Assert.AreEqual(1, Rec.Position, 'Rate code 22 should map to position 1');
    end;

    [Test]
    procedure Validate_RateCode8_Position2()
    var
        Rec: Record "Case Validate Table";
        Assert: Codeunit "Library Assert";
    begin
        Rec.PK := 'T2';
        Rec.Insert(false);
        Rec.Validate("Rate Code", '8');
        Assert.AreEqual(2, Rec.Position, 'Rate code 8 should map to position 2');
    end;
}
