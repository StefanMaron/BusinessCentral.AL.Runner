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

    // --- Tests for issue #1506: MaxStrLen on record field after Init() with InitValue ---
    // Before the fix, TableInitValueRegistry.BuildInitValue stored NavText without an
    // explicit MaxLength, so GetFieldValueSafe returned a NavText with MaxLength=Int32.MaxValue
    // instead of the declared field length.

    [Test]
    procedure MaxStrLen_TextFieldAfterInit_ReturnsFieldLength()
    var
        Rec: Record "MaxStrLen InitValue Test";
    begin
        // [GIVEN] A table with Text[100] field that has InitValue = 'default'
        // [WHEN] Init() is called and MaxStrLen is evaluated on the field
        // [THEN] Returns 100, not Int32.MaxValue (2147483647)
        Rec.Init();
        Assert.AreEqual(100, MaxStrLen(Rec.Msg), 'MaxStrLen(Text[100] with InitValue) should be 100');
    end;

    [Test]
    procedure MaxStrLen_CodeFieldAfterInit_ReturnsFieldLength()
    var
        Rec: Record "MaxStrLen InitValue Test";
    begin
        // [GIVEN] A table with Code[10] field that has InitValue = 'INIT'
        // [WHEN] Init() is called and MaxStrLen is evaluated on the field
        // [THEN] Returns 10
        Rec.Init();
        Assert.AreEqual(10, MaxStrLen(Rec.ShortCode), 'MaxStrLen(Code[10] with InitValue) should be 10');
    end;

    [Test]
    procedure MaxStrLen_TextFieldAfterInsertGet_ReturnsFieldLength()
    var
        Rec: Record "MaxStrLen InitValue Test";
    begin
        // [GIVEN] A record inserted with a Text[100] field value then re-read via Get
        // [WHEN] MaxStrLen is evaluated on the field from the retrieved record
        // [THEN] Returns 100 — not the MaxLength of the stored NavText value
        Rec.PK := 9999;
        Rec.Msg := 'Hello World';
        Rec.Insert();
        Rec.Get(9999);
        Assert.AreEqual(100, MaxStrLen(Rec.Msg), 'MaxStrLen(Text[100] after Get) should be 100');
    end;
}
