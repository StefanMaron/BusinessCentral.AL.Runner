codeunit 116000 "DB Stubs Src"
{
    procedure GetSessionId(): Integer
    begin
        exit(Database.SessionId());
    end;

    procedure HasTableConnectionCRM(connName: Text): Boolean
    begin
        exit(Database.HasTableConnection(TableConnectionType::CRM, connName));
    end;
}
