// RadObjectMetadataSnapshotReloadTests — the fourth replay path for #3548's general
// (kind, id) object-metadata capture: the --watch / --server RAD shadow snapshot.
//
// Three of the four paths that keep AlObjectMetadataRegistry populated across a warm run
// are covered by ObjectMetadataCaptureTests (cold emit, bundle AL-output cache HIT,
// source-dependency cache HIT). This is the fourth, and it is the one where the runner
// actively DESTROYS the registry: BcRuntime.ResetForNewBundleReload() calls
// AlObjectMetadataRegistry.Clear() at the top of every reload cycle, and the per-module
// shadow snapshot on the BcCompiler instance is the only thing that puts back an object
// the next cycle's partial (or skipped) Emit never re-registers.
//
// So the Clear() is a regression on its own, and the snapshot is what makes it safe. That
// pairing is what this file pins, in both directions:
//
//   survives   Table 90410 / Page 90410 / Codeunit 90413 are never touched between the two
//              cycles, so a delta compile emits nothing for them — after the Clear they can
//              only come back from the snapshot.
//   vacated    Codeunit 90411's file is DELETED, so it must NOT be answerable afterwards.
//              This is the half the snapshot can get wrong in the other direction: replaying
//              a stale entry resurrects an object the bundle no longer declares.
//   changed    Table 90412 gains a field, so it must answer the NEW document, not the one
//              captured on cycle 1.
//
// Every assertion names (kind, id) and compares document CONTENT. A count would pass against
// a snapshot holding the wrong documents, and — because table 90410 and page 90410 share an
// id on purpose — so would anything keyed on the id alone.
//
// ── Why in-process rather than a spawned `--watch` subprocess ────────────────────────────
//
// #3548 steps 1 and 2 convert no consumer, so nothing reads this registry yet and there is
// no AL-observable behaviour a subprocess could assert on — only the
// AL_RUNNER_TRACE_OBJECT_METADATA trace text, which shows registrations rather than what the
// registry answers, and cannot express "this (kind, id) is NOT answerable". Driving the same
// two calls --watch drives (BcRuntime.ResetForNewBundleReload, then
// BcCompiler.TryEmitIncremental) reads the registry directly and states the claim exactly.
// Same reasoning, and the same shape, as RadQuerySymbolsSnapshotModuleScopeTests, which
// reaches BcCompiler's private RAD snapshot for the neighbouring #2939 property.
//
// The fast-path assertion below is load-bearing for that: if TryEmitIncremental returns null
// the caller falls back to a whole-module Emit, which repopulates the registry by itself and
// would make every assertion here pass with no snapshot at all.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RadObjectMetadataSnapshotReloadTests : IDisposable
{
    private const string Module = "RadObjMetaModule";

    // Table and page deliberately SHARE id 90410 — AL allows it, and it is what makes the
    // per-(kind, id) claim testable: a snapshot keyed on the id alone answers both with one
    // document and passes every positive assertion below.
    private const int SharedId = 90410;
    private const int VacatedCodeunitId = 90411;
    private const int ChangedTableId = 90412;
    private const int KeptCodeunitId = 90413;

    // Only ever present in the post-edit table, so "answered the new document" cannot be
    // satisfied by the cycle-1 one.
    private const string AddedFieldMarker = "ZebraMarkerBeta";

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public RadObjectMetadataSnapshotReloadTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-rad-objmeta-reload");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Process-global state this test writes into. The collection is serial, but leaving a
        // scratch bundle's objects registered would still be visible to whatever runs next.
        try { AlObjectMetadataRegistry.Clear(); } catch { /* best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // The RAD fast path requires exactly one object per file, so every object gets its own.
    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private const string RowTable = """
        table 90410 "Rad Obj Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    private const string RowPage = """
        page 90410 "Rad Obj Page"
        {
            PageType = Card;
            SourceTable = "Rad Obj Row";
            layout
            {
                area(Content)
                {
                    group(General) { field("Entry No."; Rec."Entry No.") { ApplicationArea = All; } }
                }
            }
        }
        """;

    /// <summary>The object whose file is deleted on cycle 2. Referenced by nothing, so its
    /// removal is a clean vacate rather than a delta-compile error.</summary>
    private const string GoneCodeunit = """
        codeunit 90411 "Rad Obj Gone"
        {
            procedure Vanishes(): Integer
            begin
                exit(11);
            end;
        }
        """;

    /// <summary>The control for the vacated assertion: a codeunit that is NOT deleted. Without
    /// it, "Codeunit 90411 does not answer" is also satisfied by a snapshot that drops every
    /// codeunit, or captures none in the first place.</summary>
    private const string KeptCodeunit = """
        codeunit 90413 "Rad Obj Keep"
        {
            procedure Stays(): Integer
            begin
                exit(13);
            end;
        }
        """;

    private const string ChangedTableBefore = """
        table 90412 "Rad Obj Changed"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    private const string ChangedTableAfter = """
        table 90412 "Rad Obj Changed"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; ZebraMarkerBeta; Text[30]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """;

    private static string Doc(string kind, int id)
    {
        Assert.True(
            AlObjectMetadataRegistry.TryGet(kind, id, out var xml),
            $"no metadata document is answerable for {kind} {id}.");
        return xml;
    }

    /// <summary>
    /// One reload cycle, exactly as --watch runs it: Emit + baseline, then
    /// ResetForNewBundleReload, then TryEmitIncremental. Asserts the registry really is empty
    /// between the two — the whole point of the snapshot is that it survives a Clear() that
    /// actually happened, and every later assertion is vacuous if it did not.
    /// </summary>
    [SkippableFact]
    public void RadReload_KeepsEveryUntouchedObjectsDocumentPerKindAndId()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Row.Table.al", RowTable);
        WriteAl("Row.Page.al", RowPage);
        WriteAl("Gone.Codeunit.al", GoneCodeunit);
        WriteAl("Keep.Codeunit.al", KeptCodeunit);
        WriteAl("Changed.Table.al", ChangedTableBefore);

        // ── cycle 1: the cold compile that populates the registry and the shadow snapshot ──
        var compiler = new BcCompiler();
        var cold = compiler.Emit(new[] { _root }, Module, trackIncrementalBaseline: true);
        Assert.Empty(cold.Diagnostics);

        var tableDocBefore = Doc("Table", SharedId);
        var pageDocBefore = Doc("Page", SharedId);
        var keptDocBefore = Doc("Codeunit", KeptCodeunitId);
        var changedDocBefore = Doc("Table", ChangedTableId);
        Assert.True(AlObjectMetadataRegistry.TryGet("Codeunit", VacatedCodeunitId, out _),
            $"Codeunit {VacatedCodeunitId} must be captured on cycle 1 — otherwise 'it is gone after "
            + "cycle 2' is satisfied by it never having been there.");

        // Fixture guard: table 90410 and page 90410 really are two entries carrying different
        // documents. Everything per-(kind, id) below rests on this.
        Assert.NotEqual(tableDocBefore, pageDocBefore);
        Assert.Contains("Rad Obj Row", tableDocBefore, StringComparison.Ordinal);
        Assert.Contains("Rad Obj Page", pageDocBefore, StringComparison.Ordinal);
        Assert.DoesNotContain(AddedFieldMarker, changedDocBefore, StringComparison.Ordinal);

        // ── the edit: one object vacated, one changed, three untouched ──
        File.Delete(Path.Combine(_root, "Gone.Codeunit.al"));
        WriteAl("Changed.Table.al", ChangedTableAfter);

        // ── cycle 2, first half: the reload's own Clear() ──
        BcRuntime.ResetForNewBundleReload();
        Assert.False(AlObjectMetadataRegistry.TryGet("Table", SharedId, out _),
            "the reload did not clear AlObjectMetadataRegistry, so nothing below measures the shadow "
            + "snapshot: every document would still be there because it was never removed.");
        Assert.Equal(0, AlObjectMetadataRegistry.Count);

        // ── cycle 2, second half: the RAD delta ──
        var delta = compiler.TryEmitIncremental(new[] { _root }, Module, appRootDir: null, out var fallbackReason);
        Assert.True(delta != null,
            "the incremental path fell back to a whole-module Emit, which repopulates the registry by "
            + "itself — so this test would pass with no shadow snapshot at all and prove nothing. "
            + $"fallbackReason: {fallbackReason}");

        // 1. Untouched objects. A delta compile emits nothing for these, so after the Clear the
        //    snapshot replay is the only thing that can answer them — and it must answer with the
        //    SAME document BC produced on cycle 1, not merely with something.
        Assert.Equal(tableDocBefore, Doc("Table", SharedId));
        Assert.Equal(pageDocBefore, Doc("Page", SharedId));
        Assert.Equal(keptDocBefore, Doc("Codeunit", KeptCodeunitId));

        // 2. Per (kind, id), not per id: the two 90410 entries must still be distinct after the
        //    round trip through the snapshot.
        Assert.NotEqual(Doc("Table", SharedId), Doc("Page", SharedId));

        // 3. The destructive half. Codeunit 90411's file is gone, so the snapshot must have
        //    dropped it — replaying it would resurrect an object the bundle no longer declares.
        Assert.False(AlObjectMetadataRegistry.TryGet("Codeunit", VacatedCodeunitId, out var resurrected),
            $"Codeunit {VacatedCodeunitId}'s source file was deleted before this cycle, but the RAD "
            + "shadow snapshot still answered for it — UpdateRadMetadataSnapshotDelta did not drop the "
            + $"vacated identity, so the replay put a retired object back. Document ({resurrected.Length} "
            + "chars) begins: " + resurrected[..Math.Min(200, resurrected.Length)]);

        // 4. The changed object answers the NEW document. Without this a snapshot that replayed
        //    cycle 1 verbatim over the delta's own fresh registration would look correct.
        var changedDocAfter = Doc("Table", ChangedTableId);
        Assert.Contains(AddedFieldMarker, changedDocAfter, StringComparison.Ordinal);
        Assert.NotEqual(changedDocBefore, changedDocAfter);

        // 5. Nothing was invented: a kind this bundle never declares under a known id, and an id
        //    it never declares at all, must both stay unanswerable.
        Assert.False(AlObjectMetadataRegistry.TryGet("TableExtension", SharedId, out _),
            $"{SharedId} is a table and a page here, and no tableextension — a registry answering any "
            + "kind for a known id passes every positive assertion above.");
        Assert.False(AlObjectMetadataRegistry.TryGet("Table", 90419, out _));
    }

    /// <summary>
    /// The zero-work cycle: --watch re-runs with no edit at all (a save that changed no bytes,
    /// or a sibling bundle's edit). TryEmitIncremental returns the previous output verbatim
    /// without calling Emit anywhere, so EVERY document in the registry comes from the replay —
    /// which makes this the sharpest statement of the property, and the cheapest way for the
    /// snapshot to be silently missing.
    /// </summary>
    [SkippableFact]
    public void RadReload_WithNoEditAtAll_StillAnswersEveryObjectItAnsweredBefore()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Row.Table.al", RowTable);
        WriteAl("Row.Page.al", RowPage);
        WriteAl("Keep.Codeunit.al", KeptCodeunit);

        var compiler = new BcCompiler();
        Assert.Empty(compiler.Emit(new[] { _root }, Module, trackIncrementalBaseline: true).Diagnostics);

        var before = AlObjectMetadataRegistry.Snapshot()
            .ToDictionary(e => AlObjectMetadataRegistry.KeyFor(e.Kind, e.Id, e.Name), e => e.Xml, StringComparer.Ordinal);
        Assert.Equal(3, before.Count);

        BcRuntime.ResetForNewBundleReload();
        Assert.Equal(0, AlObjectMetadataRegistry.Count);

        var delta = compiler.TryEmitIncremental(new[] { _root }, Module, appRootDir: null, out var fallbackReason);
        Assert.True(delta != null, $"a no-edit cycle must take the fast path. fallbackReason: {fallbackReason}");

        var after = AlObjectMetadataRegistry.Snapshot()
            .ToDictionary(e => AlObjectMetadataRegistry.KeyFor(e.Kind, e.Id, e.Name), e => e.Xml, StringComparer.Ordinal);

        // Key set AND document per key. Comparing the dictionaries rather than the counts is
        // what stops a snapshot that replays three of the wrong documents from passing.
        Assert.Equal(
            before.Keys.OrderBy(k => k, StringComparer.Ordinal),
            after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (key, xml) in before)
            Assert.Equal(xml, after[key]);
    }
}
