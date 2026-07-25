/// <summary>
/// Pins the Integer system virtual table (2000000026).
///
/// The Integer metatable is parsed out of the platform package so the table
/// exists, but nothing provides rows for it — so every Record Integer is empty.
/// Real BC exposes one row per value of Number.
///
/// This is not an exotic surface: `dataitem(Name; Integer)` with a
/// DataItemTableView filter is THE standard idiom for a synthetic report
/// dataset, used by 28 of Pageworks' 29 test reports. With zero rows the report
/// body never executes, so Report.SaveAs completes successfully, writes nothing
/// and raises no error. Every `asserterror Report.SaveAs(...)` around such a
/// report then fails with "An error was expected inside an ASSERTERROR
/// statement" — the runner returning a silent wrong answer where real BC either
/// renders or throws.
///
/// The negative tests carry as much weight as the positive ones: a provider
/// that answers every Find with true, or that ignores the filter and returns a
/// fixed row, would satisfy the positive cases on their own. Those cases are
/// pinned explicitly below.
///
/// NOT COVERED HERE: the end-to-end `dataitem(X; Integer)` report path. Driving it
/// needs report execution, which has its own separate gaps in the runner — Report.Run()
/// does not execute a report at all (OnPreReport never fires) and SaveAs on a report
/// INSTANCE variable NREs. Those are distinct defects; pinning them from this suite
/// would report an Integer failure for a report bug. The integration evidence for the
/// report case is the Pageworks suite.
/// </summary>
codeunit 61881 "IVT Tests"
{
    Subtype = Test;

    [Test]
    procedure ConstFilter_YieldsExactlyTheRequestedRow()
    var
        IntRec: Record Integer;
    begin
        // The exact shape PageworksPartSlotTestRpt uses:
        //   dataitem(OneRow; Integer) DataItemTableView = sorting(Number) where(Number = const(1))
        IntRec.SetRange(Number, 1);
        if not IntRec.FindFirst() then
            Error('Record Integer with Number = 1 was not found — the Integer virtual table has no rows.');

        if IntRec.Number <> 1 then
            Error('Expected Number = 1, got %1', IntRec.Number);

        if IntRec.Count() <> 1 then
            Error('Expected exactly 1 row for Number = const(1), got %1', IntRec.Count());
    end;

    [Test]
    procedure RangeFilter_YieldsEveryValueInOrder()
    var
        IntRec: Record Integer;
        Expected: Integer;
        Seen: Integer;
    begin
        // Proves the provider honours a range AND returns ascending Number, rather
        // than repeating one row: a fixed-row provider fails the ordering check.
        IntRec.SetRange(Number, 5, 9);
        if IntRec.Count() <> 5 then
            Error('Expected 5 rows for Number in [5..9], got %1', IntRec.Count());

        Expected := 5;
        if IntRec.FindSet() then
            repeat
                if IntRec.Number <> Expected then
                    Error('Expected Number %1 at position %2, got %3', Expected, Seen + 1, IntRec.Number);
                Expected += 1;
                Seen += 1;
            until IntRec.Next() = 0;

        if Seen <> 5 then
            Error('Expected to iterate 5 rows, iterated %1', Seen);
    end;

    [Test]
    procedure ZeroAndNegativeNumbersAreRealRows()
    var
        IntRec: Record Integer;
    begin
        // Real BC's Integer table spans the signed range, so 0 and negatives exist.
        // A provider seeded with only 1..N would pass the two tests above and fail here.
        IntRec.SetRange(Number, 0);
        if not IntRec.FindFirst() then
            Error('Record Integer with Number = 0 was not found — 0 is a real row in BC.');
        if IntRec.Number <> 0 then
            Error('Expected Number = 0, got %1', IntRec.Number);

        IntRec.Reset();
        IntRec.SetRange(Number, -3, -1);
        if IntRec.Count() <> 3 then
            Error('Expected 3 rows for Number in [-3..-1], got %1', IntRec.Count());
    end;

    [Test]
    procedure EmptyRange_FindsNothing()
    var
        IntRec: Record Integer;
    begin
        // Negative control: a provider that answers true unconditionally fails here.
        IntRec.SetRange(Number, 10, 4); // inverted — matches nothing
        if IntRec.FindFirst() then
            Error('Record Integer returned a row for the empty range [10..4] (Number = %1).', IntRec.Number);
        if not IntRec.IsEmpty() then
            Error('Expected IsEmpty() = true for the empty range [10..4].');
        if IntRec.Count() <> 0 then
            Error('Expected 0 rows for the empty range [10..4], got %1', IntRec.Count());
    end;
}
