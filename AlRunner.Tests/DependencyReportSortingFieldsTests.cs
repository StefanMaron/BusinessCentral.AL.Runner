// DependencyReportSortingFieldsTests — runner-mechanism guard for #3627.
//
// The gap
// -------
// "Report Data Items" (2000000203) answers Sorting Fields as a comma-separated list of
// field NUMBERS. #3620 reached those numbers by reading BC's own emitted document, whose
// DataItemTableView is already in BC's normal form — SORTING(Field5,Field2). A report from
// a PRECOMPILED dependency has no such document: its SymbolReference.json states
// DataItemTableView as AL SOURCE TEXT — sorting("Alt Code", Description), field NAMES —
// and RecordPatches.FieldNumbersFrom drops every token that is not Field<N>. So the column
// answered EMPTY for every Base Application report.
//
// Measured on Base Application 28.1.49838.53910's SymbolReference.json: 659 reports,
// 1927 data items, 1795 stating a DataItemTableView and 1759 of those stating a SORTING
// clause — 3201 sorting tokens, 988 bare identifiers and 2213 double-quoted names, and
// ZERO in Field<N> form. Every one of them answered empty.
//
// Why this test is here and not in the corpus
// -------------------------------------------
// A corpus test is compiled from AL source BY THE RUNNER, so it takes the source-compiled
// path where the column already answers correctly (pinned upstream by corpus PR #302).
// The dependency path is only reachable with a precompiled .app in hand, which is the
// structural precompiled-only case .claude/rules/bc-behavior-tests-go-upstream.md
// describes. The BC-behaviour claim underneath ("2000000203 reports field numbers") is
// already adjudicated upstream; what is pinned here is the runner mechanism that reaches
// the same numbers from a symbol file's AL text.
//
// What would make this test vacuous
// ---------------------------------
// Asserting merely "not empty" would pass against any stub. Every assertion below names
// the exact field numbers, chosen so they are neither the declaration order nor the
// primary key: the fixture's fields are 1/2/5 and the sorting clauses name them in orders
// that no default could produce.
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Drives EnumerateKnownReports and the process-global _parsedTables / registered .app list,
// so it takes the RecordPatches parser-statics lock for the same reason
// DependencyReportProcessingOnlyTests does.
[Collection(RecordPatchesSerialCollection.Name)]
public class DependencyReportSortingFieldsTests
{
    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    // Far outside every other test's range: the registered .app list is process-global.
    private const int QuotedNamesReportId = 88451101;
    private const int BareNameReportId = 88451102;
    private const int NoSortingReportId = 88451103;
    private const int UnknownFieldReportId = 88451104;
    private const int NormalFormReportId = 88451105;
    private const int ExtensionFieldReportId = 88451106;
    private const int DepTableId = 88451190;

