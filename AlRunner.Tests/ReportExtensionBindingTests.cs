// Issue #4909: each reportextension of a report has its request-page extension registered on the
// report's request page at construction — the last step of BC's NavReport.RegisterReportExtension,
// not the whole method (its report triggers and data items are #4918). These pin which extensions
// the runner finds for a report and
// that one the metadata declares but whose compiled type is not loaded refuses rather than being
// left unbound. The end-to-end claim (a [RequestPageHandler] reads and writes the extension's
// field) is the corpus's: codeunits 67546 and 67547.
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public class ReportExtensionBindingTests
{
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Reports": [
            { "Id": 88490901, "Name": "RXB Report", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" } },
            { "Id": 88490902, "Name": "RXB Plain Report", "RequestPage": { "Id": 0, "Name": "RequestOptionsPage" } }
          ],
          "ReportExtensions": [
            { "Id": 88490911, "Name": "RXB Report Ext", "Target": "RXB Report",
              "RequestPage": { "ControlChanges": [] } }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-report-extension-binding-tests");
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
    public void APrecompiledReportExtension_IsFoundForItsTargetReport_AndForNoOther()
        => WithDependencyApp(() =>
        {
            Assert.Equal(new[] { 88490911 }, RecordPatches.ReportExtensionIdsFor(88490901));
            Assert.Empty(RecordPatches.ReportExtensionIdsFor(88490902));
        });

    [Fact]
    public void AReportExtensionWhoseCompiledTypeIsNotLoaded_Refuses_RatherThanStayingUnbound()
        => WithDependencyApp(() =>
        {
            var ex = Assert.Throws<RunnerOutOfScopeException>(() => NavReportSync.ResolveReportExtensionTypes(88490901));
            Assert.Contains("reportextension 88490911", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ReportExtension88490911", ex.Message, StringComparison.Ordinal);
            Assert.Empty(NavReportSync.ResolveReportExtensionTypes(88490902));
        });
}
