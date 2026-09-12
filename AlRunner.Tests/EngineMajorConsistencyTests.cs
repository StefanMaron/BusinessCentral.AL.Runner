// EngineMajorConsistencyTests — issue #4031.
//
// A single-build runner (no variants/ shipped) can only run the BC major it was built
// against. The guard used to read the major from bin/Microsoft.Dynamics.Nav.Ncl.dll, which
// Directory.Build.targets strips from the build output, and the shadow child's copy comes from
// the SELECTED artifact, so it agreed with the selection by construction: the guard never
// fired, and a cross-major selection ended in CS1705 (bundle run) or went unchecked
// (--precompile). The guard now reads the baked-in BcEngineVersion.
//
// Neither subprocess test needs real artifacts: an empty version-named directory under
// AL_RUNNER_ARTIFACTS_ROOT is enough for selection, and the guard runs before anything reads it.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class EngineMajorConsistencyTests
{
    private const string MismatchPrefix = "BC engine/version mismatch:";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static Version EngineBuild() => BcArtifacts.EngineBuiltVersion()
        ?? throw new InvalidOperationException(
            "EngineBuiltVersion() is null — rebuild AlRunner before running this test.");

    // ------------------------------------------------------------ the decision, in-process

    [Fact]
    public void DescribeEngineMajorMismatch_DifferentMajor_NamesBothVersions()
    {
        var msg = BcArtifacts.DescribeEngineMajorMismatch(
            new Version("28.1.49838.53910"), new Version("27.0.38460.53260"), shippedVariantCount: 0);

        Assert.NotNull(msg);
        Assert.StartsWith(MismatchPrefix, msg);
        Assert.Contains("built for BC 28.1.49838.53910", msg);
        Assert.Contains("selected BC version is 27.0.38460.53260", msg);
    }

    [Fact]
    public void DescribeEngineMajorMismatch_SameMajorDifferentMinor_ReturnsNull()
    {
        // Minor skew is the separate warning's business (#2008), never a refusal.
        Assert.Null(BcArtifacts.DescribeEngineMajorMismatch(
            new Version("28.1.49838.53910"), new Version("28.4.53241.53955"), shippedVariantCount: 0));
    }

    [Fact]
    public void DescribeEngineMajorMismatch_VariantsShipped_ReturnsNull()
    {
        // A multi-variant install serves other majors by swapping engines; the variant
        // resolver is the authority there, and the running process's own build is not.
        Assert.Null(BcArtifacts.DescribeEngineMajorMismatch(
            new Version("28.1.49838.53910"), new Version("27.0.38460.53260"), shippedVariantCount: 2));
    }

    [Fact]
    public void DescribeEngineMajorMismatch_UnstampedBinary_ReturnsNull()
    {
        Assert.Null(BcArtifacts.DescribeEngineMajorMismatch(
            null, new Version("27.0.38460.53260"), shippedVariantCount: 0));
    }

    // ------------------------------------------------------------ both call sites, subprocess

    [Fact]
    public void BundleRun_CrossMajorSelection_RefusesWithExit2()
    {
        var foreign = ForeignMajorVersion();
        var (root, home) = FakeArtifactsRoot(foreign);
        try
        {
            var (exit, output) = Run(root, home, $"--bc-version {foreign} --no-auto-provision");

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.Contains($"BC version selection failed: {MismatchPrefix}", output);
            Assert.Contains($"selected BC version is {foreign}", output);
        }
        finally { Cleanup(root, home); }
    }

    [Fact]
    public void Precompile_CrossMajorManifest_RefusesWithExit2_AndWritesNoDll()
    {
        var foreign = ForeignMajorVersion();
        var (root, home) = FakeArtifactsRoot(foreign);
        var work = TestScratch.FlatDir("al-runner-4031-work-");
        try
        {
            var appPath = WriteAppPackage(work, foreign);
            var outputDll = Path.Combine(work, "Refused.dll");

            var (exit, output) = Run(root, home, $"--precompile \"{appPath}\" --out \"{outputDll}\"");

            Assert.True(exit == 2, $"exit {exit}.\n{output}");
            Assert.Contains($"BC version selection failed: {MismatchPrefix}", output);
            Assert.Contains($"selected BC version is {foreign}", output);
            Assert.False(File.Exists(outputDll), $"--precompile refused but wrote {outputDll}.\n{output}");
        }
        finally
        {
            Cleanup(root, home);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static Version ForeignMajorVersion() => new(EngineBuild().Major + 1, 0, 0, 0);

    private static (string Root, string Home) FakeArtifactsRoot(Version version)
    {
        var root = TestScratch.FlatDir("al-runner-4031-root-");
        Directory.CreateDirectory(Path.Combine(root, version.ToString()));
        var home = TestScratch.FlatDir("al-runner-4031-home-");
        Directory.CreateDirectory(home);
        return (root, home);
    }

    private static void Cleanup(string root, string home)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
        try { Directory.Delete(home, recursive: true); } catch { }
    }

    private static string WriteAppPackage(string dir, Version version)
    {
        var name = "Precompile4031";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{Guid.NewGuid()}" Name="{name}" Publisher="Repro4031" Version="{version}"
                   Application="{version}" Platform="{version}"/>
              <Dependencies/>
            </Package>
            """;
        Directory.CreateDirectory(dir);
        var appPath = Path.Combine(dir, name + ".app");
        using var fs = new FileStream(appPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var es = zip.CreateEntry("NavxManifest.xml").Open())
            es.Write(Encoding.UTF8.GetBytes(xml));
        return appPath;
    }

    private static (int Exit, string Output) Run(string artifactsRoot, string home, string runnerArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + " " + runnerArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment[BcArtifacts.ArtifactsRootEnvVar] = artifactsRoot;
        psi.Environment["HOME"] = home;
        psi.Environment["USERPROFILE"] = home;
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner {runnerArgs} did not exit within 120s.");
        }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
