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
//
// TWO KINDS OF CONSUMER, and the second is the one a rendering change hides from.
// The arms above pin what the map RENDERS. The arms at the bottom pin what JOINS on it:
// Program.cs's `execute` handler feeds one sourcePaths into AlCoverageSourceMap.Build AND
// AlMemberSyntaxIndex.Build and matches them on the file path, so making the map absolute
// broke that lookup for a relative sourcePaths until AlMemberSyntaxIndex.NormalizePath was
// canonicalised to match. A consumer that DISPLAYS a path prints whatever it is handed and
// notices nothing; a consumer that MATCHES on it returns null, and "no loops for that scope"
// is indistinguishable from "that scope has no loops". Nothing exercised a relative
// sourcePaths through the server path, which is why CI stayed green through the regression.

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

    // ── the JOIN: a consumer that MATCHES on the path, not one that displays it ──────

    /// <summary>
    /// The map's path is not only rendered — it is also a <b>join key</b>. <c>Program.cs</c>'s
    /// <c>execute</c> handler feeds ONE <c>sourcePaths</c> into two builders and joins them:
    ///
    /// <code>
    /// var syntaxSourceMap = AlCoverageSourceMap.Build(sourcePaths, relativeTo: null);
    /// AlScopeSyntaxResolver.Configure(AlMemberSyntaxIndex.Build(sourcePaths), syntaxSourceMap);
    /// </code>
    ///
    /// <para>So making the map absolute (the fix above) silently broke that join for a
    /// RELATIVE <c>sourcePaths</c>: the map went absolute while <c>AlMemberSyntaxIndex</c>
    /// still keyed on the raw spelling, and every <c>FindMember</c> missed. The observable is
    /// an absent record rather than a wrong value — <c>iterationTracking</c> loops and
    /// <c>captureValues</c> write sets resolved for no scope at all.</para>
    ///
    /// <para>Measured while repairing this, with only the source-map change in the tree:
    /// merge base <c>f566aeea</c> answered <c>found</c>, the unrepaired head answered
    /// <c>NULL</c>. That is why this arm exists and why no rendering assertion caught it — a
    /// display consumer prints whatever it is handed, while a keyed lookup returns null.</para>
    /// </summary>
    [SkippableFact]
    public void MapPath_FromARelativeRoot_StillFindsTheMemberInTheSyntaxIndex()
    {
        RequireEngine();
        var (relDir, _) = TwoDirs();

        // Exactly the production pairing: one root spelling into both builders.
        var map = AlCoverageSourceMap.Build(new[] { relDir }, relativeTo: null);
        var index = AlMemberSyntaxIndex.Build(new[] { relDir });

        var mapPath = PathOf(map, 70941);
        Assert.True(Path.IsPathRooted(mapPath), "precondition: the map renders absolute");

        Assert.NotNull(index.FindMember(mapPath, "Run", null));
    }

    /// <summary>
    /// The same join with the root spelled absolutely — the arm that was already working, so a
    /// repair that merely swapped which spelling wins would break it. Both spellings of one
    /// tree must reach the same member.
    /// </summary>
    [SkippableFact]
    public void MapPath_FromAnAbsoluteRoot_StillFindsTheMemberInTheSyntaxIndex()
    {
        RequireEngine();
        var (relDir, _) = TwoDirs();
        var absDir = Path.GetFullPath(relDir);

        var map = AlCoverageSourceMap.Build(new[] { absDir }, relativeTo: null);
        var index = AlMemberSyntaxIndex.Build(new[] { absDir });

        Assert.NotNull(index.FindMember(PathOf(map, 70941), "Run", null));
    }

    /// <summary>
    /// The index must join across a spelling MISMATCH between its two sides, which is the
    /// state the production code was in: built from one spelling, looked up with another.
    /// Pinning this means a future change to either side cannot reintroduce the defect by
    /// moving only one of them.
    /// </summary>
    [SkippableFact]
    public void SyntaxIndex_BuiltRelatively_IsFoundByAnAbsoluteLookup_AndViceVersa()
    {
        RequireEngine();
        var (relDir, _) = TwoDirs();
        var absFile = Path.GetFullPath(Path.Combine(relDir, "Alpha.Codeunit.al")).Replace('\\', '/');
        var relFile = Path.Combine(relDir, "Alpha.Codeunit.al").Replace('\\', '/');
        Assert.NotEqual(absFile, relFile);

        var builtRelative = AlMemberSyntaxIndex.Build(new[] { relDir });
        Assert.NotNull(builtRelative.FindMember(absFile, "Run", null));
        Assert.NotNull(builtRelative.FindMember(relFile, "Run", null));

        var builtAbsolute = AlMemberSyntaxIndex.Build(new[] { Path.GetFullPath(relDir) });
        Assert.NotNull(builtAbsolute.FindMember(absFile, "Run", null));
        Assert.NotNull(builtAbsolute.FindMember(relFile, "Run", null));
    }

    /// <summary>
    /// <see cref="AlMemberSyntaxIndex.FromMembers"/> is a second, public entry point, and its
    /// members do NOT have to come from <c>Build</c> — a caller can hand it
    /// <c>AlMemberSyntax</c> values carrying any spelling. <c>Build</c>'s own members arrive
    /// pre-normalised by <c>Parse</c>, so the insert-side call is a no-op on that path and a
    /// mutation of it is absorbed there; this arm is what makes the line reachable, by handing
    /// <c>FromMembers</c> a relative path directly and looking it up absolutely.
    /// </summary>
    [SkippableFact]
    public void SyntaxIndex_FromMembers_KeysOnTheNormalisedPath_NotTheCallersSpelling()
    {
        RequireEngine();
        var (relDir, _) = TwoDirs();
        var relFile = Path.Combine(relDir, "Alpha.Codeunit.al");
        Assert.False(Path.IsPathRooted(relFile), "the fixture must hand FromMembers a relative path");

        // Parse with the RELATIVE spelling, then rewrite FilePath to that same relative
        // spelling, which is what an external caller constructing members would hold.
        var parsed = AlMemberSyntaxIndex.Parse(File.ReadAllText(relFile), relFile);
        var members = parsed.Select(m => m with { FilePath = relFile }).ToList();
        Assert.NotEmpty(members);

        var index = AlMemberSyntaxIndex.FromMembers(members);

        Assert.NotNull(index.FindMember(
            Path.GetFullPath(relFile).Replace('\\', '/'), "Run", null));
    }

    /// <summary>
    /// The negative direction, so the arms above cannot be satisfied by an index that matches
    /// everything: a file that is genuinely not in the index still answers null.
    /// </summary>
    [SkippableFact]
    public void SyntaxIndex_AFileItNeverIndexed_StillAnswersNull()
    {
        RequireEngine();
        var (relDir, absDir) = TwoDirs();

        var index = AlMemberSyntaxIndex.Build(new[] { relDir });

        // beta/ was never handed to this index, and Alpha declares no member called "Absent".
        Assert.Null(index.FindMember(
            Path.Combine(absDir, "Beta.Codeunit.al").Replace('\\', '/'), "Run", null));
        Assert.Null(index.FindMember(
            Path.GetFullPath(Path.Combine(relDir, "Alpha.Codeunit.al")).Replace('\\', '/'),
            "Absent", null));
    }
}
