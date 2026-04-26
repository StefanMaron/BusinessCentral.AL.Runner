codeunit 59991 "EIR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIR Src";

    [Test]
    procedure RecordId_Fresh_TableNoIsZero()
    begin
        // A default-initialised ErrorInfo has a RecordId with TableNo 0.
        Assert.AreEqual(0, Src.FreshRecordId_TableNo(),
            'Fresh ErrorInfo RecordId.TableNo must be 0');
    end;

    [Test]
    procedure RecordId_Fresh_EqualsDefault()
    begin
        // Format() comparison against a default RecordId.
        Assert.IsTrue(Src.FreshRecordId_MatchesDefault(),
            'Fresh ErrorInfo RecordId must equal the default RecordId');
    end;

    [Test]
    procedure RecordId_Read_DoesNotThrow()
    begin
        // Reading the property into a local variable must complete.
        Assert.IsTrue(Src.ReadRecordId_DoesNotThrow(),
            'Reading ErrorInfo.RecordId must complete without throwing');
    end;

    [Test]
    procedure RecordId_ConsecutiveReads_Stable()
    begin
        // Two consecutive reads on the same ErrorInfo must yield equal values.
        Assert.IsTrue(Src.ReadRecordIdTwice_Stable(),
            'Consecutive RecordId reads must be stable');
    end;
}
