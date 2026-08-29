codeunit 64535 "Qja Tests"
{
    // Regression for issue #2137 (Query column aggregation silently returned unaggregated,
    // ungrouped rows) — the two runner-specific surfaces the fix in RecordPatches.QueryProjection.cs
    // could not implement now throw a named RunnerOutOfScopeException instead of silently
    // returning the same wrong-rows bug via a different code path. Both are tracked for a
    // real implementation by follow-up issue #2146.
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

    // Positive (must throw): a multi-dataitem JOIN query with an aggregated column (Method =
    // Sum) has no in-memory GROUP BY available (AlRunner.QueryJoin.JoinExecutor only joins
    // and projects) — it must fail loudly rather than return unaggregated joined rows.
    [Test]
    procedure JoinWithAggregateColumn_ThrowsOutOfScope()
    var
        Q: Query "Qja Join Sum";
        Assert: Codeunit "Qja Assert";
    begin
        Initialize();

        asserterror
        begin
            Q.Open();
            if Q.Read() then;
        end;

        Assert.ExpectedError('out-of-scope: NavQuery (multi-dataitem join with Method=Sum/Count/Average/Min/Max)');
        Assert.ExpectedError('query-join-aggregation-not-supported');
    end;

    // Negative (sibling, must NOT throw): the exact same join shape, minus the aggregated
    // column, must keep working — proves the guard is scoped to aggregation, not to joins in
    // general. Without this, a fix that refused EVERY multi-dataitem join (not just
    // aggregated ones) would pass the positive test above and silently break every ordinary
    // join query in the runner.
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

    // Positive (must throw): a runtime SetFilter on an AGGREGATED column is a HAVING-clause
    // filter (evaluated against the aggregated result), which the WHERE-style per-row filter
    // pushdown (TranslateQueryFilters) cannot express — it must fail loudly rather than
    // silently filter raw rows by the unaggregated source value instead.
    [Test]
    procedure SetFilterOnAggregateColumn_ThrowsOutOfScope()
    var
        Q: Query "Qja Single Sum";
        Assert: Codeunit "Qja Assert";
    begin
        Initialize();
        Q.SetFilter(TotalAmount, '>100');

        asserterror
        begin
            Q.Open();
            if Q.Read() then;
        end;

        Assert.ExpectedError('out-of-scope: NavQuery.SetRange/SetFilter on an aggregated (Method=Sum/Count/Average/Min/Max) column');
        Assert.ExpectedError('query-having-filter-not-supported');
    end;

    // Negative (sibling, must NOT throw): a runtime filter on the NON-aggregated column of
    // the very same query must still work — proves the guard is scoped to the filtered
    // column's OWN aggregation method, not to "any filter on a query that happens to also
    // have an aggregate column somewhere". Without this, a fix that refused every filter on
    // an aggregate-bearing query would pass the positive test above and silently break
    // ordinary WHERE-style filtering on aggregate queries.
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
