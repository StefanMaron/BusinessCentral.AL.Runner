/// Helper codeunit exercising Database.UserSecurityId.
codeunit 59540 "USI Src"
{
    procedure GetUserSecurityId(): Guid
    begin
        exit(Database.UserSecurityId);
    end;

    procedure IsUserSecurityIdNonNull(): Boolean
    var
        g: Guid;
    begin
        g := Database.UserSecurityId;
        exit(not IsNullGuid(g));
    end;

    procedure BothReadsEqual(): Boolean
    var
        a: Guid;
        b: Guid;
    begin
        a := Database.UserSecurityId;
        b := Database.UserSecurityId;
        exit(a = b);
    end;
}
