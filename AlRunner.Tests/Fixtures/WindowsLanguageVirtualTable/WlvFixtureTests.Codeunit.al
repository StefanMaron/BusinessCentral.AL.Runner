// Fixture suite for WindowsLanguageVirtualTableTests.cs (#2581).
//
// Asserts the columns that have a real source. Every value here comes from BC's own
// WindowsLanguageHelper or from the CultureInfo it hands back, so this is about the ROUTE
// working, not about inventing language data. The stubbed license and localization columns are
// asserted separately in tests/runner-extras/windows-language-license-stub, because "the runner
// answers permitted" is a runner-specific claim rather than BC behaviour.
codeunit 60801 "WLV Fixture Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit "WLV Assert";

    [Test]
    procedure WindowsLanguage_Get1033_ReturnsTruthfulColumns()
    var
        W: Record "Windows Language";
    begin
        // Before the fix this returned false — the table answered zero rows.
        Assert.IsTrue(W.Get(1033), 'Get(1033) must find English (United States).');
        Assert.AreEqual('English (United States)', W.Name, 'Unexpected Name.');
        Assert.AreEqual('en-US', W."Language Tag", 'Unexpected Language Tag.');
        Assert.AreEqual('ENU', W."Abbreviated Name", 'Unexpected Abbreviated Name.');
        // The OEM code page, not the ANSI one (which would be 1252). BC reads
        // TextInfo.OEMCodePage here, and this is the assertion that pins that choice.
        Assert.AreEqual('437', W."Primary CodePage", 'Primary CodePage must be the OEM page.');
    end;

    [Test]
    procedure WindowsLanguage_Get1031_IsADifferentRow()
    var
        W: Record "Windows Language";
    begin
        // A second row: a provider answering one fixed row would pass the test above.
        Assert.IsTrue(W.Get(1031), 'Get(1031) must find German (Germany).');
        Assert.AreEqual('German (Germany)', W.Name, 'Unexpected Name for 1031.');
        Assert.AreEqual('de-DE', W."Language Tag", 'Unexpected Language Tag for 1031.');
        Assert.AreEqual('DEU', W."Abbreviated Name", 'Unexpected Abbreviated Name for 1031.');
    end;

    [Test]
    procedure WindowsLanguage_PrimaryLanguageId_GroupsSublanguagesTogether()
    var
        EnUs: Record "Windows Language";
        EnGb: Record "Windows Language";
        DeDe: Record "Windows Language";
    begin
        // Structural, not a magic number: two English sublanguages share one Primary Language
        // ID and German does not. That relationship is what the column means, and it holds
        // without hardcoding a value the runner itself produced.
        Assert.IsTrue(EnUs.Get(1033), 'Get(1033) must succeed.');
        Assert.IsTrue(EnGb.Get(2057), 'Get(2057) must find English (United Kingdom).');
        Assert.IsTrue(DeDe.Get(1031), 'Get(1031) must succeed.');

        Assert.AreEqual(EnUs."Primary Language ID", EnGb."Primary Language ID",
            'en-US and en-GB must share a Primary Language ID.');
        Assert.AreNotEqualInt(DeDe."Primary Language ID", EnUs."Primary Language ID",
            'German must not share English''s Primary Language ID.');
    end;

    [Test]
    procedure WindowsLanguage_GetOnAnUnusedId_ReturnsFalse()
    var
        W: Record "Windows Language";
    begin
        // Negative control: a provider answering every Get with a row would pass the rest.
        Assert.IsFalse(W.Get(999999), 'Windows Language must not have a row for an unused id.');
    end;

    [Test]
    procedure WindowsLanguage_FilterOnLanguageId_DiscriminatesBetweenRows()
    var
        W: Record "Windows Language";
    begin
        W.SetRange("Language ID", 1033);
        Assert.AreEqual(1, W.Count(), 'A filter on one existing language id must select one row.');

        W.SetRange("Language ID", 999999);
        Assert.AreEqual(0, W.Count(), 'A filter on an unused language id must select no rows.');
        Assert.IsTrue(W.IsEmpty(), 'IsEmpty must be true for a filter naming no language.');
    end;
}
