/// Source codeunit that calls Database.TenantId() — used to prove the
/// runner handles the call without error and returns a deterministic stub.

codeunit 61830 "DTI Src"
{
    procedure GetTenantId(): Text
    begin
        exit(Database.TenantId());
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
