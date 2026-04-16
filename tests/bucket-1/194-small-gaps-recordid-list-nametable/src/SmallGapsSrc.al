/// Source codeunit exercising RecordId.TableNo, RecordId.GetRecord, List.Sort, XmlNameTable.Add/Get.
codeunit 100100 "SGP Src"
{
    // ── RecordId ──────────────────────────────────────────────────────────

    /// Return the table number from a RecordId.
    procedure GetTableNo(RecId: RecordId): Integer
    begin
        exit(RecId.TableNo());
    end;

    // ── List.Sort ─────────────────────────────────────────────────────────

    /// Sort a list of integers in-place and return the first element.
    procedure SortListGetFirst(var L: List of [Integer]): Integer
    begin
        L.Sort();
        exit(L.Get(1));
    end;

    /// Sort a list of integers in-place and return the last element.
    procedure SortListGetLast(var L: List of [Integer]): Integer
    begin
        L.Sort();
        exit(L.Get(L.Count()));
    end;

    /// Sort an empty list — must not throw; returns count (0).
    procedure SortEmptyList(var L: List of [Integer]): Integer
    begin
        L.Sort();
        exit(L.Count());
    end;

    // ── XmlNameTable ──────────────────────────────────────────────────────

    /// Add a string to the name table and retrieve it back.
    procedure NameTableAddAndGet(value: Text): Text
    var
        mgr: XmlNamespaceManager;
        nt: XmlNameTable;
        result: Text;
    begin
        nt := mgr.NameTable();
        nt.Add(value);
        nt.Get(value, result);
        exit(result);
    end;

    /// Get a string that was never added — should return empty text.
    procedure NameTableGetMissing(value: Text): Text
    var
        mgr: XmlNamespaceManager;
        nt: XmlNameTable;
        result: Text;
    begin
        nt := mgr.NameTable();
        nt.Get(value, result);
        exit(result);
    end;
}
