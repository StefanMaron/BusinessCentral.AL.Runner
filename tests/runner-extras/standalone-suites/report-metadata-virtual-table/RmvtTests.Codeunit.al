/// <summary>
/// Pins the Report Metadata (2000000139) and Report Data Items (2000000203)
/// system virtual tables.
///
/// Both metatables are parsed out of the System package so the tables exist,
/// but nothing provides rows for them. Every <c>Report Metadata.Get(id)</c>
/// therefore returns false and <c>Report Data Items</c> is always empty — for
/// every report, including ones the runner compiled itself moments earlier.
/// Real BC answers truthfully: these two tables ARE the documented way to
/// discover a report's caption, its request-page flag and its dataset shape.
///
/// Pageworks depends on exactly this. Its report-discovery entity set binds to
/// Report Metadata and filters on <c>FirstDataItemTableID &lt;&gt; 0</c>, so it
/// lists nothing at all; its <c>DeriveSourceTable</c> reads
/// FirstDataItemTableID and falls back to Report Data Items, so it returns 0
/// for every report, including Base Application report 1306 whose root data
/// item is plainly Sales Invoice Header (112).
///
/// The negative tests carry as much weight as the positive ones: a provider
/// that answered true to every Get, that echoed the report's first table for
/// every data item, or that ignored the Report ID filter, would satisfy the
/// positive cases alone. Those shapes are pinned out explicitly below.
/// </summary>
codeunit 61952 "RMVT Tests"
{
    Subtype = Test;

    [Test]
    procedure ReportMetadataGet_ReturnsNameAndCaptionForACompiledReport()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        if not ReportMetadata.Get(61950) then
            Error('Report Metadata.Get(61950) returned false, but report 61950 is defined in this app and was just compiled.');

        if ReportMetadata.Name <> 'RMVT Doc Report' then
            Error('Report Metadata.Name was "%1", expected "RMVT Doc Report".', ReportMetadata.Name);

        // Caption is deliberately DIFFERENT from the object name, so a provider
        // that filled Caption from Name would fail right here.
        if ReportMetadata.Caption <> 'RMVT Document Report' then
            Error('Report Metadata.Caption was "%1", expected "RMVT Document Report".', ReportMetadata.Caption);

        // AL's UseRequestPage default is true, and stays true for a report that
        // declares no explicit requestpage block (BC generates the standard one).
        if not ReportMetadata.UseRequestPage then
            Error('Report Metadata.UseRequestPage was false for report 61950, expected true (the AL default).');

        if ReportMetadata.ProcessingOnly then
            Error('Report Metadata.ProcessingOnly was true for report 61950, which declares a dataset and no ProcessingOnly property.');
    end;

    [Test]
    procedure ReportMetadataGet_FirstDataItemTableIdIsTheRootDataItemTable()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        if not ReportMetadata.Get(61950) then
            Error('Report Metadata.Get(61950) returned false, but report 61950 is defined in this app.');

        // 61950 = "RMVT Header", the ROOT data item — not 61951 ("RMVT Line",
        // the nested one), which is what a provider walking the tree in the
        // wrong order would report.
        if ReportMetadata.FirstDataItemTableID <> 61950 then
            Error('Report Metadata.FirstDataItemTableID was %1, expected 61950 (table "RMVT Header", the root data item).',
                ReportMetadata.FirstDataItemTableID);
    end;

    [Test]
    procedure ReportMetadataGet_ProcessingOnlyReportHasNoDataItemTable()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        if not ReportMetadata.Get(61951) then
            Error('Report Metadata.Get(61951) returned false, but report 61951 is defined in this app.');

        if not ReportMetadata.ProcessingOnly then
            Error('Report Metadata.ProcessingOnly was false for report 61951, which declares ProcessingOnly = true.');

        // The exact condition Pageworks' discovery entity set filters OUT.
        if ReportMetadata.FirstDataItemTableID <> 0 then
            Error('Report Metadata.FirstDataItemTableID was %1 for a report with no dataset, expected 0.',
                ReportMetadata.FirstDataItemTableID);
    end;

    // Negative: a report id that does not exist must not resolve. A provider
    // that answered true unconditionally would pass every test above.
    [Test]
    procedure ReportMetadataGet_UnknownReportIdReturnsFalse()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        if ReportMetadata.Get(99999999) then
            Error('Report Metadata.Get(99999999) returned true, but no such report exists.');
    end;

    [Test]
    procedure ReportDataItems_ListBothDataItemsWithTheirOwnTableAndIndentation()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 61950);
        if not ReportDataItems.FindSet() then
            Error('Report Data Items had no rows for report 61950, which declares two data items.');

        if ReportDataItems.Count() <> 2 then
            Error('Report Data Items returned %1 row(s) for report 61950, expected 2 (Header and its nested Line).',
                ReportDataItems.Count());

        // Root data item.
        ReportDataItems.SetRange("Indentation Level", 0);
        if not ReportDataItems.FindFirst() then
            Error('Report Data Items had no Indentation Level 0 row for report 61950.');
        if ReportDataItems.Name <> 'Header' then
            Error('The root data item of report 61950 was named "%1", expected "Header".', ReportDataItems.Name);
        if ReportDataItems."Related Table ID" <> 61950 then
            Error('The root data item of report 61950 bound table %1, expected 61950 ("RMVT Header").',
                ReportDataItems."Related Table ID");

        // Nested data item — a DIFFERENT table and a nonzero indentation, so a
        // provider that emitted one flat row per report, or echoed the root
        // table for every row, fails here.
        ReportDataItems.SetRange("Indentation Level", 1);
        if not ReportDataItems.FindFirst() then
            Error('Report Data Items had no Indentation Level 1 row for report 61950, which nests Line inside Header.');
        if ReportDataItems.Name <> 'Line' then
            Error('The nested data item of report 61950 was named "%1", expected "Line".', ReportDataItems.Name);
        if ReportDataItems."Related Table ID" <> 61951 then
            Error('The nested data item of report 61950 bound table %1, expected 61951 ("RMVT Line").',
                ReportDataItems."Related Table ID");
    end;

    // Negative: the Report ID filter must actually select. A provider that
    // ignored it would hand the previous test's rows back here too.
    [Test]
    procedure ReportDataItems_ProcessingOnlyReportHasNoRows()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 61951);
        if not ReportDataItems.IsEmpty() then
            Error('Report Data Items returned %1 row(s) for report 61951, which declares no dataset at all.',
                ReportDataItems.Count());
    end;

    // ── The dependency path (#3627) ────────────────────────────────────────
    //
    // Everything above reads a report the runner SOURCE-COMPILED moments ago,
    // whose Sorting Fields come from BC's own emitted document (#3620). The
    // tests below read Base Application report 1306, which lives in a
    // PRECOMPILED dependency: it has no emitted document at all, so its row is
    // built from that .app's SymbolReference.json, where DataItemTableView is
    // AL SOURCE TEXT — sorting("Document No.", "Line No."), field NAMES.
    //
    // Before #3627 the runner dropped every token that was not Field<N> and
    // answered EMPTY here, for all 1759 of Base Application's data items that
    // state a SORTING clause. The expected values below are field NUMBERS
    // resolved through those names, and each is chosen so no default can
    // produce it: Sales Invoice Header."No." is field 3, not field 1, so a
    // provider echoing the primary key or the first field fails immediately.

    [Test]
    procedure ReportDataItems_SortingFieldsOfAPrecompiledDependencyReport()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        // Root data item of Base App 1306 "Standard Sales - Invoice":
        // dataitem Header over Sales Invoice Header, sorting("No.").
        // "No." is field 3 of table 112 — NOT field 1 — so "3" cannot come
        // from a field ordinal, a declaration order, or a primary-key default.
        ReportDataItems.SetRange("Report ID", 1306);
        ReportDataItems.SetRange("Indentation Level", 0);
        if not ReportDataItems.FindFirst() then
            Error('Report Data Items had no Indentation Level 0 row for Base Application report 1306.');

        if ReportDataItems."Sorting Fields" <> '3' then
            Error('Report 1306 root data item Sorting Fields was "%1", expected "3" — Sales Invoice Header."No." resolved from the dependency symbol file''s sorting("No.").',
                ReportDataItems."Sorting Fields");
    end;

    [Test]
    procedure ReportDataItems_MultiFieldSortingOfADependencyReportKeepsClauseOrder()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        // dataitem VATAmountLine over VAT Amount Line, sorting("VAT Identifier",
        // "VAT Calculation Type", "Tax Group Code", "Use Tax", Positive) —
        // fields 5, 9, 10, 13 and 16. Five tokens, non-contiguous, and the last
        // one is BARE (unquoted), which is the other spelling a symbol file
        // uses. The order is the CLAUSE's, not ascending by chance: any
        // implementation that sorted, deduplicated, or dropped the bare token
        // answers something else.
        ReportDataItems.SetRange("Report ID", 1306);
        ReportDataItems.SetRange(Name, 'VATAmountLine');
        if not ReportDataItems.FindFirst() then
            Error('Report Data Items had no VATAmountLine row for Base Application report 1306.');

        if ReportDataItems."Sorting Fields" <> '5,9,10,13,16' then
            Error('Report 1306 VATAmountLine Sorting Fields was "%1", expected "5,9,10,13,16".',
                ReportDataItems."Sorting Fields");
    end;

    // Negative: a dependency data item whose view states a WHERE clause but NO
    // sorting(...) must answer EMPTY, not a fabricated primary key. This is
    // BC's own answer — GetSortingFieldsIfAny returns string.Empty — and it is
    // what stops the two tests above from being satisfied by an implementation
    // that always says something. Base Application report 705 "Inventory
    // Availability" carries one data item of each kind, so both directions are
    // measured on the same report through the same code path.
    [Test]
    procedure ReportDataItems_DependencyDataItemWithNoSortingClauseAnswersEmpty()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        // dataitem Item, view where(Type = const(Inventory)) — a filter and no
        // sorting at all. Item's primary key is field 1, so an implementation
        // defaulting to the primary key would answer "1" here.
        ReportDataItems.SetRange("Report ID", 705);
        ReportDataItems.SetRange(Name, 'Item');
        if not ReportDataItems.FindFirst() then
            Error('Report Data Items had no Item row for Base Application report 705.');

        if ReportDataItems."Sorting Fields" <> '' then
            Error('Report 705 data item Item states only where(Type = const(Inventory)) and no sorting(...), so Sorting Fields must be empty; it was "%1".',
                ReportDataItems."Sorting Fields");

        // Control, same report and same code path: the sibling data item DOES
        // state a clause and must answer its field numbers — so the empty
        // answer above is a real distinction, not a report the runner failed to
        // read at all. Stockkeeping Unit sorts ("Location Code", "Variant Code",
        // "Item No.") = 1,3,2 — an order that is neither ascending nor field
        // order, so it cannot be produced by sorting or by echoing a key.
        ReportDataItems.SetRange(Name, 'Stockkeeping Unit');
        if not ReportDataItems.FindFirst() then
            Error('Report Data Items had no Stockkeeping Unit row for Base Application report 705.');

        if ReportDataItems."Sorting Fields" <> '1,3,2' then
            Error('Report 705 data item Stockkeeping Unit Sorting Fields was "%1", expected "1,3,2".',
                ReportDataItems."Sorting Fields");
    end;
}
