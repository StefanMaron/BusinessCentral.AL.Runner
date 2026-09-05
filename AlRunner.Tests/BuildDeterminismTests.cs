using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1881 — <c>RunnerFingerprint.ContentHash</c> (AlRunner/Infrastructure/RunnerFingerprint.cs)
/// is a whole-file SHA-256 of al-runner.dll, stamped into every runner-owned cache key
/// (DependencyLoader.cs:554, Program.cs:3960, Program.cs:4836). Left to the .NET SDK's
/// defaults, TWO independent mechanisms embed the current git commit SHA into those bytes:
///
///   1. <c>IncludeSourceRevisionInInformationalVersion</c> (SDK default <c>true</c>) appends
///      "+&lt;sha&gt;" to <c>AssemblyInformationalVersionAttribute</c>.
///   2. Implicit SourceLink auto-import (<c>Microsoft.NET.Sdk.SourceLink.props</c>, active
///      unless <c>SuppressImplicitGitSourceLink</c> is set — true even with ZERO
///      <c>Microsoft.SourceLink.*</c> package references) generates
///      <c>&lt;Project&gt;.sourcelink.json</c> embedding the commit SHA in a
///      raw.githubusercontent.com URL. That file is embedded in the portable PDB, which
///      changes the PDB's content/GUID, which is reflected back into the DLL's own Debug
///      Directory (CodeView) entry — so the DLL's bytes change even though the PDB is a
///      separate file (DebugType=portable, not embedded in the DLL).
///
/// The repo-root <c>Directory.Build.props</c> disables both, so <c>ContentHash</c> becomes a
/// function of the runner's CODE, not of the COMMIT it happened to be built at. Before this
/// fix every commit — including doc-only and CI-config-only commits — invalidated every
/// on-disk runner cache key (source-dependency cache, AL-output cache). See #1877 for the
/// A/B that surfaced this and #1881 for the full root-cause writeup.
///
/// Both directions matter (tdd.md), and the positive direction is load-bearing and easy to
/// get wrong: a test that only asserts "the hash changed between two builds" passes against
/// ANY implementation, including a no-op. The negative direction guards the opposite failure
/// mode — the fix must not swallow real source-code differences too, or a stale cache HIT
/// would serve wrong compiled output, which is far worse than a missed cache (see
/// RunnerFingerprint.cs's own doc header and Directory.Build.props's comment).
///
/// These tests build a small STANDALONE probe project rather than the real
/// AlRunner.csproj: that project needs the real BC service-tier artifacts on disk (RAR
/// resolves against them) and a native cross-compiler for its Win32-stub target, both
/// heavyweight and environment-dependent — the wrong cost for a config-knob regression
/// guard, and it would multiply by every BC-version leg in the CI matrix. The probe is
/// generated at test time DIRECTLY UNDER THE REPO ROOT (a sibling of AlRunner/, tests/, …)
/// so MSBuild's own upward Directory.Build.props search finds the SAME real, shipped
/// Directory.Build.props that AlRunner.csproj inherits — this exercises the actual shipped
/// mechanism, not a hand-copied reimplementation of it.
///
/// "Different commit" is simulated by overriding the <c>SourceRevisionId</c> MSBuild
/// property on the command line rather than by checking out two real commits: SourceLink's
/// own git-detection task (Microsoft.Build.Tasks.Git.targets) only sets
/// <c>SourceRevisionId</c> "Condition=\"'$(SourceRevisionId)' == ''\"" — i.e. an explicit
/// override IS exactly what a different real commit would have produced, without paying for
/// a git checkout inside the test.
///
/// The probe test alone proves only that the shipped <c>Directory.Build.props</c> makes *a*
/// classlib deterministic — it would stay green even if someone later set
/// <c>IncludeSourceRevisionInInformationalVersion=true</c> (or reintroduced SourceLink)
/// directly inside <c>AlRunner.csproj</c>, overriding the repo-wide default on the exact
/// assembly that matters. <see cref="InformationalVersion_OnTheActualRunnerAssembly_DoesNotContainAGitSha"/>
/// closes that gap for near-zero cost: a plain reflection read of the already-built
/// al-runner.dll under test, no extra <c>dotnet build</c>, no subprocess. The two tests cover
/// different halves — mechanism vs. the real artifact.
/// </summary>
public class BuildDeterminismTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RevisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>
    /// Writes a minimal, self-contained classlib project directly under the repo root (so it
    /// inherits the repo's real Directory.Build.props via MSBuild's normal upward search) and
    /// returns its .csproj path. <paramref name="marker"/> is embedded into the compiled
    /// source so callers can force a genuine content difference between two builds.
    ///
    /// Deliberately reuses the SAME project directory (same absolute path) across every call
    /// in a test — a portable PDB embeds each source Document's file PATH (independent of
    /// SourceLink), so comparing builds from two DIFFERENT directories would reintroduce the
    /// exact absolute-path confound the #1877/#1881 investigation had to control for, and
    /// would swamp the git-SHA-embedding signal this test exists to isolate.
    /// </summary>
    private static string WriteProbeProject(string dir, string marker)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Probe.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>DeterminismProbe</AssemblyName>
                <RootNamespace>DeterminismProbe</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), $$"""
            namespace DeterminismProbe;
            public static class Probe
            {
                public const string Marker = "{{marker}}";
                public static string Hello() => "hello " + Marker;
            }
            """);
        return Path.Combine(dir, "Probe.csproj");
    }

    /// <summary>
    /// Builds the probe project at <paramref name="csprojPath"/> with the given simulated
    /// commit revision, into <paramref name="outDir"/>, and returns the built DLL's
    /// SHA-256 — the exact algorithm <c>RunnerFingerprint.ComputeContentHash</c> uses.
    /// </summary>
    private static string BuildAndHash(string csprojPath, string sourceRevisionId, string outDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{csprojPath}\" -c Release -p:SourceRevisionId={sourceRevisionId} -o \"{outDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("probe build hung");
        }
        Assert.True(p.ExitCode == 0,
            $"probe build failed (exit {p.ExitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");

        var dllPath = Path.Combine(outDir, "DeterminismProbe.dll");
        Assert.True(File.Exists(dllPath), $"expected build output not found at '{dllPath}'");
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(dllPath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// The load-bearing claim (positive direction): the SAME source, built at two DIFFERENT
    /// simulated commits, produces a byte-identical DLL — hence an identical content hash.
    /// Also proves the negative direction in the same run for build-cost reasons (this test
    /// spends real `dotnet build` invocations, so it does both claims together rather than
    /// duplicating the "same source" build across two separate [Fact]s): holding the
    /// revision constant and changing the SOURCE still produces a DIFFERENT hash, so the fix
    /// does not overreach into hiding genuine code changes — a stale cache HIT would serve
    /// wrong compiled output, which is far worse than a missed cache.
    /// </summary>
    [Fact]
    public void ProbeBuild_SameSourceDifferentCommit_YieldsIdenticalHash_ButSourceChangeStillInvalidates()
    {
        var root = Path.Combine(RepoRoot, ".build-determinism-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Same project directory (same absolute path) reused for every build below — see
            // WriteProbeProject's doc comment for why that matters.
            var proj = WriteProbeProject(root, marker: "X");

            var hashA = BuildAndHash(proj, RevisionA, Path.Combine(root, "out-a"));
            var hashB = BuildAndHash(proj, RevisionB, Path.Combine(root, "out-b")); // same source, different simulated commit

            // Positive / load-bearing: identical source at two different simulated commits ->
            // identical DLL bytes -> identical ContentHash.
            Assert.Equal(hashA, hashB);

            // Negative / regression guard: rewrite the SAME file at the SAME path with
            // genuinely different content, holding the simulated commit constant at
            // RevisionA. The hash MUST still differ. The fix targets ONLY the two SDK
            // behaviors that embed the commit identity; it must not make ContentHash blind
            // to actual code changes — a stale cache HIT would serve wrong compiled output,
            // far worse than a missed cache.
            WriteProbeProject(root, marker: "Y");
            var hashC = BuildAndHash(proj, RevisionA, Path.Combine(root, "out-c"));
            Assert.NotEqual(hashA, hashC);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Pins the fix directly on the assembly it actually has to hold for: al-runner.dll
    /// itself, as already built for this test run (no extra `dotnet build`, no subprocess —
    /// a plain reflection read). The probe test above proves the MECHANISM works on a generic
    /// classlib; it would stay green even if AlRunner.csproj later set
    /// IncludeSourceRevisionInInformationalVersion=true (or reintroduced SourceLink) directly,
    /// silently overriding the repo-wide Directory.Build.props default on the one assembly
    /// RunnerFingerprint.ContentHash actually hashes. This closes that gap.
    ///
    /// Asserts on a 40-hex-char match rather than on the literal "+": SemVer build metadata is
    /// legitimate and may reappear in AssemblyInformationalVersionAttribute for other reasons
    /// (e.g. a future prerelease/build tag) — a 40-character hex git SHA is specifically the
    /// thing that must never be there again.
    /// </summary>
    [Fact]
    public void InformationalVersion_OnTheActualRunnerAssembly_DoesNotContainAGitSha()
    {
        var info = typeof(AlRunner.Infrastructure.RunnerFingerprint).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            !.InformationalVersion;

        Assert.DoesNotMatch("[0-9a-f]{40}", info);
    }

    /// <summary>
    /// Copies the repo's REAL, shipped <c>Directory.Build.props</c> into
    /// <paramref name="dir"/> alongside the probe, then writes the probe there.
    ///
    /// The other probe test deliberately relies on MSBuild's upward search finding the
    /// repo-root copy. This one cannot: the property under test
    /// (<c>PathMap</c>) is anchored on <c>$(MSBuildThisFileDirectory)</c> — the directory
    /// the props file itself sits in — so two probes that both inherit the repo-root file
    /// would both be mapped relative to the repo root and would differ by their own
    /// subdirectory name, which is not the situation being modelled. The real situation is
    /// TWO CHECKOUTS of the same repository at two different absolute paths, each with its
    /// own <c>Directory.Build.props</c> at its own root. Copying the shipped file to each
    /// probe root reproduces exactly that, and still exercises the shipped file's real
    /// content rather than a hand-written reimplementation of it.
    /// </summary>
    private static string WriteProbeProjectWithOwnBuildProps(string dir, string marker)
    {
        var csproj = WriteProbeProject(dir, marker);
        File.Copy(Path.Combine(RepoRoot, "Directory.Build.props"),
                  Path.Combine(dir, "Directory.Build.props"), overwrite: true);
        return csproj;
    }

    /// <summary>
    /// Issue #2818. The load-bearing claim: the SAME source built at two DIFFERENT absolute
    /// paths produces a byte-identical DLL, hence an identical
    /// <c>RunnerFingerprint.ContentHash</c>, hence identical runner cache keys.
    ///
    /// Measured before the fix, on the real <c>AlRunner.csproj</c>: the same git tree
    /// (<c>rev-parse HEAD^{tree}</c> equal, both worktrees clean), built with the same
    /// command by the same SDK on the same machine, produced two different al-runner.dll
    /// hashes. The byte diff located the term — the absolute path of the PDB, written into
    /// the DLL's CodeView debug-directory entry, plus the deterministic PDB ID / MVID that
    /// hash the source documents' absolute paths. Both are functions of WHERE the tree was
    /// built, not of the source.
    ///
    /// That falsified the premise recorded in <c>NclCecilRewrite.ComputeCacheKey</c> and in
    /// <c>RunnerFingerprint</c>'s own header — "stable across rebuilds of unchanged source"
    /// — for any rebuild that moves. A rebuild in place was, and still is, stable (measured
    /// too); only a change of path broke it.
    ///
    /// Also proves the negative direction in the same run, for the same build-cost reason
    /// the sibling probe test does: with the paths held fixed, changing the SOURCE must
    /// still change the hash. Without that half, a <c>PathMap</c> that over-reached (or a
    /// hash that had been made blind to real code changes) would still pass — and a stale
    /// cache HIT serving wrong compiled output is far worse than a missed cache.
    /// </summary>
    [Fact]
    public void ProbeBuild_SameSourceDifferentBuildPath_YieldsIdenticalHash_ButSourceChangeStillInvalidates()
    {
        var id = Guid.NewGuid().ToString("N");
        // Deliberately different lengths: a path term that leaks shifts everything after it,
        // so equal-length roots would be the weakest possible version of this check.
        var rootA = Path.Combine(RepoRoot, ".build-determinism-path-a-" + id);
        var rootB = Path.Combine(RepoRoot, ".build-determinism-path-bbbbbbbbbbbbbbbb-" + id);
        try
        {
            var projA = WriteProbeProjectWithOwnBuildProps(rootA, marker: "X");
            var projB = WriteProbeProjectWithOwnBuildProps(rootB, marker: "X");

            // Simulated commit held CONSTANT at RevisionA across every build below, so the
            // only thing varying between A and B is the absolute path.
            var hashA = BuildAndHash(projA, RevisionA, Path.Combine(rootA, "out"));
            var hashB = BuildAndHash(projB, RevisionA, Path.Combine(rootB, "out"));

            Assert.Equal(hashA, hashB);

            // Negative / regression guard: same two roots, genuinely different source in B.
            WriteProbeProjectWithOwnBuildProps(rootB, marker: "Y");
            var hashC = BuildAndHash(projB, RevisionA, Path.Combine(rootB, "out-c"));
            Assert.NotEqual(hashA, hashC);
        }
        finally
        {
            if (Directory.Exists(rootA)) Directory.Delete(rootA, recursive: true);
            if (Directory.Exists(rootB)) Directory.Delete(rootB, recursive: true);
        }
    }

    /// <summary>
    /// Issue #2818, pinned on the artifact that actually matters — al-runner.dll as built
    /// for this very test run — for the cost of one file read and no <c>dotnet build</c>.
    /// The probe test above proves the MECHANISM on a generic classlib; it would stay green
    /// if <c>AlRunner.csproj</c> later cleared or overrode <c>PathMap</c> on the one assembly
    /// <c>RunnerFingerprint.ContentHash</c> hashes.
    ///
    /// The CodeView entry is where the leak is directly readable: before the fix it held
    /// <c>/home/&lt;user&gt;/…/&lt;checkout&gt;/AlRunner/obj/Release/net8.0/al-runner.pdb</c>,
    /// an absolute path from the build machine. Asserting it is repo-relative under the
    /// mapped root is a statement about the same property the hash comparison above measures,
    /// read from a different angle: this one names the offending bytes, that one proves the
    /// whole file is clean (the CodeView path is not the only path-bearing term — the
    /// deterministic PDB ID and MVID hash the source document paths too).
    /// </summary>
    [Fact]
    public void RunnerAssembly_DebugDirectory_DoesNotEmbedTheBuildMachinesAbsolutePath()
    {
        var loc = typeof(AlRunner.Infrastructure.RunnerFingerprint).Assembly.Location;
        Assert.True(File.Exists(loc), $"al-runner.dll not found on disk at '{loc}'");

        using var fs = File.OpenRead(loc);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var codeView = pe.ReadDebugDirectory()
            .Where(e => e.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView)
            .Select(pe.ReadCodeViewDebugDirectoryData)
            .ToList();

        Assert.True(codeView.Count > 0,
            "al-runner.dll carries no CodeView debug-directory entry, so this guard would " +
            "check nothing — it must not silently pass. If DebugType was deliberately " +
            "changed to 'none', delete this test and say so.");

        foreach (var cv in codeView)
        {
            Assert.StartsWith("/_/", cv.Path.Replace('\\', '/'));
            Assert.EndsWith("al-runner.pdb", cv.Path.Replace('\\', '/'));
        }
    }
}
