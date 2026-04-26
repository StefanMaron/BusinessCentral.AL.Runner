codeunit 60301 "RFE Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RFE Src";

    [Test]
    procedure FieldExist_KnownField_True()
    begin
        // Field 1 ("Id") exists on table 60300.
        Assert.IsTrue(Src.FieldExist_ByNo(60300, 1),
            'FieldExist must return true for a known field number');
    end;

    [Test]
    procedure FieldExist_Unknown_False()
    begin
        // Field 999 does not exist on table 60300.
        Assert.IsFalse(Src.FieldExist_ByNo(60300, 999),
            'FieldExist must return false for an unknown field number');
    end;

    [Test]
    procedure FieldExist_SecondField_True()
    begin
        // Field 2 ("Name") exists.
        Assert.IsTrue(Src.FieldExist_ByNo(60300, 2),
            'FieldExist must return true for a second known field');
    end;

    [Test]
    procedure RecordLevelLocking_ReturnsTrue()
    begin
        // Standalone contract: always use row-level locking (no SQL table hints).
        Assert.IsTrue(Src.RecordLevelLocking_Get(),
            'RecordRef.RecordLevelLocking must return true in standalone mode');
    end;

    [Test]
    procedure FieldExist_Known_DiffersFromUnknown_NegativeTrap()
    begin
        // Negative trap: FieldExist must actually introspect.
        Assert.AreNotEqual(
            Src.FieldExist_ByNo(60300, 1),
            Src.FieldExist_ByNo(60300, 999),
            'FieldExist on known and unknown field must produce different results');
    end;
}
