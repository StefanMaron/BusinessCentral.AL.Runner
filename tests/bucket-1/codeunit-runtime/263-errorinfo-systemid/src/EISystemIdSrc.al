/// Helper codeunit that exercises ErrorInfo.SystemId get and set.
codeunit 61930 "EI SystemId Src"
{
    procedure SetAndGet(): Guid
    var
        ei: ErrorInfo;
        g: Guid;
    begin
        g := CreateGuid();
        ei.SystemId(g);
        exit(ei.SystemId());
    end;

    procedure GetDefault(): Guid
    var
        ei: ErrorInfo;
    begin
        exit(ei.SystemId());
    end;

    procedure SetAndGetSpecific(g: Guid): Guid
    var
        ei: ErrorInfo;
    begin
        ei.SystemId(g);
        exit(ei.SystemId());
    end;
}
