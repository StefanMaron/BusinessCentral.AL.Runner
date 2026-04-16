/// Tests for RecordId.TableNo and XmlNameTable.Add/Get.
codeunit 100101 "SGP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SGP Src";

    // ── RecordId.TableNo ──────────────────────────────────────────────────

    [Test]
    procedure RecordId_DefaultTableNo_IsZero()
    var
        RecId: RecordId;
    begin
        // [GIVEN] a default (empty) RecordId
        // [WHEN] TableNo is called
        // [THEN] result is 0
        Assert.AreEqual(0, Src.GetTableNo(RecId), 'default RecordId.TableNo() must be 0');
    end;

    // ── XmlNameTable ──────────────────────────────────────────────────────

    [Test]
    procedure NameTable_AddAndGet_ReturnsValue()
    begin
        // [GIVEN] a name table with value 'hello' added
        // [WHEN] Get is called for 'hello'
        // [THEN] the same value is returned
        Assert.AreEqual('hello', Src.NameTableAddAndGet('hello'), 'NameTable.Get must return added value');
    end;

    [Test]
    procedure NameTable_GetMissing_ReturnsEmpty()
    begin
        // [GIVEN] a name table with nothing added
        // [WHEN] Get is called for 'missing'
        // [THEN] empty text is returned
        Assert.AreEqual('', Src.NameTableGetMissing('missing'), 'NameTable.Get for unknown value must return empty');
    end;
}
