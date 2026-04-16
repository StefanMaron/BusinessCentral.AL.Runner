/// Helper codeunit exercising Database.DataFileInformation.
/// Signature per the AL compiler (9 parameters):
///   DataFileInformation(ShowDialog: Boolean; var Name: Text; var Description: Text;
///     var HasApplication: Boolean; var HasApplicationData: Boolean;
///     var HasGlobalData: Boolean; var TenantInformation: Text;
///     var CreationDateTime: DateTime; var DatabaseVersion: Text)
codeunit 61300 "DFI Helper"
{
    procedure CallDataFileInformation(ShowDialog: Boolean)
    var
        Name: Text;
        Description: Text;
        HasApplication: Boolean;
        HasApplicationData: Boolean;
        HasGlobalData: Boolean;
        TenantInformation: Text;
        CreationDateTime: DateTime;
        DatabaseVersion: Text;
    begin
        Database.DataFileInformation(ShowDialog, Name, Description, HasApplication,
            HasApplicationData, HasGlobalData, TenantInformation, CreationDateTime, DatabaseVersion);
    end;

    procedure CallAndReturnFlag(ShowDialog: Boolean): Boolean
    var
        Name: Text;
        Description: Text;
        HasApplication: Boolean;
        HasApplicationData: Boolean;
        HasGlobalData: Boolean;
        TenantInformation: Text;
        CreationDateTime: DateTime;
        DatabaseVersion: Text;
    begin
        Database.DataFileInformation(ShowDialog, Name, Description, HasApplication,
            HasApplicationData, HasGlobalData, TenantInformation, CreationDateTime, DatabaseVersion);
        exit(true);
    end;
}
