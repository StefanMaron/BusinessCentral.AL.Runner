// ReportLayoutObsoleteAndExcelSheetTests — a precompiled dependency layout's ObsoleteState and
// ExcelLayoutMultipleDataSheets reach the Report Layout List row mapping, cold and from the
// on-disk symbol cache (#4045). The BC-behaviour claim itself is pinned by corpus codeunit 60974.

using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reads the on-disk symbol cache, so it needs CacheRoots to itself (CacheRootsSerialCollection).
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ReportLayoutObsoleteAndExcelSheetTests : IDisposable
{
    private const int ReportId = 9870;

    private readonly string _root;

    public ReportLayoutObsoleteAndExcelSheetTests()
    {
        _root = TestScratch.Dir("al-runner-report-layout-obsolete-excel");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        BcAppSymbolCache.ResetProcessCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "0d4f6b3e-8a21-4c57-9e0b-5f7a2c3d9e18",
          "Name": "Report Layout Obsolete Fixture",
          "Reports": [
            {
              "Id": {{ReportId}},
              "Name": "Layout Fixture",
              "DataItems": [],
              "Properties": [ { "Name": "DefaultRenderingLayout", "Value": "Plain" } ],
              "Layouts": [
                { "Name": "Plain", "Properties": [ { "Name": "Type", "Value": "Word" } ] },
                { "Name": "Retiring", "Properties": [
                    { "Name": "Type", "Value": "RDLC" },
                    { "Name": "ObsoleteState", "Value": "Pending" },
                    { "Name": "ObsoleteReason", "Value": "replaced" } ] },
                { "Name": "Sheets", "Properties": [
                    { "Name": "Type", "Value": "Excel" },
                    { "Name": "ExcelLayoutMultipleDataSheets", "Value": "1" } ] },
                { "Name": "OneSheet", "Properties": [
                    { "Name": "Type", "Value": "Excel" },
                    { "Name": "ExcelLayoutMultipleDataSheets", "Value": "0" } ] }
              ]
            }
          ]
        }
        """;

    private string WriteApp()
    {
        var appPath = Path.Combine(_root, "layouts.app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static Dictionary<string, AlReportLayoutInfo> Layouts(BcAppSymbolCache.ReportSymbol report)
        => RecordPatches.DependencyReportLayouts(report).ToDictionary(l => l.Name);

    private static void AssertMapped(Dictionary<string, AlReportLayoutInfo> layouts)
    {
        Assert.Equal(4, layouts.Count);

        Assert.True(RecordPatches.IsObsoleteLayout(layouts["Retiring"]));
        Assert.False(RecordPatches.IsObsoleteLayout(layouts["Plain"]));
        Assert.False(RecordPatches.IsObsoleteLayout(layouts["Sheets"]));

        // Enum "Excel Sheet Configuration": Default 0, Single Data sheet 1, Multiple data sheets 2.
        Assert.Equal(2, RecordPatches.ExcelSheetConfigurationOrdinal(layouts["Sheets"]));
        Assert.Equal(1, RecordPatches.ExcelSheetConfigurationOrdinal(layouts["OneSheet"]));
        Assert.Equal(0, RecordPatches.ExcelSheetConfigurationOrdinal(layouts["Plain"]));
        Assert.Equal(0, RecordPatches.ExcelSheetConfigurationOrdinal(layouts["Retiring"]));
    }

    [Fact]
    public void Symbol_file_obsolete_state_and_excel_sheets_reach_the_row_mapping_cold_and_warm()
    {
        var appPath = WriteApp();
        BcAppSymbolCache.ResetProcessCacheForTests();

        var cold = Assert.Single(BcAppSymbolCache.Get(appPath).Reports);
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        AssertMapped(Layouts(cold));

        // The warm run: served from the on-disk entry the cold run wrote, not re-parsed.
        BcAppSymbolCache.ResetProcessCacheForTests();
        var warm = Assert.Single(BcAppSymbolCache.Get(appPath).Reports);
        Assert.Equal(1, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        AssertMapped(Layouts(warm));
    }

    [Theory]
    [InlineData("No", false)]
    [InlineData("Pending", true)]
    [InlineData("Removed", true)]
    [InlineData("", false)]
    public void IsObsolete_follows_bc_rule_obsolete_state_not_no(string state, bool expected)
    {
        var layout = new AlReportLayoutInfo(ReportId, "L", "RDLC", "", "", "", "", "", ObsoleteState: state);
        Assert.Equal(expected, RecordPatches.IsObsoleteLayout(layout));
    }

    [Fact]
    public void A_non_boolean_excel_sheet_value_is_refused_not_read_as_default()
    {
        var layout = new AlReportLayoutInfo(ReportId, "L", "Excel", "", "", "", "", "",
            ExcelLayoutMultipleDataSheets: "maybe");
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.ExcelSheetConfigurationOrdinal(layout));
        Assert.Contains("ExcelLayoutMultipleDataSheets = 'maybe'", ex.Message);
    }

    [Fact]
    public void Merge_keeps_a_declared_obsolete_state_and_sheet_value_over_an_empty_one()
    {
        var informed = new AlReportLayoutInfo(ReportId, "L", "Excel", "", "", "", "", "",
            ObsoleteState: "Pending", ExcelLayoutMultipleDataSheets: "1");
        var poorer = new AlReportLayoutInfo(ReportId, "L", "Excel", "", "", "", "", "");
        var merged = AlReportLayoutRegistry.Merge(informed, poorer);
        Assert.Equal("Pending", merged.ObsoleteState);
        Assert.Equal("1", merged.ExcelLayoutMultipleDataSheets);
    }
}
