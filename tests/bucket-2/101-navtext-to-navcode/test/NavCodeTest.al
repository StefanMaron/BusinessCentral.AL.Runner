// Test suite for NavText → NavCode cast fix.
// Bug: FieldRef.Value setter stores NavText in a Code[N] field slot
// (via MockRecordRef.SetFieldValue → SetFieldValueSafe(fieldNo, NavType.Text, navText)).
// A subsequent direct record field read emits (NavCode)GetFieldValueSafe(2, NavType.Code),
// which throws InvalidCastException because the stored value is NavText not NavCode.
// Fix: SetFieldValueSafe must coerce NavText → NavCode when expectedType == NavType.Code.
codeunit 98401 "NTC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // Positive: set a Code field via FieldRef (NavText), then read it directly.
    // Before the fix this throws InvalidCastException; after it must round-trip.
    [Test]
    procedure FieldRef_TextValue_ThenDirectRead_Succeeds()
    var
        Helper: Codeunit "NTC Helper";
        Result: Code[20];
    begin
        Result := Helper.InsertAndReadViaFieldRef(1, 'ALPHA');
        Assert.AreEqual('ALPHA', Result, 'Code field set via FieldRef (NavText) must round-trip without cast error');
    end;

    // Positive: code is uppercased — ensures we don't just return NavText.ToString().
    [Test]
    procedure FieldRef_LowercaseText_IsUppercased()
    var
        Helper: Codeunit "NTC Helper";
        Result: Code[20];
    begin
        Result := Helper.InsertAndReadViaFieldRef(2, 'lower');
        Assert.AreEqual('LOWER', Result, 'Code field must uppercase the NavText value on coercion');
    end;

    // Positive: empty text in Code field slot returns empty Code.
    [Test]
    procedure FieldRef_EmptyText_ReturnsEmptyCode()
    var
        Helper: Codeunit "NTC Helper";
        Result: Code[20];
    begin
        Result := Helper.InsertAndReadViaFieldRef(3, '');
        Assert.AreEqual('', Result, 'Empty NavText in Code slot must return empty Code');
    end;

    // Regression: direct Code assignment still works after the fix.
    [Test]
    procedure DirectCode_Assignment_StillWorks()
    var
        Helper: Codeunit "NTC Helper";
        Result: Code[20];
    begin
        Helper.InsertWithCodeCategory(4, 'BETA');
        Result := Helper.GetCategory(4);
        Assert.AreEqual('BETA', Result, 'Direct Code assignment must still round-trip correctly');
    end;
}
