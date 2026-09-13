// DependencyReportLayoutTests — #2297: a report in a precompiled dependency declares its layouts
// only in SymbolReference.json, so the runner must read them there. The BC-observable claims
// (Report.DefaultLayout, "Report Layout List" rows for Base App reports 1306/1320) live in the
// corpus; these pin the runner's own parse and emit.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class DependencyReportLayoutTests
{
    // Real shapes from Base Application 28.1: 1306 uses the rendering syntax (Word default, not
    // the first-declared RDLC), 1320 the legacy one, 795 neither.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "Sales",
              "Reports": [
                {
                  "Id": 1306,
                  "Name": "Standard Sales - Invoice",
                  "Properties": [
                    { "Name": "Caption", "Value": "Sales - Invoice" },
                    { "Name": "DefaultRenderingLayout", "Value": "StandardSalesInvoice.docx" }
                  ],
                  "Layouts": [
                    { "Name": "StandardSalesInvoice.rdlc", "Properties": [
                      { "Name": "Type", "Value": "RDLC" },
                      { "Name": "LayoutFile", "Value": "./Sales/History/StandardSalesInvoice.rdlc" },
                      { "Name": "Caption", "Value": "Standard Sales Invoice (RDLC)" } ] },
                    { "Name": "StandardSalesInvoice.docx", "Properties": [
                      { "Name": "Type", "Value": "Word" },
                      { "Name": "LayoutFile", "Value": "./Sales/History/StandardSalesInvoice.docx" },
                      { "Name": "Summary", "Value": "Simple layout." } ] }
                  ]
                },
                {
                  "Id": 1320,
                  "Name": "Notification Email",
                  "Properties": [
                    { "Name": "WordLayout", "Value": "./System/Notifications/NotificationEmail.docx" },
                    { "Name": "DefaultLayout", "Value": "Word" }
                  ]
                },
                {
                  "Id": 795,
                  "Name": "Adjust Cost - Item Entries",
                  "Properties": [ { "Name": "ProcessingOnly", "Value": "1" } ]
                }
              ]
            }
          ]
        }
        """;

    private static IReadOnlyList<BcAppSymbolCache.ReportSymbol> ReadReports(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
            w.Write(SymbolReference);
        return BcAppSymbolCache.Get(appPath).Reports;
    }

    private static void WithReports(Action<IReadOnlyList<BcAppSymbolCache.ReportSymbol>> body)
    {
        var dir = TestScratch.Dir("al-runner-dep-report-layout-tests");
        Directory.CreateDirectory(dir);
        try { body(ReadReports(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Layouts_AreReadVerbatim_WithTheDefaultRenderingLayout()
    {
        WithReports(reports =>
        {
            var invoice = Assert.Single(reports, r => r.Id == 1306);
            Assert.Equal("StandardSalesInvoice.docx", invoice.DefaultRenderingLayout);
            Assert.NotNull(invoice.Layouts);
            Assert.Equal(new[] { "StandardSalesInvoice.rdlc", "StandardSalesInvoice.docx" },
                invoice.Layouts!.Select(l => l.Name));
            Assert.Equal(new[] { "RDLC", "Word" }, invoice.Layouts.Select(l => l.Type));
            Assert.Equal("./Sales/History/StandardSalesInvoice.docx", invoice.Layouts[1].LayoutFile);
            Assert.Equal("Standard Sales Invoice (RDLC)", invoice.Layouts[0].Caption);
            Assert.Null(invoice.Layouts[0].Summary);
            Assert.Equal("Simple layout.", invoice.Layouts[1].Summary);
            Assert.Null(invoice.LegacyDefaultLayout);

            var legacy = Assert.Single(reports, r => r.Id == 1320);
            Assert.Null(legacy.Layouts);
            Assert.Null(legacy.DefaultRenderingLayout);
            Assert.Equal("Word", legacy.LegacyDefaultLayout);
        });
    }

    [Fact]
    public void EmittedMetadata_RenderingSyntax_CarriesTheDefaultLayoutsTypeAndTheLayoutNames()
    {
        WithReports(reports =>
        {
            var root = XDocument.Parse(RecordPatches.EmitReportXml(
                reports.Single(r => r.Id == 1306), sourceExprByColumn: null)).Root!;

            Assert.Equal("StandardSalesInvoice.docx", root.Element("DefaultLayoutName")?.Value);
            // The Type of the layout the default NAMES — not the first declared (RDLC).
            Assert.Equal("Word", root.Element("DefaultRenderingLayoutType")?.Value);
            Assert.Null(root.Element("DefaultLayout"));
            Assert.Equal(new[] { "StandardSalesInvoice.rdlc", "StandardSalesInvoice.docx" },
                root.Element("Layouts")!.Elements("Layout").Select(l => l.Element("Name")!.Value));
        });
    }

    [Fact]
    public void EmittedMetadata_LegacySyntax_CarriesDefaultLayout_AndNoInventedName()
    {
        WithReports(reports =>
        {
            var root = XDocument.Parse(RecordPatches.EmitReportXml(
                reports.Single(r => r.Id == 1320), sourceExprByColumn: null)).Root!;

            Assert.Equal("Word", root.Element("DefaultLayout")?.Value);
            Assert.Null(root.Element("DefaultLayoutName"));
            Assert.Null(root.Element("DefaultRenderingLayoutType"));
            Assert.Null(root.Element("Layouts"));
        });
    }

    [Fact]
    public void EmittedMetadata_NoLayoutDeclared_StatesNoDefault()
    {
        WithReports(reports =>
        {
            var root = XDocument.Parse(RecordPatches.EmitReportXml(
                reports.Single(r => r.Id == 795), sourceExprByColumn: null)).Root!;

            Assert.Null(root.Element("DefaultLayout"));
            Assert.Null(root.Element("DefaultLayoutName"));
            Assert.Null(root.Element("DefaultRenderingLayoutType"));
            Assert.Null(root.Element("Layouts"));
        });
    }

    [Fact]
    public void LayoutListRows_MarkOnlyTheNamedDefault_AndClaimNoLayoutBytes()
    {
        WithReports(reports =>
        {
            var rows = RecordPatches.DependencyReportLayouts(reports.Single(r => r.Id == 1306)).ToList();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(1306, r.ReportId));
            Assert.All(rows, r => Assert.Equal(string.Empty, r.ResolvedPath));
            Assert.Equal("StandardSalesInvoice.docx", Assert.Single(rows, r => r.IsDefault).Name);
            Assert.Equal("RDLC", rows.Single(r => r.Name == "StandardSalesInvoice.rdlc").LayoutType);

            Assert.Empty(RecordPatches.DependencyReportLayouts(reports.Single(r => r.Id == 1320)));
        });
    }
}
