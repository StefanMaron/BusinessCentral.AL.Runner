// EDIT-MARKER: 1
// The line above is what the driving test rewrites between --watch cycles. It is a comment in
// THIS file and never in WqsSum.Query.al, deliberately: the defect is about a query the bundle
// still declares but did not touch this cycle, so the query file stays byte-identical while
// something else in the same app changes.
codeunit 70623 "WQS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "WQS Assert";

    local procedure Seed()
    var
        Row: Record "WQS Row";
    begin
        Row.DeleteAll();
        Row.Init(); Row."Entry No." := 1; Row."Cust No." := 'C1'; Row.Amount := 10; Row.Insert();
        Row.Init(); Row."Entry No." := 2; Row."Cust No." := 'C1'; Row.Amount := 32; Row.Insert();
        Row.Init(); Row."Entry No." := 3; Row."Cust No." := 'C2'; Row.Amount := 5;  Row.Insert();
    end;

    [Test]
    procedure QueryReadsItsAggregatedColumnValues()
    var
        Q: Query "WQS Sum";
        Seen: Integer;
        C1Total: Decimal;
    begin
        Seed();
        Assert.IsTrue(Q.Open(), 'the query must open');
        while Q.Read() do begin
            Seen += 1;
            if Q.CustNo = 'C1' then
                C1Total := Q.TotalAmount;
        end;
        Q.Close();

        // Both halves are concrete values only a correctly-resolved column id can produce. A
        // column id resolved from the WRONG symbol source reads another column's value or none
        // at all — a wrong number, not a crash, which is why this asserts the numbers.
        Assert.AreEqual(2, Seen, 'the query must group the three rows into two customers');
        Assert.AreEqual(42, C1Total, 'C1''s two rows (10 + 32) must sum to 42');
    end;
}
