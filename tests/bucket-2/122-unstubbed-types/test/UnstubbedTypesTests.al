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
    procedure NotificationRecallPreservesId()
    var
        Helper: Codeunit "Notification Helper";
        Id: Guid;
        EmptyGuid: Guid;
    begin
        // Positive: Recall completes and Id is still accessible
        Id := Helper.RecallNotification();
        Assert.AreNotEqual(EmptyGuid, Id, 'Notification.Id should be non-empty after Recall');
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
    procedure NotificationScopePreservesId()
    var
        Helper: Codeunit "Notification Helper";
        Id: Guid;
        EmptyGuid: Guid;
    begin
        // Positive: Setting Scope and sending preserves Id
        Id := Helper.SetScopeAndGetId();
        Assert.AreNotEqual(EmptyGuid, Id, 'Notification with Scope should have non-empty Id after Send');
    end;

    [Test]
    procedure NotificationAddActionPreservesMessage()
    var
        Helper: Codeunit "Notification Helper";
    begin
        // Positive: AddAction + Send preserves the message
        Assert.AreEqual('With action', Helper.AddActionAndGetMessage(), 'Notification.Message should be preserved after AddAction + Send');
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
    procedure TaskSchedulerTaskExistsReturnsFalse()
    var
        Helper: Codeunit "TaskScheduler Helper";
    begin
        // Positive: TaskExists returns false (task ran synchronously, no longer active)
        Assert.IsFalse(Helper.TaskExistsReturnsBool(), 'TaskExists should return false for a completed synchronous task');
    end;

    [Test]
    procedure TaskSchedulerCancelTaskReturnsTrue()
    var
        Helper: Codeunit "TaskScheduler Helper";
    begin
        // Positive: CancelTask returns true (no-op success)
        Assert.IsTrue(Helper.CancelTaskReturnsTrue(), 'CancelTask should return true');
    end;

    [Test]
    procedure TaskSchedulerSetTaskReadyReturnsTrue()
    var
        Helper: Codeunit "TaskScheduler Helper";
    begin
        // Positive: SetTaskReady returns true (no-op success)
        Assert.IsTrue(Helper.SetTaskReadyReturnsTrue(), 'SetTaskReady should return true');
    end;

    // === DataTransfer Tests ===

    [Test]
    procedure DataTransferCopyRowsIsNoOp()
    var
        Helper: Codeunit "DataTransfer Helper";
    begin
        // Positive: CopyRows is a no-op — target table remains empty
        Assert.IsTrue(Helper.CopyRowsLeavesTargetEmpty(), 'CopyRows should be a no-op: target must remain empty');
    end;

    [Test]
    procedure DataTransferCopyFieldsIsNoOp()
    var
        Helper: Codeunit "DataTransfer Helper";
    begin
        // Positive: CopyFields is a no-op — target table remains empty
        Assert.IsTrue(Helper.CopyFieldsLeavesTargetEmpty(), 'CopyFields should be a no-op: target must remain empty');
    end;

    [Test]
    procedure DataTransferAddConstantValueIsNoOp()
    var
        Helper: Codeunit "DataTransfer Helper";
    begin
        // Positive: AddConstantValue + CopyRows is a no-op
        Assert.IsTrue(Helper.AddConstantValueNoError(), 'AddConstantValue + CopyRows should be a no-op: target must remain empty');
    end;
}
