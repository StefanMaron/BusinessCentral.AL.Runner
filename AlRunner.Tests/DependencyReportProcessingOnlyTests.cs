// DependencyReportProcessingOnlyTests — runner-mechanism guard for #2397.
//
// The gap
// -------
// RecordPatches.IsReportProcessingOnly answered from _parsedReports alone, and that
// dictionary only ever holds reports the runner compiled FROM AL SOURCE. Every report
// living in a precompiled dependency .app (all 659 Base Application reports, System
// Application, any ISV app) is absent from it, so the expression fell through to `false`
// — "not processing-only" — no matter what the report actually declares.
//
// NavReportSync.TryRunOrControlFlow reads exactly that answer to decide whether Run()
// must attempt a layout, so a genuinely processing-only Base App report got a layout
// attempt and died on the out-of-scope rendering throw. Measured on Base Application
// 28.1.49838.53910: report 950 "Create Time Sheets" declares
// `{ "Name": "ProcessingOnly", "Value": "1" }` in SymbolReference.json, and the runner
// still answered false for it — 31 Tests-SINGLESERVER tests in Codeunit136506 ended there.
//
// What this file pins
// -------------------
// The runner-only C# mechanism: given a registered dependency .app, ProcessingOnly is read
// from that app's own symbol data, AL's default is applied when the symbol file states
// nothing, an unknown report still answers false, and a report the runner DID source-parse
// keeps its source-parsed answer even when a dependency declares the same id differently.
// The BC-observable claim underneath it (report 950 runs to completion and writes rows) is
// plain BC behaviour and belongs upstream against a service tier, not here.
//
// The .app shape below (a zip holding SymbolReference.json) mirrors
// BcAppSymbolCacheReportTests and DependencyPageMetadataXmlTests.
using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// This class drives TryParseReportFile and reads _parsedReports back, so the parser-statics
// guard requires RecordPatchesSerialCollection. It also reaches BcAppSymbolCache (via
// AddBcAppPath), which normally argues for CacheRootsSerialCollection — a class can only
// join one. RecordPatches wins because the parse statics are the ones that would produce a
// WRONG answer under a race: every .app written here is a fresh GUID-named file, and the
// symbol cache key is content-addressed (BcAppSymbolCacheContentAddressedKeyTests), so the
// worst a concurrent CacheRoots override can do is turn a cache HIT into a MISS and make
// Get() re-parse the same file to the same result.
[Collection(RecordPatchesSerialCollection.Name)]
public class DependencyReportProcessingOnlyTests
{
    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    // Ids far outside every other test's range: _parsedReports and the registered .app list
    // are both process-global, so a shared id could read back another test's fact.
    private const int DepProcessingOnlyReportId = 88123601;
    private const int DepLayoutReportId = 88123602;
    private const int SourceWinsReportId = 88123603;
    private const int SourceOnlyProcessingOnlyReportId = 88123604;
    private const int UnknownReportId = 88123609;

