/// <summary>
/// Pins the Table Metadata (2000000136) system virtual table.
///
/// The metatable is parsed out of the System package so the table exists, but
/// nothing provides rows for it, so <c>Table Metadata.Get(id)</c> returns false
/// for every table in the application -- including tables the runner compiled
/// moments earlier. Real BC has a row for every table.
///
/// The consequence is not confined to AL that reads the table directly. Base
/// Application "Page Management" (codeunit 700) resolves a record's lookup/card
/// page through it, so <c>GetPageID</c> / <c>PageRun</c> / <c>PageRunModal</c> on
/// a custom table fail with "The Table Metadata does not exist. Identification
/// fields and values: ID='...'" even when the table plainly declares
/// LookupPageId.
///
/// The negative tests carry as much weight as the positive ones. A provider that
/// answered true to every Get, that ignored the ID filter, or that fabricated a
/// non-zero LookupPageID for a table declaring none would satisfy the positive
/// cases alone -- and the last of those would be the worst kind of failure here,
/// because 0 is exactly the value Page Management branches on.
/// </summary>
codeunit 62104 "TMVT Tests"
{
    Subtype = Test;

    [Test]
    procedure TableMetadataGet_ReturnsRowForACompiledTable()
    var
        TableMetadata: Record "Table Metadata";
    begin
        if not TableMetadata.Get(Database::"TMVT Row") then
            Error('Table Metadata.Get(%1) returned false, but table 62100 is defined in this app and was just compiled.',
                Database::"TMVT Row");

        if TableMetadata.ID <> Database::"TMVT Row" then
            Error('Table Metadata.ID was %1, expected %2.', TableMetadata.ID, Database::"TMVT Row");

        if TableMetadata.Name <> 'TMVT Row' then
            Error('Table Metadata.Name was "%1", expected "TMVT Row".', TableMetadata.Name);

        // Caption is deliberately DIFFERENT from the object name, so a provider
        // that filled Caption from Name would fail right here.
        if TableMetadata.Caption <> 'TMVT Probe Row' then
            Error('Table Metadata.Caption was "%1", expected "TMVT Probe Row".', TableMetadata.Caption);
    end;

    [Test]
    procedure TableMetadataGet_CarriesTheDeclaredLookupAndDrillDownPages()
    var
        TableMetadata: Record "Table Metadata";
    begin
        if not TableMetadata.Get(Database::"TMVT Row") then
            Error('Table Metadata.Get(%1) returned false, but table 62100 is defined in this app.',
                Database::"TMVT Row");

        if TableMetadata.LookupPageID <> Page::"TMVT Row List" then
            Error('Table Metadata.LookupPageID was %1, expected %2 (the declared LookupPageId).',
                TableMetadata.LookupPageID, Page::"TMVT Row List");

        // Distinct from LookupPageID on purpose: a provider echoing one page id
        // into both slots passes the assertion above and fails this one.
        if TableMetadata.DrillDownPageID <> Page::"TMVT Row Card" then
            Error('Table Metadata.DrillDownPageID was %1, expected %2 (the declared DrillDownPageId).',
                TableMetadata.DrillDownPageID, Page::"TMVT Row Card");
    end;

    [Test]
    procedure TableMetadataGet_ReportsZeroPagesForATableDeclaringNone()
    var
        TableMetadata: Record "Table Metadata";
    begin
        if not TableMetadata.Get(Database::"TMVT Plain") then
            Error('Table Metadata.Get(%1) returned false, but table 62103 is defined in this app.',
                Database::"TMVT Plain");

        // 0 is the meaningful answer Page Management branches on. Inventing a page
        // id here would be a silent wrong answer, so it is pinned explicitly.
        if TableMetadata.LookupPageID <> 0 then
            Error('Table Metadata.LookupPageID was %1 for a table declaring no LookupPageId, expected 0.',
                TableMetadata.LookupPageID);

        if TableMetadata.DrillDownPageID <> 0 then
            Error('Table Metadata.DrillDownPageID was %1 for a table declaring no DrillDownPageId, expected 0.',
                TableMetadata.DrillDownPageID);
    end;

    [Test]
    procedure TableMetadataGet_ReturnsFalseForATableIdThatDoesNotExist()
    var
        TableMetadata: Record "Table Metadata";
    begin
        // 62119 is inside this app's id range and deliberately unused. A provider
        // that answered true to every Get -- or that ignored the ID filter and
        // handed back whatever row it had -- fails here.
        if TableMetadata.Get(62119) then
            Error('Table Metadata.Get(62119) returned true, but no table 62119 exists in this application.');
    end;

    [Test]
    procedure TableMetadataFindSet_IsFilteredByTheIdRange()
    var
        TableMetadata: Record "Table Metadata";
    begin
        // Proves the rows are real filterable rows rather than a single row
        // synthesised per Get: exactly the two tables this app declares fall in
        // the range, and 62119 (asserted absent above) does not.
        TableMetadata.SetRange(ID, 62100, 62119);
        if TableMetadata.Count() <> 2 then
            Error('Table Metadata had %1 row(s) in id range 62100..62119, expected exactly 2 (tables 62100 and 62103).',
                TableMetadata.Count());
    end;

    [Test]
    procedure PageManagementGetPageID_ResolvesTheDeclaredLookupPage()
    var
        TmvtRow: Record "TMVT Row";
        PageManagement: Codeunit "Page Management";
        PageId: Integer;
    begin
        // The reported symptom: Base App Page Management reads Table Metadata to
        // resolve a record's page, and threw
        // NavCSideRecordNotFoundException "The Table Metadata does not exist"
        // before this table had rows. This runs the real, unmodified Base App
        // codeunit against the provider.
        PageId := PageManagement.GetPageID(TmvtRow);

        if PageId <> Page::"TMVT Row List" then
            Error('Page Management.GetPageID returned %1, expected %2 (the table''s declared LookupPageId).',
                PageId, Page::"TMVT Row List");
    end;
}
