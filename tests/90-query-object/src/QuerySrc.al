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

/// Codeunit that exercises query variables without calling Open/Read/Close.
/// Proves that Query objects compile and non-data code runs correctly.
codeunit 59000 "Query Logic"
{
    /// Returns a constant to verify the codeunit compiles alongside the query.
    procedure GetStatus(): Text
    var
        Q: Query "Item Ledger Query";
    begin
        exit('query-ready');
    end;

    /// Calls Q.Open() — must throw NotSupportedException in standalone mode.
    procedure TryOpen()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.Open();
    end;

    /// Calls Q.Read() — must throw NotSupportedException in standalone mode.
    procedure TryRead()
    var
        Q: Query "Item Ledger Query";
    begin
        Q.Read();
    end;

    /// Sets a filter on a query column and calls Open — must throw NotSupportedException.
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
}
