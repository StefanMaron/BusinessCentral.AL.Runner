/// Tests proving RecordRef survives a Variant round-trip and FieldRef.Value
/// works correctly when the underlying record was opened via RecordRef.
/// Covers issue #983.
///
/// Test strategy:
///   RoundtripRecordRefViaVariant — assigns RecordRef → Variant → RecordRef;
///     proves the BC-emitted NavIndirectValueToNavValue<NavRecordRef> path works.
///   GetNameViaRecordRef — reads a text field via FieldRef.Value and Format();
///     proves the canonical real-world trigger from the issue report.
codeunit 97902 "NVI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "NVI Src";

    // ── RoundtripRecordRefViaVariant ──────────────────────────────────────────

    [Test]
    procedure RoundtripRecordRef_ReturnsCorrectTableNo()
    var
        rec: Record "NVI Table";
        rr: RecordRef;
    begin
        // [GIVEN] A RecordRef opened on NVI Table
        rr.Open(Database::"NVI Table");
        // [WHEN]  The RecordRef is round-tripped through a Variant
        // [THEN]  The recovered RecordRef has the same table number
        Assert.AreEqual(Database::"NVI Table", Src.RoundtripRecordRefViaVariant(rr),
            'Table number must survive Variant round-trip');
    end;

    // ── GetNameViaRecordRef ───────────────────────────────────────────────────

    [Test]
    procedure GetNameViaRecordRef_ReturnsFieldValue()
    var
        rec: Record "NVI Table";
        rr: RecordRef;
    begin
        // [GIVEN] A record with a known Name value
        rec.Init();
        rec.Id := 1;
        rec.Name := 'Hello';
        rec.Insert();
        rr.GetTable(rec);
        // [WHEN]  GetNameViaRecordRef is called
        // [THEN]  The formatted field value is returned
        Assert.AreEqual('Hello', Src.GetNameViaRecordRef(rr),
            'Name field value must be returned via FieldRef.Value / Format');
    end;

    [Test]
    procedure GetNameViaRecordRef_EmptyString_WhenNameBlank()
    var
        rec: Record "NVI Table";
        rr: RecordRef;
    begin
        // [GIVEN] A record with a blank Name
        rec.Init();
        rec.Id := 2;
        rec.Name := '';
        rec.Insert();
        rr.GetTable(rec);
        // [WHEN]  GetNameViaRecordRef is called
        // [THEN]  Empty string is returned
        Assert.AreEqual('', Src.GetNameViaRecordRef(rr),
            'Blank Name must return empty string');
    end;
}
