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
}
