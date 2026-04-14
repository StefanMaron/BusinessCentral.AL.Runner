codeunit 59820 "Notification Helper"
{
    procedure CreateAndSend(): Guid
    var
        N: Notification;
    begin
        N.Message := 'Test notification';
        N.Send();
        exit(N.Id);
    end;

    procedure SetAndGetData(DataKey: Text; DataValue: Text): Text
    var
        N: Notification;
    begin
        N.SetData(DataKey, DataValue);
        exit(N.GetData(DataKey));
    end;

    procedure HasDataForKey(DataKey: Text): Boolean
    var
        N: Notification;
    begin
        N.SetData(DataKey, 'val');
        exit(N.HasData(DataKey));
    end;

    procedure HasDataForMissingKey(): Boolean
    var
        N: Notification;
    begin
        exit(N.HasData('nonexistent'));
    end;

    procedure RecallNotification()
    var
        N: Notification;
    begin
        N.Message := 'To recall';
        N.Send();
        N.Recall();
    end;

    procedure GetMessage(): Text
    var
        N: Notification;
    begin
        N.Message := 'My message';
        exit(N.Message);
    end;

    procedure SetScope()
    var
        N: Notification;
    begin
        N.Scope := NotificationScope::LocalScope;
        N.Send();
    end;

    procedure AddActionNoError()
    var
        N: Notification;
    begin
        N.Message := 'With action';
        N.AddAction('Click', Codeunit::"Notification Helper", 'HandleAction');
        N.Send();
    end;

    procedure HandleAction(N: Notification)
    begin
        // Handler stub
    end;
}
