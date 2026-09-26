// MeasuredBcVersionsWarningTests — issue #4547.
//
// A single-build runner (a local `dotnet build`) asked for a different minor of its own major
// printed the #2008 off-version warning even for a minor CI measures on every push. The warning
// now fires only for a minor outside .github/bc-versions.txt, which the build embeds.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class MeasuredBcVersionsWarningTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string BcVersionsFile = Path.Combine(RepoRoot, ".github", "bc-versions.txt");

    private const string WarningFragment = "was explicitly selected (--bc-version/--artifact-path)";

    private static IReadOnlyList<Version> MinorsFromRepoFile() =>
        BcArtifacts.ParseMeasuredMinors(File.ReadAllText(BcVersionsFile));

    public static IEnumerable<object[]> EveryCiVersion() =>
        MinorsFromRepoFile().Select(v => new object[] { v.ToString() });

    [Fact]
    public void ParseMeasuredMinors_SkipsCommentLines_AndReadsEveryToken()
    {
        var parsed = BcArtifacts.ParseMeasuredMinors("# 26.0 is a comment\n27.0 27.5\n\n# 99.9\n28.4 28.5\n");
        Assert.Equal(new[] { new Version(27, 0), new Version(27, 5), new Version(28, 4), new Version(28, 5) }, parsed);
    }

    [Fact]
    public void EmbeddedMeasuredList_EqualsTheRepositorysBcVersionsFile()
    {
        var embedded = BcArtifacts.MeasuredBcMinors();
        Assert.NotNull(embedded);
        var fromFile = MinorsFromRepoFile();
        Assert.NotEmpty(fromFile);
        Assert.Equal(fromFile, embedded);
    }

    /// <summary>Every version CI runs, against an engine built for any other CI minor of the
    /// same major — the shape of a local build asked for a sibling minor. None may warn.</summary>
    [Theory]
    [MemberData(nameof(EveryCiVersion))]
    public void EveryCiVersion_OnAnyCiEngineMinorOfItsMajor_PrintsNoOffVersionWarning(string ciVersion)
    {
        var selectedMinor = Version.Parse(ciVersion);
        var measured = BcArtifacts.MeasuredBcMinors();
        Assert.NotNull(measured);
        var selected = new Version(selectedMinor.Major, selectedMinor.Minor, 11111, 22222);
        var engines = measured!.Where(m => m.Major == selectedMinor.Major).ToList();
        Assert.Contains(selectedMinor, engines);
        foreach (var engineMinor in engines)
        {
            var built = new Version(engineMinor.Major, engineMinor.Minor, 33333, 44444);
            var message = BcArtifacts.DescribeExplicitEngineMinorMismatch(built, selected, measured);
            Assert.True(message == null,
                $"engine built for {built}, --bc-version {selected}: expected no warning, got: {message}");
        }
    }

    [Fact]
    public void UnmeasuredMinor_OfTheEnginesMajor_StillWarns()
    {
        var measured = BcArtifacts.MeasuredBcMinors();
        Assert.NotNull(measured);
        var engineMinor = measured!.Max()!;
        var unmeasured = new Version(engineMinor.Major, engineMinor.Minor + 50, 1, 1);
        var message = BcArtifacts.DescribeExplicitEngineMinorMismatch(
            new Version(engineMinor.Major, engineMinor.Minor, 1, 1), unmeasured, measured);
        Assert.NotNull(message);
        Assert.Contains(WarningFragment, message);
        Assert.Contains("not a BC version CI measures", message);
    }

    /// <summary>
    /// End to end through Program.cs: this build's own engine artifacts, reachable under the
    /// name of a DIFFERENT minor of the engine's major that CI measures. A clean run with no
    /// off-version warning proves the call site passes the embedded list, not just the pure
    /// function. The unmeasured direction is ExplicitEngineMinorWarningOncePerInvocationTests.
    /// </summary>
    [SkippableFact]
    public void ExplicitMeasuredSiblingMinor_SingleBuild_RunsWithoutOffVersionWarning()
    {
        var engineVersion = BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engineVersion == null,
            "no baked-in BcEngineVersion on this build — nothing to compare a selection against.");
        TestArtifacts.SkipIf(EngineVariants.Discover(AppContext.BaseDirectory).Count > 0,
            "this install ships engine variants, and the warning is gated off for that shape (#2037).");
        var sibling = MinorsFromRepoFile()
            .Where(m => m.Major == engineVersion!.Major && m.Minor != engineVersion.Minor)
            .OrderByDescending(m => m).FirstOrDefault();
        TestArtifacts.SkipIf(sibling == null,
            $"bc-versions.txt lists no other minor of major {engineVersion!.Major}.");

        var realHome = TestArtifacts.HomeDir()
            ?? throw new InvalidOperationException("Cannot determine this machine's HOME.");
        var realEngineDir = Path.Combine(TestArtifacts.StandardCacheDir(realHome), engineVersion.ToString());
        TestArtifacts.SkipIfDirectoryMissing(realEngineDir, $"BC {engineVersion} artifacts");

        var root = TestScratch.Dir("al-runner-measured-sibling-minor");
        var cacheDir = TestScratch.Dir("al-runner-measured-sibling-minor-cache");
        Directory.CreateDirectory(root);
        var selected = new Version(sibling!.Major, sibling.Minor, engineVersion.Build, engineVersion.Revision);
        Directory.CreateSymbolicLink(Path.Combine(root, selected.ToString()), realEngineDir);
        try
        {
            var (exit, output) = Run(root, cacheDir, "--no-auto-provision", "--bc-version", selected.ToString());

            Assert.True(exit == 0, $"expected a clean run against the aliased engine artifacts. exit={exit}\n{output}");
            // #4599: the run header names the BC build at default verbosity.
            Assert.Contains($" · BC {selected} · ", output, StringComparison.Ordinal);
            var count = Regex.Matches(output, Regex.Escape(WarningFragment)).Count;
            Assert.True(count == 0,
                $"BC {sibling} is in bc-versions.txt, so an engine built for {engineVersion} must run it " +
                $"without the off-version warning; printed {count} time(s).\n{output}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    private static (int ExitCode, string Output) Run(string artifactsRoot, string cacheDir, params string[] args)
    {
        var bundle = Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp-table");
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var a in args) sb.Append(' ').Append(a);
        sb.Append($" --cache \"{cacheDir}\" \"{bundle}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRoot;
        psi.Environment.Remove("AL_RUNNER_VERBOSE");

        var output = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s");
        }
        proc.WaitForExit();
        lock (output) return (proc.ExitCode, output.ToString());
    }
}
