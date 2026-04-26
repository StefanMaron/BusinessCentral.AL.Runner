codeunit 296002 "MaxStrLen Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    [Test]
    procedure MaxStrLen_TextField_ReturnsFieldLength()
    var
        Rec: Record "MaxStrLen Test";
    begin
        // [GIVEN] A table with Text[50] field
        // [WHEN] MaxStrLen is called on the field
        // [THEN] Returns 50, not Int32.MaxValue
        Assert.AreEqual(50, MaxStrLen(Rec.ShortText), 'MaxStrLen(Text[50]) should be 50');
    end;

    [Test]
    procedure MaxStrLen_CodeField_ReturnsFieldLength()
    var
        Rec: Record "MaxStrLen Test";
    begin
        Assert.AreEqual(20, MaxStrLen(Rec.MediumCode), 'MaxStrLen(Code[20]) should be 20');
    end;

    [Test]
    procedure MaxStrLen_LongTextField_ReturnsFieldLength()
    var
        Rec: Record "MaxStrLen Test";
    begin
        Assert.AreEqual(250, MaxStrLen(Rec.LongText), 'MaxStrLen(Text[250]) should be 250');
    end;

    [Test]
    procedure MaxStrLen_BoundedVariable_ReturnsLength()
    var
        BoundedVar: Text[100];
    begin
        // [GIVEN] A local variable declared as Text[100]
        // [WHEN] MaxStrLen is called on it
        // [THEN] Returns 100
        Assert.AreEqual(100, MaxStrLen(BoundedVar), 'MaxStrLen(Text[100] variable) should be 100');
    end;
}
