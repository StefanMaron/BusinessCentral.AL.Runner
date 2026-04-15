codeunit 50950 "ErrorInfo Lib"
{
    procedure RaiseErrorInfo(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        Error(ErrInfo);
    end;

    procedure RaiseDetailedErrorInfo(Msg: Text; Detail: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.DetailedMessage := Detail;
        Error(ErrInfo);
    end;

    procedure RaiseCollectibleError(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.Collectible := true;
        Error(ErrInfo);
    end;

    procedure RaiseNonCollectibleError(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.Collectible := false;
        Error(ErrInfo);
    end;

    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure CollectSingleError(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.Collectible := true;
        Error(ErrInfo);
    end;

    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure CollectMultipleErrors(Msg1: Text; Msg2: Text; Msg3: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg1;
        ErrInfo.Collectible := true;
        Error(ErrInfo);

        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg2;
        ErrInfo.Collectible := true;
        Error(ErrInfo);

        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg3;
        ErrInfo.Collectible := true;
        Error(ErrInfo);
    end;

    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure CollectThenClear(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.Collectible := true;
        Error(ErrInfo);
        ClearCollectedErrors();
    end;

    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure CollectDetailedError(Msg: Text; Detail: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.DetailedMessage := Detail;
        ErrInfo.Collectible := true;
        Error(ErrInfo);
    end;

    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure NonCollectibleInCollectMode(Msg: Text)
    var
        ErrInfo: ErrorInfo;
    begin
        ErrInfo := ErrorInfo.Create();
        ErrInfo.Message := Msg;
        ErrInfo.Collectible := false;
        Error(ErrInfo);
    end;
}
