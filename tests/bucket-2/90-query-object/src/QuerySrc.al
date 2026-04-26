table 59000 "Query Test Item"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Item No."; Code[20]) { }
        field(3; Quantity; Decimal) { }
        field(4; "Posting Date"; Date) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

query 59000 "Item Ledger Query"
{
    QueryType = Normal;

    elements
    {
        dataitem(ItemLedger; "Query Test Item")
        {
            column(EntryNo; "Entry No.") { }
            column(ItemNo; "Item No.") { }
            column(Qty; Quantity) { Method = Sum; }
        }
    }
}

/// Codeunit that exercises query variables while avoiding Open/Read on the positive path.
/// Proves that Query objects compile and non-data code, such as Close and filter setup methods, runs correctly.
codeunit 59000 "Query Logic"
{
    /// Returns a constant to verify the codeunit compiles alongside the query.
    procedure GetStatus(): Text
    var
        Q: Query "Item Ledger Query";
    begin
        exit('query-ready');
    end;

    /// Calls Q.Open() — now works in-memory for single-dataitem queries.
    procedure TryOpen()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.Open();
    end;

    /// Calls Q.Read() without Open — throws NotSupportedException.
    procedure TryRead()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.Read();
    end;

    /// Sets a filter on a query column and calls Open — now works in-memory.
    procedure TrySetFilterAndOpen()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SetFilter(ItemNo, 'ITEM001');
        Q.Open();
    end;

    /// Calls Q.Close() — should succeed as a no-op.
    procedure TryClose()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.Close();
    end;

    /// Sets TopNumberOfRows and verifies it compiles.
    procedure TrySetTop()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.TopNumberOfRows(10);
        Q.Open();
    end;

    /// Sets a range on a query column — should succeed as a no-op.
    procedure TrySetRange()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SetRange(EntryNo, 1, 100);
    end;

    /// Sets a single-value range on a query column — should succeed as a no-op.
    procedure TrySetSingleRange()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SetRange(ItemNo, 'ITEM001');
    end;

    /// Clears a range on a query column — should succeed as a no-op.
    procedure TryClearRange()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SetRange(EntryNo);
    end;

    /// Tests multiple filter/range operations on different columns.
    procedure TryMultipleFilters()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SetFilter(ItemNo, 'ITEM*');
        Q.SetRange(EntryNo, 1, 50);
        Q.TopNumberOfRows(10);
        Q.Close();
    end;

    /// Tries Q.SaveAsCsv — no-op stub in standalone mode.
    procedure TrySaveAsCsv()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SaveAsCsv('output.csv');
    end;

    /// Tries Q.SaveAsXml — no-op stub in standalone mode.
    procedure TrySaveAsXml()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.SaveAsXml('output.xml');
    end;

    /// Returns Q.GetFilter(ColumnRef) — stub returns '' (no filter stored).
    procedure TryGetFilter(): Text
    var
        Q: Query "Item Ledger Query";
    begin
        exit(Q.GetFilter(EntryNo));
    end;

    /// Returns Q.GetFilters — stub returns '' (no filters stored).
    procedure TryGetFilters(): Text
    var
        Q: Query "Item Ledger Query";
    begin
        exit(Q.GetFilters);
    end;

    /// Returns Q.ColumnCaption(ColumnRef) — stub returns a non-empty caption.
    procedure TryColumnCaption(): Text
    var
        Q: Query "Item Ledger Query";
    begin
        exit(Q.ColumnCaption(EntryNo));
    end;

    /// Returns Q.ColumnName(ColumnRef) — stub returns a non-empty name.
    procedure TryColumnName(): Text
    var
        Q: Query "Item Ledger Query";
    begin
        exit(Q.ColumnName(EntryNo));
    end;

    /// Returns Q.ColumnNo(ColumnRef) — stub returns the column number.
    procedure TryColumnNo(): Integer
    var
        Q: Query "Item Ledger Query";
    begin
        exit(Q.ColumnNo(EntryNo));
    end;

    /// Tries Q.SaveAsJson — must throw NotSupportedException.
    procedure TrySaveAsJson()
    var
        Q: Query "Item Ledger Query";
        OutStr: OutStream;
    begin
        Q.SaveAsJson(OutStr);
    end;
}
