// Issue #4919: the name a failed TestRequestPage field assertion carries ("AssertEquals for Field:
// <name>") is the control's AL name. A precompiled report's request page has no ControlDefinition,
// so the name comes from the REPORT's symbol file, not from a page symbol of the same id. What BC
// answers is the corpus's claim (the PR body's Corpus-PR:); this pins the runner's own lookup.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches' dependency state resolves through the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class RequestPageControlNameTests
{
    // A report and a page share one id, and a control of each shares one control id, so a lookup
    // that reads the wrong id space answers the other object's name.
    private const int SharedId = 88491901;
    private const int SharedControlId = 649190001;
    private const int OnlyReportControlId = 649190002;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88491901, "Name": "RPCN Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 649190001, "Name": "PageCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "PageVar" } ] } ] } ]
            }
          ],
          "Reports": [
            {
              "Id": 88491901, "Name": "RPCN Report",
              "RequestPage": { "Id": 0, "Name": "RequestOptionsPage", "Controls": [
                { "Id": 1, "Name": "Content", "Controls": [
                  { "Kind": 8, "Id": 649190001, "Name": "RequestCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "RequestVar" } ] },
                  { "Kind": 8, "Id": 649190002, "Name": "OtherRequestCtl",
                    "Properties": [ { "Name": "SourceExpression", "Value": "OtherVar" } ] } ] } ] }
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-request-page-control-name-tests");
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
    public void ARequestPageControl_IsNamedFromItsReportsSymbols_NotFromAPageOfTheSameId()
        => WithDependencyApp(() =>
            Assert.Equal("RequestCtl", RunnerPageInstance.DeclaredControlName(SharedId, isRequestPage: true, SharedControlId)));

    [Fact]
    public void APageControl_IsStillNamedFromItsPageSymbols_NotFromAReportOfTheSameId()
        => WithDependencyApp(() =>
            Assert.Equal("PageCtl", RunnerPageInstance.DeclaredControlName(SharedId, isRequestPage: false, SharedControlId)));

    [Fact]
    public void ARequestPageControlOnlyTheReportDeclares_IsNamed_AndAnUnknownIdIsNot()
        => WithDependencyApp(() =>
        {
            Assert.Equal("OtherRequestCtl", RunnerPageInstance.DeclaredControlName(SharedId, isRequestPage: true, OnlyReportControlId));
            Assert.Null(RunnerPageInstance.DeclaredControlName(SharedId, isRequestPage: true, 649190099));
            // The page has no control 649190002, so a page lookup must not borrow the report's.
            Assert.Null(RunnerPageInstance.DeclaredControlName(SharedId, isRequestPage: false, OnlyReportControlId));
        });
}
