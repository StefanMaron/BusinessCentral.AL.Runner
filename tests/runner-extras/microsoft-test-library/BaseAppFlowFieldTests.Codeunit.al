/// <summary>
/// Moved from tests/runner-extras/microsoft-dependencies. Purchase Line (table 39) field 2701
/// "Matched Order Lines" does not exist in Base Application 27.0, 27.3, or 27.5 — verified by
/// decompiling each shipped SymbolReference.json directly; the field was introduced in BC 28.0,
/// same as Application Test Library. Living in microsoft-dependencies forced a 28.0 floor back
/// onto that whole suite despite its app.json declaring 27.0; this suite already carries that
/// floor for Application Test Library, so it costs nothing extra here.
/// </summary>
codeunit 62202 "Base App Flow Field Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "MTL Assert";

    [Test]
    procedure BaseAppFlowField_MatchedOrderLines_CalcFieldsAndSetRange()
    // Reproduces the Purch.-Post posting-path gap (RecoverySolutions CU74486).
    // Purchase Line (table 39) field 2701 "Matched Order Lines" is a FlowField:
    //   count("Matched Order Line" where("Document Line SystemId" = field(SystemId), ...)).
    // Two distinct runner gaps fixed here, both reached from this AL pattern:
    //   1. The FlowField's source table "Matched Order Line" (5817) wasn't parsed when
    //      Purchase Line's metatable was built, so its NCLMetaCalculationFormula fell back
    //      to the shared EmptyFormula (null Filters / source table 0). CalcFields then either
    //      NRE'd in FlowFieldsHelper.FieldsAndFormulaAreSelfReferencing (null Filters) or threw
    //      "no NCLMetaTable for table 0" via EmptyFormula.SourceField. Fixed by resolving the
    //      source table by name from the BC .app symbol index + a null-safe self-ref probe.
    //   2. SetRange on the FlowField forces TempTableDataProvider's filter visitor to evaluate
    //      it via FlowFieldsHelper.CalcFieldsAsync directly (the exact Purch.-Post pattern
    //      `SetRange("Matched Order Lines", 0)`).
    var
        PurchaseLine: Record "Purchase Line" temporary;
    begin
        // [GIVEN] A temporary Purchase Line with no matched order lines — this mirrors the
        // TempPurchLine that Purch.-Post filters via SetRange("Matched Order Lines", 0).
        PurchaseLine.Init();
        PurchaseLine."Document Type" := PurchaseLine."Document Type"::Invoice;
        PurchaseLine."Document No." := 'ALR-FF-1';
        PurchaseLine."Line No." := 10000;
        PurchaseLine.Insert();

        // [WHEN] CalcFields on the FlowField (was: NRE / "no NCLMetaTable for table 0").
        PurchaseLine.CalcFields("Matched Order Lines");

        // [THEN] The count is 0 — no "Matched Order Line" rows reference this line.
        Assert.AreEqual(0, PurchaseLine."Matched Order Lines",
            'Matched Order Lines FlowField must compute 0 when there are no matched order lines.');

        // [WHEN] Filtering on the FlowField via SetRange (the Purch.-Post filter-visitor path).
        PurchaseLine.Reset();
        PurchaseLine.SetRange("Document No.", 'ALR-FF-1');
        PurchaseLine.SetRange("Matched Order Lines", 0);

        // [THEN] The line matches (count 0) — the filter visitor evaluated the FlowField
        // without NRE-ing on the empty/self-referencing CalcFormula probe.
        Assert.AreEqual(1, PurchaseLine.Count(),
            'The zero-matched-order-lines Purchase Line must match SetRange(Matched Order Lines, 0).');
        Assert.IsTrue(PurchaseLine.FindFirst(),
            'FindFirst must succeed on the FlowField-filtered Purchase Line set.');
    end;
}
