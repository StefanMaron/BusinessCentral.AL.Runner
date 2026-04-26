codeunit 228001 "Notification Gaps Helper"
{
    procedure BuildScopedNotification(Msg: Text; Scp: NotificationScope): Notification
    var
        N: Notification;
    begin
        N.Message := Msg;
        N.Scope := Scp;
        exit(N);
    end;

    procedure RecallNotification(var N: Notification): Boolean
    begin
        exit(N.Recall());
    end;

    procedure ClearNotification(var N: Notification)
    begin
        Clear(N);
    end;

    procedure GetMessageAfterClear(Msg: Text): Text
    var
        N: Notification;
    begin
        N.Message := Msg;
        Clear(N);
        exit(N.Message);
    end;
}
