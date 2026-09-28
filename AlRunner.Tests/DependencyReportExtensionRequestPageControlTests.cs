// Issue #4921: a request-page control a PRECOMPILED reportextension adds is found beside the
// report's own, so its Caption and OptionCaption reach TestRequestPage. What BC answers is the
// corpus's claim, codeunit 67660 (the PR body's Corpus-PR:); these pin the runner's own lookup.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches' dependency state resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyReportExtensionRequestPageControlTests
{
    private const int ReportId = 88492101;
    private const int OtherReportId = 88492102;
    private const int OwnId = 649210001;      // the report's own field
    private const int ExtFieldId = 649210002; // added by the reportextension, inside an added group
    private const int ExtPlainId = 649210003; // added, stating no Caption
    private const int OtherExtId = 649210004; // added by an extension of ANOTHER report

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Reports": [
            {
              "Id": 88492101, "Name": "RERC Report",
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 649210001, "Name": "OwnCtl",
                    "Properties": [ { "Name": "Caption", "Value": "Own Caption" },
                                    { "Name": "SourceExpression", "Value": "OwnVar" } ] } ] } ] }
            },
            {
              "Id": 88492102, "Name": "RERC Other Report",
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [] } ] }
            }
          ],
          "ReportExtensions": [
            {
              "Id": 88492111, "Name": "RERC Report Ext", "Target": "RERC Report",
              "RequestPage": { "Id": 0, "Name": "RequestPageExtension", "ControlChanges": [
                { "Anchor": "Content", "ChangeKind": 4, "Controls": [
                  { "Kind": 1, "Id": 649210009, "Name": "ExtGroup", "Controls": [
                    { "Kind": 8, "Id": 649210002, "Name": "ExtOptCtl",
                      "Properties": [ { "Name": "Caption", "Value": "Ext Option Caption" },
                                      { "Name": "OptionCaption", "Value": "Alpha Cap,Beta Cap" },
                                      { "Name": "SourceExpression", "Value": "ExtOptVar" } ] },
                    { "Kind": 8, "Id": 649210003, "Name": "ExtPlainCtl",
                      "Properties": [ { "Name": "SourceExpression", "Value": "ExtPlainVar" } ] } ] } ] } ] }
            },
            {
              "Id": 88492112, "Name": "RERC Other Ext", "Target": "RERC Other Report",
              "RequestPage": { "Id": 0, "Name": "RequestPageExtension", "ControlChanges": [
                { "Anchor": "Content", "ChangeKind": 4, "Controls": [
                  { "Kind": 8, "Id": 649210004, "Name": "OtherCtl",
                    "Properties": [ { "Name": "Caption", "Value": "Other Caption" } ] } ] } ] }
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-dep-reportext-request-page-control-tests");
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
    public void AnExtensionAddedControl_IsFound_WithItsCaptionAndOptionCaption()
        => WithDependencyApp(() =>
        {
            var control = RecordPatches.TryGetDependencyRequestPageControl(ReportId, ExtFieldId);
            Assert.NotNull(control);
            Assert.Equal("ExtOptCtl", control!.Name);
            Assert.Equal("Ext Option Caption", control.Caption);
            Assert.Equal("Alpha Cap,Beta Cap", control.OptionCaption);
            // One stating no Caption is found too, so the control-name fallback can answer.
            Assert.Equal("ExtPlainCtl", RecordPatches.TryGetDependencyRequestPageControl(ReportId, ExtPlainId)?.Name);
        });

    [Fact]
    public void TheReportsOwnControl_IsStillFound()
        => WithDependencyApp(() =>
            Assert.Equal("Own Caption", RecordPatches.TryGetDependencyRequestPageControl(ReportId, OwnId)?.Caption));

    [Fact]
    public void AnotherReportsExtension_OrAnUnknownId_ContributesNothing()
        => WithDependencyApp(() =>
        {
            Assert.Null(RecordPatches.TryGetDependencyRequestPageControl(ReportId, OtherExtId));
            Assert.Null(RecordPatches.TryGetDependencyRequestPageControl(OtherReportId, ExtFieldId));
            Assert.Null(RecordPatches.TryGetDependencyRequestPageControl(ReportId, 649210099));
        });
}
