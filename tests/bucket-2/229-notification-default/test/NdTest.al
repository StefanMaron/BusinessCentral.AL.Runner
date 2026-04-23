/// Tests for MockNotification.Default static member (issue #1189).
/// A global 'var N: Notification' in a codeunit generates MockNotification.Default
/// for the field initializer — this must compile and evaluate to a blank notification.
codeunit 229002 "ND Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "ND Helper";

        /// Test codeunit itself has a global Notification — proves Default is usable here too.
        GlobalN: Notification;

    [Test]
    procedure GlobalNotification_InitialMessageIsEmpty()
    begin
        // Positive: A global Notification variable's initial Message must be empty.
        Assert.AreEqual('', Helper.GetGlobalMessage(), 'Global Notification message must start empty');
    end;

    [Test]
    procedure GlobalNotification_SetAndGet()
    begin
        // Positive: Setting message on global Notification must round-trip correctly.
        Helper.SetGlobalMessage('hello');
        Assert.AreEqual('hello', Helper.GetGlobalMessage(), 'Global Notification message must match what was set');
    end;

    [Test]
    procedure GlobalNotification_MessageIsEmpty()
    var
        Result: Boolean;
    begin
        // Positive: helper method using global Notification reports empty message.
        Helper.SetGlobalMessage('');
        Result := Helper.GlobalMessageIsEmpty();
        Assert.IsTrue(Result, 'Global Notification message should be empty string');
    end;

    [Test]
    procedure GlobalAndLocal_Mix()
    var
        Result: Text;
    begin
        // Positive: mixing global and local Notification in one method must work.
        Result := Helper.SetAndGet('world');
        Assert.AreEqual('world|local world', Result, 'Global and local Notification must operate independently');
    end;

    [Test]
    procedure GlobalNotification_InTestCu_IsUsable()
    begin
        // Positive: test codeunit-level global Notification variable (this) must also compile.
        GlobalN.Message := 'test-codeunit-global';
        Assert.AreEqual('test-codeunit-global', GlobalN.Message, 'Test codeunit global Notification must be usable');
    end;

    [Test]
    procedure GlobalNotification_SetNonDefault_DiffersFromBlank()
    begin
        // Negative: a set message must differ from an empty message.
        Helper.SetGlobalMessage('non-blank');
        Assert.AreNotEqual('', Helper.GetGlobalMessage(), 'Non-blank global message must differ from empty string');
    end;
}
