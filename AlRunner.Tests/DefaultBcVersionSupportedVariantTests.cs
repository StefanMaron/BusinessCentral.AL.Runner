// DefaultBcVersionSupportedVariantTests — issue #4557.
//
// A multi-variant install with no --bc-version used to select the newest CACHED artifact, and
// with nothing cached, provision the CDN's newest build of the engine's MAJOR. Microsoft
// publishes a minor before a release ships an engine variant for it, so both defaults landed on
// a version the variant check then refused with exit 2 — on every run, because the refused
// download became the newest cached directory.
//
// The unit half pins EngineVariants.ChooseDefault / DescribeUnsupported; the subprocess half
// fakes a variants/ directory in a private mirror of the build output (the
// PrecompileEngineVariantSelectionTests technique) and proves Program.cs consults them. Every
// subprocess case runs with --no-auto-provision against a scratch AL_RUNNER_ARTIFACTS_ROOT, so
// nothing is downloaded and the shared artifact cache is never read.
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefaultBcVersionSupportedVariantTests
{
    private const int SpawnTimeoutMs = 300_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static EngineVariants.Variant V(string version) => new(Version.Parse(version), "/unused");

    // The eight variants 2.11.0 shipped, per the issue's own failure message.
    private static readonly IReadOnlyList<EngineVariants.Variant> Shipped211 = new[]
    {
        V("27.0.38460.53934"), V("27.3.44313.53909"), V("27.5.46862.53931"), V("28.0.46665.54371"),
        V("28.1.49838.54368"), V("28.2.50931.54349"), V("28.3.52162.54374"), V("28.4.53241.54387"),
    };

    // ───────────────────────────── ChooseDefault ─────────────────────────────

    [Fact]
    public void ChooseDefault_EmptyCache_TargetsNewestShippedMinor_NotTheCdnsNewest()
    {
        var choice = EngineVariants.ChooseDefault(Shipped211, Array.Empty<string>());
        Assert.Equal("28.4", choice.Version);
        Assert.Empty(choice.SkippedUnsupported);
    }

    /// <summary>The issue's second-run state: the CDN's newer minor is the newest cached
    /// directory. It is skipped, named, and the newest supported cached build wins.</summary>
    [Fact]
    public void ChooseDefault_NewestCachedIsUnsupported_SkipsItAndSelectsNewestSupportedCached()
    {
        var choice = EngineVariants.ChooseDefault(Shipped211,
            new[] { "28.5.54151.55132", "28.3.52162.54374", "27.5.46862.53931", "not-a-version" });
        Assert.Equal("28.3.52162.54374", choice.Version);
        Assert.Equal(new[] { "28.5.54151.55132" }, choice.SkippedUnsupported);
    }

    [Fact]
    public void ChooseDefault_OnlyUnsupportedCached_FallsBackToNewestShippedMinor()
    {
        var choice = EngineVariants.ChooseDefault(Shipped211, new[] { "28.5.54151.55132", "29.0.1.1" });
        Assert.Equal("28.4", choice.Version);
        Assert.Equal(new[] { "29.0.1.1", "28.5.54151.55132" }, choice.SkippedUnsupported);
    }

    /// <summary>A same-minor different build is runnable (the degraded tier), so it is selected
    /// rather than skipped.</summary>
    [Fact]
    public void ChooseDefault_SameMinorDifferentBuildCached_IsSelected()
    {
        var choice = EngineVariants.ChooseDefault(Shipped211, new[] { "28.4.53241.99999" });
        Assert.Equal("28.4.53241.99999", choice.Version);
        Assert.Empty(choice.SkippedUnsupported);
    }

    [Fact]
    public void ChooseDefault_BareMajor_StaysInsideThatMajor()
    {
        var choice = EngineVariants.ChooseDefault(Shipped211,
            new[] { "28.5.54151.55132", "28.2.50931.54349" }, major: 27);
        Assert.Equal("27.5", choice.Version);
        Assert.Empty(choice.SkippedUnsupported);
    }

    [Fact]
    public void ChooseDefault_MajorWithNoShippedVariant_ReturnsNullVersion()
    {
        Assert.Null(EngineVariants.ChooseDefault(Shipped211, new[] { "29.0.1.1" }, major: 29).Version);
        Assert.Null(EngineVariants.ChooseDefault(Array.Empty<EngineVariants.Variant>(), new[] { "28.4.1.1" }).Version);
    }

    // ──────────────────────────── DescribeUnsupported ────────────────────────────

    /// <summary>The CDN's answer in the issue — newer than every shipped variant — is refused with
    /// the supported minors named.</summary>
    [Fact]
    public void DescribeUnsupported_CdnAnswerNewerThanEveryVariant_RefusesNamingSupportedMinors()
    {
        var msg = EngineVariants.DescribeUnsupported(Shipped211, "28.5.54151.55132");
        Assert.NotNull(msg);
        Assert.Contains("ships no engine for BC 28.5", msg);
        Assert.Contains("Supported BC versions: 27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3, 28.4.", msg);
    }

    [Fact]
    public void DescribeUnsupported_SupportedMinorOrBareMajorOrNoVariants_ReturnsNull()
    {
        Assert.Null(EngineVariants.DescribeUnsupported(Shipped211, "28.4"));
        Assert.Null(EngineVariants.DescribeUnsupported(Shipped211, "28.4.1.2"));
        Assert.Null(EngineVariants.DescribeUnsupported(Shipped211, "28"));
        Assert.Null(EngineVariants.DescribeUnsupported(Array.Empty<EngineVariants.Variant>(), "28.5"));
    }

    // ───────────────────────────── subprocess: Program.cs wiring ─────────────────────────────

    private static Version EngineBuild() => BcArtifacts.EngineBuiltVersion()
        ?? throw new InvalidOperationException("EngineBuiltVersion() is null — rebuild AlRunner first.");

    private static string MirrorBinDir()
    {
        var originalBinDir = Path.Combine(
            RepoRoot, "AlRunner", "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework);
        var privateDir = Directory.CreateDirectory(TestScratch.FlatDir("al-runner-4557-mirror-")).FullName;
        NclShadowRuntime.MirrorInstallDirectory(originalBinDir, privateDir);
        return privateDir;
    }

    private static void AddPlaceholderVariant(string installDir, string version)
    {
        var dir = Path.Combine(installDir, EngineVariants.VariantsDirName, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, EngineVariants.EntryAssemblyFileName), "placeholder");
    }

    private static string WriteApp(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            { "id": "{{Guid.NewGuid()}}", "name": "Repro4557", "publisher": "Repro", "version": "1.0.0.0",
              "platform": "27.0.0.0", "idRanges": [ { "from": 50000, "to": 50099 } ] }
            """);
        File.WriteAllText(Path.Combine(dir, "Test.Codeunit.al"), """
            codeunit 50000 "Repro 4557"
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

    private static (string Output, int Exit) Run(string installDir, string artifactsRoot, string work, params string[] extra)
        => RunWithSubcommand(installDir, artifactsRoot, work, subcommand: null, extra);

    private static (string Output, int Exit) RunWithSubcommand(
        string installDir, string artifactsRoot, string work, string? subcommand, params string[] extra)
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
        if (subcommand != null) psi.ArgumentList.Add(subcommand);
        psi.ArgumentList.Add("--no-auto-provision");
        psi.ArgumentList.Add("--cache");
        psi.ArgumentList.Add(Path.Combine(work, "cache"));
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(WriteApp(Path.Combine(work, "app")));
        psi.Environment[BcArtifacts.ArtifactsRootEnvVar] = artifactsRoot;
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");
        // A dead proxy: any request `provision` makes fails at once instead of reaching the CDN.
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

    /// <summary>
    /// The issue's second run: the only cached directory is a minor newer than every shipped
    /// variant. The default must skip it (saying so) and target the newest shipped minor, rather
    /// than select it and refuse with "no shipped engine variant supports".
    /// </summary>
    [Fact]
    public void NoBcVersion_OnlyAnUnsupportedNewerMinorCached_SkipsItAndTargetsNewestShippedMinor()
    {
        var build = EngineBuild();
        var installDir = MirrorBinDir();
        var work = TestScratch.FlatDir("al-runner-4557-work-");
        try
        {
            AddPlaceholderVariant(installDir, build.ToString());
            var unsupported = $"{build.Major}.{build.Minor + 1}.1.1";
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts", unsupported)).Parent!.FullName;

            var (output, exit) = Run(installDir, artifactsRoot, work);

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.DoesNotContain("no shipped engine variant supports", output);
            Assert.Contains($"skipping cached BC {unsupported}", output);
            Assert.Contains($"matches version '{build.Major}.{build.Minor}'", output);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Positive direction: a supported cached build older than the unsupported one is
    /// the one selected.</summary>
    [Fact]
    public void NoBcVersion_SupportedBuildCachedBelowUnsupportedOne_SelectsTheSupportedBuild()
    {
        var build = EngineBuild();
        var installDir = MirrorBinDir();
        var work = TestScratch.FlatDir("al-runner-4557-work-");
        try
        {
            AddPlaceholderVariant(installDir, build.ToString());
            var unsupported = $"{build.Major}.{build.Minor + 1}.1.1";
            var supported = $"{build.Major}.{build.Minor}.0.1";
            var artifactsRoot = Path.Combine(work, "artifacts");
            Directory.CreateDirectory(Path.Combine(artifactsRoot, unsupported));
            Directory.CreateDirectory(Path.Combine(artifactsRoot, supported));

            var (output, _) = Run(installDir, artifactsRoot, work, "--verbose");

            Assert.DoesNotContain("no shipped engine variant supports", output);
            Assert.Contains($"skipping cached BC {unsupported}", output);
            Assert.Contains($"selecting BC {supported}", output);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>An explicit --bc-version naming a minor no variant runs fails before provisioning,
    /// naming the supported minors.</summary>
    [Fact]
    public void ExplicitUnsupportedMinor_FailsLoudlyWithSupportedList()
    {
        var build = EngineBuild();
        var installDir = MirrorBinDir();
        var work = TestScratch.FlatDir("al-runner-4557-work-");
        try
        {
            AddPlaceholderVariant(installDir, build.ToString());
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts")).FullName;
            var requested = $"{build.Major}.{build.Minor + 1}";

            var (output, exit) = Run(installDir, artifactsRoot, work, "--bc-version", requested);

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.Contains($"ships no engine for BC {requested}", output);
            Assert.Contains($"Supported BC versions: {build.Major}.{build.Minor}.", output);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// A bare-major --bc-version on the issue's cache (only a newer, unsupported minor cached) is
    /// remapped to the newest shipped minor of that major, and the skipped build is named — rather
    /// than selecting the cached unsupported build and refusing it.
    /// </summary>
    [Fact]
    public void ExplicitBareMajor_OnlyUnsupportedNewerMinorCached_RemapsToShippedMinorAndNamesTheSkip()
    {
        var build = EngineBuild();
        var installDir = MirrorBinDir();
        var work = TestScratch.FlatDir("al-runner-4557-work-");
        try
        {
            AddPlaceholderVariant(installDir, build.ToString());
            var unsupported = $"{build.Major}.{build.Minor + 1}.1.1";
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts", unsupported)).Parent!.FullName;

            var (output, exit) = Run(installDir, artifactsRoot, work, "--bc-version", build.Major.ToString());

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.DoesNotContain("no shipped engine variant supports", output);
            Assert.Contains($"matches version '{build.Major}.{build.Minor}'", output);
            Assert.Contains($"skipping cached BC {unsupported}", output);
            Assert.Contains($"using BC {build.Major}.{build.Minor} instead", output);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// `provision` is exempt from the unsupported-minor refusal: it reaches provisioning for the
    /// named minor. Minor 99 is one no CDN publishes, and a dead proxy keeps the run offline, so
    /// the only thing measured is that the refusal did not fire first.
    /// </summary>
    [Fact]
    public void Provision_ExplicitUnsupportedMinor_IsNotRefusedBeforeProvisioning()
    {
        var build = EngineBuild();
        var installDir = MirrorBinDir();
        var work = TestScratch.FlatDir("al-runner-4557-work-");
        try
        {
            AddPlaceholderVariant(installDir, build.ToString());
            var artifactsRoot = Directory.CreateDirectory(Path.Combine(work, "artifacts")).FullName;
            var requested = $"{build.Major}.99";

            var (output, _) = RunWithSubcommand(installDir, artifactsRoot, work, "provision", "--bc-version", requested);

            Assert.DoesNotContain("ships no engine for BC", output);
            Assert.Contains($"[provision] no cached BC {requested}.x", output);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}
