// NoSourceColumnCacheTornPairTests — the no-source-column guard's cache may not be able to
// answer for a table it was never told about.
//
// RecordPatches.NavRecord_NoSourceColumnGuardForRead is prepended to
// NavRecord.GetFieldValueSafe for EVERY table in the process, and it memoises its last
// lookup so the common case (a table with no columns the runner lacks a source for) costs a
// reference compare. That memo used to be TWO plain static fields — the metatable, and its
// resolved fieldNo → column-name map — published with two separate stores, under a comment
// claiming that "a torn pair can only ever cost a redundant lookup … never a wrong answer".
//
// The comment was false in both directions, and this file is the demonstration. It is split
// into the three separate claims because they need different kinds of evidence:
//
//   1. CONSEQUENCE (deterministic). Given a torn pair, the reader produces a wrong answer —
//      either a silently skipped refusal or a refusal on a table that has nothing registered.
//      No threads: the torn state is constructed directly, so the assertion cannot flake.
//   2. REACHABILITY (threaded, skippable). Two threads publishing different tables actually
//      produce a torn pair. This one is timing-dependent, so a run that does not observe one
//      SKIPS rather than fails — a scheduler that never interleaves is not a defect in the
//      code under test, and a test that fails on a quiet machine would be worse than none.
//   3. IMMUNITY (threaded, asserted). The shipped cache — ONE field, holding only tables
//      resolved to "nothing registered" — never claims a table it was not told about has
//      nothing registered, however the publishing threads interleave.
//
// Claims 1 and 2 run against TwoFieldMemo below, a literal transcription of the code that was
// replaced; claim 3 runs against the real RecordPatches members. That split is deliberate and
// is the honest shape available here: the production reader takes a live NavRecord, which a
// unit test cannot build without a loaded bundle, so the reader is transcribed while the
// STATE it reads — the thing the fix changed — is exercised for real.
using System;
using System.Collections.Generic;
using System.Threading;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class NoSourceColumnCacheTornPairTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public NoSourceColumnCacheTornPairTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    // Object Metadata's registered no-source columns, by field number — the numbers are sparse
    // and Microsoft's (3, 6, 9, 15, 18, 27, 30, 33-37). Only the shape matters here.
    private static Dictionary<int, string> RegisteredMap() => new() { [3] = "Metadata", [9] = "Hash" };

    /// <summary>
    /// The cache exactly as it was before the fix: two fields, two stores, and a reader that
    /// trusts the pair. Transcribed rather than referenced because the shipped code no longer
    /// has this shape — that is the point of the fix.
    /// </summary>
    private sealed class TwoFieldMemo
    {
        internal object? LastMetaTable;
        internal Dictionary<int, string>? LastFields;

        internal void Publish(object metaTable, Dictionary<int, string>? fields)
        {
            LastFields = fields;      // store 1
            LastMetaTable = metaTable; // store 2 — nothing makes these two atomic together
        }

        /// <summary>The reader's verdict for one field read, as the guard would have decided it:
        /// the column name it would refuse, or null for "no refusal".</summary>
        internal string? RefusalOnFastPath(object metaTable, int fieldNo)
        {
            if (!ReferenceEquals(metaTable, LastMetaTable))
                throw new InvalidOperationException("not on the fast path — this helper only models the cached branch");
            var remembered = LastFields;
            if (remembered == null) return null;
            return remembered.TryGetValue(fieldNo, out var name) ? name : null;
        }
    }

    // ── 1. CONSEQUENCE: a torn pair produces a wrong answer, both ways ────────────────────

    [Fact]
    public void TwoFieldMemo_TornPairOnTheRegisteredTable_SilentlySkipsTheRefusal()
    {
        var registeredTable = new object();
        var memo = new TwoFieldMemo
        {
            // Interleaving: A stored its map, B (another table) overwrote the map with null,
            // then A stored its table. The pair now says "the registered table has nothing".
            LastFields = null,
            LastMetaTable = registeredTable,
        };

        Assert.Null(memo.RefusalOnFastPath(registeredTable, 3));
        Assert.Null(memo.RefusalOnFastPath(registeredTable, 9));

        // …and that is a WRONG answer, not a slow one: with the pair intact, both refuse.
        var intact = new TwoFieldMemo();
        intact.Publish(registeredTable, RegisteredMap());
        Assert.Equal("Metadata", intact.RefusalOnFastPath(registeredTable, 3));
        Assert.Equal("Hash", intact.RefusalOnFastPath(registeredTable, 9));
    }

    [Fact]
    public void TwoFieldMemo_TornPairOnAnUnrelatedTable_RefusesAColumnThatHasASource()
    {
        var unrelatedTable = new object();
        var memo = new TwoFieldMemo
        {
            // The mirror interleaving: B stored null, A stored the registered map, then B
            // stored its own table. An ordinary table now carries Object Metadata's map.
            LastFields = RegisteredMap(),
            LastMetaTable = unrelatedTable,
        };

        Assert.Equal("Metadata", memo.RefusalOnFastPath(unrelatedTable, 3));
        Assert.Equal("Hash", memo.RefusalOnFastPath(unrelatedTable, 9));

        // With the pair intact the same reads are silent, which is what makes the above a
        // spurious refusal rather than a difference of opinion.
        var intact = new TwoFieldMemo();
        intact.Publish(unrelatedTable, null);
        Assert.Null(intact.RefusalOnFastPath(unrelatedTable, 3));
        Assert.Null(intact.RefusalOnFastPath(unrelatedTable, 9));
    }

    // ── 2. REACHABILITY: two publishers really do tear the pair ───────────────────────────

    [SkippableFact]
    public void TwoFieldMemo_ConcurrentPublication_ProducesATornPair()
    {
        var registeredTable = new object();
        var otherTable = new object();
        var map = RegisteredMap();
        var memo = new TwoFieldMemo();

        int skippedRefusal = 0;      // (registered table, null map)
        int spuriousRefusal = 0;     // (other table, registered map)
        var stop = new ManualResetEventSlim(false);

        var publishers = new[]
        {
            new Thread(() => { while (!stop.IsSet) memo.Publish(registeredTable, map); }),
            new Thread(() => { while (!stop.IsSet) memo.Publish(otherTable, null); }),
        };
        var observer = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                // Read the pair the way the guard did: table first, then the map.
                var table = memo.LastMetaTable;
                var fields = memo.LastFields;
                if (ReferenceEquals(table, registeredTable) && fields == null) Interlocked.Increment(ref skippedRefusal);
                else if (ReferenceEquals(table, otherTable) && fields != null) Interlocked.Increment(ref spuriousRefusal);
            }
        });

        foreach (var t in publishers) { t.IsBackground = true; t.Start(); }
        observer.IsBackground = true;
        observer.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && (Volatile.Read(ref skippedRefusal) == 0 || Volatile.Read(ref spuriousRefusal) == 0))
            Thread.Sleep(5);

        stop.Set();
        foreach (var t in publishers) t.Join(TimeSpan.FromSeconds(5));
        observer.Join(TimeSpan.FromSeconds(5));

        _out.WriteLine($"torn pairs observed: skipped-refusal={skippedRefusal}, spurious-refusal={spuriousRefusal}");

        // Not an assertion: whether a scheduler interleaves inside the window is a
        // property of the machine, and claim 1 above already pins the consequence.
        Skip.If(skippedRefusal == 0 && spuriousRefusal == 0,
            "no torn pair observed in 5s on this machine — the consequence is pinned deterministically above");

        Assert.True(skippedRefusal > 0 || spuriousRefusal > 0);
    }

    // ── 3. IMMUNITY: the shipped one-field cache ──────────────────────────────────────────

    [Fact]
    public void ShippedCache_NeverClaimsAnUnpublishedTableHasNoNoSourceColumns()
    {
        // Stands in for Object Metadata: a table the guard must ALWAYS resolve properly,
        // because it is never a legitimate occupant of the "nothing registered" slot.
        var registeredTable = new object();

        // Sanity, before any concurrency: a table nobody published is not known.
        Assert.False(RecordPatches.IsKnownToHaveNoNoSourceColumns(registeredTable));

        int falsePositives = 0;
        var stop = new ManualResetEventSlim(false);

        // Four threads publishing four DIFFERENT unregistered tables as fast as they can —
        // the same traffic that tore the old pair.
        var publishers = new Thread[4];
        for (int i = 0; i < publishers.Length; i++)
        {
            var mine = new object();
            publishers[i] = new Thread(() =>
            {
                while (!stop.IsSet) RecordPatches.RememberHasNoNoSourceColumns(mine);
            })
            { IsBackground = true };
        }

        var observer = new Thread(() =>
        {
            while (!stop.IsSet)
                if (RecordPatches.IsKnownToHaveNoNoSourceColumns(registeredTable))
                    Interlocked.Increment(ref falsePositives);
        })
        { IsBackground = true };

        foreach (var t in publishers) t.Start();
        observer.Start();
        Thread.Sleep(300);
        stop.Set();
        foreach (var t in publishers) t.Join(TimeSpan.FromSeconds(5));
        observer.Join(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Volatile.Read(ref falsePositives));

        // And the positive direction, so this is not a test that would pass against a cache
        // that always says "unknown": a table that IS published reads back as known.
        var published = new object();
        RecordPatches.RememberHasNoNoSourceColumns(published);
        Assert.True(RecordPatches.IsKnownToHaveNoNoSourceColumns(published));
        Assert.False(RecordPatches.IsKnownToHaveNoNoSourceColumns(registeredTable));
    }
}
