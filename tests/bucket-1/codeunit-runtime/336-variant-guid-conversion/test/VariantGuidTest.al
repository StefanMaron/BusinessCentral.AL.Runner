codeunit 1320507 "VG Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VG Src";

    [Test]
    procedure GetBySystemId_FromVariant_ReturnsTrue()
    var
        rec: Record "VG Table";
        v: Variant;
    begin
        rec.Init();
        rec.Id := 1;
        rec.Insert();
        v := rec.SystemId;

        Assert.IsTrue(Src.GetBySystemIdFromVariant(v),
            'RecordRef.GetBySystemId should accept a Variant holding a Guid');
    end;

    [Test]
    procedure GetBySystemId_FromVariant_Missing_ReturnsFalse()
    var
        v: Variant;
    begin
        v := CreateGuid();

        Assert.IsFalse(Src.GetBySystemIdFromVariant(v),
            'RecordRef.GetBySystemId should return false for a missing SystemId');
    end;
}
