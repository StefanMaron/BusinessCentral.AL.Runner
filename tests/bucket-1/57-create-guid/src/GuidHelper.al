codeunit 56500 "Guid Helper"
{
    procedure GetNewGuid(): Guid
    begin
        exit(CreateGuid());
    end;

    procedure GetNewSequentialGuid(): Guid
    begin
        exit(CreateSequentialGuid());
    end;
}
