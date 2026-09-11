// CoverageSourceMapScanFailureTests — #3847: AlCoverageSourceMap.Build could not say "I could
// not read the sources", so it said "those objects have no executable statements" instead.
//
// Two silent skips produced a map that is SUCCESSFULLY short:
//
//     if (!Directory.Exists(root)) continue;                        // a root that is not there
//     catch (IOException) { return Array.Empty<ParsedObject>(); }   // a file that cannot be read
//
// Neither is distinguishable from a bundle that genuinely declares nothing mappable — which is
// a legitimate state, so `Count == 0` cannot be the check either. Both consumers turn a
// missing entry into a confident negative: coverage reports the statements uncovered, and DAP
// answers `verified: false` with the message `no executable AL statement on this line in this
// file`, a specific claim about the AL made on the strength of a read that did not happen.
//
// `.claude/rules/guards-need-a-third-state.md` applied to a builder rather than a guard: the
// genuinely-absent case stays a pass, and only the UNMEASURABLE one becomes the third state.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CoverageSourceMapScanFailureTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public CoverageSourceMapScanFailureTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-sourcemap-scan-failure");
        Directory.CreateDirectory(_root);
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The constraint that keeps the fix from trading one defect for another: a root that
    /// exists and declares nothing mappable is a REAL state, and stays a clean pass. If this
    /// went red the fix would have swapped a false green for a false red.
    /// </summary>
    [SkippableFact]
    public void Build_ARootWithNothingMappable_ReportsNoFailures()
    {
        RequireEngine();
        // An interface carries no executable code, so it is legitimately absent from the map.
        File.WriteAllText(Path.Combine(_root, "Empty.Interface.al"), """
        interface "Cov Scan Nothing"
        {
            procedure Unused(): Integer
        }
        """);

        var map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: _root);

        Assert.Empty(map.ScanFailures);
        Assert.Equal(0, map.Count);
    }

    /// <summary>
    /// RED before the fix: <c>ScanFailures</c> does not exist, and the root is skipped by a
    /// bare <c>continue</c> that leaves nothing behind. A root the caller named and that is not
    /// there is not the same as a root with nothing in it — the caller asserted it exists.
    /// </summary>
    [Fact]
    public void Build_ARootThatIsNotThere_IsReportedRatherThanSkippedSilently()
    {
        // No RequireEngine: a root that is not there is never opened, so nothing parses AL.
        // The third state must still be provable on a box with no BC artifacts (#3884 review).
        var missing = Path.Combine(_root, "no-such-directory");

        var map = AlCoverageSourceMap.Build(new[] { _root, missing }, relativeTo: _root);

        var failure = Assert.Single(map.ScanFailures);
        Assert.Equal(missing, failure.Path);
        Assert.Contains("does not exist", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one that produces the wrong ANSWER rather than a short map: a file that is there and
    /// cannot be read parsed as "this file declares no objects", so its objects were missing
    /// from the map and every consumer blamed the AL.
    ///
    /// The file is locked with <see cref="FileShare.None"/> for the duration, which is how
    /// <c>File.ReadAllText</c> is made to throw deterministically rather than by permissions,
    /// which differ per platform and per CI user.
    /// </summary>
    [SkippableFact]
    public void Build_AnAlFileThatCannotBeRead_IsReportedRatherThanReadingAsNoObjects()
    {
        RequireEngine();
        var readable = Path.Combine(_root, "Readable.Codeunit.al");
        File.WriteAllText(readable, """
        codeunit 63660 "Cov Scan Readable"
        {
            procedure Value(): Integer
            begin
                exit(5);
            end;
        }
        """);
        var locked = Path.Combine(_root, "Locked.Codeunit.al");
        File.WriteAllText(locked, """
        codeunit 63661 "Cov Scan Locked"
        {
            procedure Value(): Integer
            begin
                exit(6);
            end;
        }
        """);

        AlSourceLocationMap map;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: _root);

        // The readable file's object is still mapped — one unreadable file does not cost the
        // rest of the scan.
        Assert.True(map.ContainsKey(("CodeUnit", 63660)));
        // And the unreadable one is NOT reported as an object-free file.
        Assert.False(map.ContainsKey(("CodeUnit", 63661)));
        var failure = Assert.Single(map.ScanFailures);
        Assert.Equal(locked, failure.Path);
        Assert.Contains("could not be read", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── the consumer: what the debugger tells the user ──────────────────────────────────

    /// <summary>
    /// The whole point of the third state, at the place the wrong claim was made. A breakpoint
    /// in a file the scan could not read used to come back "no executable AL statement on this
    /// line in this file" — a measured-sounding fact about AL that nobody read.
    /// </summary>
    [SkippableFact]
    public void UnverifiedReason_ForAFileThatCouldNotBeRead_SaysSoRatherThanBlamingTheLine()
    {
        RequireEngine();
        var locked = Path.Combine(_root, "Locked.Codeunit.al");
        File.WriteAllText(locked, """
        codeunit 63670 "Cov Reason Locked"
        {
            procedure Value(): Integer
            begin
                exit(7);
            end;
        }
        """);

        AlSourceLocationMap map;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: null);

        var reason = DapUnverifiedReason.For(compileFailure: null, map, locked);

        Assert.Contains("could not be read", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no executable AL statement", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The measured answer must survive: a file that WAS read, on a line with no statement,
    /// still says exactly that. Without this the fix could turn every unverified breakpoint
    /// into "could not be read", which is the opposite false claim.
    /// </summary>
    [SkippableFact]
    public void UnverifiedReason_ForAFileThatWasRead_StillBlamesTheLine()
    {
        RequireEngine();
        var readable = Path.Combine(_root, "Readable.Codeunit.al");
        File.WriteAllText(readable, """
        codeunit 63671 "Cov Reason Readable"
        {
            procedure Value(): Integer
            begin
                exit(8);
            end;
        }
        """);

        var map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: null);

        var reason = DapUnverifiedReason.For(compileFailure: null, map, readable);

        Assert.Equal("no executable AL statement on this line in this file", reason);
    }

    /// <summary>
    /// A scan failure elsewhere must not be reported against an unrelated file. The failure
    /// list is process-wide for one Build call, so without a path test every unverified
    /// breakpoint in the bundle would inherit one unreadable file's excuse.
    /// </summary>
    [SkippableFact]
    public void UnverifiedReason_ForAReadableFileBesideAnUnreadableOne_StillBlamesTheLine()
    {
        RequireEngine();
        var readable = Path.Combine(_root, "Readable.Codeunit.al");
        File.WriteAllText(readable, """
        codeunit 63672 "Cov Reason Other"
        {
            procedure Value(): Integer
            begin
                exit(9);
            end;
        }
        """);
        var locked = Path.Combine(_root, "Locked.Codeunit.al");
        File.WriteAllText(locked, """
        codeunit 63673 "Cov Reason Locked Other"
        {
            procedure Value(): Integer
            begin
                exit(10);
            end;
        }
        """);

        AlSourceLocationMap map;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: null);

        Assert.Single(map.ScanFailures);
        Assert.Equal("no executable AL statement on this line in this file",
            DapUnverifiedReason.For(compileFailure: null, map, readable));
        Assert.Contains("could not be read",
            DapUnverifiedReason.For(compileFailure: null, map, locked),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file UNDER a directory the scan could not enter. The failure names the directory, not
    /// the file, so equality alone would miss every source beneath it — and those are exactly
    /// the sources nothing measured.
    /// </summary>
    [Fact]
    public void UnverifiedReason_ForAFileUnderAnUnscannedRoot_SaysSoRatherThanBlamingTheLine()
    {
        var missingRoot = Path.Combine(_root, "gone");
        var underIt = Path.Combine(missingRoot, "src", "Thing.Codeunit.al");

        var map = AlCoverageSourceMap.Build(new[] { _root, missingRoot }, relativeTo: null);

        Assert.Single(map.ScanFailures);
        var reason = DapUnverifiedReason.For(compileFailure: null, map, underIt);
        Assert.Contains("could not be read", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A compile failure outranks both: nothing bound because nothing compiled, and saying the
    /// source was unreadable instead would send the user to the wrong place.
    /// </summary>
    [Fact]
    public void UnverifiedReason_WhenTheBundleDidNotCompile_SaysThatFirst()
    {
        var missingRoot = Path.Combine(_root, "gone");
        var map = AlCoverageSourceMap.Build(new[] { missingRoot }, relativeTo: null);
        Assert.Single(map.ScanFailures);

        var reason = DapUnverifiedReason.For("error AL0134: nope", map, Path.Combine(missingRoot, "X.al"));

        Assert.Contains("did not compile", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AL0134", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// #3884 review: the arm nothing of mine proved. SafeDirectoryScan has always reported the
    /// directories it could not enter and this caller discarded them with <c>out _</c>; that
    /// forwarding is what the fix restores, and deleting the loop again would leave a test of
    /// SafeDirectoryScan itself perfectly green.
    ///
    /// Permission bits cannot produce the condition here — they do not bite on Windows or for
    /// a root CI user, which is why the four InaccessibleDirectoryScanTests rows skip — so the
    /// scanner is injected instead. This is the forwarding, not the scanning.
    /// </summary>
    [Fact]
    public void Build_ADirectoryTheScannerCouldNotEnter_IsForwardedAsAFailure()
    {
        var unreadable = Path.Combine(_root, "locked-subdir");

        var map = AlCoverageSourceMap.Build(
            new[] { _root }, relativeTo: null,
            (string root, out IReadOnlyList<string> inaccessible) =>
            {
                inaccessible = new[] { unreadable };
                return Array.Empty<string>();
            });

        var failure = Assert.Single(map.ScanFailures);
        Assert.Equal(unreadable, failure.Path);
        Assert.Equal(SourceScanFailureKind.Directory, failure.Kind);
        Assert.True(map.IsIncomplete);
    }

    /// <summary>
    /// The scanner reporting nothing inaccessible is a clean pass — the constraint again, at
    /// the seam: injecting a scanner must not itself manufacture a failure.
    /// </summary>
    [Fact]
    public void Build_AScannerThatReportsNothingInaccessible_ReportsNoFailures()
    {
        var map = AlCoverageSourceMap.Build(
            new[] { _root }, relativeTo: null,
            (string root, out IReadOnlyList<string> inaccessible) =>
            {
                inaccessible = Array.Empty<string>();
                return Array.Empty<string>();
            });

        Assert.Empty(map.ScanFailures);
        Assert.False(map.IsIncomplete);
    }

    /// <summary>
    /// #3884 review: containment belongs to a ROOT or a DIRECTORY, which cover the sources
    /// beneath them — never to a FILE, which covers exactly one path. A file failure used as a
    /// prefix claimed paths "under" a file, which is a shape a DAP client can ask about even
    /// when the filesystem cannot hold it.
    /// </summary>
    [Fact]
    public void UnverifiedReason_ForAPathUnderAFileFailure_StillBlamesTheLine()
    {
        var locked = Path.Combine(_root, "Locked.Codeunit.al");
        var map = AlCoverageSourceMap.Build(
            new[] { _root }, relativeTo: null,
            (string root, out IReadOnlyList<string> inaccessible) =>
            {
                inaccessible = Array.Empty<string>();
                return Array.Empty<string>();
            });
        map.AddScanFailure(locked, "the file could not be read (test)", SourceScanFailureKind.File);

        // The file itself is covered...
        Assert.Contains("could not be read",
            DapUnverifiedReason.For(null, map, locked), StringComparison.OrdinalIgnoreCase);
        // ...and a path lexically beneath it is not.
        Assert.Equal("no executable AL statement on this line in this file",
            DapUnverifiedReason.For(null, map, Path.Combine(locked, "Child.al")));
    }

    /// <summary>
    /// #3884 review: a root supplied as a FILE said "the source root does not exist", which is
    /// false — it does exist, and the remedy is different.
    /// </summary>
    [Fact]
    public void Build_ARootThatIsAFile_SaysSoRatherThanThatItIsAbsent()
    {
        var asFile = Path.Combine(_root, "not-a-directory.al");
        File.WriteAllText(asFile, "// not a directory\n");

        var map = AlCoverageSourceMap.Build(new[] { asFile }, relativeTo: null);

        var failure = Assert.Single(map.ScanFailures);
        Assert.Contains("is a file", failure.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not exist", failure.Reason, StringComparison.OrdinalIgnoreCase);
        // File, not Root: Root means a container and gets containment, so classifying a file
        // root as one made it claim paths "under" a file (#3884 Copilot review).
        Assert.Equal(SourceScanFailureKind.File, failure.Kind);
        Assert.Equal("no executable AL statement on this line in this file",
            DapUnverifiedReason.For(null, map, Path.Combine(asFile, "Child.al")));
        Assert.Contains("is a file",
            DapUnverifiedReason.For(null, map, asFile), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #3884 review: the warning is per BUILD, not per process. A path that failed, was
    /// repaired, and failed again is a new event — a process-wide memo of warned paths
    /// swallowed the second one, and under --server and --watch the process outlives many
    /// builds.
    ///
    /// Asserted on the map rather than on stderr: what the memo suppressed was the WARNING,
    /// but the contract that matters to a caller is that every build reports its own failures.
    /// </summary>
    [Fact]
    public void Build_TheSameRootFailingTwice_IsReportedBothTimes()
    {
        var missing = Path.Combine(_root, "gone-then-back");

        var first = AlCoverageSourceMap.Build(new[] { missing }, relativeTo: null);
        Assert.Single(first.ScanFailures);

        Directory.CreateDirectory(missing);
        var repaired = AlCoverageSourceMap.Build(new[] { missing }, relativeTo: null);
        Assert.Empty(repaired.ScanFailures);

        Directory.Delete(missing);
        var again = AlCoverageSourceMap.Build(new[] { missing }, relativeTo: null);
        Assert.Single(again.ScanFailures);
        Assert.True(again.IsIncomplete);
    }
}
