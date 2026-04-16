codeunit 97000 "EI PageNo Src"
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
