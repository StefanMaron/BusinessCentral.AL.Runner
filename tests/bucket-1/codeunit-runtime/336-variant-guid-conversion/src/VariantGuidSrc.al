codeunit 1320506 "VG Src"
{
    procedure GetBySystemIdFromVariant(reference: Variant): Boolean
    var
        rr: RecordRef;
    begin
        rr.Open(Database::"VG Table");
        exit(rr.GetBySystemId(reference));
    end;
}
