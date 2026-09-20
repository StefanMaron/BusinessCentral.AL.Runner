// CoverageSourceMapPathCoordinateTests — #4344.
//
// AlCoverageSourceMap.Build's own doc comment promises the file's path "relative to
// relativeTo when given, else absolute". The `else absolute` half was not true: the render
// step spelled each file as SafeDirectoryScan derived it from the root, so a RELATIVE root
// produced a RELATIVE filename while an absolute one produced an absolute filename, in the
// SAME map.
//
// That is reachable because the two root sources disagree by construction, not by accident:
//
//   - RootsWithParsedSourceDependencies adds the CALLER's own spelling (it canonicalises only
//     for the dedup key), so a relative sourcePath stays relative;
//   - the registry dirs it appends come from Program.cs's GetFullPath'd bundleAbs, so they are
//     already absolute.
//
// Measured on the unfixed tree with the CoverageDependencySource fixture, cwd = repo root,
// execution root spelled relatively:
//
//     CodeUnit70880 -> AlRunner.Tests/.../run/CdsRun.Codeunit.al           [REL]
//     CodeUnit70860 -> /home/.../AlRunner.Tests/.../dep/CdsSubject.Codeunit.al  [ABS]
//
// One document, two coordinate systems, decided by how the caller spelled its own path.
//
// Why `relativeTo: null` must mean ABSOLUTE rather than "relative to something":
//   - docs/server-mode.md documents coverage[].file as an absolute path
//     ("file":"/tmp/.../Scratch.al"), and the absolute-sourcePath case already produces that;
//   - the same map supplies DAP `source.path` on stack frames (AlDapStackWalker.Walk returns
//     the map's raw string, and Program.cs writes it through unchanged), where the client is
//     an editor resolving the path against the filesystem with no stated base;
//   - the server process's own working directory is not a base a client can know: a client
//     that spawned the runner from elsewhere cannot resolve a path relative to it.
//
// The CLI --coverage site is deliberately NOT changed. It passes
// relativeTo: WorkingDirectory.TryGet(), and Cobertura's <source>.</source> element is the
// base its `filename` attributes are read against (AlCoverageReport.WriteCobertura writes
// exactly "."), so relative-to-cwd is that format's contract (#3120). This fix touches only
// what `relativeTo: null` renders.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CoverageSourceMapPathCoordinateTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public CoverageSourceMapPathCoordinateTests(BcEngineFixture engine)
    {
        _engine = engine;
        // Under the process's own working directory, so the test can name the same tree both
        // relatively and absolutely without depending on where TestScratch puts things.
        _root = Path.Combine(Environment.CurrentDirectory, "al-runner-4344-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Two sibling directories under <see cref="_root"/>, each declaring one codeunit, so a
    /// map built over both has two entries whose spellings can be compared against each other.
    /// </summary>
    private (string RelDir, string AbsDir) TwoDirs()
    {
        var a = Path.Combine(_root, "alpha");
        var b = Path.Combine(_root, "beta");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "Alpha.Codeunit.al"), """
        codeunit 70941 "Cov Coord Alpha"
        {
            procedure Run(): Integer
            begin
                exit(1);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(b, "Beta.Codeunit.al"), """
        codeunit 70942 "Cov Coord Beta"
        {
            procedure Run(): Integer
            begin
                exit(2);
            end;
        }
        """);
        return (Path.GetRelativePath(Environment.CurrentDirectory, a), b);
    }

    private static string PathOf(AlSourceLocationMap map, int id) =>
        map.TryGetValue(("CodeUnit", id), out var p)
            ? p
            : throw new Xunit.Sdk.XunitException(
                $"CodeUnit{id} is absent from the map; present: "
                + string.Join(", ", map.Select(kv => $"{kv.Key.Label}{kv.Key.Id}={kv.Value}")));

    // ── the defect: relativeTo:null must answer ONE coordinate system ────────────────

    /// <summary>
    /// RED before the fix: the relatively-spelled root renders a RELATIVE filename and the
    /// absolutely-spelled one renders an ABSOLUTE filename, in one map. The assertion is on
    /// both entries being rooted, which is what <c>relativeTo: null</c>'s doc comment
    /// ("else absolute") already promises.
    /// </summary>
    [SkippableFact]
    public void Build_RelativeToNull_MixedRootSpellings_RendersEveryFileAbsolute()
    {
        RequireEngine();
        var (relDir, absDir) = TwoDirs();
        Assert.False(Path.IsPathRooted(relDir), "the fixture must spell one root relatively");
        Assert.True(Path.IsPathRooted(absDir), "the fixture must spell the other root absolutely");

        var map = AlCoverageSourceMap.Build(new[] { relDir, absDir }, relativeTo: null);

        var fromRelativeRoot = PathOf(map, 70941);
        var fromAbsoluteRoot = PathOf(map, 70942);

        Assert.True(Path.IsPathRooted(fromRelativeRoot),
            $"the file reached through a RELATIVE root rendered as '{fromRelativeRoot}'. "
            + "relativeTo: null is documented as absolute, and the server protocol and DAP "
            + "source.path both need it (#4344).");
        Assert.True(Path.IsPathRooted(fromAbsoluteRoot),
            $"the file reached through an ABSOLUTE root rendered as '{fromAbsoluteRoot}'.");
    }

    /// <summary>
    /// The same claim stated as the issue states it — one document, one coordinate system —
    /// rather than as a property of each entry. It fails for a fix that normalises only the
    /// entry the test happens to name first.
    /// </summary>
    [SkippableFact]
    public void Build_RelativeToNull_MixedRootSpellings_DoesNotMixCoordinateSystems()
    {
        RequireEngine();
        var (relDir, absDir) = TwoDirs();

        var map = AlCoverageSourceMap.Build(new[] { relDir, absDir }, relativeTo: null);

        var rooted = map.Select(kv => Path.IsPathRooted(kv.Value)).Distinct().ToList();
        Assert.True(rooted.Count == 1,
            "the map mixes absolute and relative filenames: "
            + string.Join(", ", map.Select(kv => $"{kv.Key.Label}{kv.Key.Id}={kv.Value}")));
    }

    /// <summary>
    /// The path is not merely rooted but RESOLVED — the file it names exists. A fix that
    /// prefixed the working directory onto an already-absolute path would satisfy
    /// IsPathRooted and name nothing.
    /// </summary>
    [SkippableFact]
    public void Build_RelativeToNull_RendersAPathThatResolvesToTheRealFile()
    {
        RequireEngine();
        var (relDir, absDir) = TwoDirs();

        var map = AlCoverageSourceMap.Build(new[] { relDir, absDir }, relativeTo: null);

        foreach (var id in new[] { 70941, 70942 })
        {
            var p = PathOf(map, id);
            Assert.True(File.Exists(p), $"CodeUnit{id} rendered as '{p}', which is not a file.");
        }
        // And the two are genuinely different files, so the normalisation did not collapse them.
        Assert.NotEqual(PathOf(map, 70941), PathOf(map, 70942));
    }

    /// <summary>
    /// A root spelled with redundant segments is normalised too — <c>a/../a</c> and <c>a</c>
    /// are one directory, and a consumer keying on the filename string must not see two.
    /// This is the same defect one step along: the caller's spelling reaching the output.
    /// </summary>
    [SkippableFact]
    public void Build_RelativeToNull_ARootWithRedundantSegments_RendersTheCanonicalPath()
    {
        RequireEngine();
        var (_, absDir) = TwoDirs();
        var noisy = Path.Combine(absDir, "..", "beta");

        var map = AlCoverageSourceMap.Build(new[] { noisy }, relativeTo: null);

        var p = PathOf(map, 70942);
        Assert.DoesNotContain("..", p);
        Assert.Equal(Path.GetFullPath(Path.Combine(absDir, "Beta.Codeunit.al")).Replace('\\', '/'), p);
    }

    // ── the other direction: relativeTo != null is UNCHANGED ─────────────────────────

    /// <summary>
    /// The CLI --coverage contract. Cobertura's <c>&lt;source&gt;.&lt;/source&gt;</c> is the base
    /// its filenames are read against, so a caller passing <c>relativeTo</c> must still get
    /// RELATIVE filenames — including for a root spelled absolutely, which is how the
    /// registry's dirs always arrive.
    ///
    /// <para>This arm CAN move: mutating the render step to ignore <c>relativeTo</c> reds it
    /// (the mutation table in the PR body). It is not an arm that passes under every
    /// implementation.</para>
    /// </summary>
    [SkippableFact]
    public void Build_WithRelativeTo_StillRendersRelativeFilenames_ForBothRootSpellings()
    {
        RequireEngine();
        var (relDir, absDir) = TwoDirs();

        var map = AlCoverageSourceMap.Build(new[] { relDir, absDir }, relativeTo: _root);

        var fromRelativeRoot = PathOf(map, 70941);
        var fromAbsoluteRoot = PathOf(map, 70942);

        Assert.False(Path.IsPathRooted(fromRelativeRoot),
            $"relativeTo was given, so the filename must be relative; got '{fromRelativeRoot}'.");
        Assert.False(Path.IsPathRooted(fromAbsoluteRoot),
            $"relativeTo was given, so the filename must be relative; got '{fromAbsoluteRoot}'.");

        // The exact strings, not merely their rootedness: this is the CLI's output shape.
        Assert.Equal("alpha/Alpha.Codeunit.al", fromRelativeRoot);
        Assert.Equal("beta/Beta.Codeunit.al", fromAbsoluteRoot);
    }

    /// <summary>
    /// The relative-root half of the arm above, stated separately because it is the one the
    /// fix could plausibly break: the fix normalises the path the render step sees, and a
    /// version that normalised BEFORE applying relativeTo would still pass the assertion above
    /// while a version that normalised the RESULT would not.
    /// </summary>
    [SkippableFact]
    public void Build_WithRelativeTo_IsUnaffectedByTheRootsSpelling()
    {
        RequireEngine();
        var (relDir, absDir) = TwoDirs();

        var viaRelative = AlCoverageSourceMap.Build(new[] { relDir }, relativeTo: _root);
        var viaAbsolute = AlCoverageSourceMap.Build(
            new[] { Path.GetFullPath(relDir) }, relativeTo: _root);

        Assert.Equal(PathOf(viaAbsolute, 70941), PathOf(viaRelative, 70941));
        Assert.Equal("alpha/Alpha.Codeunit.al", PathOf(viaRelative, 70941));
        _ = absDir;
    }
}
