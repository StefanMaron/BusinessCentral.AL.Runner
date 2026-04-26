/// Helper codeunit exercising ErrorInfo.Create, Message, ErrorType — issue #215.
codeunit 129000 "EIM Src"
{
    procedure CreateWithMessage(Msg: Text): ErrorInfo
    begin
        exit(ErrorInfo.Create(Msg));
    end;

    procedure GetMessage(ErrInfo: ErrorInfo): Text
    begin
        exit(ErrInfo.Message());
    end;

    procedure SetErrorTypeClient(var ErrInfo: ErrorInfo)
    begin
        ErrInfo.ErrorType(ErrorType::Client);
    end;

    procedure GetErrorType(ErrInfo: ErrorInfo): ErrorType
    begin
        exit(ErrInfo.ErrorType());
    end;
}
