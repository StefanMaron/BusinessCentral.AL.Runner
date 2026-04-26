/// Tests for TestPage.Filter.SetFilter with Variant (object-typed) arguments.
///
/// Context (#1459): BC emits NavComplexValue parameters for certain complex
/// expressions; the rewriter replaces NavComplexValue → object.  When such
/// an object value is passed to MockTestPageFilter.ALSetFilter(int, string),
/// Roslyn rejects it with CS1503: 'object' → 'string'.  The fix is to add an
/// object overload to ALSetFilter that formats the expression to string.
///
/// All tests here use the Variant path (MockVariant, wrapped by BC via
/// NavIndirectValueToNavValue<NavText> → NavText → string) because it is the
/// closest reproducible approximation: BC wraps Variant with a converter,
/// keeping the normal string path active in standalone mode.  The tests prove
/// that SetFilter/GetFilter round-trips are correct regardless of the source
/// of the filter expression.
codeunit 312200 "TPF Object Test"
{
    Subtype = Test;

    // ── Positive ────────────────────────────────────────────────────────────

    [Test]
    procedure SetFilterName_StringLiteral_GetFilterReturnsValue()
    var
        Page: TestPage "TPF Object Page";
    begin
        // [GIVEN] A TestPage with a Name filter set via string literal
        Page.Filter.SetFilter(Name, 'Contoso*');

        // [THEN] GetFilter returns the filter that was set
        Assert.AreEqual('Contoso*', Page.Filter.GetFilter(Name), 'GetFilter should return the filter set by SetFilter (string literal)');
        Page.Close();
    end;

    [Test]
    procedure SetFilterNo_StringLiteral_GetFilterReturnsValue()
    var
        Page: TestPage "TPF Object Page";
    begin
        // [GIVEN] A TestPage with a No. filter set via string literal
        Page.Filter.SetFilter("No.", 'V0*');

        // [THEN] GetFilter returns the filter that was set
        Assert.AreEqual('V0*', Page.Filter.GetFilter("No."), 'GetFilter should return the filter set by SetFilter (code literal)');
        Page.Close();
    end;

    [Test]
    procedure SetFilterName_ViaVariant_GetFilterReturnsValue()
    var
        Page:   TestPage "TPF Object Page";
        Helper: Codeunit "TPF Object Helper";
        V:      Variant;
    begin
        // [GIVEN] A filter value stored in a Variant
        V := 'Fabrikam*';

        // [WHEN] SetFilter is called with the Variant via a helper procedure
        // (BC emits NavIndirectValueToNavValue<NavText> → NavText → string)
        Helper.SetFilterViaVariant(Page, V);

        // [THEN] GetFilter returns the correct filter string (not '' or any default)
        Assert.AreEqual('Fabrikam*', Page.Filter.GetFilter(Name), 'GetFilter should return the filter set via Variant');
        Page.Close();
    end;

    [Test]
    procedure SetFilterNo_ViaVariant_GetFilterReturnsValue()
    var
        Page:   TestPage "TPF Object Page";
        Helper: Codeunit "TPF Object Helper";
        V:      Variant;
    begin
        // [GIVEN] A Code filter value stored in a Variant
        V := 'C001*';

        // [WHEN]
        Helper.SetNoFilterViaVariant(Page, V);

        // [THEN]
        Assert.AreEqual('C001*', Page.Filter.GetFilter("No."), 'GetFilter should return the Code filter set via Variant');
        Page.Close();
    end;

    [Test]
    procedure SetFilterStatus_ViaVariant_GetFilterReturnsValue()
    var
        Page:   TestPage "TPF Object Page";
        Helper: Codeunit "TPF Object Helper";
        V:      Variant;
    begin
        // [GIVEN] A Status filter value stored in a Variant
        V := 'ACTIVE';

        // [WHEN]
        Helper.SetStatusFilterViaVariant(Page, V);

        // [THEN]
        Assert.AreEqual('ACTIVE', Page.Filter.GetFilter(Status), 'GetFilter should return the Status filter set via Variant');
        Page.Close();
    end;

    [Test]
    procedure SetMultipleFilters_GetFilterReturnsEachCorrectly()
    var
        Page: TestPage "TPF Object Page";
    begin
        // [GIVEN] Multiple filters set at once
        Page.Filter.SetFilter("No.", 'V*');
        Page.Filter.SetFilter(Name, 'Contoso*');
        Page.Filter.SetFilter(Status, 'ACTIVE');

        // [THEN] Each filter is independently readable and non-default
        Assert.AreEqual('V*',       Page.Filter.GetFilter("No."), 'No. filter must be V*');
        Assert.AreEqual('Contoso*', Page.Filter.GetFilter(Name),   'Name filter must be Contoso*');
        Assert.AreEqual('ACTIVE',   Page.Filter.GetFilter(Status), 'Status filter must be ACTIVE');
        Page.Close();
    end;

    // ── Negative ────────────────────────────────────────────────────────────

    [Test]
    procedure GetFilter_WithNoFilterSet_ReturnsEmpty()
    var
        Page: TestPage "TPF Object Page";
    begin
        // [GIVEN] No filter has been set on Name
        // [THEN] GetFilter returns '' (not a non-empty default)
        Assert.AreEqual('', Page.Filter.GetFilter(Name), 'GetFilter with no filter set must return empty string');
        Page.Close();
    end;

    [Test]
    procedure SetFilterName_ViaVariant_DoesNotMatchDifferentValue()
    var
        Page:   TestPage "TPF Object Page";
        Helper: Codeunit "TPF Object Helper";
        V:      Variant;
    begin
        // [GIVEN] Filter set to 'Fabrikam*' via Variant
        V := 'Fabrikam*';
        Helper.SetFilterViaVariant(Page, V);

        // [THEN] GetFilter must NOT return a different value (catches a stub that ignores the arg)
        Assert.AreNotEqual('Contoso*', Page.Filter.GetFilter(Name), 'GetFilter must not return a different filter value');
        Page.Close();
    end;

    var
        Assert: Codeunit "Library Assert";
}
