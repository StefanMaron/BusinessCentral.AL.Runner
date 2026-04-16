codeunit 63001 "FieldNo Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // --- Record.FieldNo(fieldName) ---

    [Test]
    procedure FieldNo_KnownField_ReturnsNonZero()
    var
        Rec: Record "FN Test Table";
    begin
        // Code is field 1 — FieldNo must return a positive number
        Assert.AreNotEqual(0, Rec.FieldNo(Code), 'FieldNo(Code) must be non-zero');
    end;

    [Test]
    procedure FieldNo_SecondField_ReturnsNonZero()
    var
        Rec: Record "FN Test Table";
    begin
        // Description is field 2 — FieldNo must return a positive number
        Assert.AreNotEqual(0, Rec.FieldNo(Description), 'FieldNo(Description) must be non-zero');
    end;

    [Test]
    procedure FieldNo_DifferentFields_ReturnDifferentNumbers()
    var
        Rec: Record "FN Test Table";
    begin
        // Code(1) and Description(2) must have distinct field numbers
        Assert.AreNotEqual(Rec.FieldNo(Code), Rec.FieldNo(Description),
            'Code and Description must have different field numbers');
    end;

    [Test]
    procedure FieldNo_SameField_IsIdempotent()
    var
        Rec: Record "FN Test Table";
    begin
        // Calling FieldNo twice on the same field yields the same result
        Assert.AreEqual(Rec.FieldNo(Code), Rec.FieldNo(Code),
            'FieldNo(Code) must return the same number each time');
    end;

    [Test]
    procedure FieldNo_Code_Returns1()
    var
        Rec: Record "FN Test Table";
    begin
        // Code is declared as field 1 — prove exact value
        Assert.AreEqual(1, Rec.FieldNo(Code), 'FieldNo(Code) must equal 1');
    end;

    [Test]
    procedure FieldNo_Description_Returns2()
    var
        Rec: Record "FN Test Table";
    begin
        // Description is declared as field 2 — prove exact value
        Assert.AreEqual(2, Rec.FieldNo(Description), 'FieldNo(Description) must equal 2');
    end;
}
