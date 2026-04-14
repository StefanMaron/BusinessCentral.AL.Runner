codeunit 56700 "TP TestPage Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestPageOpenEditAndClose()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage variable
        // [WHEN] OpenEdit and Close are called
        TP.OpenEdit();
        TP.Close();
        // [THEN] Execution completes without error
        Assert.IsTrue(true, 'TestPage OpenEdit/Close must compile and no-op');
    end;

    [Test]
    procedure TestPageOpenViewAndClose()
    var
        TP: TestPage "TP Test Card";
    begin
        TP.OpenView();
        TP.Close();
        Assert.IsTrue(true, 'TestPage OpenView/Close must compile and no-op');
    end;

    [Test]
    procedure TestPageOpenNewAndClose()
    var
        TP: TestPage "TP Test Card";
    begin
        TP.OpenNew();
        TP.Close();
        Assert.IsTrue(true, 'TestPage OpenNew/Close must compile and no-op');
    end;

    [Test]
    procedure TestPageTrap()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage variable
        // [WHEN] Trap is called (marks page as expecting modal open)
        TP.Trap();
        // [THEN] No crash
        Assert.IsTrue(true, 'TestPage Trap must compile and no-op');
    end;

    [Test]
    procedure TestPageSetValueAndRead()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage opened for editing
        TP.OpenNew();
        // [WHEN] Setting field values
        TP.NameField.SetValue('Hello');
        TP.AmountField.SetValue(42);
        // [THEN] Values can be read back
        Assert.AreEqual('Hello', TP.NameField.Value, 'Name field should be Hello');
        Assert.AreEqual('42', TP.AmountField.Value, 'Amount field should be 42');
        TP.Close();
    end;

    [Test]
    procedure TestPageSetValueNegative()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage opened for editing
        TP.OpenNew();
        TP.NameField.SetValue('Hello');
        // [THEN] The value should not be something else
        Assert.AreNotEqual('World', TP.NameField.Value, 'Name field should not be World');
        TP.Close();
    end;

    [Test]
    procedure TestPageOkInvoke()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage opened for editing with values set
        TP.OpenNew();
        TP.NameField.SetValue('Test');
        // [WHEN] OK action is invoked
        TP.OK().Invoke();
        // [THEN] No crash (the page closes successfully)
        Assert.IsTrue(true, 'OK().Invoke() must compile and no-op');
    end;

    [Test]
    [HandlerFunctions('ConfirmYesHandler')]
    procedure ConfirmHandlerAnswersYes()
    var
        Logic: Codeunit "TP Confirm Logic";
    begin
        // [GIVEN] A ConfirmHandler that replies true
        // [WHEN] Code calls Confirm()
        // [THEN] The handler intercepts and returns true
        Assert.IsTrue(Logic.DoSomethingWithConfirm(), 'Confirm handler should reply true');
    end;

    [ConfirmHandler]
    procedure ConfirmYesHandler(Question: Text; var Reply: Boolean)
    begin
        Reply := true;
    end;

    [Test]
    [HandlerFunctions('ConfirmNoHandler')]
    procedure ConfirmHandlerAnswersNo()
    var
        Logic: Codeunit "TP Confirm Logic";
    begin
        // [GIVEN] A ConfirmHandler that replies false
        // [WHEN] Code calls Confirm()
        // [THEN] The handler intercepts and returns false
        Assert.IsFalse(Logic.DoSomethingWithConfirm(), 'Confirm handler should reply false');
    end;

    [ConfirmHandler]
    procedure ConfirmNoHandler(Question: Text; var Reply: Boolean)
    begin
        Reply := false;
    end;

    [Test]
    [HandlerFunctions('MessageCaptureHandler')]
    procedure MessageHandlerCaptures()
    var
        Logic: Codeunit "TP Confirm Logic";
    begin
        // [GIVEN] A MessageHandler registered
        // [WHEN] Code calls Message()
        Logic.ShowMessage();
        // [THEN] No crash — the handler intercepts the message
        Assert.IsTrue(true, 'MessageHandler should intercept Message call');
    end;

    [MessageHandler]
    procedure MessageCaptureHandler(Msg: Text)
    begin
        // Just capture — no assertion needed here
    end;

    [Test]
    [HandlerFunctions('ConfirmQuestionHandler')]
    procedure ConfirmHandlerReceivesQuestion()
    var
        Logic: Codeunit "TP Confirm Logic";
    begin
        // [GIVEN] A ConfirmHandler that validates the question text
        // [WHEN] Code calls Confirm('Are you sure?')
        // [THEN] Handler receives the correct question text and replies true
        Assert.IsTrue(Logic.DoSomethingWithConfirm(), 'Confirm handler should reply true when question matches');
    end;

    [ConfirmHandler]
    procedure ConfirmQuestionHandler(Question: Text; var Reply: Boolean)
    begin
        // Validate that the question text is passed correctly
        if Question = 'Are you sure?' then
            Reply := true
        else
            Reply := false;
    end;

    [Test]
    procedure TestPageFieldDefaultIsEmpty()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage opened without setting any values
        TP.OpenNew();
        // [THEN] Field value should be empty by default
        Assert.AreEqual('', TP.NameField.Value, 'Unset field should return empty text');
        TP.Close();
    end;

    [Test]
    procedure TestPageMultipleFieldSets()
    var
        TP: TestPage "TP Test Card";
    begin
        // [GIVEN] A TestPage with a field set to one value
        TP.OpenNew();
        TP.NameField.SetValue('First');
        Assert.AreEqual('First', TP.NameField.Value, 'First set should stick');
        // [WHEN] The same field is set to a different value
        TP.NameField.SetValue('Second');
        // [THEN] The new value replaces the old one
        Assert.AreEqual('Second', TP.NameField.Value, 'Second set should overwrite first');
        // [NEGATIVE] The old value should NOT be returned
        Assert.AreNotEqual('First', TP.NameField.Value, 'Old value should not persist');
        TP.Close();
    end;
}
