codeunit 56721 "Modal Handler Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    [HandlerFunctions('OkModalHandler')]
    procedure TestModalHandlerOk()
    var
        Opener: Codeunit "Modal Opener";
        Result: Action;
    begin
        // [GIVEN] A ModalPageHandler that invokes OK
        // [WHEN] Code opens a modal page
        Result := Opener.OpenModalAndGetResult();
        // [THEN] The result should be OK (LookupOK)
        Assert.AreEqual(Action::LookupOK, Result, 'Modal should return OK');
    end;

    [ModalPageHandler]
    procedure OkModalHandler(var TestPage: TestPage "Modal Edit Page")
    begin
        TestPage.NameField.SetValue('hello');
        TestPage.OK().Invoke();
    end;

    [Test]
    [HandlerFunctions('CancelModalHandler')]
    procedure TestModalHandlerCancel()
    var
        Opener: Codeunit "Modal Opener";
        Result: Action;
    begin
        // [GIVEN] A ModalPageHandler that invokes Cancel
        // [WHEN] Code opens a modal page
        Result := Opener.OpenModalAndGetResult();
        // [THEN] The result should be LookupCancel
        Assert.AreEqual(Action::LookupCancel, Result, 'Modal should return Cancel');
    end;

    [ModalPageHandler]
    procedure CancelModalHandler(var TestPage: TestPage "Modal Edit Page")
    begin
        TestPage.Cancel().Invoke();
    end;

    [Test]
    procedure TestModalNoHandler()
    var
        Opener: Codeunit "Modal Opener";
    begin
        // [GIVEN] No handler registered
        // [WHEN] Code opens a modal page
        // [THEN] Should fail with descriptive error
        asserterror Opener.OpenModalAndGetResult();
        Assert.ExpectedError('No ModalPageHandler registered');
    end;
}
