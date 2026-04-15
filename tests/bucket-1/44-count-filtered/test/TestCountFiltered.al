codeunit 54700 "Test Count Filtered"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CountReturnsTotalOnEmptyTable()
    var
        Rec: Record "Count Probe";
    begin
        // Negative: empty table has 0 count.
        Assert.AreEqual(0, Rec.Count, 'Empty table must have Count 0');
    end;

    [Test]
    procedure CountReturnsTotalWhenNoFilter()
    var
        Rec: Record "Count Probe";
    begin
        Seed();
        Assert.AreEqual(5, Rec.Count,
            'Count must be 5 across the whole table (no filter)');
    end;

    [Test]
    procedure CountReturnsFilteredSubset()
    var
        Rec: Record "Count Probe";
    begin
        // Positive: SetRange filters Count to matching subset.
        Seed();
        Rec.SetRange(Status, 1);
        Assert.AreEqual(3, Rec.Count,
            'Count must reflect Status=1 rows only (A, C, E)');

        Rec.SetRange(Status, 2);
        Assert.AreEqual(2, Rec.Count,
            'Count must reflect Status=2 rows only (B, D)');
    end;

    [Test]
    procedure CountFilteredToZeroWhenNoMatch()
    var
        Rec: Record "Count Probe";
    begin
        // Negative: filter with no matches yields Count 0.
        Seed();
        Rec.SetRange(Status, 999);
        Assert.AreEqual(0, Rec.Count,
            'Count must be 0 for a filter that matches nothing');
    end;

    [Test]
    procedure CountReturnsFullAfterReset()
    var
        Rec: Record "Count Probe";
    begin
        // Positive/reset: clearing filters restores the full count.
        Seed();
        Rec.SetRange(Status, 1);
        Assert.AreEqual(3, Rec.Count, 'Precondition: filtered to 3');

        Rec.Reset();
        Assert.AreEqual(5, Rec.Count,
            'Count must return to full 5 after Reset');
    end;

    [Test]
    procedure CountRespectsRangeFilter()
    var
        Rec: Record "Count Probe";
    begin
        // Positive: range-style SetRange is honoured.
        Seed();
        Rec.SetRange(Amount, 20, 40);
        Assert.AreEqual(3, Rec.Count,
            'Amount 20..40 covers rows B(20), C(30), D(40) — expect 3');
    end;

    local procedure Seed()
    var
        Rec: Record "Count Probe";
    begin
        Insert2('A', 1, 10);
        Insert2('B', 2, 20);
        Insert2('C', 1, 30);
        Insert2('D', 2, 40);
        Insert2('E', 1, 50);
    end;

    local procedure Insert2("No.": Code[20]; Status: Integer; Amount: Decimal)
    var
        Rec: Record "Count Probe";
    begin
        Rec.Init();
        Rec."No." := "No.";
        Rec.Status := Status;
        Rec.Amount := Amount;
        Rec.Insert(true);
    end;
}
