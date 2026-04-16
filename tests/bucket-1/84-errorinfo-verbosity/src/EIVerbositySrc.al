codeunit 83600 "EI Verbosity Src"
{
    procedure SetAndGet(): Integer
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Error);
        exit(EI.Verbosity().AsInteger());
    end;

    procedure DefaultVerbosity(): Integer
    var
        EI: ErrorInfo;
    begin
        exit(EI.Verbosity().AsInteger());
    end;

    procedure OverwriteVerbosity(): Integer
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Warning);
        EI.Verbosity(Verbosity::Normal);
        exit(EI.Verbosity().AsInteger());
    end;
}
