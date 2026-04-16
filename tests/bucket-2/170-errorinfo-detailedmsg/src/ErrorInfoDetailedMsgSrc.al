/// Helper codeunit exercising ErrorInfo.DetailedMessage() getter and setter.
codeunit 59910 "EID Src"
{
    procedure SetAndGet(detail: Text): Text
    var
        ei: ErrorInfo;
    begin
        ei.DetailedMessage := detail;
        exit(ei.DetailedMessage);
    end;

    procedure FreshDetailedMessage(): Text
    var
        ei: ErrorInfo;
    begin
        // A default-initialised ErrorInfo should have an empty DetailedMessage.
        exit(ei.DetailedMessage);
    end;

    procedure SetDifferentValues(a: Text; b: Text): Text
    var
        ei: ErrorInfo;
    begin
        ei.DetailedMessage := a;
        ei.DetailedMessage := b;
        exit(ei.DetailedMessage);
    end;
}
