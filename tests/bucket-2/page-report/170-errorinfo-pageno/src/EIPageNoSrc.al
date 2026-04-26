// Renumbered from 97000 to avoid collision in new bucket layout (#1385).
codeunit 1097000 "EI PageNo Src"
{
    procedure SetAndGet(): Integer
    var
        EI: ErrorInfo;
    begin
        EI.PageNo(21);
        exit(EI.PageNo());
    end;

    procedure DefaultPageNo(): Integer
    var
        EI: ErrorInfo;
    begin
        exit(EI.PageNo());
    end;

    procedure OverwritePageNo(): Integer
    var
        EI: ErrorInfo;
    begin
        EI.PageNo(10);
        EI.PageNo(99);
        exit(EI.PageNo());
    end;
}
