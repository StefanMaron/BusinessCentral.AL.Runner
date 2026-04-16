codeunit 83600 "EI Verbosity Src"
{
    procedure SetError_GetIsError(): Boolean
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Error);
        exit(EI.Verbosity() = Verbosity::Error);
    end;

    procedure SetWarning_GetIsWarning(): Boolean
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Warning);
        exit(EI.Verbosity() = Verbosity::Warning);
    end;

    procedure OverwriteReturnsLatest(): Boolean
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Warning);
        EI.Verbosity(Verbosity::Error);
        exit(EI.Verbosity() = Verbosity::Error);
    end;

    procedure SetError_GetIsNotWarning(): Boolean
    var
        EI: ErrorInfo;
    begin
        EI.Verbosity(Verbosity::Error);
        exit(EI.Verbosity() <> Verbosity::Warning);
    end;
}
