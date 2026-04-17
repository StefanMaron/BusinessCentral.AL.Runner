/// Exercises ErrorInfo.Create (0-arg static factory) + Message property roundtrip
/// and Message(text) / Message() method-form setter/getter.
/// ErrorInfo.Create(msg) overloads and ErrorType are BC 17+ and not recognised
/// by the BC 16.2 AL compiler (stage-1); they are tracked separately as gaps.
codeunit 97900 "EIC Create Src"
{
    // ------------------------------------------------------------------
    // ErrorInfo.Create() — 0-arg static factory
    // ------------------------------------------------------------------

    /// Create() + Message property setter + Message property getter roundtrip.
    procedure GetMessageFromCreated(Msg: Text): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        exit(ErrInfo.Message);
    end;

    /// Create() returns a fresh instance — default Message is empty.
    procedure GetDefaultMessage(): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        exit(ErrInfo.Message);
    end;

    // ------------------------------------------------------------------
    // Message(text) / Message() — method-form setter and getter
    // ------------------------------------------------------------------

    /// Message(text) method setter, then Message property getter.
    procedure SetMessageViaMethod(Msg: Text): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo.Message(Msg);
        exit(ErrInfo.Message);
    end;

    /// Message property setter, then Message() method getter.
    procedure GetMessageViaMethodGetter(Msg: Text): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo.Message := Msg;
        exit(ErrInfo.Message());
    end;
}
