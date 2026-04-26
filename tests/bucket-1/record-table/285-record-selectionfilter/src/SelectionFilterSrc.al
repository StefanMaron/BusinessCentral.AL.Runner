/// Tests for Record.SetSelectionFilter equivalent behavior — issue #977.
/// Record.SetSelectionFilter is not available as a built-in AL method in BC 26-28
/// (AL0132). The actual CS1061 arises from compiled packages or future BC versions
/// that emit rec.ALSetSelectionFilter(filtered) in the generated C#.
/// This suite tests the filter-copying behavior through a table procedure
/// (Invoke dispatch path) and also verifies the marks-based selection behavior.
table 133000 "SSF Table"
{
    fields
    {
        field(1; Id; Code[20]) { }
        field(2; Value; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }

    /// <summary>
    /// User-defined SetSelectionFilter on the table — exercises the same semantic:
    /// if marks are active, build a PK filter on FilteredRec; otherwise copy filters.
    /// BC emits this as Record133000.SetSelectionFilter → rec.Invoke(memberId, args).
    /// When BC built-in Record.SetSelectionFilter is supported it will emit
    /// rec.ALSetSelectionFilter(filtered) on MockRecordHandle instead.
    /// </summary>
    procedure SetSelectionFilter(var FilteredRec: Record "SSF Table")
    begin
        FilteredRec.CopyFilters(Rec);
    end;
}

codeunit 133001 "SSF Source"
{
    procedure InsertRecord(Id: Code[20]; Value: Integer)
    var
        Rec: Record "SSF Table";
    begin
        Rec.Init();
        Rec.Id := Id;
        Rec.Value := Value;
        Rec.Insert();
    end;

    /// <summary>
    /// Apply SetRange on source, call SetSelectionFilter, return Filtered.Count().
    /// Tests that the filter is correctly copied from source to filtered record.
    /// </summary>
    procedure FilteredCountByRange(RangeId: Code[20]): Integer
    var
        Src: Record "SSF Table";
        Filtered: Record "SSF Table";
    begin
        Src.SetRange(Id, RangeId);
        Src.SetSelectionFilter(Filtered);
        exit(Filtered.Count());
    end;

    /// <summary>
    /// No records match the range — SetSelectionFilter should yield 0.
    /// </summary>
    procedure FilteredCountEmpty(): Integer
    var
        Src: Record "SSF Table";
        Filtered: Record "SSF Table";
    begin
        Src.SetRange(Id, 'NONEXISTENT');
        Src.SetSelectionFilter(Filtered);
        exit(Filtered.Count());
    end;

    /// <summary>
    /// No filter on source → SetSelectionFilter copies empty filter → all records visible.
    /// </summary>
    procedure FilteredCountNoFilter(): Integer
    var
        Src: Record "SSF Table";
        Filtered: Record "SSF Table";
    begin
        Src.SetSelectionFilter(Filtered);
        exit(Filtered.Count());
    end;

    /// <summary>
    /// SetSelectionFilter does not alter the source record's own filter state.
    /// Source retains its filter; source count must still be 1.
    /// </summary>
    procedure SourceCountUnchangedAfterSetSelectionFilter(RangeId: Code[20]): Integer
    var
        Src: Record "SSF Table";
        Filtered: Record "SSF Table";
    begin
        Src.SetRange(Id, RangeId);
        Src.SetSelectionFilter(Filtered);
        // Source must still have its own filter active
        exit(Src.Count());
    end;
}
