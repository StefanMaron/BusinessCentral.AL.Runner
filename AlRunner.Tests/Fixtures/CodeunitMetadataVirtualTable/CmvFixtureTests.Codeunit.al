// Fixture suite for CodeunitMetadataVirtualTableTests.cs (#2544).
//
// Every assertion here is about the runner's OWN population of the CodeUnit Metadata
// (2000000137) virtual table from codeunits this bundle compiles FROM SOURCE. What the
// table answers on real BC is settled upstream, against a live service tier, by
// "Test Codeunit Metadata Virt T" (60962) in the al-language corpus.
codeunit 60764 "CMV Fixture Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "CMV Assert";

    [Test]
    procedure CodeunitMetadata_SourceCompiledCodeunit_ColumnsComeFromItsDeclaration()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(
            CodeunitMetadata.Get(Codeunit::"CMV Bound"),
            'CodeUnit Metadata has no row for a codeunit this bundle compiled from source.');

        Assert.AreEqual('CMV Bound', CodeunitMetadata.Name, 'Unexpected Name.');
        Assert.AreEqual(
            Database::"CMV Target", CodeunitMetadata.TableNo,
            'TableNo must resolve the declared table name to its object id.');
        // Compared as text on purpose: this fixture's own minimal Assert compares
        // Format() of two Variants, and Format() of an option LITERAL is its ordinal
        // while Format() of an option FIELD is its name. Naming the option explicitly
        // keeps the assertion about the value rather than about that asymmetry.
        Assert.AreEqual(
            'Normal', Format(CodeunitMetadata.Subtype),
            'A codeunit declaring no Subtype must report Subtype::Normal.');
        Assert.IsFalse(
            CodeunitMetadata.SingleInstance,
            'A codeunit declaring no SingleInstance must report false.');
    end;

    [Test]
    procedure CodeunitMetadata_SingleInstanceCodeunit_ReportsTrueAndNoTableNo()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(
            CodeunitMetadata.Get(Codeunit::"CMV Single"),
            'CodeUnit Metadata has no row for CMV Single.');

        Assert.IsTrue(
            CodeunitMetadata.SingleInstance,
            'CMV Single declares SingleInstance = true, so the column must read true.');
        Assert.AreEqual(
            0, CodeunitMetadata.TableNo,
            'CMV Single declares no TableNo, so the column must read 0.');
    end;

    [Test]
    procedure CodeunitMetadata_TestCodeunit_ReportsSubtypeTest()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(
            CodeunitMetadata.Get(Codeunit::"CMV Fixture Tests"),
            'CodeUnit Metadata has no row for the test codeunit itself.');

        Assert.AreEqual(
            'Test', Format(CodeunitMetadata.Subtype),
            'A codeunit declaring Subtype = Test must report Subtype::Test.');
    end;

    [Test]
    procedure CodeunitMetadata_UnknownCodeunitId_ReturnsFalse()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        // Negative control: a provider answering every Get with a fixed or blank row would
        // pass every positive test above and fail here.
        Assert.IsFalse(
            CodeunitMetadata.Get(99999999),
            'CodeUnit Metadata must not have a row for an id no codeunit uses.');
    end;

    [Test]
    procedure CodeunitMetadata_FilterOnId_DiscriminatesBetweenRows()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        CodeunitMetadata.SetRange(ID, Codeunit::"CMV Bound");
        Assert.AreEqual(1, CodeunitMetadata.Count(), 'A filter on one existing codeunit id must select one row.');
        Assert.IsTrue(CodeunitMetadata.FindSet(), 'FindSet must succeed for a filter naming an existing codeunit.');
        Assert.AreEqual('CMV Bound', CodeunitMetadata.Name, 'The filtered row must be the codeunit the filter named.');

        CodeunitMetadata.SetRange(ID, 99999999);
        Assert.AreEqual(0, CodeunitMetadata.Count(), 'A filter on an unused id must select no rows.');
        Assert.IsFalse(CodeunitMetadata.FindSet(), 'FindSet must fail for a filter naming no codeunit.');
        Assert.IsTrue(CodeunitMetadata.IsEmpty(), 'IsEmpty must be true for a filter naming no codeunit.');
    end;
}
