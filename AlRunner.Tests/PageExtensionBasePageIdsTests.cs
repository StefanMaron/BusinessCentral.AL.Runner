// PageExtensionBasePageIdsTests — #5025: the registry affectedOnly reads a pageextension's base
// page from must resolve a base page that ships in a dependency .app, which no source parse sees.
// The source-parsed half is driven end to end by ServerAffectedSelectionPageExtensionTests.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: registers .apps and calls ResetForReload, moving the
// registration epoch other classes in that collection assert on (DependencyPageSymbolIndexMemoTests).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class PageExtensionBasePageIdsTests : IDisposable
{
    private readonly string _root;

    public PageExtensionBasePageIdsTests()
    {
        _root = TestScratch.Dir("al-runner-5025-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Process-wide unique among AlRunner.Tests statics: this file owns 881250xx.
    private const int BasePageId = 88125001;
    private const int ExtensionId = 88125011;
    private const int QualifiedExtensionId = 88125012;
    private const int OrphanExtensionId = 88125013;

    private string WriteApp()
    {
        var json = $$"""
            { "RuntimeVersion": "15.1", "Namespaces": [ { "Name": "Issue5025",
              "Pages": [ { "Id": {{BasePageId}}, "Name": "Issue5025 Base", "Properties": [ { "Name": "PageType", "Value": "Card" } ] } ],
              "PageExtensions": [
                { "Id": {{ExtensionId}}, "Name": "Issue5025 Ext", "TargetObject": "Issue5025 Base" },
                { "Id": {{QualifiedExtensionId}}, "Name": "Issue5025 Qualified", "TargetObject": "#63ca2fa4-4f03-4f2b-a480-172fef340d3f#Issue5025Base" },
                { "Id": {{OrphanExtensionId}}, "Name": "Issue5025 Orphan", "TargetObject": "Issue5025 Nowhere" } ] } ] }
            """;
        var appPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(json);
        return appPath;
    }

    [Fact]
    public void DependencyPageExtension_ResolvesToItsDependencyBasePage_ByName()
    {
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteApp());

        var bases = RecordPatches.PageExtensionBasePageIds();

        Assert.NotNull(bases);
        Assert.Equal(new[] { BasePageId }, bases![ExtensionId]);
        // Module-qualified and space-insensitive, the rule every other page-name lookup uses.
        Assert.Equal(new[] { BasePageId }, bases[QualifiedExtensionId]);
        // Known, but its base is not: empty, which selection reads as "cannot be resolved".
        Assert.Empty(bases[OrphanExtensionId]);
    }
}
