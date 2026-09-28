// Issue #4928: a PRECOMPILED reportextension's modify() of a request-page control's Caption and
// OptionCaption, read from its symbol file. What BC answers is the corpus's claim, codeunit 67670
// (the PR body's Corpus-PR:); these pin the runner's own lookup.
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches' dependency state resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyReportExtensionModifyCaptionTests
{
    private const int ReportId = 88492801;
    private const int ClashReportId = 88492802;
    private const int TextId = 649280001;
    private const int OptId = 649280002;
    private const int PlainId = 649280003;
    private const int ClashId = 649280004;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Reports": [
            {
              "Id": 88492801, "Name": "RMC Report",
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 649280001, "Name": "TextCtl",
                    "Properties": [ { "Name": "Caption", "Value": "Own Text" } ] },
                  { "Kind": 8, "Id": 649280002, "Name": "OptCtl",
                    "Properties": [ { "Name": "Caption", "Value": "Own Option" },
                                    { "Name": "OptionCaption", "Value": "Low,High" } ] },
                  { "Kind": 8, "Id": 649280003, "Name": "PlainCtl",
                    "Properties": [ { "Name": "Caption", "Value": "Own Plain" } ] } ] } ] }
            },
            {
              "Id": 88492802, "Name": "RMC Clash Report",
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 649280004, "Name": "ClashCtl",
                    "Properties": [ { "Name": "Caption", "Value": "Own Clash" } ] } ] } ] }
            }
          ],
          "ReportExtensions": [
            {
              "Id": 88492811, "Name": "RMC Report Ext", "Target": "RMC Report",
              "RequestPage": { "Id": 0, "Name": "RequestPageExtension", "ControlChanges": [
                { "Anchor": "TextCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "Caption", "Value": "Ext Text" } ] },
                { "Anchor": "OptCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "Caption", "Value": "Ext Option" },
                                  { "Name": "OptionCaption", "Value": "Lower,Higher" },
                                  { "Name": "ApplicationArea", "Value": "#Basic" } ] } ] }
            },
            {
              "Id": 88492812, "Name": "RMC Clash Ext A", "Target": "RMC Clash Report",
              "RequestPage": { "Id": 0, "Name": "RequestPageExtension", "ControlChanges": [
                { "Anchor": "ClashCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "Caption", "Value": "Clash A" } ] } ] }
            },
            {
              "Id": 88492813, "Name": "RMC Clash Ext B", "Target": "RMC Clash Report",
              "RequestPage": { "Id": 0, "Name": "RequestPageExtension", "ControlChanges": [
                { "Anchor": "ClashCtl", "ChangeKind": 9,
                  "Properties": [ { "Name": "Caption", "Value": "Clash B" } ] } ] }
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-dep-reportext-modify-caption-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);
            RecordPatches.AddBcAppPath(appPath);
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AModifiedCaption_AndOptionCaption_AreTheExtensionsValues()
        => WithDependencyApp(() =>
        {
            Assert.Equal("Ext Text", RecordPatches.ReportExtensionModifiedControlText(ReportId, TextId, "TextCtl", "Caption"));
            Assert.Equal("Ext Option", RecordPatches.ReportExtensionModifiedControlText(ReportId, OptId, "OptCtl", "Caption"));
            Assert.Equal("Lower,Higher", RecordPatches.ReportExtensionModifiedControlText(ReportId, OptId, "OptCtl", "OptionCaption"));
        });

    [Fact]
    public void AControlNoExtensionModifies_AnswersNull_SoItsOwnCaptionStands()
        => WithDependencyApp(() =>
        {
            Assert.Null(RecordPatches.ReportExtensionModifiedControlText(ReportId, PlainId, "PlainCtl", "Caption"));
            // A modify() stating Caption only leaves OptionCaption alone.
            Assert.Null(RecordPatches.ReportExtensionModifiedControlText(ReportId, TextId, "TextCtl", "OptionCaption"));
            // Another report's control of the same name borrows nothing.
            Assert.Null(RecordPatches.ReportExtensionModifiedControlText(ClashReportId, TextId, "TextCtl", "Caption"));
        });

    [Fact]
    public void TwoExtensionsModifyingOneControlDifferently_Refuse()
        => WithDependencyApp(() =>
        {
            var ex = Assert.ThrowsAny<RunnerOutOfScopeException>(() =>
                RecordPatches.ReportExtensionModifiedControlText(ClashReportId, ClashId, "ClashCtl", "Caption"));
            Assert.Contains("'Clash A' and 'Clash B'", ex.Message);
        });
}
