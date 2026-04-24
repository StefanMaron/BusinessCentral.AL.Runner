// Test suite for issue #1279: Dialog.Update with Code field value causes CS0121
// ambiguity between MockDialog.ALUpdate(int, NavValue) and ALUpdate(int, int).
// NavValue has implicit conversion to int, so the compiler cannot choose.
// Fix: remove the int overload since NavValue already covers it.
codeunit 1279002 "DNIA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ShowJobProgress_CodeField_NoAmbiguity()
    var
        Helper: Codeunit "DNIA Helper";
        Result: Text;
    begin
        // [GIVEN] A Code[20] value passed to Dialog.Update with an int field number
        // [WHEN] ShowJobProgress is called — internally calls Window.Update(3, JobNo)
        // where JobNo is Code[20] (emitted as NavValue by BC compiler)
        Result := Helper.ShowJobProgress('JOB-100');

        // [THEN] Should compile and return the expected string
        Assert.AreEqual('Processed:JOB-100', Result, 'Dialog.Update(int, NavValue) must not be ambiguous with ALUpdate(int, int)');
    end;

    [Test]
    procedure ShowCountInDialog_IntegerField_StillWorks()
    var
        Helper: Codeunit "DNIA Helper";
        Result: Text;
    begin
        // [GIVEN] An Integer value passed to Dialog.Update
        // [WHEN] ShowCountInDialog is called — internally calls Window.Update(1, Count)
        Result := Helper.ShowCountInDialog(42);

        // [THEN] Should compile and return the expected string
        Assert.AreEqual('Count:42', Result, 'Dialog.Update with Integer must still work after removing int overload');
    end;

    [Test]
    procedure ShowJobProgress_EmptyCode_NoAmbiguity()
    var
        Helper: Codeunit "DNIA Helper";
        Result: Text;
    begin
        // [GIVEN] An empty Code value
        // [WHEN] ShowJobProgress is called
        Result := Helper.ShowJobProgress('');

        // [THEN] Should compile and return expected result
        Assert.AreEqual('Processed:', Result, 'Dialog.Update with empty Code must not be ambiguous');
    end;
}
