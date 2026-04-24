/// Exercises string-to-NavText conversion patterns.
/// BC may emit string literals where NavText parameters are expected.
codeunit 1278001 "StringNavText Src"
{
    /// Creates an ErrorInfo with a string literal message.
    /// BC emits ErrorInfo.Create('msg') as NavALErrorInfo.ALCreate(string),
    /// which the rewriter maps to AlCompat.CreateErrorInfo(string).
    /// Without a string overload, this triggers CS1503 (string → NavText).
    procedure CreateErrorInfoWithLiteral(): Text
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create('Something went wrong');
        exit(ErrInfo.Message);
    end;

    /// Creates an ErrorInfo from a Text variable.
    procedure CreateErrorInfoFromVar(): Text
    var
        ErrInfo: ErrorInfo;
        Msg: Text;
    begin
        Msg := 'Variable message';
        ErrInfo := ErrorInfo.Create(Msg);
        exit(ErrInfo.Message);
    end;

    /// Raises an error using ErrorInfo.Create with a literal.
    /// This exercises the full Error(ErrorInfo) path.
    procedure RaiseErrorInfoLiteral()
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create('Deliberate test error');
        Error(ErrInfo);
    end;
}
