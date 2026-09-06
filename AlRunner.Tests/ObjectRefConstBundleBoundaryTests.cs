// ObjectRefConstBundleBoundaryTests — issue #3207.
//
// WHAT WAS UNPINNED
// -----------------
// #3205 added RecordPatches._objectRefConstIds, a process-global memo of successful
// (kind, name) -> object-id resolutions for AL's `const(Database::X)` / `const(Report::X)`
// syntax. Nothing cleared it. ResetForReload — the per-bundle reload path a --server /
// --watch process runs between bundles — clears _parsedTables, _metaTableCache and the
// registered .app set (ClearPerBundleBcAppPaths, #2755) and invalidates _bcSymbolTableIndex
// (#2478), which are precisely the three inputs this memo's answer is derived from. So the
// memo outlived every input it was computed from and DEFEATED an invalidation its own
// dependency performs: bundle 2 declaring the same object name at a different id kept
// getting bundle 1's id, and the where() condition was pinned to the wrong rows while every
// other index correctly described bundle 2.
//
// Silent, and plausible-looking: a FlowField whose condition names the wrong table id sums
// a real number over the wrong rows. That is the failure #3205's own doc comment says it is
// avoiding ("a silently wrong id would pin the condition to the wrong rows"), and it is the
// third defect of this exact shape in this exact reset path — #2478 (an index reset that did
// not reset enough) and #2755 (a registered set that was not cleared) were the first two.
//
// WHY A TEST OVER TWO BUNDLES, NOT ONE
// ------------------------------------
// The defect is unreachable from the corpus and from every CI leg, because a leg is one
// single-bundle CLI invocation and a memo is only wrong across a bundle boundary. A test
// asserting over one bundle passes identically before and after the fix and proves nothing:
// ObjectReferenceConstResolutionTests' ResolvesRepeatedly case is exactly that, and it stays
// green on the broken build. So the boundary is constructed deliberately here — register,
// resolve, ResetForReload, register a DIFFERENT package declaring the same names at
// different ids, resolve again — which is what Program.cs does between two bundles in one
// server process (reset at 4049, re-register at 4533/4534, the reset always first).
//
// Two directions, because clearing the memo has to be right in both:
//   * A name that moves id must answer with the NEW id (the reported defect).
//   * A name that bundle 2 does not declare at all must go back to being unresolvable and
//     come back as the text as written — loud-failures.md's answer, and the one a memo
//     holding a phantom id would silently replace with a plausible number.
//
// WHY RUNNER-LOCAL AND NOT UPSTREAM
// ---------------------------------
// Nothing here is a claim about Business Central. The subject is the lifetime of a runner
// cache across the runner's own bundle-reload boundary — a concept BC does not have. The
// BC-behaviour half of #3195 (what a const(Database::X) resolves to) is asked upstream in
// StefanMaron/BusinessCentral.AL.Language.Tests, codeunit 60329.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: this registers symbol .apps into RecordPatches' process-global _bcAppPaths
// and calls ResetForReload(), which clears roughly twenty static dictionaries.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ObjectRefConstBundleBoundaryTests : IDisposable
{
    private readonly string _root;

    public ObjectRefConstBundleBoundaryTests()
    {
        _root = TestScratch.Dir("al-runner-objref-const-boundary");
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
    }

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // One .app declaring a table and a report under names that are STABLE across bundles while
    // their ids are not — the shape a --server process meets when the same object is rebuilt at
    // a different id, or when a second workspace reuses a name. `alsoDeclareRemoved` carries an
    // object that exists in bundle 1 and not in bundle 2, so the miss direction is testable.
    // no-base-app-in-csharp-tests.md: a bare SymbolReference package, no application floor.
    private string WriteSymbolApp(string fileName, int tableId, int reportId, bool alsoDeclareRemoved)
    {
        // Built with an explicit JSON writer rather than a raw string literal: the fixture
        // differs between the two bundles only in the ids and in whether the third table is
        // present, and that is the whole point of it.
        var tables = new List<object>
        {
            new
            {
                Id = tableId,
                Name = "ORB Shared Table",
                Fields = new object[] { new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } } },
            },
        };
        if (alsoDeclareRemoved)
            tables.Add(new
            {
                Id = 70899,
                Name = "ORB Bundle One Only",
                Fields = new object[] { new { Id = 1, Name = "Entry No.", TypeDefinition = new { Name = "Integer" } } },
            });

        var symbolReference = System.Text.Json.JsonSerializer.Serialize(new
        {
            AppId = Guid.NewGuid().ToString(),
            Name = "ORB Boundary Fixture",
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = tables,
            Reports = new object[] { new { Id = reportId, Name = "ORB Shared Report" } },
            Codeunits = Array.Empty<object>(),
            Pages = Array.Empty<object>(),
            Queries = Array.Empty<object>(),
            XmlPorts = Array.Empty<object>(),
            EnumTypes = Array.Empty<object>(),
        });

        var appPath = Path.Combine(_root, fileName);
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
            w.Write(symbolReference);
        return appPath;
    }

    [Fact]
    public void AResolutionMemoisedInBundleOne_DoesNotSurviveTheReloadIntoBundleTwo()
    {
        // Bundle 1.
        RecordPatches.AddBcAppPath(WriteSymbolApp("orb-bundle-1.app", 70801, 70811, alsoDeclareRemoved: true));

        // Asserted, not assumed: if registration silently did not happen, every assertion below
        // would be satisfied by "unresolvable both times" and the test would prove nothing.
        Assert.Equal("70801", RecordPatches.ResolveObjectReferenceConst("Database::\"ORB Shared Table\""));
        Assert.Equal("70811", RecordPatches.ResolveObjectReferenceConst("Report::\"ORB Shared Report\""));
        Assert.Equal("70899", RecordPatches.ResolveObjectReferenceConst("Database::\"ORB Bundle One Only\""));

        // The bundle boundary itself.
        RecordPatches.ResetForReload();

        // Bundle 2: same names, different ids, and one object bundle 1 had that this one drops.
        RecordPatches.AddBcAppPath(WriteSymbolApp("orb-bundle-2.app", 70802, 70812, alsoDeclareRemoved: false));

        // The reported defect. On the broken build these answer 70801 / 70811 — bundle 1's ids,
        // held by a memo whose every input ResetForReload has just discarded.
        Assert.Equal("70802", RecordPatches.ResolveObjectReferenceConst("Database::\"ORB Shared Table\""));
        Assert.Equal("70812", RecordPatches.ResolveObjectReferenceConst("Report::\"ORB Shared Report\""));

        // The other direction: an object bundle 2 does not declare is unresolvable again, so the
        // text comes back as written for BC's own evaluator to refuse by name. A memo holding
        // 70899 would instead pin the condition to a table this bundle has never heard of.
        Assert.Equal("Database::\"ORB Bundle One Only\"",
            RecordPatches.ResolveObjectReferenceConst("Database::\"ORB Bundle One Only\""));
    }

    [Fact]
    public void TheMemoStillMemoises_WithinOneBundle_AndIsEmptiedByTheReload()
    {
        // The control that stops the fix from being "clear it so often it never caches". #3205's
        // reason for the memo stands INSIDE a bundle: every table field carrying a Database::
        // const re-resolves the same name, and the id genuinely cannot change without a reload.
        RecordPatches.AddBcAppPath(WriteSymbolApp("orb-bundle-solo.app", 70821, 70831, alsoDeclareRemoved: false));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("70821", RecordPatches.ResolveObjectReferenceConst("Database::\"ORB Shared Table\""));
            Assert.Equal("70831", RecordPatches.ResolveObjectReferenceConst("Report::\"ORB Shared Report\""));
        }

        // Asserted against the memo itself, because "the same answer three times" is equally
        // satisfied by no memo at all. The key is (kind, lower-cased name), so a cache keyed
        // carelessly on the name alone would also show up here as one entry instead of two.
        var memo = Memo();
        Assert.True(memo.Contains(("Table", "orb shared table")),
            "the Table resolution was not memoised — #3205's caching is what this PR must keep");
        Assert.True(memo.Contains(("Report", "orb shared report")),
            "the Report resolution was not memoised — #3205's caching is what this PR must keep");

        // And the reload empties it. This is the fix stated directly: on the broken build the
        // two entries above are still there afterwards, keyed on names whose every backing index
        // ResetForReload has just discarded.
        RecordPatches.ResetForReload();
        Assert.Empty((System.Collections.IEnumerable)Memo());
    }

    /// <summary>The memo itself. Read by reflection on purpose: no public surface reports it,
    /// and inferring "it was cleared" from a later lookup cannot tell a cleared memo from one
    /// that happens to agree with the current bundle.</summary>
    private static System.Collections.IDictionary Memo()
    {
        var field = typeof(RecordPatches).GetField("_objectRefConstIds",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "RecordPatches._objectRefConstIds not found — this test tracks that field.");
        return (System.Collections.IDictionary)field.GetValue(null)!;
    }
}
