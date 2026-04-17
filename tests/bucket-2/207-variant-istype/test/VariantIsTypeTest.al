codeunit 60341 "VIT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VIT Src";

    [Test]
    procedure IsInteger_True_ForInt()
    var
        v: Variant;
    begin
        v := 42;
        Assert.IsTrue(Src.IsIntegerCheck(v),
            'Variant.IsInteger must return true for an Integer value');
    end;

    [Test]
    procedure IsInteger_False_ForText()
    var
        v: Variant;
    begin
        v := 'hello';
        Assert.IsFalse(Src.IsIntegerCheck(v),
            'Variant.IsInteger must return false for a Text value');
    end;

    [Test]
    procedure IsText_True_ForText()
    var
        v: Variant;
    begin
        v := 'hello';
        Assert.IsTrue(Src.IsTextCheck(v),
            'Variant.IsText must return true for a Text value');
    end;

    [Test]
    procedure IsText_False_ForInt()
    var
        v: Variant;
    begin
        v := 42;
        Assert.IsFalse(Src.IsTextCheck(v),
            'Variant.IsText must return false for an Integer value');
    end;

    [Test]
    procedure IsBoolean_True()
    var
        v: Variant;
    begin
        v := true;
        Assert.IsTrue(Src.IsBooleanCheck(v),
            'Variant.IsBoolean must return true for a Boolean value');
    end;

    [Test]
    procedure IsDecimal_True()
    var
        v: Variant;
    begin
        v := 3.14;
        Assert.IsTrue(Src.IsDecimalCheck(v),
            'Variant.IsDecimal must return true for a Decimal value');
    end;

    [Test]
    procedure IsDate_True()
    var
        v: Variant;
    begin
        v := Today();
        Assert.IsTrue(Src.IsDateCheck(v),
            'Variant.IsDate must return true for a Date value');
    end;

    [Test]
    procedure IsDateTime_True()
    var
        v: Variant;
    begin
        v := CurrentDateTime();
        Assert.IsTrue(Src.IsDateTimeCheck(v),
            'Variant.IsDateTime must return true for a DateTime value');
    end;

    [Test]
    procedure IsGuid_True()
    var
        v: Variant;
    begin
        v := CreateGuid();
        Assert.IsTrue(Src.IsGuidCheck(v),
            'Variant.IsGuid must return true for a Guid value');
    end;

    [Test]
    procedure IsInteger_And_IsText_DifferOnInt_NegativeTrap()
    var
        v: Variant;
    begin
        v := 42;
        Assert.AreNotEqual(Src.IsIntegerCheck(v), Src.IsTextCheck(v),
            'IsInteger and IsText must not both return the same value for an Integer');
    end;
}