    // Two dependency reports that differ ONLY in the ProcessingOnly property, plus a third
    // whose id is also declared in AL source below. 88123602 states no ProcessingOnly at
    // all, which is how the vast majority of real Base App reports look — it must keep
    // answering false, or this fix would silence the layout throw for every report (that
    // is #2436's subject, deliberately not touched here).
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "DRPO",
              "Reports": [
                {
                  "Id": 88123601,
                  "Name": "DRPO Create Time Sheets",
                  "Properties": [
                    { "Name": "Caption", "Value": "DRPO Create Time Sheets" },
                    { "Name": "ProcessingOnly", "Value": "1" }
                  ]
                },
                {
                  "Id": 88123602,
                  "Name": "DRPO Standard Invoice",
                  "Properties": [
                    { "Name": "Caption", "Value": "DRPO Standard Invoice" }
                  ]
                },
                {
                  "Id": 88123603,
                  "Name": "DRPO Shadowed Report",
                  "Properties": [
                    { "Name": "ProcessingOnly", "Value": "1" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    // The runner's own bundle declares 88123603 WITHOUT ProcessingOnly (so: AL's default,
    // false) and 88123604 WITH it. 88123603 is the precedence probe — the dependency above
    // says "1" for the same id.
    private const string AlSource = """
        report 88123603 "DRPO Shadowed Report"
        {
            Caption = 'DRPO Shadowed Report';
        }

        report 88123604 "DRPO Source Processing Only"
        {
            ProcessingOnly = true;
        }
        """;

    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    [Fact]
    public void DependencyReportDeclaringProcessingOnly_IsReportedProcessingOnly()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-proconly-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            // The whole point of #2397: this report exists ONLY in a precompiled
            // dependency, so nothing ever parsed its AL source, and it still has to answer
            // with what the dependency's own symbol data states.
            Assert.True(RecordPatches.IsReportProcessingOnly(DepProcessingOnlyReportId),
                $"report {DepProcessingOnlyReportId} declares ProcessingOnly in the dependency's "
                + "SymbolReference.json, so the runner must not send it down the layout path");

            // Negative, same .app, same code path: a report that declares no ProcessingOnly
            // takes AL's default and MUST still be treated as layout-bound. Without this the
            // fix could pass by answering true for everything.
            Assert.False(RecordPatches.IsReportProcessingOnly(DepLayoutReportId),
                $"report {DepLayoutReportId} declares no ProcessingOnly, so AL's default (false) "
                + "applies and the layout attempt must still happen");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportNoSourceAndNoDependencyDeclares_IsNotProcessingOnly()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-proconly-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            // Control: the .app really is registered and readable, so the assertion below
            // is an observation rather than a vacuous "nothing was loaded".
            Assert.True(RecordPatches.IsReportProcessingOnly(DepProcessingOnlyReportId));

            Assert.False(RecordPatches.IsReportProcessingOnly(UnknownReportId),
                "an id no source and no dependency describes keeps AL's default of false — the "
                + "symbol fallback must not invent an answer for a report nobody declares");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SourceParsedReport_KeepsItsOwnAnswer_EvenWhenADependencyDeclaresTheSameId()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-proconly-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));
            ParseReports(AlSource);

            // Control: both ids really did reach the parser.
            Assert.True(ParsedReportExists(SourceWinsReportId));
            Assert.True(ParsedReportExists(SourceOnlyProcessingOnlyReportId));

            // The bundle's own source declares no ProcessingOnly for 88123603 while the
            // dependency states "1". A source-parsed entry always beats a dependency's
            // (see the AlReportParser file header), so the answer is AL's default, false.
            // An implementation that ORs the two together returns true here.
            Assert.False(RecordPatches.IsReportProcessingOnly(SourceWinsReportId),
                "a report the runner source-compiled must keep its source-parsed ProcessingOnly, "
                + "not inherit a dependency's value for the same id");

            // And the pre-existing source-only path is unchanged.
            Assert.True(RecordPatches.IsReportProcessingOnly(SourceOnlyProcessingOnlyReportId),
                "a source-parsed report declaring ProcessingOnly = true must still answer true");
        }
        finally
        {
            ForgetParsedReports();
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void ParseReports(string source)
    {
        var m = RecordPatchesType.GetMethod("TryParseReportFile", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RecordPatches.TryParseReportFile not found by reflection.");
        m.Invoke(null, new object[] { source });
    }

    private static IDictionary ParsedReports() => (IDictionary)RecordPatchesType
        .GetField("_parsedReports", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static bool ParsedReportExists(int id) => ParsedReports().Contains(id);

    // Drop the fixtures out of the process-wide parser state so they cannot leak.
    private static void ForgetParsedReports()
    {
        var d = ParsedReports();
        d.Remove(SourceWinsReportId);
        d.Remove(SourceOnlyProcessingOnlyReportId);
    }
}
