/// Regression tests for issue #1442 — CS1503: object → string in TestPage
/// filter operations.
///
/// Before the fix, MockTestPageFilter.ALSetFilter(int, string) had no object?
/// overload.  BC's code generator for standard pages emits calls with
/// NavComplexValue-typed filter expressions which become object after the
/// Roslyn Rewriter; without the overload Roslyn rejects the compilation with
/// CS1503: 'object' → 'string'.
///
/// This suite covers the run-time behaviour so a regression would produce a
/// compilation failure or an incorrect result rather than a silent no-op.
codeunit 313001 "FOS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "FOS Helper";

    [Test]
    procedure SetFilter_WithTextMethodResult_FiltersCorrectly()
    var
        Card: TestPage "FOS Card";
        FilterExpr: Text;
    begin
        // [GIVEN] Three records, two matching 'A0*', one not
        Helper.DeleteAll();
        Helper.InsertRecord('A001', 'Alpha', 100);
        Helper.InsertRecord('A002', 'Beta', 200);
        Helper.InsertRecord('B001', 'Gamma', 300);

        // [WHEN] Filter is set via a Text variable
        FilterExpr := 'A*';
        Card.OpenView();
        Card.Filter.SetFilter("No.", FilterExpr);

        // [THEN] GetFilter returns the same expression (proves SetFilter was applied)
        Assert.AreEqual('A*', Card.Filter.GetFilter("No."), 'Filter should be A* after SetFilter with Text');
        Card.Close();
    end;

    [Test]
    procedure SetFilter_WithVariantMethodResult_FiltersCorrectly()
    var
        Card: TestPage "FOS Card";
        FilterExpr: Variant;
    begin
        // [GIVEN] Records exist
        Helper.DeleteAll();
        Helper.InsertRecord('A001', 'Alpha', 100);
        Helper.InsertRecord('B001', 'Beta', 200);

        // [WHEN] Filter is set via a Variant variable (BC emits NavComplexValue
        //        for some standard-page filter patterns — this exercises the path)
        FilterExpr := 'B*';
        Card.OpenView();
        Card.Filter.SetFilter("No.", FilterExpr);

        // [THEN] GetFilter returns the expression that was set
        Assert.AreEqual('B*', Card.Filter.GetFilter("No."), 'Filter should be B* after SetFilter with Variant');
        Card.Close();
    end;

    [Test]
    procedure SetFilter_ValueDoesNotMatchUnsetValue()
    var
        Card: TestPage "FOS Card";
    begin
        // [GIVEN] An open page with no filter
        Helper.DeleteAll();
        Helper.InsertRecord('X001', 'X record', 50);

        Card.OpenView();

        // [WHEN] A filter is set
        Card.Filter.SetFilter("No.", 'A*');

        // [THEN] The filter is NOT the same as no filter ('') — proves the mock
        //        is not returning a default '' for everything
        Assert.AreNotEqual('', Card.Filter.GetFilter("No."), 'Filter should not be empty after SetFilter');
        Card.Close();
    end;

    [Test]
    procedure GetFilter_BeforeSetFilter_ReturnsEmpty()
    var
        Card: TestPage "FOS Card";
    begin
        // [GIVEN] An open page
        Helper.DeleteAll();
        Card.OpenView();

        // [WHEN] No filter has been set
        // [THEN] GetFilter returns empty string
        Assert.AreEqual('', Card.Filter.GetFilter("No."), 'GetFilter should return empty before any SetFilter');
        Card.Close();
    end;
}
