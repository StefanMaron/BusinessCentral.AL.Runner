/// Helper codeunit exercising Database.SetDefaultTableConnection.
codeunit 59730 "SDTC Src"
{
    procedure CallSet(ct: TableConnectionType; name: Text)
    begin
        Database.SetDefaultTableConnection(ct, name);
    end;

    procedure CallSetAndReturnFlag(ct: TableConnectionType; name: Text): Boolean
    begin
        Database.SetDefaultTableConnection(ct, name);
        exit(true);
    end;
}
