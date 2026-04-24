/// Helper procedures that exercise RecordRef.CurrentKeyIndex get/set
/// — the BC-emitted C# uses the `:=` form which compiles to a property
/// setter on MockRecordRef.ALCurrentKeyIndex.
codeunit 1218001 "CKI Src"
{
    procedure SetCurrentKeyIndex(var RecRef: RecordRef; Index: Integer)
    begin
        RecRef.CurrentKeyIndex := Index;
    end;

    procedure GetCurrentKeyIndex(var RecRef: RecordRef): Integer
    begin
        exit(RecRef.CurrentKeyIndex);
    end;

    procedure GetKeyCount(var RecRef: RecordRef): Integer
    begin
        exit(RecRef.KeyCount());
    end;
}
