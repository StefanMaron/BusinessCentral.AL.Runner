codeunit 84700 "CE Src"
{
    /// Raises two collectible errors (no [ErrorBehavior] here — caller must be in collect mode).
    procedure RaiseTwoErrors()
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo.Collectible := true;
        ErrInfo.Message := 'First error';
        Error(ErrInfo);

        ErrInfo.Collectible := true;
        ErrInfo.Message := 'Second error';
        Error(ErrInfo);
    end;

    /// Returns the count of currently collected errors.
    procedure CountCollectedErrors(): Integer
    begin
        if HasCollectedErrors() then
            exit(GetCollectedErrors().Count());
        exit(0);
    end;

    /// Returns whether we are currently inside a collecting context.
    procedure IsCollecting(): Boolean
    begin
        exit(IsCollectingErrors());
    end;

    /// Source-side method with [ErrorBehavior(Collect)] that raises one collectible error.
    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure RaiseOneInCollectContext(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo.Collectible := true;
        ErrInfo.Message := Msg;
        Error(ErrInfo);
    end;

    /// Returns first message from collected errors (or empty string).
    procedure GetFirstMessage(): Text
    var
        Errors: List of [ErrorInfo];
        Err: ErrorInfo;
    begin
        if not HasCollectedErrors() then
            exit('');
        Errors := GetCollectedErrors();
        Err := Errors.Get(1);
        exit(Err.Message);
    end;
}
