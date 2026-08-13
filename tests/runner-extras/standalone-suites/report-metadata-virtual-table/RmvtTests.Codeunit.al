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
}
