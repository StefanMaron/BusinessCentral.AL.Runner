// A TableType = CRM table, shaped like Base Application's own CRM tables (Guid primary key
// with ExternalAccess = Insert, ExternalName/ExternalType on every field). BC's
// DataAccessSource serves it through the session's current CRM table connection, never SQL.
table 65530 "Tcl CRM Entity"
{
    TableType = CRM;
    ExternalName = 'tcl_entity';
    Caption = 'Tcl CRM Entity';
    DataClassification = SystemMetadata;

    fields
    {
        field(1; EntityId; Guid)
        {
            ExternalName = 'tcl_entityid';
            ExternalType = 'Uniqueidentifier';
            ExternalAccess = Insert;
            Caption = 'Entity Id';
            DataClassification = SystemMetadata;
        }
        field(2; Name; Text[100])
        {
            ExternalName = 'tcl_name';
            ExternalType = 'String';
            Caption = 'Name';
            DataClassification = SystemMetadata;
        }
    }

    keys
    {
        key(PK; EntityId)
        {
            Clustered = true;
        }
    }
}
