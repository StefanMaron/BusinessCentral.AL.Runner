/// <summary>
/// Pins the AllObjWithCaption (2000000058) system virtual table.
///
/// AllObjWithCaption is AllObj plus the Object Caption column, and it is the
/// documented way for AL to put an object's caption on screen: a lookup page
/// with <c>SourceTable = AllObjWithCaption</c>, a
/// <c>TableRelation = AllObjWithCaption."Object ID"</c>, or a
/// <c>CalcFormula = lookup(AllObjWithCaption."Object Caption" where(...))</c>
/// FlowField. All of those are normal, supported AL.
///
/// It is virtual on the service tier, and the runner routes every table to the
/// same empty in-memory store — so <c>Get(&lt;type&gt;, &lt;id&gt;)</c> answered
/// false for every object that has ever existed, and every caption lookup
/// silently produced an empty string rather than an error. Pageworks reads it
/// in five places (report and table caption resolution in the layout studio and
/// in the dataset designer), all of which quietly rendered blank.
///
/// The negatives carry as much weight as the positives. A provider that copied
/// Object Name into Object Caption would pass every caption assertion below
/// except the two that deliberately declare a caption different from the name.
/// A provider that ignored the Object Type half of the primary key would pass
/// every Get except <c>AllObjWithCaptionGet_WrongObjectTypeForAKnownIdIsNotFound</c>.
/// </summary>
codeunit 61972 "AOWC Tests"
{
    Subtype = Test;

    [Test]
    procedure AllObjWithCaptionGet_ReturnsTheDeclaredCaptionForACompiledTable()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        if not AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 61970) then
            Error('AllObjWithCaption.Get(Table, 61970) returned false, but table 61970 is defined in this app and was just compiled.');

        if AllObjWithCaption."Object Name" <> 'AOWC Header' then
            Error('Object Name was "%1", expected "AOWC Header".', AllObjWithCaption."Object Name");

        // Caption is deliberately DIFFERENT from the object name, so a provider that
        // filled Object Caption from Object Name fails right here.
        if AllObjWithCaption."Object Caption" <> 'AOWC Header Caption' then
            Error('Object Caption was "%1", expected "AOWC Header Caption".', AllObjWithCaption."Object Caption");
    end;

    [Test]
    procedure AllObjWithCaptionGet_CaptionFallsBackToTheObjectNameWhenUndeclared()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        if not AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 61971) then
            Error('AllObjWithCaption.Get(Table, 61971) returned false, but table 61971 is defined in this app.');

        // Table 61971 declares no Caption property. AL's own default caption is the
        // object name, which is what a real service tier reports — NOT an empty string.
        if AllObjWithCaption."Object Caption" <> 'AOWC NoCaption' then
            Error('Object Caption was "%1" for a table that declares no Caption, expected the object name "AOWC NoCaption".',
                AllObjWithCaption."Object Caption");
    end;

    [Test]
    procedure AllObjWithCaptionGet_ReturnsTheDeclaredCaptionForACompiledReport()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        if not AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Report, 61970) then
            Error('AllObjWithCaption.Get(Report, 61970) returned false, but report 61970 is defined in this app.');

        if AllObjWithCaption."Object Caption" <> 'AOWC Document Report' then
            Error('Object Caption was "%1", expected "AOWC Document Report".', AllObjWithCaption."Object Caption");
    end;

    // Reports living in a PRECOMPILED dependency have no AL source to parse here; their
    // caption comes from the .app's SymbolReference.json. Base Application report 1306 is
    // named "Standard Sales - Invoice" and captioned "Sales - Invoice" — a pair that
    // differs, so this pins the dependency-symbol route and the no-name-fallback rule at
    // the same time.
    [Test]
    procedure AllObjWithCaptionGet_ReturnsTheCaptionOfAPrecompiledDependencyReport()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        if not AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Report, 1306) then
            Error('AllObjWithCaption.Get(Report, 1306) returned false, but Base Application report 1306 exists in the resolved artifact.');

        if AllObjWithCaption."Object Name" <> 'Standard Sales - Invoice' then
            Error('Object Name was "%1", expected "Standard Sales - Invoice".', AllObjWithCaption."Object Name");

        if AllObjWithCaption."Object Caption" <> 'Sales - Invoice' then
            Error('Object Caption was "%1", expected "Sales - Invoice".', AllObjWithCaption."Object Caption");
    end;

    // Negative: an object id that does not exist must not resolve. A provider that
    // answered true unconditionally would pass every test above.
    [Test]
    procedure AllObjWithCaptionGet_UnknownObjectIdReturnsFalse()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        if AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 99999999) then
            Error('AllObjWithCaption.Get(Table, 99999999) returned true, but no such table exists.');
    end;

    // Negative: the key is (Object Type, Object ID), not Object ID alone. 61970 is a real
    // table AND a real report in this app, but there is no codeunit 61970 — a provider
    // that keyed on the id alone would hand the table's row back here.
    [Test]
    procedure AllObjWithCaptionGet_WrongObjectTypeForAKnownIdIsNotFound()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        if AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Codeunit, 61970) then
            Error('AllObjWithCaption.Get(Codeunit, 61970) returned true, but 61970 is a table and a report in this app, never a codeunit.');
    end;

    // Filtered iteration, not just Get: the lookup pages that bind to this table read it
    // through a filtered FindSet, so the rows must survive BC's own filter engine.
    [Test]
    procedure AllObjWithCaption_FilteredIterationYieldsOnlyTheMatchingObjects()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        AllObjWithCaption.SetRange("Object Type", AllObjWithCaption."Object Type"::Table);
        AllObjWithCaption.SetRange("Object ID", 61970, 61971);
        if AllObjWithCaption.Count() <> 2 then
            Error('Filtered AllObjWithCaption returned %1 row(s) for tables 61970..61971, expected exactly 2.',
                AllObjWithCaption.Count());

        // The report shares id 61970 but is a different Object Type, so the type filter
        // must exclude it — a provider ignoring the filter would return 3.
        AllObjWithCaption.SetRange("Object Type", AllObjWithCaption."Object Type"::Report);
        AllObjWithCaption.SetRange("Object ID", 61970, 61971);
        if AllObjWithCaption.Count() <> 1 then
            Error('Filtered AllObjWithCaption returned %1 row(s) for reports 61970..61971, expected exactly 1.',
                AllObjWithCaption.Count());
    end;
}
