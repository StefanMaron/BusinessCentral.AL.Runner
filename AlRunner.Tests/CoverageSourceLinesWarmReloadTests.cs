// CoverageSourceLinesWarmReloadTests — #4572 review: the text CodeCoveragePatches serves in place
// of BC's "User AL Code" must follow an edit across a --watch / --server reload. The source map
// behind it is memoised for the process, so a line-shifting edit re-read through the previous
// cycle's spans puts every Code Coverage (2000000049) line row on the wrong line — correct cold,
// wrong warm (local-test-scope.md).
//
// Driven through the reload path the runner uses: RecordPatches.ResetForReload (reached from
// BcRuntime.ResetForNewBundleReload) followed by AddSourceDirs over the same directory.
// BcEngineCollection, because AddSourceDirs parses with BC's parser in-process; the class calls
// ResetForReload, which ParserStaticsIsolationGuardTests admits for this collection.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CoverageSourceLinesWarmReloadTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;
    private readonly string _file;

    public CoverageSourceLinesWarmReloadTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-coverage-source-lines-warm-reload");
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "Warm.Codeunit.al");
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static readonly string[] ObjectLines =
    {
        "codeunit 63660 \"Cov Warm Reload\"",
        "{",
        "    procedure A(): Integer",
        "    begin",
        "        exit(1);",
        "    end;",
        "}",
    };

    private static readonly string[] Cold = new[] { "namespace Cov.Warm;", "", "using System.Utilities;", "" }.Concat(ObjectLines).ToArray();

    // One more preamble line: every line of the object moves down by one.
    private static readonly string[] Warm = new[] { "namespace Cov.Warm;", "", "using System.Utilities;", "using System.Text;", "" }.Concat(ObjectLines).ToArray();

    private void Write(string[] lines, DateTime lastWriteUtc)
    {
        File.WriteAllText(_file, string.Join("\n", lines) + "\n");
        // Pinned, so the edit is a different stamp even on a filesystem with coarse timestamps.
        File.SetLastWriteTimeUtc(_file, lastWriteUtc);
    }

    private static IReadOnlyList<string> Served()
        => CodeCoveragePatches.SourceCodeLinesFor(new { ObjectType = "Codeunit", ObjectNumber = 63660 }).SourceCodeLines;

    [SkippableFact]
    public void WarmReload_AfterALineShiftingEdit_ServesTheEditedText()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        Write(Cold, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        RecordPatches.AddSourceDirs(new[] { _root });
        Assert.Equal(Cold, Served());

        // What a --watch save / a --server request for the edited bundle does.
        Write(Warm, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));
        RecordPatches.ResetForReload();
        RecordPatches.AddSourceDirs(new[] { _root });

        Assert.Equal(Warm, Served());
    }

    [SkippableFact]
    public void FileEditedWithoutAReparse_RefusesRatherThanServingShiftedLines()
    {
        RequireEngine();
        RecordPatches.ResetForReload();
        Write(Cold, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        RecordPatches.AddSourceDirs(new[] { _root });
        Assert.Equal(Cold, Served());

        // Edited on disk, but nothing re-parsed it: the spans the run holds describe the old text.
        Write(Warm, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));

        var ex = Assert.Throws<InvalidOperationException>(() => Served());
        Assert.Contains("changed after the run parsed it", ex.Message);
    }
}
