/// Helper codeunit exercising Database.GetDefaultTableConnection.
/// Signature per the AL compiler: `GetDefaultTableConnection(ConnectionType: TableConnectionType): Text`.
codeunit 59670 "GDTC Src"
{
    procedure GetDefault(ct: TableConnectionType): Text
    begin
        exit(Database.GetDefaultTableConnection(ct));
    end;

    procedure CallCompletes(ct: TableConnectionType): Boolean
    var
        name: Text;
    begin
        name := Database.GetDefaultTableConnection(ct);
        exit(true);
    end;
}
