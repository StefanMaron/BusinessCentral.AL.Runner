codeunit 228002 "Notification Gaps Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Notification Gaps Helper";

    // ── Gap 1: NotificationScope assignment ──────────────────────

    [Test]
    procedure Scope_LocalScope_IsRetained()
    var
        N: Notification;
    begin
        // Positive: setting Scope to LocalScope should compile and round-trip
        N := Helper.BuildScopedNotification('msg', NotificationScope::LocalScope);
        Assert.AreEqual(NotificationScope::LocalScope, N.Scope, 'Scope should be LocalScope');
    end;

    [Test]
    procedure Scope_GlobalScope_IsRetained()
    var
        N: Notification;
    begin
        // Positive: setting Scope to GlobalScope should compile and round-trip
        N := Helper.BuildScopedNotification('scoped', NotificationScope::GlobalScope);
        Assert.AreEqual(NotificationScope::GlobalScope, N.Scope, 'Scope should be GlobalScope');
    end;

    // ── Gap 2: Recall() returns Boolean ──────────────────────────

    [Test]
    procedure Recall_ReturnsBooleanTrue()
    var
        N: Notification;
        Recalled: Boolean;
    begin
        // Positive: Recall() must return a boolean (used in if-statement);
        // standalone mode always returns true
        N.Message := 'recall me';
        Recalled := Helper.RecallNotification(N);
        Assert.IsTrue(Recalled, 'Recall() should return true');
    end;

    [Test]
    procedure Recall_CanBeUsedInIfStatement()
    var
        N: Notification;
        Hit: Boolean;
    begin
        // Positive: prove that if Notification.Recall() then ... compiles and executes
        N.Message := 'if test';
        Hit := false;
        if N.Recall() then
            Hit := true;
        Assert.IsTrue(Hit, 'Branch inside if Recall() should be taken');
    end;

    // ── Gap 3: Clear(Notification) resets message ─────────────────

    [Test]
    procedure Clear_ResetsMessageToEmpty()
    var
        Result: Text;
    begin
        // Positive: after Clear() the Message should be empty string
        Result := Helper.GetMessageAfterClear('before clear');
        Assert.AreEqual('', Result, 'Message should be empty after Clear()');
    end;

    [Test]
    procedure Clear_DirectOnVariable()
    var
        N: Notification;
    begin
        // Positive: Clear(N) directly in test code should compile and reset message
        N.Message := 'to be cleared';
        Clear(N);
        Assert.AreEqual('', N.Message, 'Message should be empty after Clear(N)');
    end;
}
