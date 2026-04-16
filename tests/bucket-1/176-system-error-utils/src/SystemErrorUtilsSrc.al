codeunit 103000 "SEU Src"
{
    procedure GetLastErrText(): Text
    begin
        exit(GetLastErrorText());
    end;

    procedure GetLastErrCode(): Text
    begin
        exit(GetLastErrorCode());
    end;

    procedure GetLastErrCallStack(): Text
    begin
        exit(GetLastErrorCallStack());
    end;

    procedure IsCollecting(): Boolean
    begin
        exit(IsCollectingErrors());
    end;

    procedure HasCollected(): Boolean
    begin
        exit(HasCollectedErrors());
    end;

    procedure ClearLast()
    begin
        ClearLastError();
    end;

    procedure ClearCollected()
    begin
        ClearCollectedErrors();
    end;

    procedure GetCollected(): List of [ErrorInfo]
    begin
        exit(GetCollectedErrors());
    end;
}
