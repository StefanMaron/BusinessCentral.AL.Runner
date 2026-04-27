/// Proving tests for miscellaneous not-tested overloads sweep (issue #1400).
codeunit 1318001 "Sweep Misc Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Sweep Misc Src";

    // ── TextBuilder.Replace (Text, Text, Integer, Integer) ───────────────────

    [Test]
    procedure TextBuilder_Replace_Range_OnlyReplacesInRange()
    begin
        // 'Hello World': W is at 1-based position 7 with length 5
        Assert.AreEqual('Hello Xorld',
            Src.TextBuilder_Replace_Range('Hello World', 'W', 'X', 7, 5),
            'TextBuilder.Replace(old, new, start, count) must replace within the range');
    end;

    [Test]
    procedure TextBuilder_Replace_Range_OutOfRange_NoOp()
    begin
        Assert.AreEqual('Hello World',
            Src.TextBuilder_Replace_Range('Hello World', 'H', 'Z', 100, 5),
            'TextBuilder.Replace with startIndex out of range must not modify the content');
    end;

    // ── IsolatedStorage.Get (Text, DataScope, Text) ──────────────────────────

    [Test]
    procedure IsoStorage_Get_DataScope_RoundTrips()
    begin
        Assert.AreEqual('scope-value',
            Src.IsoStorage_Set_Get_DataScope('scope-key-1', 'scope-value', DataScope::Module),
            'IsolatedStorage.Get(Text, DataScope, Text) must retrieve the stored value');
    end;

    [Test]
    procedure IsoStorage_Get_DataScope_DifferentKeys_DifferentValues()
    begin
        Assert.AreNotEqual(
            Src.IsoStorage_Set_Get_DataScope('scope-k-a', 'alpha', DataScope::Module),
            Src.IsoStorage_Set_Get_DataScope('scope-k-b', 'beta', DataScope::Module),
            'Different keys must store and retrieve different values');
    end;

    // ── IsolatedStorage.Get (Text, Text) ─────────────────────────────────────

    [Test]
    procedure IsoStorage_Get_2Arg_RoundTrips()
    begin
        Assert.AreEqual('my-value',
            Src.IsoStorage_Set_Get_2Arg('key-2arg', 'my-value'),
            'IsolatedStorage.Get(Text, Text) must retrieve the stored value');
    end;

    // ── IsolatedStorage.Contains (Text, DataScope) ───────────────────────────

    [Test]
    procedure IsoStorage_Contains_DataScope_True()
    begin
        Assert.IsTrue(
            Src.IsoStorage_Contains_DataScope('contains-key', DataScope::Module),
            'IsolatedStorage.Contains(Text, DataScope) must return true after Set');
    end;

    // ── FileUpload.CreateInStream (InStream, TextEncoding) ───────────────────

    [Test]
    procedure FileUpload_CreateInStream_WithEncoding_IsNoOp()
    begin
        Assert.IsTrue(Src.FileUpload_CreateInStream_WithEncoding_NoThrow(),
            'FileUpload.CreateInStream(InStream, TextEncoding) must not throw');
    end;

    // ── RecordRef.Field (Text) ───────────────────────────────────────────────

    [Test]
    procedure RecordRef_Field_ByName_ReturnsCorrectFieldName()
    begin
        Assert.AreEqual('Name',
            Src.RecordRef_Field_ByName(Database::"SMO Rec", 'Name'),
            'RecordRef.Field(Text) must return a FieldRef for the field with that name');
    end;

    // ── RecordRef.FindSet (Boolean, Boolean) ─────────────────────────────────

    [Test]
    procedure RecordRef_FindSet_TwoArg_EmptyTableReturnsFalse()
    begin
        Assert.AreEqual(false,
            Src.RecordRef_FindSet_TwoArg(Database::"SMO Rec"),
            'RecordRef.FindSet(false, false) on empty table must return false');
    end;

    // ── RecordRef.CopyLinks (Table) ──────────────────────────────────────────

    [Test]
    procedure RecordRef_CopyLinks_Table_IsNoOp()
    begin
        Assert.IsTrue(Src.RecordRef_CopyLinks_Table_NoThrow(),
            'RecordRef.CopyLinks(Table) must not throw in standalone mode');
    end;

    // ── HttpHeaders.Add (Text, Text) ─────────────────────────────────────────

    [Test]
    procedure HttpHeaders_Add_Text_IsNoOp()
    begin
        Assert.IsTrue(Src.HttpHeaders_Add_Text_NoThrow(),
            'HttpHeaders.Add(Text, Text) must not throw');
    end;

    // ── HttpHeaders.TryAddWithoutValidation (Text, Text) ─────────────────────

    [Test]
    procedure HttpHeaders_TryAddWithoutValidation_IsNoOp()
    begin
        Assert.IsTrue(Src.HttpHeaders_TryAddWithoutValidation_NoThrow(),
            'HttpHeaders.TryAddWithoutValidation(Text, Text) must not throw');
    end;
}
