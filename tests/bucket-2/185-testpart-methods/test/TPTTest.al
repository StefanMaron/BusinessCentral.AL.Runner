/// Tests for TestPart methods — covers all 20 methods listed in issue #687.
codeunit 97302 "TPT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // --- Navigation ---

    [Test]
    procedure First_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsTrue(TP.Lines.First(), 'Part.First must return true');
        TP.Close();
    end;

    [Test]
    procedure Last_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(TP.Lines.Last(), 'Part.Last must return false (empty stub)');
        TP.Close();
    end;

    [Test]
    procedure Next_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(TP.Lines.Next(), 'Part.Next must return false (empty stub)');
        TP.Close();
    end;

    [Test]
    procedure Previous_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(TP.Lines.Previous(), 'Part.Previous must return false (empty stub)');
        TP.Close();
    end;

    [Test]
    procedure Prev_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(TP.Lines.Prev(), 'Part.Prev must return false (empty stub)');
        TP.Close();
    end;

    [Test]
    procedure New_DoesNotError()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        TP.Lines.New();
        Assert.IsTrue(true, 'Part.New must not raise an error');
        TP.Close();
    end;

    [Test]
    procedure GoToRecord_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
        Rec: Record "TPT Record";
    begin
        Rec.Id := 1;
        Rec.Insert(false);
        TP.OpenEdit();
        Assert.IsTrue(TP.Lines.GoToRecord(Rec), 'Part.GoToRecord must return true');
        TP.Close();
    end;

    [Test]
    procedure GoToKey_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsTrue(TP.Lines.GoToKey(1), 'Part.GoToKey must return true');
        TP.Close();
    end;

    // --- Expand / IsExpanded ---

    [Test]
    procedure Expand_DoesNotError()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        TP.Lines.Expand(true);
        Assert.IsTrue(true, 'Part.Expand must not raise an error');
        TP.Close();
    end;

    [Test]
    procedure IsExpanded_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(TP.Lines.IsExpanded(), 'Part.IsExpanded must return false (stub)');
        TP.Close();
    end;

    // --- Validation ---

    [Test]
    procedure ValidationErrorCount_ReturnsZero()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.AreEqual(0, TP.Lines.ValidationErrorCount(), 'Part.ValidationErrorCount must return 0');
        TP.Close();
    end;

    [Test]
    procedure GetValidationError_ReturnsEmpty()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.AreEqual('', TP.Lines.GetValidationError(1), 'Part.GetValidationError must return empty string');
        TP.Close();
    end;

    // --- Properties ---

    [Test]
    procedure Caption_ReturnsTestPage()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.AreEqual('TestPage', TP.Lines.Caption, 'Part.Caption must return TestPage stub');
        TP.Close();
    end;

    [Test]
    procedure Editable_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsTrue(TP.Lines.Editable, 'Part.Editable must return true');
        TP.Close();
    end;

    [Test]
    procedure Enabled_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsTrue(TP.Lines.Enabled(), 'Part.Enabled must return true');
        TP.Close();
    end;

    [Test]
    procedure Visible_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsTrue(TP.Lines.Visible(), 'Part.Visible must return true');
        TP.Close();
    end;

    // --- Field access ---

    [Test]
    procedure GetField_StoresAndReturnsValue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        TP.Lines.NameField.SetValue('hello');
        Assert.AreEqual('hello', TP.Lines.NameField.Value, 'Part field must store and return the set value');
        TP.Close();
    end;

    // --- FindField ---

    [Test]
    procedure FindFirstField_ReturnsTrue()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsTrue(
            TP.Lines.FindFirstField(TP.Lines.NameField, 'x'),
            'Part.FindFirstField must return true (stub)');
        TP.Close();
    end;

    [Test]
    procedure FindNextField_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(
            TP.Lines.FindNextField(TP.Lines.NameField, 'x'),
            'Part.FindNextField must return false (stub)');
        TP.Close();
    end;

    [Test]
    procedure FindPreviousField_ReturnsFalse()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.IsFalse(
            TP.Lines.FindPreviousField(TP.Lines.NameField, 'x'),
            'Part.FindPreviousField must return false (stub)');
        TP.Close();
    end;

    // --- Inequality / negative cases ---

    [Test]
    procedure First_DiffersFromLast()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.AreNotEqual(TP.Lines.First(), TP.Lines.Last(), 'Part.First and Part.Last must return different values');
        TP.Close();
    end;

    [Test]
    procedure FindFirstField_DiffersFromFindNextField()
    var
        TP: TestPage "TPT Card";
    begin
        TP.OpenEdit();
        Assert.AreNotEqual(
            TP.Lines.FindFirstField(TP.Lines.NameField, 'x'),
            TP.Lines.FindNextField(TP.Lines.NameField, 'x'),
            'FindFirstField and FindNextField must return different values');
        TP.Close();
    end;
}
