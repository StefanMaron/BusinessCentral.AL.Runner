// DefaultBcVersionFloorTests — issue #4590.
//
// app.json's application/platform versions are a floor: with no --bc-version the runner must
// select the newest version it can run AT OR ABOVE that floor, refuse when none is, and warn
// (not refuse) when an explicit --bc-version is below it.
//
// The unit half pins EngineVariants.ChooseDefault's floor handling, BcVersionFloor.Meets and the
// floor reader; the subprocess half drives Program.cs the way DefaultBcVersionSupportedVariantTests
// does (a private mirror of the build output with placeholder variants, --no-auto-provision, a
// scratch AL_RUNNER_ARTIFACTS_ROOT), so nothing is downloaded and the shared cache is never read.
// Fixtures declare the floor through "platform" only (.claude/rules/no-base-app-in-csharp-tests.md);
// the reader takes the higher of the two fields through InProcessAppPackager.ReadMinimumBcVersion,
// which BcVersionFloorSkipTests already covers for "application".
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefaultBcVersionFloorTests
{
    private const int SpawnTimeoutMs = 300_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static EngineVariants.Variant V(string version) => new(Version.Parse(version), "/unused");

    private static readonly IReadOnlyList<EngineVariants.Variant> Shipped = new[]
    {
        V("27.5.46862.53931"), V("28.1.49838.54368"), V("28.3.52162.54374"), V("28.4.53241.54387"),
    };

    // ───────────────────────────── ChooseDefault with a floor ─────────────────────────────

    /// <summary>The owner's run behind the issue: dependencies need 28.4, the newest cached build is
    /// 28.1. The cached builds below the floor are skipped and the newest shipped minor is targeted.</summary>
    [Fact]
    public void ChooseDefault_FloorAboveEveryCachedBuild_SkipsThemAndTargetsNewestShippedMinor()
    {
        var choice = EngineVariants.ChooseDefault(Shipped,
            new[] { "28.1.49838.54368", "27.5.46862.53931" }, floor: new Version(28, 4, 0, 0));

        Assert.Equal("28.4", choice.Version);
        Assert.False(choice.FloorUnmet);
        Assert.Equal(new[] { "28.1.49838.54368", "27.5.46862.53931" }, choice.SkippedBelowFloor);
        Assert.Equal(
            "[bc] skipping cached BC 28.1.49838.54368, 27.5.46862.53931: below the minimum BC 28.4.0.0 " +
            "the project's app.json declares — using BC 28.4 instead.",
            choice.FloorSkipLine());
    }

    /// <summary>Control: a floor the newest cached build meets changes nothing.</summary>
    [Fact]
    public void ChooseDefault_FloorMetByNewestCached_SelectsItAndSkipsNothing()
    {
        var choice = EngineVariants.ChooseDefault(Shipped,
            new[] { "28.3.52162.54374", "28.1.49838.54368" }, floor: new Version(28, 1, 0, 0));

        Assert.Equal("28.3.52162.54374", choice.Version);
        Assert.Empty(choice.SkippedBelowFloor);
        Assert.Null(choice.FloorSkipLine());
    }

    /// <summary>A cached build above the floor is preferred over provisioning, even when an older
    /// cached build is below it.</summary>
    [Fact]
    public void ChooseDefault_CachedBuildAtFloorMinor_IsPreferredOverProvisioning()
    {
        var choice = EngineVariants.ChooseDefault(Shipped,
            new[] { "28.3.52162.54374", "28.1.49838.54368" }, floor: new Version(28, 2, 0, 0));

        Assert.Equal("28.3.52162.54374", choice.Version);
        Assert.Empty(choice.SkippedBelowFloor);
    }

    /// <summary>Full versions are compared, not major.minor: a cached 28.4 build below a 28.4
    /// build-level floor is skipped.</summary>
    [Fact]
    public void ChooseDefault_ComparesFullVersions_NotJustMajorMinor()
    {
        var choice = EngineVariants.ChooseDefault(Shipped,
            new[] { "28.4.53241.54387" }, floor: new Version(28, 4, 60000, 0));

        Assert.Equal(new[] { "28.4.53241.54387" }, choice.SkippedBelowFloor);
        Assert.Equal("28.4", choice.Version);
    }

    [Fact]
    public void ChooseDefault_NoShippedVariantMeetsFloor_ReportsFloorUnmet()
    {
        var choice = EngineVariants.ChooseDefault(Shipped,
            new[] { "28.4.53241.54387" }, floor: new Version(28, 5, 0, 0));

        Assert.Null(choice.Version);
        Assert.True(choice.FloorUnmet);
        Assert.Equal(new[] { "28.4.53241.54387" }, choice.SkippedBelowFloor);
    }

    /// <summary>No floor keeps #4557's behaviour, and never reports FloorUnmet.</summary>
    [Fact]
    public void ChooseDefault_NoFloor_KeepsNewestSupportedCachedAndNeverReportsFloorUnmet()
    {
        var choice = EngineVariants.ChooseDefault(Shipped, new[] { "27.5.46862.53931" });
        Assert.Equal("27.5.46862.53931", choice.Version);
        Assert.False(choice.FloorUnmet);
    }

    // ───────────────────────────── BcVersionFloor.Meets ─────────────────────────────

    [Theory]
    [InlineData("28.4", "28.4.0.0", true)]
    [InlineData("28.4.53241.54387", "28.4.0.0", true)]
    [InlineData("28.5", "28.4.0.0", true)]
    [InlineData("29.0.1.1", "28.4.0.0", true)]
    [InlineData("28", "28.4.0.0", true)]
    [InlineData("28.3", "28.4.0.0", false)]
    [InlineData("28.3.99999.99999", "28.4.0.0", false)]
    [InlineData("27", "28.4.0.0", false)]
    [InlineData("28.4.1.0", "28.4.2.0", false)]
    [InlineData("28.4.2.0", "28.4.2.0", true)]
    [InlineData("not-a-version", "28.4.0.0", true)]
    public void Meets_ComparesTheComponentsTheInputHas(string input, string floor, bool expected)
        => Assert.Equal(expected, BcVersionFloor.Meets(input, Version.Parse(floor)));

    // ───────────────────────────── the floor reader ─────────────────────────────

    /// <summary>The highest floor across every bundle in the run wins, not the first bundle's.</summary>
    [Fact]
    public void TryDeriveBcFloorFromProject_TakesTheHighestFloorAcrossBundles()
    {
        var work = TestScratch.FlatDir("al-runner-4590-floor-");
        try
        {
            var a = WriteApp(Path.Combine(work, "a"), "27.0.0.0");
            var b = WriteApp(Path.Combine(work, "b"), "28.2.0.0");
            var c = WriteApp(Path.Combine(work, "c"), "28.1.0.0");

            Assert.Equal(new Version(28, 2, 0, 0), ProgramSupport.TryDeriveBcFloorFromProject(new[] { a, b, c }));
            Assert.Null(ProgramSupport.TryDeriveBcFloorFromProject(new[] { Path.Combine(work, "missing") }));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    // ───────────────────────────── subprocess: Program.cs wiring ─────────────────────────────

    private static Version EngineBuild() => BcArtifacts.EngineBuiltVersion()
        ?? throw new InvalidOperationException("EngineBuiltVersion() is null — rebuild AlRunner first.");

    private static string MirrorBinDir()
    {
        var originalBinDir = Path.Combine(
            RepoRoot, "AlRunner", "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework);
        var privateDir = Directory.CreateDirectory(TestScratch.FlatDir("al-runner-4590-mirror-")).FullName;
        NclShadowRuntime.MirrorInstallDirectory(originalBinDir, privateDir);
        return privateDir;
    }

    private static void AddPlaceholderVariant(string installDir, string version)
    {
        var dir = Path.Combine(installDir, EngineVariants.VariantsDirName, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, EngineVariants.EntryAssemblyFileName), "placeholder");
    }

    private static string WriteApp(string dir, string platformFloor)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            { "id": "{{Guid.NewGuid()}}", "name": "Repro4590", "publisher": "Repro", "version": "1.0.0.0",
              "platform": "{{platformFloor}}", "idRanges": [ { "from": 50000, "to": 50099 } ] }
            """);
        File.WriteAllText(Path.Combine(dir, "Test.Codeunit.al"), """
            codeunit 50000 "Repro 4590"
            {
                Subtype = Test;
                [Test]
                procedure Nothing()
                begin
                end;
            }
            """);
        return dir;
    }

    private static (string Output, int Exit) Run(
        string installDir, string artifactsRoot, string work, string platformFloor, params string[] extra)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.ArgumentList.Add(Path.Combine(installDir, "al-runner.dll"));
        psi.ArgumentList.Add("--no-auto-provision");
        psi.ArgumentList.Add("--cache");
        psi.ArgumentList.Add(Path.Combine(work, "cache"));
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(WriteApp(Path.Combine(work, "app"), platformFloor));
        psi.Environment[BcArtifacts.ArtifactsRootEnvVar] = artifactsRoot;
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");
        foreach (var proxy in new[] { "HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy", "ALL_PROXY", "all_proxy" })
            psi.Environment[proxy] = "http://127.0.0.1:9";
        psi.Environment.Remove("NO_PROXY");
        psi.Environment.Remove("no_proxy");

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(SpawnTimeoutMs), $"al-runner did not exit within {SpawnTimeoutMs / 1000}s");
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void WithScratch(Action<string, string> body)
    {
        var installDir = MirrorBinDir();
        var work = TestScratch.FlatDir("al-runner-4590-work-");
        try { body(installDir, work); }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    // A minor older than the engine's own, in the same major; null when the engine is an x.0 build.
    private static string? OlderMinorBuild(Version build) =>
        build.Minor > 0 ? $"{build.Major}.{build.Minor - 1}.1.1" : null;

    /// <summary>
    /// Multi-variant install, the issue's shape: a supported build below the floor is the only one
    /// cached. The default skips it, says so, and targets the floor's minor instead of running
    /// the older build.
    /// </summary>
    [SkippableFact]
    public void NoBcVersion_OnlyCachedBuildIsBelowTheFloor_SkipsItAndTargetsANewerShippedMinor()
    {
        var build = EngineBuild();
        var older = OlderMinorBuild(build);
        Skip.If(older == null, $"engine {build} is an x.0 build; no older minor in its major to cache");
        WithScratch((installDir, work) =>
        {
            AddPlaceholderVariant(installDir, build.ToString());
            AddPlaceholderVariant(installDir, older!);
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts", older!)).Parent!.FullName;
            var floor = $"{build.Major}.{build.Minor}.0.0";

            var (output, _) = Run(installDir, artifactsRoot, work, floor, "--verbose");

            Assert.DoesNotContain($"selecting BC {older}", output);
            Assert.Contains($"skipping cached BC {older}: below the minimum BC {floor}", output);
            Assert.Contains($"matches version '{build.Major}.{build.Minor}'", output);
        });
    }

    /// <summary>Multi-variant install, a floor above every shipped variant: refused, naming the
    /// minimum and the supported versions.</summary>
    [Fact]
    public void NoBcVersion_FloorAboveEveryShippedVariant_RefusesNamingTheMinimum()
    {
        var build = EngineBuild();
        WithScratch((installDir, work) =>
        {
            AddPlaceholderVariant(installDir, build.ToString());
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts", build.ToString())).Parent!.FullName;
            var floor = $"{build.Major}.{build.Minor + 1}.0.0";

            var (output, exit) = Run(installDir, artifactsRoot, work, floor);

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.Contains($"declares a minimum of BC {floor}", output);
            Assert.Contains($"supported BC versions: {build.Major}.{build.Minor})", output);
        });
    }

    /// <summary>An explicit --bc-version below the floor is warned about and still honoured.</summary>
    [Fact]
    public void ExplicitBcVersionBelowTheFloor_WarnsAndRunsIt()
    {
        var build = EngineBuild();
        WithScratch((installDir, work) =>
        {
            AddPlaceholderVariant(installDir, build.ToString());
            // Only the floor's minor is cached, so reaching selection for the requested minor
            // proves the request was honoured rather than redirected to the floor.
            var floor = $"{build.Major}.{build.Minor + 1}.0.0";
            var artifactsRoot = Directory.CreateDirectory(
                Path.Combine(work, "artifacts", $"{build.Major}.{build.Minor + 1}.1.1")).Parent!.FullName;
            var requested = $"{build.Major}.{build.Minor}";

            var (output, _) = Run(installDir, artifactsRoot, work, floor, "--bc-version", requested);

            Assert.Contains($"--bc-version {requested} is below the minimum BC {floor}", output);
            Assert.DoesNotContain("declares a minimum of BC", output);
            Assert.Contains($"matches version '{requested}'", output);
        });
    }

    /// <summary>Single-build (dev) install: a floor above the engine's own minor is refused.</summary>
    [Fact]
    public void SingleBuild_FloorAboveTheEngine_RefusesNamingTheMinimum()
    {
        var build = EngineBuild();
        WithScratch((installDir, work) =>
        {
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts", build.ToString())).Parent!.FullName;
            var floor = $"{build.Major}.{build.Minor + 1}.0.0";

            var (output, exit) = Run(installDir, artifactsRoot, work, floor);

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.Contains($"declares a minimum of BC {floor}", output);
            Assert.Contains($"supported BC versions: {build.Major}.{build.Minor})", output);
        });
    }

    /// <summary>
    /// Single-build install with only an older minor cached and no provisioning: the offline
    /// fallback lands below the floor, and the selection is refused rather than run.
    /// </summary>
    [SkippableFact]
    public void SingleBuild_OfflineFallbackLandsBelowTheFloor_IsRefused()
    {
        var build = EngineBuild();
        var older = OlderMinorBuild(build);
        Skip.If(older == null, $"engine {build} is an x.0 build; no older minor in its major to cache");
        WithScratch((installDir, work) =>
        {
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts", older!)).Parent!.FullName;
            var floor = $"{build.Major}.{build.Minor}.0.0";

            var (output, exit) = Run(installDir, artifactsRoot, work, floor);

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.Contains($"selected BC {older} is below the minimum BC {floor}", output);
        });
    }
}
