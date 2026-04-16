/// Tests for RecordId.TableNo, List.Sort, XmlNameTable.Add/Get.
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

    // ── List.Sort ─────────────────────────────────────────────────────────

    [Test]
    procedure ListSort_SortsAscending()
    var
        L: List of [Integer];
    begin
        // [GIVEN] unsorted list [3, 1, 2]
        L.Add(3);
        L.Add(1);
        L.Add(2);
        // [WHEN] Sort is called
        // [THEN] first element is 1, last is 3
        Assert.AreEqual(1, Src.SortListGetFirst(L), 'first element after sort must be 1');
        Assert.AreEqual(3, Src.SortListGetLast(L), 'last element after sort must be 3');
    end;

    [Test]
    procedure ListSort_EmptyList_NoError()
    var
        L: List of [Integer];
    begin
        // [GIVEN] empty list
        // [WHEN] Sort is called
        // [THEN] no error and count stays 0
        Assert.AreEqual(0, Src.SortEmptyList(L), 'empty list count must be 0 after sort');
    end;

    [Test]
    procedure ListSort_AlreadySorted_StaysOrdered()
    var
        L: List of [Integer];
    begin
        // [GIVEN] already-sorted list [1, 2, 3]
        L.Add(1);
        L.Add(2);
        L.Add(3);
        // [WHEN] Sort is called
        // [THEN] first element is still 1
        Assert.AreEqual(1, Src.SortListGetFirst(L), 'already-sorted list first element must be 1');
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
