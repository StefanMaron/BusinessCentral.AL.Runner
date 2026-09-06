// BundleQuerySymbolsResetTests — issue #2939.
//
// RecordPatches._bcQuerySymbolJsonPaths is the sibling of _bcAppPaths that #2755 deliberately
// left alone: a process-global List<string> that RegisterBundleQuerySymbolsJson appends to and
// nothing ever removes from. It is the second input to EnsureBcSymbolQueryIndex, and
// InvalidateBcAppIndexes nulls _bcSymbolQueryIndex on every reload precisely so the next lookup
// rebuilds it FROM this list — the same registered/derived split #2755 was about.
//
// ── WHY THE DEFECT IS WORSE THAN "A UNION", WHICH IS WHAT MAKES IT ASSERTABLE ────────────────
//
// #2939 describes the effect as bundle 2 resolving "against its own registered query symbols
// UNION every earlier bundle's". The union is real, but the merge is FIRST-WINS:
//
//     foreach (var jsonPath in _bcQuerySymbolJsonPaths)
//         foreach (var q in BcAppSymbolCache.GetFromJson(jsonPath).Queries)
//             if (!idx.ContainsKey(q.Id)) idx[q.Id] = q;      // <- first registration wins
//
// and the surviving entry is the EARLIER bundle's, because it is earlier in the list. So when
// two bundles in one --server session declare the same query id — the ordinary case for two
// checkouts of one app, or an app whose query is edited between --watch cycles — bundle 2 does
// not get a superset of its own answer. It gets bundle 1's answer INSTEAD of its own, and the
// column ids it hands to NavQuery.GetColumnValueSafe are the previous bundle's.
//
// That is the assertion below: not "the list grew", but "query 79960 came back with the wrong
// bundle's columns". A test that only checked the list length would pass against an
// implementation that merged last-wins and still be measuring nothing about the ids AL reads.
//
// ── WHY THE FIXTURE IS A HAND-WRITTEN SymbolReference.json ───────────────────────────────────
//
// This list holds LOOSE SymbolReference.json files, not .app zips (see the field's own comment)
// — BcCompiler.EmitAndRegisterBundleQuerySymbols writes one per compiled bundle that declares a
// query, and Program.cs's two AL-output-cache HIT paths re-register the cached copy. So
// SymbolAppFixture, which BcAppPathsResetTests uses, is the wrong shape here: it builds a
// registrable .app for _bcAppPaths. The JSON below is the minimum BcAppSymbolCache.GetFromJson
// parses into a QuerySymbol — Queries[].Elements[].Columns[] with explicit Ids, which are
// exactly the BC-compiler-assigned column ids the whole mechanism exists to carry verbatim.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial, for the same reason BcAppPathsResetTests is: this registers into
// RecordPatches' process-global query-symbol source list and calls ResetForReload(), which
// clears roughly twenty static dictionaries shared with every other test in the process.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BundleQuerySymbolsResetTests : IDisposable
{
    private const int QueryId = 79960;
    private readonly string _root;

    public BundleQuerySymbolsResetTests() => _root = TestScratch.Dir("al-runner-query-symbols-reset");

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// One bundle's freshly-compiled query symbols, in the shape BcAppSymbolCache.GetFromJson
    /// reads. <paramref name="columnName"/> and <paramref name="columnId"/> are what the
    /// assertions discriminate on — two bundles declaring the SAME query id with DIFFERENT
    /// column ids is the situation the runner has to get right.
    /// </summary>
    private string WriteQuerySymbolsJson(string bundle, string columnName, int columnId)
    {
        var dir = Path.Combine(_root, bundle);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SymbolReference.json");
        File.WriteAllText(path, $$"""
        {
          "AppId": "{{Guid.NewGuid()}}",
          "Name": "{{bundle}}",
          "Queries": [
            {
              "Id": {{QueryId}},
              "Name": "QsrQuery",
              "Elements": [
                {
                  "Id": 1,
                  "Name": "QsrDataItem",
                  "RelatedTable": "QsrRow",
                  "Columns": [
                    { "Id": {{columnId}}, "Name": "{{columnName}}", "SourceColumn": "{{columnName}}Field" }
                  ]
                }
              ]
            }
          ]
        }
        """);
        return path;
    }

    private static (string Name, int Id) OnlyColumnOf(int queryId)
    {
        var q = RecordPatches.TryGetQuerySymbol(queryId);
        Assert.NotNull(q);
        var dataItem = Assert.Single(q!.DataItems);
        var column = Assert.Single(dataItem.Columns);
        return (column.Name, column.Id);
    }

    [Fact]
    public void ASecondBundleDoesNotInheritTheFirstBundlesQuerySymbolSources()
    {
        // The registered set itself, asserted directly for the same reason BcAppPathsResetTests
        // asserts _bcAppPaths directly: inferring it from a lookup conflates this list with the
        // DERIVED index, which InvalidateBcAppIndexes already drops on every reload — so a
        // lookup-only test passes on the broken build for the wrong reason.
        RecordPatches.ResetForReload();
        var first = WriteQuerySymbolsJson("QsrFirst", "Alpha", 11);
        RecordPatches.RegisterBundleQuerySymbolsJson(first);
        Assert.Contains(first, RecordPatches.RegisteredBundleQuerySymbolJsonPathsForTests());

        // What Program.cs does between bundles: reset, then this bundle registers its own.
        RecordPatches.ResetForReload();
        var second = WriteQuerySymbolsJson("QsrSecond", "Beta", 22);
        RecordPatches.RegisterBundleQuerySymbolsJson(second);

        var registered = RecordPatches.RegisteredBundleQuerySymbolJsonPathsForTests();
        Assert.Contains(second, registered);
        Assert.DoesNotContain(first, registered);
    }

    [Fact]
    public void BundleTwosQueryResolvesToItsOwnColumnIds_NotBundleOnes()
    {
        // The AL-visible half, and the one that says what the defect COST. Two bundles in one
        // process declare query 79960 with different column ids; bundle 2 must read its own.
        RecordPatches.ResetForReload();
        RecordPatches.RegisterBundleQuerySymbolsJson(WriteQuerySymbolsJson("QsrOne", "Alpha", 11));

        // Precondition, asserted rather than assumed: if the JSON did not parse into a
        // QuerySymbol at all, everything below would be measuring an absent registration.
        Assert.Equal(("Alpha", 11), OnlyColumnOf(QueryId));

        RecordPatches.ResetForReload();
        RecordPatches.RegisterBundleQuerySymbolsJson(WriteQuerySymbolsJson("QsrTwo", "Beta", 22));

        // On the broken build this is ("Alpha", 11): the merge in EnsureBcSymbolQueryIndex is
        // first-wins and bundle 1's still-registered file is first in the list, so bundle 2's
        // own column id 22 never reaches the index. The column ID is the load-bearing half —
        // it is what NavQuery.ValidateExpectedType/GetColumnValueSafe are called with, so a
        // stale one is a wrong VALUE read out of a real row, not a crash.
        Assert.Equal(("Beta", 22), OnlyColumnOf(QueryId));
    }

    [Fact]
    public void AfterTheReset_ReRegisteringTheSameFileStillResolves()
    {
        // The other direction: the control that stops the fix from being "clear it so hard the
        // bundle loses its own queries". Every bundle that declares a query re-registers on
        // both the compile path (BcCompiler.Emit) and the AL-output cache HIT path
        // (Program.cs), so a cleared entry must be registrable again — RegisterBundleQuerySymbolsJson
        // skips a path it already holds, so a clear that did not really clear shows up here.
        RecordPatches.ResetForReload();
        var path = WriteQuerySymbolsJson("QsrAgain", "Gamma", 33);
        RecordPatches.RegisterBundleQuerySymbolsJson(path);
        Assert.Equal(("Gamma", 33), OnlyColumnOf(QueryId));

        RecordPatches.ResetForReload();
        Assert.Null(RecordPatches.TryGetQuerySymbol(QueryId));

        RecordPatches.RegisterBundleQuerySymbolsJson(path);
        Assert.Equal(("Gamma", 33), OnlyColumnOf(QueryId));
    }

    [Fact]
    public void WithinOneBundle_SeveralQuerySymbolSourcesStillAccumulate()
    {
        // The invariant the fix must NOT break, and the reason this is a clear-on-reload rather
        // than a clear-on-register: a single bundled run compiles several app groups, each
        // registering its own SymbolReference.json between two resets. Those must accumulate —
        // only a RELOAD boundary discards them.
        RecordPatches.ResetForReload();
        var a = WriteQuerySymbolsJson("QsrGroupA", "Alpha", 11);
        var b = WriteQuerySymbolsJson("QsrGroupB", "Beta", 22);
        RecordPatches.RegisterBundleQuerySymbolsJson(a);
        RecordPatches.RegisterBundleQuerySymbolsJson(b);

        var registered = RecordPatches.RegisteredBundleQuerySymbolJsonPathsForTests();
        Assert.Contains(a, registered);
        Assert.Contains(b, registered);
    }
}
