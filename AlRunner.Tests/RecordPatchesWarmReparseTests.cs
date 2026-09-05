// RecordPatchesWarmReparseTests — issue #2588.
//
// Under --watch, every save re-enters the per-bundle loop, which calls
// BcRuntime.ResetForNewBundleReload -> RecordPatches.ResetForReload. That empties _sourceDirs
// and every parsed dictionary, so the AddSourceDirs immediately after re-reads and re-parses
// the WHOLE tree to service an edit to one file.
//
// These pin a COUNT — RecordPatches.ParseObjectTextCallCount, the number of real
// SyntaxTree.ParseObjectText builds — never a duration. A duration test on this box would be
// worthless: identical work has measured 1.9 s and 3.1 s with other agents running. The count
// is exact and machine-independent, and it is what the fix is actually about.
//
// Mode: this is the --watch/--server warm-reload path, driven at the RecordPatches level
// rather than through a real watcher, because the claim is about ResetForReload +
// AddSourceDirs and nothing about the file-watching mechanism.
//
// BcEngineCollection, not RecordPatchesSerialCollection: a class carries one [Collection], and
// AddSourceDirs only parses once Register() has run, which needs the in-process engine.
// ParserStaticsIsolationGuardTests explicitly admits BcEngineCollection for exactly this case
// (#2543), so calling ResetForReload here is sanctioned rather than evaded.
//
// Credit: the approach, the retraction problem and both implementation details are
// Mikkel Mansa Vilhelmsen's (@vhn) findings, from vhn/main commit af4157c5. Not copied.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordPatchesWarmReparseTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public RecordPatchesWarmReparseTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-warm-reparse-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string TableText(int tableId, string label, string extraFieldName = "Filler") => $$"""
        table {{tableId}} "Warm Reparse {{label}}"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "{{extraFieldName}}"; Text[30]) { }
            }
            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """;

    private string WriteTableDir(int tableId, string label)
    {
        var dir = Path.Combine(_root, label);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{label}.al"), TableText(tableId, label));
        return dir;
    }

    private void AssertTablesResolve(params int[] ids)
    {
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);
        foreach (var id in ids)
            Assert.Equal(id, RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, id, false, 0).TableId);
    }

    [SkippableFact]
    public void WarmReload_WithNothingChanged_ReparsesNothing()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ids = new[] { 93760, 93761, 93762, 93763, 93764 };
        var dirs = new[]
        {
            WriteTableDir(ids[0], "W0"), WriteTableDir(ids[1], "W1"), WriteTableDir(ids[2], "W2"),
            WriteTableDir(ids[3], "W3"), WriteTableDir(ids[4], "W4"),
        };

        // ── Cold: one real parse per file, the #1903 baseline. ──
        var beforeCold = RecordPatches.ParseObjectTextCallCount;
        RecordPatches.AddSourceDirs(dirs);
        Assert.Equal(5, RecordPatches.ParseObjectTextCallCount - beforeCold);
        AssertTablesResolve(ids);

        // ── Warm: exactly what a --watch save does, with no file touched. ──
        RecordPatches.ResetForReload();
        var beforeWarm = RecordPatches.ParseObjectTextCallCount;
        RecordPatches.AddSourceDirs(dirs);
        var warmParses = RecordPatches.ParseObjectTextCallCount - beforeWarm;

        Assert.Equal(0, warmParses);

        // The state must be rebuilt in full regardless. Without this, "skip the stage
        // entirely" would satisfy the count assertion above while serving nothing.
        AssertTablesResolve(ids);
    }

    [SkippableFact]
    public void SameTextUnderDifferentPreprocessorSymbols_IsNotServedFromTheCache()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The #1900 regression this cache could reintroduce through a different door. The
        // parse is a pure function of (text, symbols), so a cache keyed on content ALONE
        // would serve one --define set's tree to another. Identical bytes, two symbol sets,
        // must cost two real parses.
        var dir = WriteTableDir(93780, "S0");
        var dirs = new[] { dir };
        var originalSymbols = AlRunner.BcCompiler.GetExtraPreprocessorSymbols().ToList();
        try
        {
            AlRunner.BcCompiler.SetExtraPreprocessorSymbols(new[] { "WARM_REPARSE_A" });
            var before = RecordPatches.ParseObjectTextCallCount;
            RecordPatches.AddSourceDirs(dirs);
            Assert.Equal(1, RecordPatches.ParseObjectTextCallCount - before);

            // Same directory, same bytes, DIFFERENT symbols: a real parse, not a hit.
            RecordPatches.ResetForReload();
            AlRunner.BcCompiler.SetExtraPreprocessorSymbols(new[] { "WARM_REPARSE_B" });
            var beforeB = RecordPatches.ParseObjectTextCallCount;
            RecordPatches.AddSourceDirs(dirs);
            Assert.Equal(1, RecordPatches.ParseObjectTextCallCount - beforeB);

            // Back to the FIRST symbol set: now it is a hit, which proves the key really is
            // the symbols and not "every second call misses".
            RecordPatches.ResetForReload();
            AlRunner.BcCompiler.SetExtraPreprocessorSymbols(new[] { "WARM_REPARSE_A" });
            var beforeAgain = RecordPatches.ParseObjectTextCallCount;
            RecordPatches.AddSourceDirs(dirs);
            Assert.Equal(0, RecordPatches.ParseObjectTextCallCount - beforeAgain);
        }
        finally
        {
            AlRunner.BcCompiler.SetExtraPreprocessorSymbols(originalSymbols);
        }
    }

    [SkippableFact]
    public void WarmReload_WithOneFileEdited_ReparsesOnlyThatFile()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ids = new[] { 93770, 93771, 93772, 93773, 93774 };
        var dirs = new[]
        {
            WriteTableDir(ids[0], "E0"), WriteTableDir(ids[1], "E1"), WriteTableDir(ids[2], "E2"),
            WriteTableDir(ids[3], "E3"), WriteTableDir(ids[4], "E4"),
        };

        RecordPatches.AddSourceDirs(dirs);
        AssertTablesResolve(ids);

        // Edit exactly one file — the one-object delta a save actually produces.
        File.WriteAllText(Path.Combine(dirs[2], "E2.al"), TableText(ids[2], "E2", "Renamed"));

        RecordPatches.ResetForReload();
        var before = RecordPatches.ParseObjectTextCallCount;
        RecordPatches.AddSourceDirs(dirs);
        var parses = RecordPatches.ParseObjectTextCallCount - before;

        // One file moved, so one real parse. Asserting the exact number rather than "fewer
        // than five" is what makes this catch a memo that quietly stops hitting.
        Assert.Equal(1, parses);

        // And the edit is actually visible: the memo must not serve the pre-edit tree.
        AssertTablesResolve(ids);
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        var edited = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, ids[2], false, 0);
        Assert.Contains(edited.Fields.Cast<object>(), f => f.ToString()!.Contains("Renamed", StringComparison.Ordinal));
    }
}