    // The fixture table's fields are 1 / 2 / 5, deliberately non-contiguous, so a resolved
    // answer cannot coincide with a declaration ordinal. The reports below then name them
    // in orders that are neither field order nor primary-key order.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Namespaces": [
            {
              "Name": "DRSF",
              "Reports": [
                {
                  "Id": 88451101,
                  "Name": "DRSF Quoted Names",
                  "Properties": [],
                  "DataItems": [
                    {
                      "Id": 11,
                      "Name": "Src",
                      "RelatedTable": "DRSF Sample",
                      "Indentation": 0,
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(\"Alt Code\", Description)" }
                      ]
                    }
                  ]
                },
                {
                  "Id": 88451102,
                  "Name": "DRSF Bare Name",
                  "Properties": [],
                  "DataItems": [
                    {
                      "Id": 12,
                      "Name": "Src",
                      "RelatedTable": "DRSF Sample",
                      "Indentation": 0,
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(Description) where(\"Alt Code\" = const('X'))" }
                      ]
                    }
                  ]
                },
                {
                  "Id": 88451103,
                  "Name": "DRSF No Sorting",
                  "Properties": [],
                  "DataItems": [
                    {
                      "Id": 13,
                      "Name": "Src",
                      "RelatedTable": "DRSF Sample",
                      "Indentation": 0,
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "where(\"Alt Code\" = const('X'))" }
                      ]
                    }
                  ]
                },
                {
                  "Id": 88451104,
                  "Name": "DRSF Unknown Field",
                  "Properties": [],
                  "DataItems": [
                    {
                      "Id": 14,
                      "Name": "Src",
                      "RelatedTable": "DRSF Sample",
                      "Indentation": 0,
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(\"Alt Code\", \"No Such Field\", Description)" }
                      ]
                    }
                  ]
                },
                {
                  "Id": 88451106,
                  "Name": "DRSF Extension Field",
                  "Properties": [],
                  "DataItems": [
                    {
                      "Id": 16,
                      "Name": "Src",
                      "RelatedTable": "DRSF Sample",
                      "Indentation": 0,
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "sorting(\"Alt Code\", \"DRSF Ext Field\")" }
                      ]
                    }
                  ]
                },
                {
                  "Id": 88451105,
                  "Name": "DRSF Normal Form",
                  "Properties": [],
                  "DataItems": [
                    {
                      "Id": 15,
                      "Name": "Src",
                      "RelatedTable": "DRSF Sample",
                      "Indentation": 0,
                      "Properties": [
                        { "Name": "DataItemTableView", "Value": "SORTING(Field5,Field2) ORDER(1)" }
                      ]
                    }
                  ]
                }
              ]
            }
          ],
          "Tables": [
            {
              "Id": 88451190,
              "Name": "DRSF Sample",
              "Properties": [],
              "Fields": [
                { "TypeDefinition": { "Name": "Integer" }, "Properties": [], "Id": 1, "Name": "Entry No." },
                { "TypeDefinition": { "Name": "Text[50]" }, "Properties": [], "Id": 2, "Name": "Description" },
                { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [], "Id": 5, "Name": "Alt Code" }
              ]
            }
          ],
          "TableExtensions": [
            {
              "Id": 88451191,
              "Name": "DRSF Sample Ext",
              "TargetObject": "DRSF Sample",
              "Fields": [
                { "TypeDefinition": { "Name": "Code[10]" }, "Properties": [], "Id": 40, "Name": "DRSF Ext Field" }
              ]
            }
          ]
        }
        """;

    private static string WriteApp(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    [Fact]
    public void DependencyReportSortingClause_ResolvesQuotedFieldNamesToNumbers()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-sorting-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            // "Alt Code" is field 5 and Description is field 2, and the clause names them in
            // that order — so the expected answer is neither field order (2,5) nor the
            // ordinal positions of the tokens (1,2). A stub returning "" fails; one echoing
            // declaration order fails; one sorting the numbers fails.
            Assert.Equal("5,2", SortingFieldsOf(QuotedNamesReportId, dataItemId: 11));

            // Negative direction on the same code path: a bare (unquoted) AL identifier is
            // the other half of what a symbol file writes — 988 of Base Application's 3201
            // sorting tokens are bare — and the WHERE clause that follows must not leak into
            // the answer.
            Assert.Equal("2", SortingFieldsOf(BareNameReportId, dataItemId: 12));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DependencyReportWithNoSortingClause_AnswersEmpty()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-sorting-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            // Control: the .app really is registered and its reports really are in the
            // inventory, so the empty assertion below is an observation, not a vacuous
            // "nothing was loaded".
            Assert.Equal("5,2", SortingFieldsOf(QuotedNamesReportId, dataItemId: 11));

            // A data item whose view states only a WHERE clause declares no sorting, and
            // GetSortingFieldsIfAny answers string.Empty for that on a real tier. The fix
            // must not invent a primary key here.
            Assert.Equal(string.Empty, SortingFieldsOf(NoSortingReportId, dataItemId: 13));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SortingToken_ThatNamesNoField_IsDroppedAndTheRestSurvive()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-sorting-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            // BC's AddSortingField traces and SKIPS a name FindFieldMatch cannot resolve, so
            // the surrounding tokens still answer. An implementation that abandoned the whole
            // clause on one bad token would answer ""; one that emitted a placeholder would
            // answer "5,0,2" or leak the name.
            Assert.Equal("5,2", SortingFieldsOf(UnknownFieldReportId, dataItemId: 14));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SortingClauseAlreadyInBcNormalForm_KeepsAnsweringFieldNumbers()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-sorting-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            // Regression guard on the path #3620 already fixed: a view already stating
            // SORTING(Field5,Field2) must keep resolving through the Field<N> rule and must
            // not be pushed through a name lookup that would find no field called "Field5".
            Assert.Equal("5,2", SortingFieldsOf(NormalFormReportId, dataItemId: 15));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A sorting token may name a field a TABLEEXTENSION added, and <c>ParsedTable.Fields</c>
    /// does not carry one. Found by asking whether a sibling doing the same job has a guard
    /// this one lacks: <c>TryResolveDependencyFieldId</c>, the page-control field map and the
    /// AL page parser each resolve a dependency field name through
    /// <c>GetAllFieldsIncludingExtensions</c>, each with its own comment saying "not
    /// table.Fields alone" (#2490).
    ///
    /// <para>Base Application 28.1 has no such sorting token today — 0 of its 3201 resolve
    /// only through an extension — so this could not have been caught by the corpus, by the
    /// AL-level tests in this PR, or by any Base Application measurement. It would have
    /// shipped and answered a SHORTER sort key than BC applies for the first ISV app whose
    /// report sorts by an extension field.</para>
    /// </summary>
    [Fact]
    public void SortingToken_NamingATableExtensionField_Resolves()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-sorting-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            // "Alt Code" is a base field (5) and "DRSF Ext Field" is contributed by
            // tableextension 88451191 as field 40. Against table.Fields alone the second
            // token is dropped and the answer is "5" — a shorter key, silently.
            Assert.Equal("5,40", SortingFieldsOf(ExtensionFieldReportId, dataItemId: 16));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The cost guard #3627 exists for. Populating a virtual table enumerates EVERY report,
    /// so a per-object field-number resolution is paid once per data item per POPULATION,
    /// and on Base Application that is 1927 of them. This asserts the inventory is built
    /// once and then handed back, so a later read cannot re-pay it — the property a filtered
    /// pass/fail run cannot see, and the one whose loss blew the 60s per-test watchdog on
    /// the first attempt at #3607.
    /// </summary>
    [Fact]
    public void ReportInventory_IsBuiltOnce_AndRepeatedReadsAreCached()
    {
        var dir = TestScratch.Dir("al-runner-dep-report-sorting-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir));

            // First read builds the inventory (and pays whatever resolution costs).
            var first = EnumerateKnownReports();
            var cold = Stopwatch.StartNew();
            var second = EnumerateKnownReports();
            cold.Stop();

            // Same LIST INSTANCE, not merely an equal one: that is what proves the second
            // read did no work at all rather than re-deriving an identical answer quickly.
            // An implementation that resolved lazily per read would return a fresh list here
            // even if its answers matched.
            Assert.Same(first, second);

            // And the values survive the caching, so "cached" cannot be satisfied by caching
            // an empty answer.
            Assert.Equal("5,2", SortingFieldsOf(QuotedNamesReportId, dataItemId: 11));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable EnumerateKnownReports()
    {
        var m = RecordPatchesType.GetMethod("EnumerateKnownReports", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RecordPatches.EnumerateKnownReports not found by reflection.");
        return (IEnumerable)m.Invoke(null, null)!;
    }

    /// <summary>
    /// The <c>SortingFields</c> the inventory carries for one data item of one report — the
    /// exact string BuildReportDataItemValue hands to the "Sorting Fields" column.
    /// </summary>
    private static string SortingFieldsOf(int reportId, int dataItemId)
    {
        foreach (var row in EnumerateKnownReports())
        {
            var t = row.GetType();
            if ((int)t.GetProperty("Id")!.GetValue(row)! != reportId) continue;
            var items = (IEnumerable)t.GetProperty("DataItems")!.GetValue(row)!;
            foreach (var item in items)
            {
                var it = item.GetType();
                if ((int)it.GetProperty("Id")!.GetValue(item)! != dataItemId) continue;
                return (string)it.GetProperty("SortingFields")!.GetValue(item)!;
            }
            throw new InvalidOperationException(
                $"report {reportId} is in the inventory but has no data item {dataItemId}");
        }
        throw new InvalidOperationException(
            $"report {reportId} is not in the report inventory — the fixture .app did not register");
    }
}
