/// Minimal stub for the Tenant Media system table (ID 2000000184).
/// BC's compiler generates NavMediaSystemRecord as the base class for
/// system tables in this ID range — the runner's rewriter must handle that.
table 2000000184 "Tenant Media"
{
    fields
    {
        field(1; ID; Guid) { }
        field(2; "Mime Type"; Text[100]) { }
        field(3; Content; Media) { }
    }
    keys
    {
        key(PK; ID) { Clustered = true; }
    }
}

/// Helper codeunit that references the Tenant Media system table.
/// Exercises basic record operations against the in-memory mock.
codeunit 303000 "TMD Helper"
{
    procedure IsEmpty(): Boolean
    var
        TenantMedia: Record "Tenant Media";
    begin
        exit(TenantMedia.IsEmpty());
    end;

    procedure CountRecords(): Integer
    var
        TenantMedia: Record "Tenant Media";
    begin
        exit(TenantMedia.Count());
    end;
}
