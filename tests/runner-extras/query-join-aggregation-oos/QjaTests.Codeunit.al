codeunit 64535 "Qja Tests"
{
    // Regression for issue #2137 (Query column aggregation silently returned unaggregated,
    // ungrouped rows). The two surfaces this suite originally pinned as loud
    // RunnerOutOfScopeException throws — a multi-dataitem JOIN query with an aggregated
    // column, and a runtime SetFilter/SetRange on an aggregated column (a HAVING-clause
    // filter) — are now implemented for real by issue #2146
    // (RecordPatches.QueryProjection.cs / AlRunner.QueryJoin.JoinExecutor), so the two
    // "must throw" tests below became "must aggregate/filter correctly" tests instead.
    //
    // The general BC-language claim ("a Method=Sum column groups by the query's other
    // columns and computes the aggregate per group, across a JOIN and under a HAVING-style
    // runtime filter, the same way real BC's compiled SQL does") is plain BC behaviour, and
    // IS NOW COVERED upstream in the al-language corpus: it merged as
    // StefanMaron/BusinessCentral.AL.Language.Tests#74 (commit 6262dd6506dd20a39ee1626ed6a0ddd24d0685cd)
    // and passes on both BC 27.5 and BC 28.3; the submodule pin in this repo was bumped to it
    // alongside the #2146 fix. JoinWithAggregateColumn_GroupsJoinedRowsAndAggregates and
    // SetFilterOnAggregateColumn_EvaluatesAgainstGroupResult below now duplicate that upstream
    // coverage and are a TRIMMING CANDIDATE for a follow-up cleanup PR (not trimmed here, to
    // avoid unrelated count-baseline churn in this PR). JoinWithoutAggregateColumn_StillReturnsJoinedRows
    // and SetFilterOnNonAggregateColumn_StillWorks are negative siblings that still add
    // something the corpus does not cover: they pin that the GROUP BY/HAVING code paths this
    // fix added are correctly SCOPED to queries that actually have an aggregated column, and
    // do not misfire on an ordinary join or an ordinary WHERE-style filter just because the
    // query also happens to have an aggregate column somewhere.
    Subtype = Test;

    local procedure Initialize()
    var
        Order: Record "Qja Order";
        Customer: Record "Qja Customer";
    begin
        Order.DeleteAll();
        Customer.DeleteAll();

        Customer.Init();
        Customer."No." := 'C1';
        Customer.Name := 'Contoso';
        Customer.Insert();

        Order.Init();
        Order."Entry No." := 1;
        Order."Cust No." := 'C1';
        Order.Amount := 100;
        Order.Insert();

        Order.Init();
        Order."Entry No." := 2;
        Order."Cust No." := 'C1';
        Order.Amount := 200;
        Order.Insert();
    end;

    // #2146: a multi-dataitem JOIN query with an aggregated column (Method = Sum) now groups
    // the JOINED rows by every other Normal column (here: CustNo from Order, CustName from
    // the joined Customer) and aggregates per group — both of C1's orders (100+200) join to
    // the SAME customer row, so they land in one group and TotalAmount sums to 300, not one
    // row per joined pair echoing its own unsummed Amount.
    [Test]
    procedure JoinWithAggregateColumn_GroupsJoinedRowsAndAggregates()
    var
        Q: Query "Qja Join Sum";
        Assert: Codeunit "Qja Assert";
        RowCount: Integer;
    begin
        Initialize();

        Q.Open();
        while Q.Read() do begin
            RowCount += 1;
            Assert.AreEqual('C1', Q.CustNo, 'CustNo column');
            Assert.AreEqual(300, Q.TotalAmount, 'TotalAmount must aggregate over BOTH of C1''s orders (100+200), not echo one raw joined row');
        end;
        Q.Close();

        Assert.AreEqual(1, RowCount, 'JOIN+GROUP BY must return exactly 1 row (grouped by CustNo/CustName), not one row per joined Order/Customer pair (would be 2)');
    end;

    // Negative sibling: the exact same join shape, minus the aggregated column, must keep
    // returning one row PER JOINED PAIR (no grouping) — proves the GROUP BY path is scoped
    // to queries that actually have an aggregated column, not to joins in general. Without
    // this, a fix that grouped EVERY multi-dataitem join (not just aggregated ones) would
    // pass the positive test above and silently break every ordinary join query.
    [Test]
    procedure JoinWithoutAggregateColumn_StillReturnsJoinedRows()
    var
        Q: Query "Qja Join Plain";
        Assert: Codeunit "Qja Assert";
        RowCount: Integer;
    begin
        Initialize();

        Q.Open();
        while Q.Read() do begin
            RowCount += 1;
            Assert.AreEqual('C1', Q.CustNo, 'CustNo column');
            Assert.AreEqual('Contoso', Q.CustName, 'CustName column (joined from Qja Customer)');
        end;
        Q.Close();

        Assert.AreEqual(2, RowCount, 'a plain (non-aggregated) join must still return one row per matching Order/Customer pair');
    end;

    // #2146: a runtime SetFilter on an AGGREGATED column is a HAVING-clause filter, now
    // evaluated against the AGGREGATED result (300, C1's grouped sum), not a raw per-row
    // value. '>100' excludes order 1's raw amount (100 is not > 100) but the correct HAVING
    // answer keeps C1's group because its SUM (300) satisfies the filter — a WHERE-style
    // (pre-aggregation, per-row) application of the same filter would instead keep only
    // order 2 (200 > 100) and group to a WRONG sum of 200, a different, distinguishable
    // answer from the correct 300.
    [Test]
    procedure SetFilterOnAggregateColumn_EvaluatesAgainstGroupResult()
    var
        Q: Query "Qja Single Sum";
        Assert: Codeunit "Qja Assert";
        RowCount: Integer;
    begin
        Initialize();
        Q.SetFilter(TotalAmount, '>100');

        Q.Open();
        while Q.Read() do begin
            RowCount += 1;
            Assert.AreEqual('C1', Q.CustNo, 'CustNo column');
            Assert.AreEqual(300, Q.TotalAmount, 'HAVING TotalAmount>100 must evaluate against the GROUPED sum (300), not a raw per-row value (100 or 200)');
        end;
        Q.Close();

        Assert.AreEqual(1, RowCount, 'C1''s grouped sum (300) satisfies HAVING TotalAmount>100');
    end;

    // Negative sibling: a runtime filter on the NON-aggregated column of the very same query
    // must still be pushed down WHERE-style (pre-aggregation) — proves the HAVING path is
    // scoped to the filtered column's OWN aggregation method, not to "any filter on a query
    // that happens to also have an aggregate column somewhere". Without this, treating every
    // filter on an aggregate-bearing query as HAVING would silently break ordinary WHERE-style
    // filtering on aggregate queries.
    [Test]
    procedure SetFilterOnNonAggregateColumn_StillWorks()
    var
        Q: Query "Qja Single Sum";
        Assert: Codeunit "Qja Assert";
        RowCount: Integer;
    begin
        Initialize();
        Q.SetFilter(CustNo, 'C1');

        Q.Open();
        while Q.Read() do begin
            RowCount += 1;
            Assert.AreEqual('C1', Q.CustNo, 'CustNo column');
            Assert.AreEqual(300, Q.TotalAmount, 'TotalAmount must still aggregate correctly under a non-aggregate filter');
        end;
        Q.Close();

        Assert.AreEqual(1, RowCount, 'a WHERE-style filter on the non-aggregate column must still group to one row for C1');
    end;
}
