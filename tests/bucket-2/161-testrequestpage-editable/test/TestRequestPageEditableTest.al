codeunit 61902 "TRE TestRequestPage Editable Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        EditableResult: Boolean;

    [Test]
    [HandlerFunctions('EditableCheckHandler')]
    procedure EditableField_ReturnsTrue()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Positive: .Editable() on a TestRequestPage field must return true (mock always-editable stub).
        Helper.RunReport();
        Assert.IsTrue(EditableResult, 'TestRequestPage field Editable() must return true');
    end;

    [Test]
    [HandlerFunctions('EditableNotFalseHandler')]
    procedure EditableField_NotFalse()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Negative: .Editable() must not return false (guards against a broken always-false stub).
        Helper.RunReport();
        Assert.AreNotEqual(false, EditableResult, 'TestRequestPage.Editable must not return false');
    end;

    [Test]
    [HandlerFunctions('CalledTwiceHandler')]
    procedure EditableField_CalledTwice_NoError()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Edge case: calling .Editable() multiple times must not error.
        Helper.RunReport();
        Assert.IsTrue(EditableResult, 'TestRequestPage.Editable called twice must return true');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    [RequestPageHandler]
    procedure EditableCheckHandler(var RequestPage: TestRequestPage "TRE Report")
    begin
        EditableResult := RequestPage.EditableField.Editable();
    end;

    [RequestPageHandler]
    procedure EditableNotFalseHandler(var RequestPage: TestRequestPage "TRE Report")
    begin
        EditableResult := RequestPage.EditableField.Editable();
    end;

    [RequestPageHandler]
    procedure CalledTwiceHandler(var RequestPage: TestRequestPage "TRE Report")
    var
        First: Boolean;
        Second: Boolean;
    begin
        First := RequestPage.EditableField.Editable();
        Second := RequestPage.EditableField.Editable();
        EditableResult := First and Second;
    end;
}
