codeunit 61902 "TRE TestRequestPage Editable Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        EditableResult: Boolean;
        ReadOnlyResult: Boolean;

    [Test]
    [HandlerFunctions('EditableCheckHandler')]
    procedure EditableField_ReturnsTrue()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Positive: an explicitly Editable=true field must return true from .Editable().
        Helper.RunReport();
        Assert.IsTrue(EditableResult, 'Editable field must return true from TestRequestPage.Editable()');
    end;

    [Test]
    [HandlerFunctions('ReadOnlyCheckHandler')]
    procedure ReadOnlyField_ReturnsFalse()
    var
        Helper: Codeunit "TRE Helper";
    begin
        // Negative: an explicitly Editable=false field must return false from .Editable().
        Helper.RunReport();
        Assert.IsFalse(ReadOnlyResult, 'Read-only field must return false from TestRequestPage.Editable()');
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
    procedure ReadOnlyCheckHandler(var RequestPage: TestRequestPage "TRE Report")
    begin
        ReadOnlyResult := RequestPage.ReadOnlyField.Editable();
    end;
}
