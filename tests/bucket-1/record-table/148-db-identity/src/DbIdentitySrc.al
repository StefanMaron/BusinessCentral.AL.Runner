/// Helper codeunit exercising Database.SerialNumber / Database.TenantId /
/// Database.ServiceInstanceId — the identity built-ins listed in issue #478.
codeunit 59550 "DBI Src"
{
    procedure GetSerialNumber(): Text
    begin
        exit(Database.SerialNumber);
    end;

    procedure GetTenantId(): Text
    begin
        exit(Database.TenantId);
    end;

    procedure GetServiceInstanceId(): Integer
    begin
        exit(Database.ServiceInstanceId);
    end;
}
