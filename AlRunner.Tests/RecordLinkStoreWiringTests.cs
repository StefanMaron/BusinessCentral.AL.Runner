// RecordLinkStoreWiringTests — the runner-side half of issue #3378.
//
// What BC does — a row written straight into `Record "Record Link"` is visible to
// `HasLinks`, `AddLink` is visible in the table, `Record Link Management.CopyLinks` copies
// rows a test can read back — is a claim about BC, and is proven upstream by corpus codeunit
// `Test Record Link Table` (60777) against a real service tier. None of that is asserted here.
//
// What IS runner-specific, and is what these pin:
//
//   1. There is exactly ONE link store. The defect was two: the Record Link table's own rows
//      on one side, and a private dictionary in the patch layer on the other, keyed by
//      `RuntimeHelpers.GetHashCode(record)` so it could not even answer for a second record
//      variable pointing at the same row. A reintroduced private dictionary is the shape that
//      must not come back, and it is invisible to any behavioural test that happens to touch
//      only one of the two surfaces.
//
//   2. The two write routes into that table draw Link IDs from ONE AutoIncrement counter.
//      The store writes rows straight into the TempTableDataProvider, bypassing
//      `NavRecord.ALInsertAsync`; #2289 records what happens when a provider-level writer keeps
//      its own numbering — the next AL `Insert(true)` reuses a primary key and BC's own
//      duplicate-key check fires. Measured on the 10-arm reproducer in #3378: three arms failed
//      exactly that way before the counter was shared.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordLinkStoreWiringTests
{
    private static Type BcRuntimeType =>
        typeof(AlRunner.Patches.RecordPatches).Assembly.GetType("AlRunner.BcRuntime")
        ?? throw new InvalidOperationException("AlRunner.BcRuntime not found");

    private static MethodInfo TakeAutoIncrement =>
        BcRuntimeType.GetMethod("TakeAutoIncrementValue",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "BcRuntime.TakeAutoIncrementValue not found — the Record Link store's Link IDs "
            + "would silently fall back to a private sequence (issue #3378 / #2289)");

    private static long Take(int tableId, int fieldNo)
        => (long)TakeAutoIncrement.Invoke(null, new object[] { tableId, fieldNo })!;

    // A table id far outside anything the runner materialises, so this test cannot collide
    // with a real table's counter no matter what else ran first in the same process.
    private const int ScratchTableId = 1_900_000_101;
    private const int OtherScratchTableId = 1_900_000_102;

    [Fact]
    public void TakeAutoIncrementValue_HandsOutStrictlyIncreasingDistinctValues()
    {
        var taken = new List<long>();
        for (var i = 0; i < 5; i++) taken.Add(Take(ScratchTableId, 1));

        Assert.Equal(5, taken.Distinct().Count());
        Assert.Equal(taken.OrderBy(x => x).ToList(), taken);
        // Positive, because BC's Link ID is an AutoIncrement primary key and AL tests
        // `LinkId > 0` for success.
        Assert.All(taken, v => Assert.True(v > 0, $"expected a positive Link ID, got {v}"));
    }

    [Fact]
    public void TakeAutoIncrementValue_KeepsSeparateTablesOnSeparateSequences()
    {
        var first = Take(OtherScratchTableId, 1);
        Take(ScratchTableId, 1);
        Take(ScratchTableId, 1);
        var second = Take(OtherScratchTableId, 1);

        // The other table's three draws must not have advanced this one's sequence.
        Assert.Equal(first + 1, second);
    }

    [Fact]
    public void TheLinkSurfaceHasExactlyOneStore_AndItIsTheRecordLinkTable()
    {
        var recordPatches = typeof(AlRunner.Patches.RecordPatches);

        // The store's entry points, all on RecordPatches, all reaching table 2000000068.
        foreach (var name in new[]
                 {
                     "RecordLinkStore_Add", "RecordLinkStore_HasLinks", "RecordLinkStore_DeleteAll",
                     "RecordLinkStore_DeleteOne", "RecordLinkStore_Copy", "RecordLinkStore_Move",
                     "RecordLinkStore_TableHasLinks",
                 })
            Assert.True(
                recordPatches.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) != null,
                $"RecordPatches.{name} is missing — the AL link surface has lost its table-backed store (#3378)");

        var tableId = recordPatches
            .GetField("RecordLinkTableId", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue();
        Assert.Equal(2000000068, tableId);
    }

    [Fact]
    public void NoTypeInTheRunnerKeepsAPrivateLinkDictionaryBesideTheTable()
    {
        // The #3378 shape: a static dictionary of link entries somewhere in the runner, which
        // the Record Link table never sees. It was two types at once — AlRunner.BcRuntime,
        // which owns the Cecil-rewritten RecordLink helpers, and AlRunner.Patches
        // .RecordLinkPatches, whose copy was registered through the JmpHook layer that is off
        // by default. #3380 removed the second, so this asserts across the WHOLE assembly
        // rather than on BcRuntime alone; the narrower scope only ever existed because
        // RecordLinkPatches was still there.
        var offenders = typeof(AlRunner.Patches.RecordPatches).Assembly
            .GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                              .Select(f => (Type: t, Field: f)))
            .Where(x => x.Field.Name.Contains("ink", StringComparison.Ordinal)
                        && x.Field.FieldType.IsGenericType
                        && (x.Field.FieldType.GetGenericTypeDefinition().Name.StartsWith("Dictionary", StringComparison.Ordinal)
                            || x.Field.FieldType.GetGenericTypeDefinition().Name.StartsWith("ConcurrentDictionary", StringComparison.Ordinal))
                        // The offending shape is keyed by the RECORD — a per-record list of
                        // links held outside the table. A dictionary merely containing "ink"
                        // in its name (hyperlink, linker, …) is not it.
                        && x.Field.FieldType.GetGenericArguments()[0].Name.Contains("RecordId", StringComparison.Ordinal))
            .Select(x => $"{x.Type.FullName}.{x.Field.Name} : {x.Field.FieldType.Name}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "a static record-keyed link dictionary is back beside the Record Link table "
            + "(#3378 / #3380): " + string.Join(", ", offenders));
    }
}
