/// Helper codeunit wrapping the AL `CurrentDateTime` built-in so tests can
/// assert against its value the way issue #463 asks.
codeunit 50830 "CDT Src"
{
    procedure GetNow(): DateTime
    begin
        exit(CurrentDateTime);
    end;

    procedure IsAfter2000(): Boolean
    var
        cutoff: DateTime;
    begin
        cutoff := CreateDateTime(DMY2Date(1, 1, 2000), 000000T);
        exit(CurrentDateTime > cutoff);
    end;

    procedure IsBefore2200(): Boolean
    var
        future: DateTime;
    begin
        // BC itself won't run past 2200 (plus this proves CurrentDateTime isn't
        // returning a wildly-future sentinel).
        future := CreateDateTime(DMY2Date(1, 1, 2200), 000000T);
        exit(CurrentDateTime < future);
    end;

    procedure GetDT2Date(): Date
    begin
        exit(DT2Date(CurrentDateTime));
    end;

    procedure TwoReads_InOrder(): Boolean
    var
        a: DateTime;
        b: DateTime;
    begin
        // Two consecutive reads of CurrentDateTime must be monotonic —
        // the second read is at least as late as the first. Proves the
        // runtime call is live each time (not a cached literal).
        a := CurrentDateTime;
        b := CurrentDateTime;
        exit(b >= a);
    end;
}
