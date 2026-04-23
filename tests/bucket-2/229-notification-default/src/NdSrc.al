/// Helper for Notification.Default tests (issue #1189).
/// Uses a GLOBAL Notification variable — BC generates MockNotification.Default for global fields.
codeunit 229001 "ND Helper"
{
    var
        GlobalNotification: Notification;

    procedure SetGlobalMessage(Msg: Text)
    begin
        GlobalNotification.Message := Msg;
    end;

    procedure GetGlobalMessage(): Text
    begin
        exit(GlobalNotification.Message);
    end;

    procedure GlobalMessageIsEmpty(): Boolean
    begin
        exit(GlobalNotification.Message = '');
    end;

    procedure SetAndGet(Msg: Text): Text
    var
        LocalN: Notification;
    begin
        // Mix of global and local — both must work.
        GlobalNotification.Message := Msg;
        LocalN.Message := 'local ' + Msg;
        exit(GlobalNotification.Message + '|' + LocalN.Message);
    end;
}
