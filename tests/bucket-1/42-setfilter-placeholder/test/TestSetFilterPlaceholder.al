codeunit 54500 "Test SetFilter Placeholder"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure InsertLine(EntryNo: Integer; Amount: Decimal; Description: Text[100]; Quantity: Integer)
    var
        Line: Record "SalesLine Stub";
    begin
        Line.Init();
        Line."Entry No." := EntryNo;
        Line."Amount" := Amount;
        Line."Description" := Description;
        Line."Quantity" := Quantity;
        Line.Insert(true);
    end;

    local procedure SetupLines()
    begin
        InsertLine(1, 50.00, 'Apple', 5);
        InsertLine(2, 150.00, 'Banana', 10);
        InsertLine(3, 300.00, 'Cherry', 3);
        InsertLine(4, 75.00, 'Date', 7);
        InsertLine(5, 500.00, 'Elderberry', 2);
    end;

    [Test]
    procedure SetFilterWithSinglePlaceholder()
    var
        Line: Record "SalesLine Stub";
        MinAmount: Decimal;
    begin
        // Positive: SetFilter with %1 substitutes the argument correctly
        SetupLines();
        MinAmount := 100.00;

        // Filter: Amount > 100
        Line.SetFilter("Amount", '>%1', MinAmount);

        // Should find: 150, 300, 500 = 3 records
        Assert.AreEqual(3, Line.Count(), 'SetFilter(>%1, 100) should match 3 lines with Amount > 100');
    end;

    [Test]
    procedure SetFilterWithTwoPlaceholders()
    var
        Line: Record "SalesLine Stub";
        LowAmount: Decimal;
        HighAmount: Decimal;
    begin
        // Positive: SetFilter with %1 and %2 creates a range expression
        SetupLines();
        LowAmount := 50.00;
        HighAmount := 300.00;

        // Filter: Amount > 50 AND Amount < 300
        Line.SetFilter("Amount", '>%1&<%2', LowAmount, HighAmount);

        // Should find: 150 (>50 and <300), 75 (>50 and <300) = 2 records
        Assert.AreEqual(2, Line.Count(), 'SetFilter(>%1&<%2, 50, 300) should match 2 lines');
    end;

    [Test]
    procedure SetFilterPlaceholderWildcard()
    var
        Line: Record "SalesLine Stub";
        Prefix: Text;
    begin
        // Positive: %1 placeholder with wildcard suffix
        SetupLines();
        Prefix := 'B';

        Line.SetFilter("Description", '%1*', Prefix);

        // Should find: Banana = 1 record
        Assert.AreEqual(1, Line.Count(), 'SetFilter(%1*, ''B'') should match 1 description starting with B');
    end;

    [Test]
    procedure SetFilterPlaceholderExcludesNonMatching()
    var
        Line: Record "SalesLine Stub";
        ExactAmount: Decimal;
    begin
        // Negative: records outside the placeholder filter are excluded
        SetupLines();
        ExactAmount := 999.00;

        // Filter: Amount = 999 (no record has this amount)
        Line.SetFilter("Amount", '%1', ExactAmount);

        Assert.AreEqual(0, Line.Count(), 'SetFilter(%1, 999) should match 0 lines');
    end;

    [Test]
    procedure SetFilterPlaceholderEqualityMatch()
    var
        Line: Record "SalesLine Stub";
        TargetAmount: Decimal;
    begin
        // Positive: exact equality via placeholder
        SetupLines();
        TargetAmount := 300.00;

        Line.SetFilter("Amount", '%1', TargetAmount);

        Assert.AreEqual(1, Line.Count(), 'SetFilter(%1, 300) should match exactly 1 line');

        // Also verify FindFirst returns the right record
        Line.FindFirst();
        Assert.AreEqual(300.00, Line."Amount", 'Found record should have Amount = 300');
    end;

    [Test]
    procedure SetFilterPlaceholderIntegerField()
    var
        Line: Record "SalesLine Stub";
        MinQty: Integer;
        MaxQty: Integer;
    begin
        // Positive: placeholder substitution works on Integer fields
        SetupLines();
        MinQty := 3;
        MaxQty := 7;

        // Filter: Quantity >= 3 AND Quantity <= 7
        Line.SetFilter("Quantity", '>=%1&<=%2', MinQty, MaxQty);

        // Should find: Qty 5 (Apple), Qty 3 (Cherry), Qty 7 (Date) = 3 records
        Assert.AreEqual(3, Line.Count(), 'SetFilter(>=%1&<=%2, 3, 7) should match 3 lines');
    end;
}
