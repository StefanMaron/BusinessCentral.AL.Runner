table 310005 "PVR Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Active; Boolean) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    procedure CanDownloadResult(): Boolean
    begin
        exit(Active and (Name <> ''));
    end;
}
