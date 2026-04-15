codeunit 54600 "Test HasFilter"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure HasFilterFalseOnFreshRecord()
    var
        Rec: Record "HF Probe";
    begin
        // Negative: fresh record variable has no filters.
        Assert.IsFalse(Rec.HasFilter,
            'HasFilter must be false on a fresh record variable');
    end;

    [Test]
    procedure HasFilterTrueAfterSetRange()
    var
        Rec: Record "HF Probe";
    begin
        // Positive: SetRange activates a filter.
        Rec.SetRange(Status, 1);
        Assert.IsTrue(Rec.HasFilter,
            'HasFilter must be true after SetRange');
    end;

    [Test]
    procedure HasFilterTrueAfterSetFilter()
    var
        Rec: Record "HF Probe";
    begin
        // Positive: SetFilter also activates a filter.
        Rec.SetFilter(Status, '>5');
        Assert.IsTrue(Rec.HasFilter,
            'HasFilter must be true after SetFilter');
    end;

    [Test]
    procedure HasFilterFalseAfterReset()
    var
        Rec: Record "HF Probe";
    begin
        // Negative/reset: Reset clears all filters.
        Rec.SetRange(Status, 1);
        Rec.SetRange(Category, 2);
        Assert.IsTrue(Rec.HasFilter, 'Precondition: filters active');

        Rec.Reset();
        Assert.IsFalse(Rec.HasFilter,
            'HasFilter must be false after Reset');
    end;

    [Test]
    procedure HasFilterRemainsTrueAfterOneOfManyCleared()
    var
        Rec: Record "HF Probe";
    begin
        // Positive/partial: clearing one filter leaves others; HasFilter still true.
        Rec.SetRange(Status, 1);
        Rec.SetRange(Category, 2);

        Rec.SetRange(Status);  // clear the Status filter only
        Assert.IsTrue(Rec.HasFilter,
            'HasFilter must still be true while Category filter is active');

        Rec.SetRange(Category);  // clear the last filter
        Assert.IsFalse(Rec.HasFilter,
            'HasFilter must be false after the last filter is cleared');
    end;
}
