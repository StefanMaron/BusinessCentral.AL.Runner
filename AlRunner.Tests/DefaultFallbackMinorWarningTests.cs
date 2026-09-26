// DefaultFallbackMinorWarningTests — issue #4691.
//
// With no --bc-version, a single-build runner whose own minor is neither cached nor fetchable
// falls back to the latest build of its major. That fallback used to print "a different minor is
// a KNOWN-DEGRADED configuration (measured: dozens of extra failures...)" before it knew which
// minor it would land on. #4547 measured a 28.5-built runner on BC 28.4 and 28.1 giving the same
// per-test corpus result as a runner built for each, so the claim is false for a minor CI
// measures. The warning now waits for the selection and fires only for an unmeasured minor,
// or when the build carries no measured list at all (the third state).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefaultFallbackMinorWarningTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string WarningFragment = "the default selection fell back to BC";

    private static IReadOnlyList<Version> MinorsFromRepoFile() =>
        BcArtifacts.ParseMeasuredMinors(File.ReadAllText(Path.Combine(RepoRoot, ".github", "bc-versions.txt")));

    public static IEnumerable<object[]> EveryCiVersion() =>
        MinorsFromRepoFile().Select(v => new object[] { v.ToString() });

    /// <summary>Every CI minor, landed on by the fallback from an engine built for any other CI
    /// minor of the same major. None may warn.</summary>
    [Theory]
    [MemberData(nameof(EveryCiVersion))]
    public void FallbackLandingOnAnyCiMinor_PrintsNoWarning(string ciVersion)
    {
        var landed = Version.Parse(ciVersion);
        var measured = BcArtifacts.MeasuredBcMinors();
        Assert.NotNull(measured);
        var selected = new Version(landed.Major, landed.Minor, 11111, 22222);
        foreach (var engineMinor in measured!.Where(m => m.Major == landed.Major && m.Minor != landed.Minor))
        {
            var built = new Version(engineMinor.Major, engineMinor.Minor, 33333, 44444);
            var message = BcArtifacts.DescribeDefaultFallbackMinorMismatch(built, selected, measured);
            Assert.True(message == null,
                $"engine built for {built}, fallback landed on {selected}: expected no warning, got: {message}");
        }
    }

    [Fact]
    public void FallbackLandingOnUnmeasuredMinor_Warns_AndNamesTheMeasuredList()
    {
        var measured = new[] { new Version(28, 1), new Version(28, 4) };
        var message = BcArtifacts.DescribeDefaultFallbackMinorMismatch(
            new Version(28, 1, 1, 1), new Version(28, 9, 5, 5), measured);
        Assert.NotNull(message);
        Assert.Contains("[bc] warning:", message);
        Assert.Contains(WarningFragment + " 28.9.5.5", message);
        Assert.Contains("not a BC version CI measures (measured: 28.1, 28.4)", message);
        Assert.Contains("al-runner provision --bc-version 28.1", message);
        Assert.DoesNotContain("dozens", message);
    }

    /// <summary>The third state: a build with no record of what CI measures never vouches.</summary>
    [Fact]
    public void FallbackWithNoMeasuredList_Warns_AsUnknownNotMeasured()
    {
        var message = BcArtifacts.DescribeDefaultFallbackMinorMismatch(
            new Version(28, 1, 1, 1), new Version(28, 4, 5, 5), null);
        Assert.NotNull(message);
        Assert.Contains("carries no record of which BC versions CI measures", message);
    }

    [Fact]
    public void FallbackLandingOnTheEnginesOwnMinor_PrintsNoWarning()
    {
        Assert.Null(BcArtifacts.DescribeDefaultFallbackMinorMismatch(
            new Version(28, 1, 1, 1), new Version(28, 1, 9, 9), null));
    }

    /// <summary>A measured minor of ANOTHER major is not vouched for this engine.</summary>
    [Fact]
    public void FallbackLandingOnAMeasuredMinorOfAnotherMajor_Warns()
    {
        var measured = new[] { new Version(27, 5), new Version(28, 1) };
        Assert.NotNull(BcArtifacts.DescribeDefaultFallbackMinorMismatch(
            new Version(28, 1, 1, 1), new Version(27, 5, 5, 5), measured));
    }

    [Fact]
    public void OfflineFallbackNotice_MakesNoDegradationClaim_AndNeverMentionsTheCdn()
    {
        var notice = AlRunner.ProgramSupport.MajorFallbackOfflineNotice("28.1.49838.53910", "28.1", "28");
        Assert.DoesNotContain("KNOWN-DEGRADED", notice);
        Assert.DoesNotContain("dozens", notice);
        Assert.DoesNotContain("CDN", notice);
        Assert.Contains("no cached BC 28.1.x", notice);
        Assert.Contains("latest cached 28.x", notice);
        Assert.Contains("al-runner provision --bc-version 28.1", notice);
    }

    /// <summary>End to end, the measured direction: this build's own engine artifacts reachable
    /// only under the name of a DIFFERENT CI-measured minor of its major, no --bc-version. The
    /// default path falls back to it and must say nothing about degradation.</summary>
    [SkippableFact]
    public void DefaultFallback_ToMeasuredSiblingMinor_RunsWithoutWarning()
    {
        var (engine, realEngineDir) = RequireSingleBuildEngineArtifacts();
        var sibling = MinorsFromRepoFile()
            .Where(m => m.Major == engine.Major && m.Minor != engine.Minor)
            .OrderByDescending(m => m).FirstOrDefault();
        TestArtifacts.SkipIf(sibling == null, $"bc-versions.txt lists no other minor of major {engine.Major}.");

        var selected = new Version(sibling!.Major, sibling.Minor, engine.Build, engine.Revision);
        var (exit, output) = RunWithAliasedEngine(realEngineDir, selected);

        Assert.True(exit == 0, $"expected a clean run against the aliased engine artifacts. exit={exit}\n{output}");
        Assert.Contains($"[bc] selected BC {selected} (", output, StringComparison.Ordinal);
        Assert.Contains($"no cached BC {engine.Major}.{engine.Minor}.x", output, StringComparison.Ordinal);
        Assert.DoesNotContain("KNOWN-DEGRADED", output);
        Assert.DoesNotContain(WarningFragment, output);
    }

    /// <summary>End to end, the unmeasured direction: the same alias under a minor no CI leg
    /// runs. The post-selection warning prints, exactly once across the shadow re-exec.</summary>
    [SkippableFact]
    public void DefaultFallback_ToUnmeasuredMinor_WarnsOnce()
    {
        var (engine, realEngineDir) = RequireSingleBuildEngineArtifacts();
        var unmeasured = new Version(engine.Major, engine.Minor + 50, engine.Build, engine.Revision);
        Assert.DoesNotContain(new Version(unmeasured.Major, unmeasured.Minor), MinorsFromRepoFile());

        var (_, output) = RunWithAliasedEngine(realEngineDir, unmeasured);

        Assert.Contains($"[bc] selected BC {unmeasured} (", output, StringComparison.Ordinal);
        var count = Regex.Matches(output, Regex.Escape($"{WarningFragment} {unmeasured}")).Count;
        Assert.True(count == 1, $"expected the fallback warning exactly once; printed {count} time(s).\n{output}");
    }

    private static (Version Engine, string RealEngineDir) RequireSingleBuildEngineArtifacts()
    {
        var engine = BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engine == null, "no baked-in BcEngineVersion on this build.");
        TestArtifacts.SkipIf(EngineVariants.Discover(AppContext.BaseDirectory).Count > 0,
            "this install ships engine variants; the default path is artifact-first there (#2027).");
        var realHome = TestArtifacts.HomeDir()
            ?? throw new InvalidOperationException("Cannot determine this machine's HOME.");
        var dir = Path.Combine(TestArtifacts.StandardCacheDir(realHome), engine!.ToString());
        TestArtifacts.SkipIfDirectoryMissing(dir, $"BC {engine} artifacts");
        return (engine, dir);
    }

    private static (int ExitCode, string Output) RunWithAliasedEngine(string realEngineDir, Version alias)
    {
        var root = TestScratch.Dir("al-runner-default-fallback-minor");
        var cacheDir = TestScratch.Dir("al-runner-default-fallback-minor-cache");
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(Path.Combine(root, alias.ToString()), realEngineDir);
        try
        {
            var bundle = Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp-table");
            var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
            sb.Append($" --no-auto-provision --cache \"{cacheDir}\" \"{bundle}\"");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", Arguments = sb.ToString(),
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = root;
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
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
