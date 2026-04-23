/// <summary>
/// Proves that MockRecordHandle implements IConvertible so that
/// Convert.ToInt32 / Convert.ToBoolean / Convert.ChangeType(record, T) do not throw
/// "Unable to cast MockRecordHandle to IConvertible".
///
/// The BC transpiler emits NavIndirectValueToInt32 / NavIndirectValueToBoolean when a
/// Variant holding a Record is assigned to an Integer / Boolean local.  After rewriting
/// these become AlCompat.NavIndirectValueToInt32 / NavIndirectValueToBoolean, which
/// call Convert.ToInt32 / Convert.ToBoolean internally — both require IConvertible.
/// </summary>
codeunit 235002 "RIC Tests"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    /// <summary>
    /// Positive: Variant holding a default Record assigned to Integer must return 0 (not crash).
    /// </summary>
    [Test]
    procedure VariantRecord_ToInt_ReturnsZero()
    var
        Rec: Record "RIC Table";
        Helper: Codeunit "RIC Helper";
        V: Variant;
        ResultInt: Integer;
        ResultBool: Boolean;
    begin
        // Positive: IConvertible.ToInt32 must return 0 for a Record
        V := Rec;
        Helper.VariantRecordToIntBool(V, ResultInt, ResultBool);
        Assert.AreEqual(0, ResultInt, 'Convert.ToInt32(Record) must return 0');
    end;

    /// <summary>
    /// Positive: Variant holding a default Record assigned to Boolean must return false (not crash).
    /// </summary>
    [Test]
    procedure VariantRecord_ToBool_ReturnsFalse()
    var
        Rec: Record "RIC Table";
        Helper: Codeunit "RIC Helper";
        V: Variant;
        ResultInt: Integer;
        ResultBool: Boolean;
    begin
        // Positive: IConvertible.ToBoolean must return false for a Record
        V := Rec;
        Helper.VariantRecordToIntBool(V, ResultInt, ResultBool);
        Assert.AreEqual(false, ResultBool, 'Convert.ToBoolean(Record) must return false');
    end;

    /// <summary>
    /// Positive: Format(Variant-holding-Record) must return a non-empty string.
    /// Proves Convert path does not break the Format round-trip.
    /// </summary>
    [Test]
    procedure VariantRecord_Format_ReturnsNonEmpty()
    var
        Rec: Record "RIC Table";
        Helper: Codeunit "RIC Helper";
        V: Variant;
        Result: Text;
    begin
        // Positive: Format of a Variant wrapping a Record must produce a non-empty string
        V := Rec;
        Result := Helper.FormatVariantRecord(V);
        Assert.IsTrue(Result <> '', 'Format(Variant<Record>) must return a non-empty string');
    end;

    /// <summary>
    /// Positive: Record → Variant → Format round-trip with a populated key
    /// must return a string containing the key value.
    /// </summary>
    [Test]
    procedure PopulatedRecord_ToVariant_FormatContainsKey()
    var
        Rec: Record "RIC Table";
        Helper: Codeunit "RIC Helper";
        Result: Text;
    begin
        // Positive: Format of a populated Record through Variant must contain the PK
        Rec.Id := 99;
        Rec.Name := 'IConvertible';
        Rec.Insert();
        Rec.Get(99);
        Result := Helper.RecordToVariantToText(Rec);
        Assert.IsTrue(Result <> '', 'Format(populated Record via Variant) must be non-empty');
        Assert.IsTrue(StrPos(Result, '99') > 0, 'Format result must contain the key value 99');
    end;

    /// <summary>
    /// Negative: After extracting Int from a Variant-holding-Record the value is 0,
    /// not a garbage value — assert a specific non-default would also be wrong, so we
    /// confirm it is exactly 0 (the safe default from IConvertible.ToInt32).
    /// </summary>
    [Test]
    procedure VariantRecord_ToInt_IsExactlyZero_NotGarbage()
    var
        Rec: Record "RIC Table";
        Helper: Codeunit "RIC Helper";
        V: Variant;
        ResultInt: Integer;
        ResultBool: Boolean;
    begin
        // Negative: the extracted integer must be exactly 0, not some random value
        Rec.Id := 7;
        V := Rec;
        Helper.VariantRecordToIntBool(V, ResultInt, ResultBool);
        Assert.AreEqual(0, ResultInt, 'IConvertible.ToInt32 must yield 0, not the record key');
    end;
}
