/// Exercises RecordRef stub methods: IsDirty, LoadFields, CopyLinks,
/// ReadConsistency, RecordLevelLocking, SecurityFiltering, Truncate.
codeunit 92000 "RRS Src"
{
    procedure GetIsDirty(var RecRef: RecordRef): Boolean
    begin
        exit(RecRef.IsDirty());
    end;

    procedure CallLoadFields(var RecRef: RecordRef; FieldNo: Integer)
    begin
        RecRef.LoadFields(FieldNo);
    end;

    procedure CallCopyLinks(var RecRef: RecordRef; FromRef: RecordRef)
    begin
        RecRef.CopyLinks(FromRef);
    end;

    procedure GetReadConsistency(var RecRef: RecordRef): Boolean
    begin
        exit(RecRef.ReadConsistency());
    end;

    procedure GetSecurityFiltering(var RecRef: RecordRef): SecurityFilter
    begin
        exit(RecRef.SecurityFiltering);
    end;

    procedure SetSecurityFiltering(var RecRef: RecordRef; Filter: SecurityFilter)
    begin
        RecRef.SecurityFiltering := Filter;
    end;

    procedure CallTruncate(var RecRef: RecordRef)
    begin
        RecRef.Truncate();
    end;
}
