/// Source codeunit exercising RecordId.TableNo and XmlNameTable.Add/Get.
codeunit 100100 "SGP Src"
{
    // ── RecordId.TableNo ──────────────────────────────────────────────────

    /// Return the table number from a RecordId.
    procedure GetTableNo(RecId: RecordId): Integer
    begin
        exit(RecId.TableNo());
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

    /// Get a string that was never added — should return empty text (no exception).
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
