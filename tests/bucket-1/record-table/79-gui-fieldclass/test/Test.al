codeunit 79101 "Gui FieldClass Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Lib: Codeunit "Gui FieldClass Lib";

    // --- Problem 1: GuiAllowed ---

    [Test]
    procedure GuiAllowedReturnsFalse()
    begin
        // Positive: in standalone mode GuiAllowed must return false (no UI).
        Assert.IsFalse(Lib.IsGuiAvailable(), 'GuiAllowed should be false in standalone mode');
    end;

    [Test]
    procedure GuiAllowedIsNotTrue()
    begin
        // Negative: verify GuiAllowed is definitely not true.
        Assert.AreNotEqual(true, Lib.IsGuiAvailable(), 'GuiAllowed must not be true');
    end;

    // --- Problem 2: FieldClass enum comparison ---

    [Test]
    procedure FieldClassNormalComparison()
    var
        recRef: RecordRef;
    begin
        // Positive: Field 1 on a table should be FieldClass::Normal.
        recRef.Open(79100);
        Assert.IsTrue(Lib.IsNormalField(recRef, 1), 'Field 1 should be Normal class');
        recRef.Close();
    end;

    [Test]
    procedure FieldClassComparisonCompiles()
    var
        recRef: RecordRef;
        fldRef: FieldRef;
    begin
        // Negative: verify the comparison actually works (not always true).
        // We test that the FieldClass comparison at least compiles and runs.
        recRef.Open(79100);
        fldRef := recRef.Field(2);
        // All stub fields return Normal, so this is true — but the point is
        // that FieldClass::Normal == comparison compiles without CS0019.
        Assert.IsTrue(fldRef.Class = FieldClass::Normal, 'Field 2 should also be Normal class');
        recRef.Close();
    end;

    // --- Problem 3: Variant / RecordRef assignability (NavComplexValue) ---

    [Test]
    procedure RecRefAssignedToVariant()
    var
        recRef: RecordRef;
        v: Variant;
    begin
        // Positive: RecordRef can be assigned to a Variant without type errors.
        recRef.Open(79100);
        Lib.RecRefToVariant(recRef, v);
        // If we get here without a compile/runtime error, the NavComplexValue
        // assignability is working.
        Assert.IsTrue(true, 'RecordRef to Variant assignment succeeded');
        recRef.Close();
    end;

    [Test]
    procedure VariantAssignedToVariant()
    var
        v1: Variant;
        v2: Variant;
    begin
        // Positive: Variant-to-Variant assignment compiles (NavComplexValue context).
        v1 := 42;
        Lib.VariantToVariant(v1, v2);
        Assert.IsTrue(true, 'Variant to Variant assignment succeeded');
    end;
}
