/// Source codeunit exercising RecordId.TableNo, RecordId.GetRecord, and XmlNameTable.Add/Get.
codeunit 100100 "SGP Src"
{
    // ── RecordId.TableNo ──────────────────────────────────────────────────

    /// Return the table number from a RecordId.
    procedure GetTableNo(RecId: RecordId): Integer
    begin
        exit(RecId.TableNo());
    end;

    // ── RecordId.GetRecord ────────────────────────────────────────────────

    /// Try to fetch the record referenced by RecId into Rec.
    /// In standalone mode (no live DB), returns false for empty RecordId.
    procedure TryGetRecord(RecId: RecordId; var Rec: Record "SGP Table"): Boolean
    begin
        exit(RecId.GetRecord(Rec));
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
