codeunit 100005 "Validate Uninit Test"
{
    Subtype = Test;

    [Test]
    procedure OnValidate_WithGlobalVar_DoesNotCrash()
    var
        Rec: Record "Validate Uninit Table";
        Assert: Codeunit "Library Assert";
    begin
        // [GIVEN] A record in the table
        Rec.PK := 'TEST';
        Rec.Insert(false);

        // [WHEN] Validate a field on a table that has a global var section
        // This triggers TryFireOnValidateInType which uses GetUninitializedObject.
        // Without InitializeUninitializedObject, the global Helper field is null
        // and accessing Helper.PK throws NullReferenceException.
        Rec.Validate("Rate Code", 'ABC');

        // [THEN] No crash, and the trigger body ran (Position defaulted to 0)
        Assert.AreEqual(0, Rec."Mapped Position", 'Default position should be 0 from uninitialized Helper.PK');
    end;

    [Test]
    procedure OnValidate_SpecialCode_SetsPosition99()
    var
        Rec: Record "Validate Uninit Table";
        Assert: Codeunit "Library Assert";
    begin
        // [GIVEN] A record
        Rec.PK := 'TEST2';
        Rec.Insert(false);

        // [WHEN] Validate with special code
        Rec.Validate("Rate Code", 'SPECIAL');

        // [THEN] Trigger sets position to 99
        Assert.AreEqual(99, Rec."Mapped Position", 'SPECIAL code should set position to 99');
    end;
}
