/// Tests for NavOption → NavText runtime conversion — issue #1199.
/// Before the fix, storing an Enum/Option field value into a Text field slot via
/// FieldRef threw: "Object of type NavOption cannot be converted to type NavText".
///
/// The fix adds NavOption → NavText coercion in:
///   1. MockRecordHandle.CoerceToExpectedType (NavType.Text + NavOption stored value)
///   2. MockCodeunitHandle.ConvertArgInternal (NavOption arg for NavText parameter)
codeunit 235001 "NOT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "NOT Helper";

    // ──────────────────────────────────────────────────────────────────────────
    // CoerceToExpectedType — NavOption stored in Text field slot
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    procedure FieldRef_EnumToText_DefaultValue_DoesNotCrash()
    var
        Rec: Record "NOT Record";
        Result: Text;
    begin
        // [GIVEN] A record with Color at its default (ordinal 0)
        Rec.Init();
        Rec.Id := 1;
        Rec.Color := Enum::"NOT Color"::" ";
        Rec.Insert();
        Rec.Get(1);

        // [WHEN] Copy Color (Enum) → ColorText (Text) via FieldRef
        // Before fix: InvalidCastException "NavOption cannot be converted to NavText"
        Helper.CopyEnumToTextViaFieldRef(Rec);

        // [THEN] ColorText was stored and read back without exception
        Result := Rec.ColorText;
        Assert.AreNotEqual('CRASH', Result, 'ColorText must be set without exception');
    end;

    [Test]
    procedure FieldRef_EnumToText_NonZeroOrdinal_IsNotEmpty()
    var
        Rec: Record "NOT Record";
        Result: Text;
    begin
        // [GIVEN] A record with Color = Red (ordinal 1)
        Rec.Init();
        Rec.Id := 2;
        Rec.Color := Enum::"NOT Color"::Red;
        Rec.Insert();
        Rec.Get(2);

        // [WHEN] Copy Color (Enum) → ColorText (Text) via FieldRef
        Helper.CopyEnumToTextViaFieldRef(Rec);

        // [THEN] ColorText is not empty — a non-zero ordinal produces a non-empty string
        Result := Rec.ColorText;
        Assert.AreNotEqual('', Result,
            'ColorText must not be empty for a non-default enum value');
    end;

    [Test]
    procedure FieldRef_EnumToText_DifferentColors_ProduceDifferentTexts()
    var
        RecRed: Record "NOT Record";
        RecBlue: Record "NOT Record";
        RedText: Text;
        BlueText: Text;
    begin
        // [GIVEN] Two records with different Color values
        RecRed.Init();
        RecRed.Id := 3;
        RecRed.Color := Enum::"NOT Color"::Red;   // ordinal 1
        RecRed.Insert();

        RecBlue.Init();
        RecBlue.Id := 4;
        RecBlue.Color := Enum::"NOT Color"::Blue;  // ordinal 3
        RecBlue.Insert();

        RecRed.Get(3);
        RecBlue.Get(4);

        // [WHEN] Copy each Color → ColorText via FieldRef
        Helper.CopyEnumToTextViaFieldRef(RecRed);
        Helper.CopyEnumToTextViaFieldRef(RecBlue);

        // [THEN] The two text values are different — proves a non-stub conversion
        RedText := RecRed.ColorText;
        BlueText := RecBlue.ColorText;
        Assert.AreNotEqual(RedText, BlueText,
            'Red and Blue must produce distinct text values');
    end;

    [Test]
    procedure RoundTrip_RedEnum_YieldsOrdinalString()
    var
        Result: Text;
    begin
        // [GIVEN/WHEN] Round-trip: insert with Color=Red (ordinal 1), copy to ColorText, read back
        Result := Helper.RoundTripEnumToText(5, Enum::"NOT Color"::Red);

        // [THEN] Returns the ordinal as string: '1'
        Assert.AreEqual('1', Result,
            'Red (ordinal 1) stored via FieldRef must read back as ''1''');
    end;

    [Test]
    procedure RoundTrip_BlueEnum_YieldsOrdinalString()
    var
        Result: Text;
    begin
        // [GIVEN/WHEN] Round-trip: insert with Color=Blue (ordinal 3), copy to ColorText, read back
        Result := Helper.RoundTripEnumToText(6, Enum::"NOT Color"::Blue);

        // [THEN] Returns the ordinal as string: '3'
        Assert.AreEqual('3', Result,
            'Blue (ordinal 3) stored via FieldRef must read back as ''3''');
    end;

    [Test]
    procedure RoundTrip_DefaultEnum_YieldsZeroString()
    var
        Result: Text;
    begin
        // [GIVEN/WHEN] Round-trip: insert with Color=" " (ordinal 0), copy to ColorText, read back
        Result := Helper.RoundTripEnumToText(7, Enum::"NOT Color"::" ");

        // [THEN] Returns the ordinal as string: '0'
        Assert.AreEqual('0', Result,
            'Default enum (ordinal 0) stored via FieldRef must read back as ''0''');
    end;
}
