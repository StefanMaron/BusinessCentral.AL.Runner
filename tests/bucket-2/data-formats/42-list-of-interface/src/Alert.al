interface IMyAlert
{
    procedure Run(): Integer;
}

codeunit 50430 "Low Priority Alert" implements IMyAlert
{
    procedure Run(): Integer
    begin
        exit(1);
    end;
}

codeunit 50431 "High Priority Alert" implements IMyAlert
{
    procedure Run(): Integer
    begin
        exit(10);
    end;
}

table 50430 "Alert Holder"
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    procedure Dispatch(): Integer
    var
        Handlers: List of [Interface IMyAlert];
        Handler: Interface IMyAlert;
        Total: Integer;
        Low: Codeunit "Low Priority Alert";
        High: Codeunit "High Priority Alert";
    begin
        Handlers.Add(Low);
        Handlers.Add(High);
        foreach Handler in Handlers do
            Total += Handler.Run();
        exit(Total);
    end;
}
