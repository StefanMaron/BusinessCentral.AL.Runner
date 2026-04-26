table 410001 "NIW Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Active; Boolean) { }
        field(4; Status; Text[50]) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    procedure CanDownloadResult(): Boolean
    begin
        exit(Active and (Name <> ''));
    end;

    procedure GetStatus()
    begin
        Status := 'FromRecord';
        Modify();
    end;
}
