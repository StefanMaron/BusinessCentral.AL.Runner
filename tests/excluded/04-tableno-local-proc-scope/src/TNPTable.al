table 310008 "TNP Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Status; Text[50]) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    procedure GetStatus()
    begin
        Status := 'FromRecord';
        Modify();
    end;
}
