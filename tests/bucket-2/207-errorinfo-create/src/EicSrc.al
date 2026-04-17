/// Exercises ErrorInfo.Create (static factory), ErrorInfo.Message (method setter),
/// and ErrorInfo.ErrorType.  All three are listed as gaps in coverage.yaml.
codeunit 97900 "EIC Src"
{
    procedure CreateWithMessage(Msg: Text): ErrorInfo
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create(Msg);
        exit(ErrInfo);
    end;

    procedure CreateWithMessageAndType(Msg: Text): ErrorInfo
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create(Msg, ErrorType::Client);
        exit(ErrInfo);
    end;

    procedure SetMessageViaMethod(Msg: Text): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo.Message(Msg);
        exit(ErrInfo.Message);
    end;

    procedure SetErrorTypeAndRead(ET: ErrorType): ErrorType
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo.ErrorType(ET);
        exit(ErrInfo.ErrorType);
    end;

    procedure GetMessageFromCreated(Msg: Text): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create(Msg);
        exit(ErrInfo.Message);
    end;
}
