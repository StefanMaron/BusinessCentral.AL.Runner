codeunit 1320515 "Variant Dict Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Variant Dict Src";

    [Test]
    procedure Variant_ToDictionary_Roundtrip()
    var
        v: Variant;
        dict: Dictionary of [Text, Integer];
        result: Dictionary of [Text, Integer];
        value: Integer;
    begin
        dict.Add('A', 42);
        v := dict;

        result := Src.GetDictFromVariant(v);

        Assert.IsTrue(result.Get('A', value), 'Dictionary should contain key A');
        Assert.AreEqual(42, value, 'Variant-to-Dictionary conversion should preserve entries');
    end;

    [Test]
    procedure Variant_ToDictionary_InvalidType_Throws()
    var
        v: Variant;
    begin
        v := 'not a dictionary';

        asserterror Src.GetDictFromVariantChecked(v);
        Assert.ExpectedError('Expected dictionary');
    end;
}
