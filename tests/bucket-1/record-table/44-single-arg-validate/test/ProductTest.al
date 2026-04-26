codeunit 56451 "SAV Product Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SingleArgValidateFiresTrigger()
    var
        Prod: Record "SAV Product";
        Cfg: Codeunit "SAV Configurator";
    begin
        // [GIVEN] A product whose Price is populated via Evaluate into a local
        Prod."No." := 'A';
        Prod.Insert();

        // [WHEN] ApplyPriceFromText writes Price and calls single-arg Validate
        Cfg.ApplyPriceFromText(Prod, '12.5');

        // [THEN] OnValidate runs (count incremented) and Price is set
        Assert.AreEqual(12.5, Prod.Price, 'Price should be 12.5 after Evaluate');
        Assert.AreEqual(1, Prod."Validated Count", 'Single-arg Validate must fire OnValidate');
    end;

    [Test]
    procedure SingleArgValidateOnDateFormulaField()
    var
        Prod: Record "SAV Product";
        Cfg: Codeunit "SAV Configurator";
    begin
        // Reporter's shape: Evaluate into DateFormula field, then single-arg Validate
        Prod."No." := 'D';
        Prod.Insert();

        Cfg.ApplyDateFormulaFromText(Prod, '<1D>');

        Assert.AreEqual(1, Prod."Validated Count", 'DateFormula Validate must fire OnValidate');
    end;

    [Test]
    procedure SingleArgValidateBubblesError()
    var
        Prod: Record "SAV Product";
        Cfg: Codeunit "SAV Configurator";
    begin
        // [GIVEN] A product
        Prod."No." := 'B';
        Prod.Insert();

        // [WHEN] ProbeNegativePrice sets Price to -1 and validates
        // [THEN] OnValidate raises and the error is caught by asserterror
        asserterror Cfg.ProbeNegativePrice(Prod);
        Assert.ExpectedError('Price must not be negative');
    end;
}
