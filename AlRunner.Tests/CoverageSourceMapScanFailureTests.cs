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
    [SkippableFact]
    public void Build_ARootThatIsNotThere_IsReportedRatherThanSkippedSilently()
    {
        RequireEngine();
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
    [SkippableFact]
    public void UnverifiedReason_ForAFileUnderAnUnscannedRoot_SaysSoRatherThanBlamingTheLine()
    {
        RequireEngine();
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
    [SkippableFact]
    public void UnverifiedReason_WhenTheBundleDidNotCompile_SaysThatFirst()
    {
        RequireEngine();
        var missingRoot = Path.Combine(_root, "gone");
        var map = AlCoverageSourceMap.Build(new[] { missingRoot }, relativeTo: null);
        Assert.Single(map.ScanFailures);

        var reason = DapUnverifiedReason.For("error AL0134: nope", map, Path.Combine(missingRoot, "X.al"));

        Assert.Contains("did not compile", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AL0134", reason, StringComparison.Ordinal);
    }
}
