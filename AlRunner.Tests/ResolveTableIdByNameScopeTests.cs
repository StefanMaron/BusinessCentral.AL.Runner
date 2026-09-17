// ResolveTableIdByNameScopeTests — issue #4139, part 1.
//
// #4106/#4128 fixed the TableRelation target and the CalcFormula source: a table NAME written by
// a precompiled (symbol-read) object must resolve in the scope of the app that declared it, not
// to a same-named BUNDLE table the declaring app cannot see. Those two sites now call
// RecordPatches.ResolveTableNameInDeclaringScope.
//
// Six other sites still call the bare ResolveTableIdByName, which checks _parsedTables — bundle
// tables included — BEFORE the dependency symbol index. #4139 lists them and asks for a
// reproducer per site before any fix, per no-assumption-fixes.md.
//
// This file measures the RESOLVER, which is what every one of those sites shares, rather than
// standing up six bundles. It is deliberately not a claim that all six are observably wrong:
// what it pins is that the selection rule differs between the two resolvers, which is the
// precondition for every one of them.
//
// Staged without a .app on disk, the same way RelationTargetDeclaringScopeTests (#4106) stages
// its state.
using System.Collections;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class ResolveTableIdByNameScopeTests
{
    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    private const string AppA = "/deps/Microsoft_Base Application.app";

    // Ids outside every other parser test's range so a leak is obvious.
    private const int DepDeclaringId = 61960;  // the dependency object's own table
    private const int DepTargetId = 61961;     // the dependency table the name means
    private const int BundleTargetId = 61962;  // a bundle table sharing that name

    private const string SharedName = "RTIBN Shipping Agent";

    private static ParsedTable Table(int id, string name, Guid? owningApp = null) =>
        new(id, name, new List<ParsedField>(), new List<int>(), OwningAppId: owningApp);

    /// <summary>
    /// The precondition behind all six sites in #4139: given a bundle table and a dependency
    /// table sharing one name, the two resolvers disagree. The scope-aware one answers the
    /// dependency table for a name written by a dependency object; the bare one answers the
    /// bundle table regardless of who wrote the name.
    /// </summary>
    [Fact]
    public void BareResolver_AnswersTheBundleTable_WhereTheScopeAwareOneAnswersTheDependencyTable()
    {
        var bundleApp = Guid.NewGuid();
        WithState(
            parsed: new[] { Table(BundleTargetId, SharedName, bundleApp) },
            index: new[]
            {
                (AppA, Table(DepDeclaringId, "RTIBN Report Source")),
                (AppA, Table(DepTargetId, SharedName)),
            },
            act: () =>
            {
                // What #4106's fix does for a name written by a dependency object.
                var scoped = RecordPatches.ResolveTableNameInDeclaringScope(
                    SharedName, SymbolTable(DepDeclaringId));
                Assert.Equal(DepTargetId, scoped?.TableId);

                // What the six unfixed sites still do. _parsedTables is consulted first, so the
                // bundle table wins even though the name was written by a dependency object that
                // cannot see the bundle.
                var bare = InvokeBare(SharedName);
                Assert.Equal(BundleTargetId, bare);

                // Stated as the difference rather than as two separate facts: this inequality is
                // the whole precondition #4139 rests on, and it is what a fix at any of the six
                // sites would remove.
                Assert.True(scoped!.TableId != bare,
                    "the two resolvers must disagree here, or #4139's premise does not hold");
            });
    }

    /// <summary>
    /// The CONTROL. With no bundle table shadowing the name, both resolvers agree — so the
    /// disagreement above is caused by the shadowing table and not by the two resolvers simply
    /// being different functions.
    /// </summary>
    [Fact]
    public void WithNoShadowingBundleTable_BothResolversAgree()
    {
        WithState(
            parsed: Array.Empty<ParsedTable>(),
            index: new[]
            {
                (AppA, Table(DepDeclaringId, "RTIBN Report Source")),
                (AppA, Table(DepTargetId, SharedName)),
            },
            act: () =>
            {
                // ORDER MATTERS, and it is load-bearing rather than incidental.
                // ResolveTableNameInDeclaringScope FAULTS its answer into _parsedTables
                // (RecordPatches.BcAppFallback.cs:768-769). Calling it first would leave the bare
                // resolver answering from tier 1, never reaching the symbol index — and then
                // disabling the index tier entirely would red NOTHING here. Bare first keeps this
                // arm pinned to the index lookup it is about. Found in review of PR #4269.
                var bare = InvokeBare(SharedName);
                var scoped = RecordPatches.ResolveTableNameInDeclaringScope(
                    SharedName, SymbolTable(DepDeclaringId));
                Assert.Equal(DepTargetId, bare);
                Assert.Equal(DepTargetId, scoped?.TableId);
            });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int InvokeBare(string name) => (int)RecordPatchesType
        .GetMethod("ResolveTableIdByName", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { name })!;

    private static IDictionary ParsedTables() => (IDictionary)RecordPatchesType
        .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static FieldInfo IndexField() =>
        RecordPatchesType.GetField("_bcSymbolTableIndex", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static ParsedTable SymbolTable(int id) =>
        ((Dictionary<int, (string AppPath, ParsedTable Table)>)IndexField().GetValue(null)!)[id].Table;

    private static void WithState(ParsedTable[] parsed, (string App, ParsedTable Table)[] index, Action act)
    {
        var ids = parsed.Select(t => t.TableId).Concat(index.Select(e => e.Table.TableId)).ToArray();
        var parsedTables = ParsedTables();
        var previousIndex = IndexField().GetValue(null);
        foreach (var id in ids)
            Assert.False(parsedTables.Contains(id), $"table id {id} is already in _parsedTables; pick another range");
        try
        {
            foreach (var t in parsed) parsedTables[t.TableId] = t;
            var staged = new Dictionary<int, (string AppPath, ParsedTable Table)>();
            foreach (var (app, t) in index) staged[t.TableId] = (app, t);
            IndexField().SetValue(null, staged);
            act();
        }
        finally
        {
            // ResolveTableIdByName FAULTS resolved symbol tables into _parsedTables, so the
            // index ids must be removed too — not only the ones staged there.
            foreach (var id in ids) parsedTables.Remove(id);
            IndexField().SetValue(null, previousIndex);
        }
    }
}
