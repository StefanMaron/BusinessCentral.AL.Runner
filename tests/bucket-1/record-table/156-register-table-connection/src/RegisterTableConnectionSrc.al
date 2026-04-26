/// Helper codeunit exercising Database.RegisterTableConnection.
/// Signature per the AL compiler: `RegisterTableConnection(TableConnectionType, Name: Text, Connection: Text)`.
codeunit 59680 "RTC Src"
{
    procedure CallRegister(ct: TableConnectionType; name: Text; conn: Text)
    begin
        Database.RegisterTableConnection(ct, name, conn);
    end;

    procedure CallRegisterAndReturnFlag(ct: TableConnectionType; name: Text; conn: Text): Boolean
    begin
        Database.RegisterTableConnection(ct, name, conn);
        exit(true);
    end;
}
