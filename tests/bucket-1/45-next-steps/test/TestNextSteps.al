codeunit 54802 "Test Next Steps"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure NextOneMovesToNextRecord()
    var
        Rec: Record "Next Probe";
        Steps: Integer;
    begin
        Seed();
        Rec.FindFirst();
        Assert.AreEqual('A', Rec."No.", 'Precondition: first record is A');

        Steps := Rec.Next(1);
        Assert.AreEqual(1, Steps, 'Next(1) should return 1');
        Assert.AreEqual('B', Rec."No.", 'After Next(1) should be at B');
    end;

    [Test]
    procedure NextSkipsNRecords()
    var
        Rec: Record "Next Probe";
        Steps: Integer;
    begin
        Seed();
        Rec.FindFirst();
        Steps := Rec.Next(2);
        Assert.AreEqual(2, Steps, 'Next(2) should return 2');
        Assert.AreEqual('C', Rec."No.", 'After Next(2) from A should be at C');
    end;

    [Test]
    procedure NextPastEndReturnsFewerSteps()
    var
        Rec: Record "Next Probe";
        Steps: Integer;
    begin
        // Negative/boundary: asking for more steps than remain returns actual moves.
        Seed();
        Rec.FindFirst();
        Rec.Next(3);
        Assert.AreEqual('D', Rec."No.", 'Precondition: at D (4th)');

        Steps := Rec.Next(10);
        Assert.AreEqual(1, Steps,
            'From D with 5 rows total, only one step remains (to E)');
        Assert.AreEqual('E', Rec."No.", 'Cursor lands on E');
    end;

    [Test]
    procedure NextAtEndReturnsZero()
    var
        Rec: Record "Next Probe";
    begin
        // Negative: at last record, Next returns 0.
        Seed();
        Rec.FindLast();
        Assert.AreEqual('E', Rec."No.", 'Precondition: at E');
        Assert.AreEqual(0, Rec.Next(1),
            'Next(1) at end must return 0');
    end;

    [Test]
    procedure NextBackwardMovesReverse()
    var
        Rec: Record "Next Probe";
        Steps: Integer;
    begin
        // Positive: negative step count moves backward.
        Seed();
        Rec.FindLast();
        Assert.AreEqual('E', Rec."No.", 'Precondition: at E');

        Steps := Rec.Next(-2);
        Assert.AreEqual(-2, Steps, 'Next(-2) should return -2');
        Assert.AreEqual('C', Rec."No.", 'After Next(-2) from E should be at C');
    end;

    [Test]
    procedure NextBackwardPastStartReturnsFewer()
    var
        Rec: Record "Next Probe";
        Steps: Integer;
    begin
        // Negative/boundary: asking to go before first returns what's possible.
        Seed();
        Rec.FindFirst();
        Rec.Next(1);
        Assert.AreEqual('B', Rec."No.", 'Precondition: at B');

        Steps := Rec.Next(-5);
        Assert.AreEqual(-1, Steps,
            'From B with only 1 row before, Next(-5) should return -1');
        Assert.AreEqual('A', Rec."No.", 'Cursor lands on A');
    end;

    [Test]
    procedure NextHonoursFilter()
    var
        Rec: Record "Next Probe";
    begin
        // Positive: Next advances within the filtered result set.
        Seed();
        Rec.SetRange(Status, 1);  // A, C, E
        Rec.FindFirst();
        Assert.AreEqual('A', Rec."No.", 'First Status=1 row is A');

        Rec.Next(1);
        Assert.AreEqual('C', Rec."No.",
            'Within Status=1 filter, Next(1) should skip B and land on C');
        Rec.Next(1);
        Assert.AreEqual('E', Rec."No.",
            'Within Status=1 filter, next should be E');
        Assert.AreEqual(0, Rec.Next(1),
            'No more filtered rows after E');
    end;

    local procedure Seed()
    var
        Rec: Record "Next Probe";
    begin
        Insert2('A', 1);
        Insert2('B', 2);
        Insert2('C', 1);
        Insert2('D', 2);
        Insert2('E', 1);
    end;

    local procedure Insert2("No.": Code[20]; Status: Integer)
    var
        Rec: Record "Next Probe";
    begin
        Rec.Init();
        Rec."No." := "No.";
        Rec.Status := Status;
        Rec.Insert(true);
    end;
}
