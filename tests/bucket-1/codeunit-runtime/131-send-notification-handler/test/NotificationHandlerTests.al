codeunit 59981 "Notification Handler Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        LibraryVariableStorage: Codeunit "Library - Variable Storage";

    [Test]
    [HandlerFunctions('CaptureNotificationHandler')]
    procedure HandlerReceivesMessage()
    var
        Sender: Codeunit "Notification Sender";
    begin
        // Positive: SendNotificationHandler intercepts Notification.Send()
        // and can read the Message property
        Sender.SendSimple('Hello from notification');
        Assert.AreEqual('Hello from notification', LibraryVariableStorage.DequeueText(), 'Handler should receive notification message');
    end;

    [Test]
    [HandlerFunctions('CaptureDataHandler')]
    procedure HandlerReceivesData()
    var
        Sender: Codeunit "Notification Sender";
    begin
        // Positive: Handler can read data set on the notification
        Sender.SendWithData('Data notification', 'MyKey', 'MyValue');
        Assert.AreEqual('Data notification', LibraryVariableStorage.DequeueText(), 'Handler should receive message');
        Assert.AreEqual('MyValue', LibraryVariableStorage.DequeueText(), 'Handler should read data from notification');
    end;

    [Test]
    procedure SendWithoutHandlerIsNoOp()
    var
        Sender: Codeunit "Notification Sender";
    begin
        // Negative: Sending without a handler should not crash — it is a no-op
        Sender.SendSimple('No handler registered');
        // Prove no handler was invoked by checking variable storage is still empty
        LibraryVariableStorage.AssertEmpty();
    end;

    [SendNotificationHandler]
    procedure CaptureNotificationHandler(var TheNotification: Notification): Boolean
    begin
        LibraryVariableStorage.Enqueue(TheNotification.Message);
        exit(true);
    end;

    [SendNotificationHandler]
    procedure CaptureDataHandler(var TheNotification: Notification): Boolean
    begin
        LibraryVariableStorage.Enqueue(TheNotification.Message);
        LibraryVariableStorage.Enqueue(TheNotification.GetData('MyKey'));
        exit(true);
    end;
}
