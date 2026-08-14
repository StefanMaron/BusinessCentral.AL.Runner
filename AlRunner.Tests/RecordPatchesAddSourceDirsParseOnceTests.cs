// RecordPatchesAddSourceDirsParseOnceTests — integration-level companion to
// RecordPatchesParseOnceTests.cs for #1903.
//
// Pins the "N registered files cost N real parses, not 8N" claim at the actual public entry
// point #1903 names, RecordPatches.AddSourceDirs, through the real BC engine bootstrap — the
// same engine-backed style RecordPatchesSourceDirBatchTests used for #1833's batching fix.
// Uses only the public AddSourceDirs API and RecordPatches.ParseObjectTextCallCount (internal,
// via InternalsVisibleTo — not reflection), so it needs no reflection-by-name access into the
// parse statics and does not join RecordPatchesSerialCollection.
//
// Deliberately kept in ITS OWN FILE, separate from RecordPatchesParseOnceTests.cs:
// ParserStaticsIsolationGuardTests scans whole FILE text for "TryParse…File" / "_parsed…"
// reflection-by-name markers and requires every class declared in a flagged file to join
// RecordPatchesSerialCollection. This class needs BcEngineCollection instead (for
// BcEngineFixture, which bootstraps the in-process BC engine), so sharing a file with the
// reflection-driven tests would either force a collection this class doesn't want or trip the
// guard.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordPatchesAddSourceDirsParseOnceTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public RecordPatchesAddSourceDirsParseOnceTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-parse-once-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteTableDir(int tableId, string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.al"), $$"""
            table {{tableId}} "Parse Once Dir {{name}}"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);
        return dir;
    }

    [SkippableFact]
    public void AddSourceDirs_ParsesEachRegisteredFileOnce_NotEightTimes()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var dirs = new[]
        {
            WriteTableDir(93750, "A"),
            WriteTableDir(93751, "B"),
            WriteTableDir(93752, "C"),
            WriteTableDir(93753, "D"),
            WriteTableDir(93754, "E"),
        };

        var before = RecordPatches.ParseObjectTextCallCount;
        RecordPatches.AddSourceDirs(dirs);
        var after = RecordPatches.ParseObjectTextCallCount;

        // 5 files, run through all eight extractors each: before #1903 this cost 40 real
        // parses (8 per file). The fix brings it to 5 — one real parse per file.
        Assert.Equal(5, after - before);

        // Content: every file's table is genuinely resolvable — proves the reduced count is
        // real memoization, not a bug that silently skipped extracting some of the files.
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);
        foreach (var id in new[] { 93750, 93751, 93752, 93753, 93754 })
        {
            var table = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, id, false, 0);
            Assert.Equal(id, table.TableId);
        }
    }
}
