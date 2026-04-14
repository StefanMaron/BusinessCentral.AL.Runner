codeunit 59830 "Unstubbed Types Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // === Notification Tests ===

    [Test]
    procedure NotificationSendNoError()
    var
        Helper: Codeunit "Notification Helper";
        Id: Guid;
        EmptyGuid: Guid;
    begin
        // Positive: Send completes without error and Id is a non-empty Guid
        Id := Helper.CreateAndSend();
        Assert.AreNotEqual(EmptyGuid, Id, 'Notification.Id should be non-empty after creation');
    end;

    [Test]
    procedure NotificationSetGetDataRoundTrip()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: SetData/GetData preserves the value
        Assert.AreEqual('myvalue', Helper.SetAndGetData('mykey', 'myvalue'), 'GetData should return the value set by SetData');
    end;

    [Test]
    procedure NotificationHasDataTrue()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: HasData returns true for a key that was set
        Assert.IsTrue(Helper.HasDataForKey('testkey'), 'HasData should return true for a set key');
    end;

    [Test]
    procedure NotificationHasDataFalse()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Negative: HasData returns false for a key that was not set
        Assert.IsFalse(Helper.HasDataForMissingKey(), 'HasData should return false for a missing key');
    end;

    [Test]
    procedure NotificationRecallNoError()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: Recall completes without error
        Helper.RecallNotification();
    end;

    [Test]
    procedure NotificationMessageProperty()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: Message property round-trips
        Assert.AreEqual('My message', Helper.GetMessage(), 'Message property should return what was set');
    end;

    [Test]
    procedure NotificationScopeNoError()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: Setting Scope and sending does not error
        Helper.SetScope();
    end;

    [Test]
    procedure NotificationAddActionNoError()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: AddAction + Send does not error
        Helper.AddActionNoError();
    end;

    // === BigText Tests ===

    [Test]
    procedure BigTextAddTextAndLength()
    var
        Helper: Codeunit "BigText Helper";
    begin
        // Positive: Two AddText calls, length = sum of both
        Assert.AreEqual(11, Helper.AddAndGetLength(), 'BigText.Length should be 11 for "Hello World"');
    end;

    [Test]
    procedure BigTextGetSubText()
    var
        Helper: Codeunit "BigText Helper";
    begin
        // Positive: GetSubText(1, 5) returns first 5 chars
        Assert.AreEqual('Hello', Helper.AddAndGetSubText(), 'GetSubText(1, 5) should return "Hello"');
    end;

    [Test]
    procedure BigTextTextPosFound()
    var
        Helper: Codeunit "BigText Helper";
    begin
        // Positive: TextPos finds "World" at position 7 (1-based)
        Assert.AreEqual(7, Helper.TextPosFound(), 'TextPos("World") should return 7');
    end;

    [Test]
    procedure BigTextTextPosMissing()
    var
        Helper: Codeunit "BigText Helper";
    begin
        // Negative: TextPos returns 0 for missing text
        Assert.AreEqual(0, Helper.TextPosMissing(), 'TextPos should return 0 for missing text');
    end;

    [Test]
    procedure BigTextGetSubTextAcrossBoundary()
    var
        Helper: Codeunit "BigText Helper";
    begin
        // Positive: GetSubText across AddText boundary
        Assert.AreEqual('lo Wo', Helper.GetSubTextAcrossBoundary(), 'GetSubText should work across AddText boundaries');
    end;

    [Test]
    procedure BigTextGetSubTextToEnd()
    var
        Helper: Codeunit "BigText Helper";
    begin
        // Positive: GetSubText without length returns from position to end
        Assert.AreEqual('World', Helper.GetSubTextNoLength(), 'GetSubText without length should return to end');
    end;

    // === TaskScheduler Tests ===

    [Test]
    procedure TaskSchedulerCreateTaskReturnsGuid()
    var
        Helper: Codeunit "TaskScheduler Helper";
        Id: Guid;
        EmptyGuid: Guid;
    begin
        // Positive: CreateTask returns a non-empty Guid
        Id := Helper.CreateTaskReturnsGuid();
        Assert.AreNotEqual(EmptyGuid, Id, 'CreateTask should return a non-empty Guid');
    end;

    [Test]
    procedure TaskSchedulerTaskExistsReturnsBool()
    var
        Helper: Codeunit "TaskScheduler Helper";
    begin
        // Positive: TaskExists returns a boolean (no crash)
        Helper.TaskExistsReturnsBool();
    end;

    [Test]
    procedure TaskSchedulerCancelTaskNoError()
    var
        Helper: Codeunit "TaskScheduler Helper";
    begin
        // Positive: CancelTask completes without error
        Helper.CancelTaskNoError();
    end;

    [Test]
    procedure TaskSchedulerSetTaskReadyNoError()
    var
        Helper: Codeunit "TaskScheduler Helper";
    begin
        // Positive: SetTaskReady completes without error
        Helper.SetTaskReadyNoError();
    end;

    // === DataTransfer Tests ===

    [Test]
    procedure DataTransferCopyRowsNoError()
    var
        Helper: Codeunit "DataTransfer Helper";
    begin
        // Positive: SetTables + AddFieldValue + CopyRows completes
        Helper.CopyRowsNoError();
    end;

    [Test]
    procedure DataTransferCopyFieldsNoError()
    var
        Helper: Codeunit "DataTransfer Helper";
    begin
        // Positive: CopyFields completes without error
        Helper.CopyFieldsNoError();
    end;

    [Test]
    procedure DataTransferAddConstantValueNoError()
    var
        Helper: Codeunit "DataTransfer Helper";
    begin
        // Positive: AddConstantValue + CopyRows completes
        Helper.AddConstantValueNoError();
    end;
}
