/// Tests for the missing TestField methods: AsBoolean, AsInteger, AsDate, AsTime,
/// AssertEquals, ValidationErrorCount, OptionCount, Activate, AssistEdit,
/// HideValue, ShowMandatory, Invoke, GetOption, GetValidationError.
codeunit 86101 "TFM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // AsInteger
    // ------------------------------------------------------------------

    [Test]
    procedure AsInteger_ReturnsSetValue()
    var
        TP: TestPage "TFM Card";
    begin
        // [GIVEN] QtyField is set to 42
        TP.OpenNew();
        TP.QtyField.SetValue(42);
        // [WHEN] AsInteger is called
        // [THEN] Returns 42
        Assert.AreEqual(42, TP.QtyField.AsInteger(), 'AsInteger must return the value set via SetValue');
        TP.Close();
    end;

    [Test]
    procedure AsInteger_Zero_ReturnsZero()
    var
        TP: TestPage "TFM Card";
    begin
        TP.OpenNew();
        TP.QtyField.SetValue(0);
        Assert.AreEqual(0, TP.QtyField.AsInteger(), 'AsInteger must return 0 when set to 0');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // AsBoolean
    // ------------------------------------------------------------------

    [Test]
    procedure AsBoolean_True_ReturnsTrue()
    var
        TP: TestPage "TFM Card";
    begin
        // [GIVEN] FlagField set to true
        TP.OpenNew();
        TP.FlagField.SetValue(true);
        // [THEN] AsBoolean returns true
        Assert.IsTrue(TP.FlagField.AsBoolean(), 'AsBoolean must return true when field is set to true');
        TP.Close();
    end;

    [Test]
    procedure AsBoolean_False_ReturnsFalse()
    var
        TP: TestPage "TFM Card";
    begin
        TP.OpenNew();
        TP.FlagField.SetValue(false);
        Assert.IsFalse(TP.FlagField.AsBoolean(), 'AsBoolean must return false when field is set to false');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // AssertEquals
    // ------------------------------------------------------------------

    [Test]
    procedure AssertEquals_MatchingValue_NoError()
    var
        TP: TestPage "TFM Card";
    begin
        // [GIVEN] NameField set to 'Acme'
        TP.OpenNew();
        TP.NameField.SetValue('Acme');
        // [WHEN] AssertEquals is called with the same value
        // [THEN] No error is raised
        TP.NameField.AssertEquals('Acme');
        Assert.IsTrue(true, 'AssertEquals must not error when values match');
        TP.Close();
    end;

    [Test]
    procedure AssertEquals_MismatchedValue_RaisesError()
    var
        TP: TestPage "TFM Card";
    begin
        // [GIVEN] NameField set to 'Acme'
        TP.OpenNew();
        TP.NameField.SetValue('Acme');
        // [WHEN] AssertEquals is called with a different value
        // [THEN] An error is raised
        asserterror TP.NameField.AssertEquals('Other');
        Assert.IsTrue(true, 'AssertEquals must error when values do not match');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // ValidationErrorCount (field-level)
    // ------------------------------------------------------------------

    [Test]
    procedure FieldValidationErrorCount_ReturnsZero()
    var
        TP: TestPage "TFM Card";
    begin
        // [GIVEN] A field with no validation errors
        TP.OpenNew();
        // [THEN] ValidationErrorCount returns 0
        Assert.AreEqual(0, TP.NameField.ValidationErrorCount(), 'Field.ValidationErrorCount must return 0 (no errors)');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // GetValidationError
    // ------------------------------------------------------------------

    [Test]
    procedure GetValidationError_ReturnsEmpty()
    var
        TP: TestPage "TFM Card";
        Err: Text;
    begin
        // [GIVEN] A field with no validation errors
        TP.OpenNew();
        // [WHEN] GetValidationError(1) is called
        Err := TP.NameField.GetValidationError(1);
        // [THEN] Returns empty string
        Assert.AreEqual('', Err, 'GetValidationError must return empty string when there are no errors');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // OptionCount / GetOption
    // ------------------------------------------------------------------

    [Test]
    procedure OptionCount_ReturnsZero()
    var
        TP: TestPage "TFM Card";
    begin
        // [GIVEN] A non-option field
        TP.OpenNew();
        // [THEN] OptionCount returns 0
        Assert.AreEqual(0, TP.NameField.OptionCount(), 'OptionCount must return 0 for non-option fields');
        TP.Close();
    end;

    [Test]
    procedure GetOption_ReturnsInteger()
    var
        TP: TestPage "TFM Card";
    begin
        TP.OpenNew();
        TP.QtyField.SetValue(2);
        // [THEN] GetOption returns the integer value stored
        Assert.AreEqual(2, TP.QtyField.GetOption(), 'GetOption must return the integer value');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // Activate, AssistEdit, HideValue, ShowMandatory, Invoke — no-ops
    // ------------------------------------------------------------------

    [Test]
    procedure Activate_DoesNotCrash()
    var
        TP: TestPage "TFM Card";
    begin
        TP.OpenNew();
        TP.NameField.Activate();
        Assert.IsTrue(true, 'Activate must be a no-op and not raise an error');
        TP.Close();
    end;

    [Test]
    procedure AssistEdit_DoesNotCrash()
    var
        TP: TestPage "TFM Card";
    begin
        TP.OpenNew();
        TP.NameField.AssistEdit();
        Assert.IsTrue(true, 'AssistEdit must be a no-op and not raise an error');
        TP.Close();
    end;

    [Test]
    procedure HideValue_DoesNotCrash()
    var
        TP: TestPage "TFM Card";
        Hidden: Boolean;
    begin
        TP.OpenNew();
        Hidden := TP.NameField.HideValue();
        Assert.IsTrue(true, 'HideValue must return a value and not raise an error');
        TP.Close();
    end;

    [Test]
    procedure ShowMandatory_DoesNotCrash()
    var
        TP: TestPage "TFM Card";
        Mandatory: Boolean;
    begin
        TP.OpenNew();
        Mandatory := TP.NameField.ShowMandatory();
        Assert.IsTrue(true, 'ShowMandatory must return a value and not raise an error');
        TP.Close();
    end;

    [Test]
    procedure Invoke_DoesNotCrash()
    var
        TP: TestPage "TFM Card";
    begin
        TP.OpenNew();
        TP.NameField.Invoke();
        Assert.IsTrue(true, 'Field.Invoke must be a no-op and not raise an error');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // AsDate
    // ------------------------------------------------------------------

    [Test]
    procedure AsDate_ReturnsSetDate()
    var
        TP: TestPage "TFM Card";
        D: Date;
    begin
        // [GIVEN] DateField set to a specific date
        TP.OpenNew();
        D := 19840101D;
        TP.DateField.SetValue(D);
        // [THEN] AsDate returns that date
        Assert.AreEqual(D, TP.DateField.AsDate(), 'AsDate must return the date set via SetValue');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // AsTime
    // ------------------------------------------------------------------

    [Test]
    procedure AsTime_ReturnsSetTime()
    var
        TP: TestPage "TFM Card";
        T: Time;
    begin
        TP.OpenNew();
        T := 120000T;
        TP.TimeField.SetValue(T);
        Assert.AreEqual(T, TP.TimeField.AsTime(), 'AsTime must return the time set via SetValue');
        TP.Close();
    end;

    // ------------------------------------------------------------------
    // AsDateTime (regression for issue #1216 — CS1501 ALAsDateTime not found)
    // ------------------------------------------------------------------

    [Test]
    procedure AsDateTime_ReturnsSetDateTime()
    var
        TP: TestPage "TFM Card";
        DT: DateTime;
    begin
        // [GIVEN] DateTimeField set to a specific datetime
        TP.OpenNew();
        DT := CreateDateTime(19840101D, 120000T);
        TP.DateTimeField.SetValue(DT);
        // [THEN] AsDateTime returns that datetime
        Assert.AreEqual(DT, TP.DateTimeField.AsDateTime(), 'AsDateTime must return the datetime set via SetValue');
        TP.Close();
    end;

    [Test]
    procedure AsDateTime_NonDateTimeField_RaisesError()
    var
        TP: TestPage "TFM Card";
        DT: DateTime;
    begin
        // [GIVEN] A text field holding a non-datetime string
        TP.OpenNew();
        TP.NameField.SetValue('not-a-datetime');
        // [WHEN] AsDateTime is called on the text field
        // [THEN] A conversion error is raised
        asserterror DT := TP.NameField.AsDateTime();
        Assert.ExpectedError('');
        TP.Close();
    end;
}
