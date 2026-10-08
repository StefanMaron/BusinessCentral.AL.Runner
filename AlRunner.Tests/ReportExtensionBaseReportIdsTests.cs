// ReportExtensionBaseReportIdsTests — #5088: the registry affectedOnly reads a reportextension's base
// report from must resolve a base report that ships in a dependency .app, which no source parse sees,
// and must say "unreadable" rather than "no extensions" when a package's symbols cannot be read.
// The source-parsed half is driven end to end by ServerAffectedSelectionReportExtensionTests.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: registers .apps and calls ResetForReload, moving the registration
// epoch other classes in that collection assert on (PageExtensionBasePageIdsTests, the same shape).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ReportExtensionBaseReportIdsTests : IDisposable
{
    private readonly string _root;

    public ReportExtensionBaseReportIdsTests()
    {
        _root = TestScratch.Dir("al-runner-5088-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Process-wide unique among AlRunner.Tests statics: this file owns 881260xx.
    private const int BaseReportId = 88126001;
    private const int OtherReportId = 88126002;
    private const int ExtensionId = 88126011;
    private const int SpacedExtensionId = 88126012;
    private const int OrphanExtensionId = 88126013;

    private string WriteApp(string symbolReference)
    {
        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(symbolReference);
        return appPath;
    }

    [Fact]
    public void DependencyReportExtension_ResolvesToItsDependencyBaseReport_ByName_AndToNoOther()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp($$"""
            {
              "RuntimeVersion": "17.0",
              "Reports": [
                { "Id": {{BaseReportId}}, "Name": "Issue5088 Base", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" } },
                { "Id": {{OtherReportId}}, "Name": "Issue5088 Other", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" } }
              ],
              "ReportExtensions": [
                { "Id": {{ExtensionId}}, "Name": "Issue5088 Ext", "Target": "Issue5088 Base", "RequestPage": { "ControlChanges": [] } },
                { "Id": {{SpacedExtensionId}}, "Name": "Issue5088 Spaced", "Target": "Issue5088Base", "RequestPage": { "ControlChanges": [] } },
                { "Id": {{OrphanExtensionId}}, "Name": "Issue5088 Orphan", "Target": "Issue5088 Nowhere", "RequestPage": { "ControlChanges": [] } }
              ]
            }
            """));

        var bases = RecordPatches.ReportExtensionBaseReportIds();

        Assert.NotNull(bases);
        Assert.Equal(new[] { BaseReportId }, bases![ExtensionId]);
        // Space-insensitive, the rule every other report-name lookup uses.
        Assert.Equal(new[] { BaseReportId }, bases[SpacedExtensionId]);
        // Known, but its base is not: empty, which selection reads as "cannot be resolved".
        Assert.Empty(bases[OrphanExtensionId]);
        Assert.DoesNotContain(OtherReportId, bases.Values.SelectMany(v => v));
    }

    [Fact]
    public void ADependencyWhoseSymbolsCannotBeRead_IsNoAnswer_NotNoExtensions()
    {
        RecordPatches.ResetForReload();
        var appPath = WriteApp("""{ "RuntimeVersion": "17.0", "Reports": [] }""");
        RecordPatches.AddBcAppPath(appPath);
        // Present but unreadable: not a vanished app, which is skipped.
        File.WriteAllText(appPath, "this is no longer a zip");

        Assert.Null(RecordPatches.ReportExtensionBaseReportIds());
    }
}
