// PrecompileEngineVariantSelectionTests — issue #2190.
//
// `--precompile` dispatches near the top of Main, before the bundle-run flow selects a
// per-BC-minor engine variant. On a multi-variant install whose shipped variants do not cover
// the selected BC version, the bundle run refuses with exit 2; `--precompile` used to compile
// anyway against whichever engine it happened to be. These tests fake a `variants/` directory
// in a private mirror of the build output and drive the real subcommand.
//
// The decision itself is EngineVariants.Resolve (unit-tested in EngineVariantsTests); this
// file proves `--precompile` actually consults it.
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompileEngineVariantSelectionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string VariantHopLine =
        "[reexec] Re-execing into a shadow runtime dir with the matching BC-minor engine variant";

    private static Version EngineBuild() => BcArtifacts.EngineBuiltVersion()
        ?? throw new InvalidOperationException(
            "EngineBuiltVersion() is null — rebuild AlRunner before running this test.");

    private static string MirrorBinDir()
    {
        var originalBinDir = Path.Combine(
            RepoRoot, "AlRunner", "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework);
        var privateDir = Directory.CreateDirectory(TestScratch.FlatDir("al-runner-2190-mirror-")).FullName;
        NclShadowRuntime.MirrorInstallDirectory(originalBinDir, privateDir);
        return privateDir;
    }

    /// <summary>A variant directory whose al-runner.dll is a placeholder: enough for discovery,
    /// never executed.</summary>
    private static void AddPlaceholderVariant(string installDir, string version)
    {
        var dir = Path.Combine(installDir, EngineVariants.VariantsDirName, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, EngineVariants.EntryAssemblyFileName), "placeholder");
    }

    /// <summary>A variant directory holding a runnable copy of this build's entry-assembly set.</summary>
    private static void AddRunnableVariant(string installDir, string version)
    {
        var dir = Path.Combine(installDir, EngineVariants.VariantsDirName, version);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "al-runner.dll", "al-runner.pdb", "al-runner.deps.json", "al-runner.runtimeconfig.json" })
        {
            var src = Path.Combine(installDir, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f));
        }
    }

    private static string WriteAppPackage(string dir, int tableId)
    {
        var bcVersion = EngineBuild().ToString();
        var name = $"Precompile2190_{tableId}";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{Guid.NewGuid()}" Name="{name}" Publisher="Repro2190" Version="{bcVersion}"
                   Application="{bcVersion}" Platform="{bcVersion}"/>
              <Dependencies/>
            </Package>
            """;
        Directory.CreateDirectory(dir);
        var appPath = Path.Combine(dir, name + ".app");
        using (var fs = new FileStream(appPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var es = zip.CreateEntry("NavxManifest.xml").Open())
                es.Write(Encoding.UTF8.GetBytes(xml));
            using (var es = zip.CreateEntry("src/Repro.Table.al").Open())
                es.Write(Encoding.UTF8.GetBytes($$"""
                    table {{tableId}} "Repro 2190 Tab {{tableId}}"
                    {
                        fields
                        {
                            field(1; "No."; Code[20]) { }
                        }
                        keys { key(PK; "No.") { Clustered = true; } }
                    }
                    """));
        }
        return appPath;
    }

    private static (string Output, int Exit) RunPrecompile(string dllPath, string appPath, string outputDll)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --precompile \"{appPath}\" --out \"{outputDll}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // --precompile's own sub-arg parser ignores --verbose; the env var is what surfaces [reexec].
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(300_000), "--precompile did not exit within 300s");
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>
    /// #2190, the defect: an install whose shipped variants cannot serve the selected BC version
    /// must refuse `--precompile` the way it refuses a bundle run — exit 2, naming the selected
    /// version and what is available — and write no DLL.
    /// </summary>
    [SkippableFact]
    public void Precompile_NoShippedVariantSupportsSelectedBc_RefusesWithExit2()
    {
        TestArtifacts.SkipIfMissing();

        var installDir = MirrorBinDir();
        var workDir = TestScratch.FlatDir("al-runner-2190-work-");
        try
        {
            AddPlaceholderVariant(installDir, "99.0.0.0");
            var appPath = WriteAppPackage(workDir, 62190);
            var outputDll = Path.Combine(workDir, "Refused.dll");

            var (output, exit) = RunPrecompile(Path.Combine(installDir, "al-runner.dll"), appPath, outputDll);

            Assert.Equal(2, exit);
            Assert.Contains($"no shipped engine variant supports BC {EngineBuild()}", output);
            Assert.Contains("Available variants: 99.0.0.0", output);
            Assert.False(File.Exists(outputDll),
                $"--precompile refused but still wrote {outputDll}.\n{output}");
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The other direction: a variant that is exactly the running engine needs no hop, so
    /// `--precompile` compiles in process and announces no variant swap.
    /// </summary>
    [SkippableFact]
    public void Precompile_VariantIsTheRunningEngine_CompilesWithoutVariantHop()
    {
        TestArtifacts.SkipIfMissing();

        var installDir = MirrorBinDir();
        var workDir = TestScratch.FlatDir("al-runner-2190-work-");
        try
        {
            AddPlaceholderVariant(installDir, EngineBuild().ToString());
            var appPath = WriteAppPackage(workDir, 62191);
            var outputDll = Path.Combine(workDir, "Exact.dll");

            var (output, exit) = RunPrecompile(Path.Combine(installDir, "al-runner.dll"), appPath, outputDll);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(outputDll), $"no DLL written at {outputDll}.\n{output}");
            Assert.Equal(0, CountOccurrences(output, VariantHopLine));
            Assert.DoesNotContain("engine variant was built against", output);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// A same-minor variant of a different build is the degraded match: `--precompile` warns,
    /// hops into it exactly once, and still produces the DLL.
    /// </summary>
    [SkippableFact]
    public void Precompile_SameMinorDifferentBuildVariant_WarnsAndHopsOnce()
    {
        TestArtifacts.SkipIfMissing();

        var installDir = MirrorBinDir();
        var workDir = TestScratch.FlatDir("al-runner-2190-work-");
        try
        {
            var build = EngineBuild();
            var degradedVersion = new Version(build.Major, build.Minor, 0, 0).ToString();
            AddRunnableVariant(installDir, degradedVersion);
            var appPath = WriteAppPackage(workDir, 62192);
            var outputDll = Path.Combine(workDir, "Degraded.dll");

            var (output, exit) = RunPrecompile(Path.Combine(installDir, "al-runner.dll"), appPath, outputDll);

            Assert.True(exit == 0, $"exit {exit}.\n{output}");
            Assert.True(File.Exists(outputDll), $"no DLL written at {outputDll}.\n{output}");
            Assert.True(CountOccurrences(output, VariantHopLine) == 1, $"variant hop line count != 1.\n{output}");
            Assert.True(CountOccurrences(output,
                $"engine variant was built against {degradedVersion}, not the selected {build}") == 1,
                $"degraded warning count != 1.\n{output}");
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
