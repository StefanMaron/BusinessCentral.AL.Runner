codeunit 54400 "Test GetFilters"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetFiltersEmptyWhenNoFiltersActive()
    var
        Rec: Record "GF Probe";
    begin
        // Negative: no filters set — GetFilters should return ''.
        Assert.AreEqual('', Rec.GetFilters(),
            'GetFilters must be empty when no filters are active');
    end;

    [Test]
    procedure GetFiltersIncludesSingleEqualityFilter()
    var
        Rec: Record "GF Probe";
        Filters: Text;
    begin
        // Positive: one SetRange equality filter is rendered as "Field: Value".
        Rec.SetRange(Status, 1);
        Filters := Rec.GetFilters();

        Assert.IsTrue(StrPos(Filters, 'Status') > 0,
            'GetFilters should mention the Status field — got: ' + Filters);
        Assert.IsTrue(StrPos(Filters, '1') > 0,
            'GetFilters should include the filter value 1 — got: ' + Filters);
    end;

    [Test]
    procedure GetFiltersCombinesMultipleFilters()
    var
        Rec: Record "GF Probe";
        Filters: Text;
    begin
        // Positive: multiple SetRange calls must all appear in the combined text.
        Rec.SetRange(Status, 1);
        Rec.SetRange(Category, 10);
        Filters := Rec.GetFilters();

        Assert.IsTrue(StrPos(Filters, 'Status') > 0,
            'Combined filters should include Status — got: ' + Filters);
        Assert.IsTrue(StrPos(Filters, 'Category') > 0,
            'Combined filters should include Category — got: ' + Filters);
        Assert.IsTrue(StrPos(Filters, '1') > 0,
            'Combined filters should include value 1 — got: ' + Filters);
        Assert.IsTrue(StrPos(Filters, '10') > 0,
            'Combined filters should include value 10 — got: ' + Filters);
    end;

    [Test]
    procedure GetFiltersEmptyAfterReset()
    var
        Rec: Record "GF Probe";
    begin
        // Negative/reset: Reset clears all filters so GetFilters returns ''.
        Rec.SetRange(Status, 5);
        Assert.AreNotEqual('', Rec.GetFilters(),
            'Precondition: filter must be active before Reset');

        Rec.Reset();
        Assert.AreEqual('', Rec.GetFilters(),
            'GetFilters must be empty after Reset');
    end;

    [Test]
    procedure GetFiltersRendersRangeFilter()
    var
        Rec: Record "GF Probe";
        Filters: Text;
    begin
        // Positive: SetRange(field, from, to) renders as "from..to".
        Rec.SetRange(Status, 1, 5);
        Filters := Rec.GetFilters();

        Assert.IsTrue(StrPos(Filters, '1..5') > 0,
            'Range filter should render as "1..5" — got: ' + Filters);
    end;
}
