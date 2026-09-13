// ExplicitEngineMinorWarningOncePerInvocationTests — issue #4038.
//
// An explicit --bc-version naming a different minor of the engine's own major prints the
// KNOWN-DEGRADED warning. On a single-build install the runner re-execs into a shadow
// runtime, and the child repeats startup, so a warning written straight to stderr prints
// once per generation. This spawns the real runner across that hop and counts.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExplicitEngineMinorWarningOncePerInvocationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string MinimalBundle =
        Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp-table");

    private const string WarningFragment = "was explicitly selected (--bc-version/--artifact-path)";

    private const int SpawnTimeoutMs = 180_000;

    private static (int ExitCode, string Output) Run(string artifactsRoot, string cacheDir, params string[] args)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var a in args) sb.Append(' ').Append(a);
        sb.Append($" --cache \"{cacheDir}\" \"{MinimalBundle}\"");

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
        if (!proc.WaitForExit(SpawnTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within {SpawnTimeoutMs / 1000}s");
        }
        proc.WaitForExit();
        lock (output) return (proc.ExitCode, output.ToString());
    }

    /// <summary>
    /// The engine's own artifacts, reachable under a second name that differs only in the
    /// minor. Selecting that name is a same-major, different-minor explicit selection, which
    /// is exactly the warning's condition, and it still runs cleanly because the files are
    /// the engine's own.
    /// </summary>
    private static (string Root, Version Selected) BuildArtifactsRootWithADifferentMinor(Version engineVersion)
    {
        var realHome = TestArtifacts.HomeDir()
            ?? throw new InvalidOperationException("Cannot determine this machine's HOME.");
        var realEngineDir = Path.Combine(TestArtifacts.StandardCacheDir(realHome), engineVersion.ToString());
        TestArtifacts.SkipIfDirectoryMissing(realEngineDir, $"BC {engineVersion} artifacts");

        var root = TestScratch.Dir("al-runner-explicit-minor-warning-once");
        Directory.CreateDirectory(root);
        // +50 keeps the alias clear of every real BC minor, so nothing else keys on it.
        var selected = new Version(engineVersion.Major, engineVersion.Minor + 50, engineVersion.Build, engineVersion.Revision);
        Directory.CreateSymbolicLink(Path.Combine(root, selected.ToString()), realEngineDir);
        return (root, selected);
    }

    /// <summary>
    /// Same alias, but a real directory whose entries symlink to the engine's artifacts
    /// except Microsoft.Dynamics.Nav.Ncl.dll, which is random bytes. That stands in for a
    /// different minor whose Ncl the Cecil rewrite cannot read: the run crashes before the
    /// queued startup lines are flushed, which is when the warning matters most.
    /// </summary>
    private static (string Root, Version Selected) BuildArtifactsRootWithACorruptNcl(Version engineVersion)
    {
        var realHome = TestArtifacts.HomeDir()
            ?? throw new InvalidOperationException("Cannot determine this machine's HOME.");
        var realEngineDir = Path.Combine(TestArtifacts.StandardCacheDir(realHome), engineVersion.ToString());
        TestArtifacts.SkipIfDirectoryMissing(realEngineDir, $"BC {engineVersion} artifacts");

        var root = TestScratch.Dir("al-runner-explicit-minor-warning-corrupt-ncl");
        var selected = new Version(engineVersion.Major, engineVersion.Minor + 50, engineVersion.Build, engineVersion.Revision);
        var aliasDir = Path.Combine(root, selected.ToString());
        Directory.CreateDirectory(aliasDir);
        const string NclName = "Microsoft.Dynamics.Nav.Ncl.dll";
        foreach (var entry in Directory.EnumerateFileSystemEntries(realEngineDir))
        {
            var name = Path.GetFileName(entry);
            if (name == NclName) continue;
            var link = Path.Combine(aliasDir, name);
            if (Directory.Exists(entry)) Directory.CreateSymbolicLink(link, entry);
            else File.CreateSymbolicLink(link, entry);
        }
        var garbage = new byte[64 * 1024];
        new Random(4038).NextBytes(garbage);
        File.WriteAllBytes(Path.Combine(aliasDir, NclName), garbage);
        return (root, selected);
    }

    [SkippableFact]
    public void ExplicitDifferentMinor_RunCrashesBeforeFlush_StillPrintsWarning_ExactlyOnce()
    {
        var engineVersion = BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engineVersion == null,
            "no baked-in BcEngineVersion on this build — nothing to compare a selection against.");
        TestArtifacts.SkipIf(EngineVariants.Discover(AppContext.BaseDirectory).Count > 0,
            "this install ships engine variants, and the warning is gated off for that shape (#2037).");

        var (root, selected) = BuildArtifactsRootWithACorruptNcl(engineVersion!);
        var cacheDir = TestScratch.Dir("al-runner-explicit-minor-warning-corrupt-ncl-cache");
        try
        {
            var (exit, output) = Run(root, cacheDir, "--no-auto-provision", "--bc-version", selected.ToString());

            Assert.True(exit != 0, $"a random-bytes Ncl.dll must not run cleanly. exit={exit}\n{output}");
            Assert.Contains("BadImageFormatException", output, StringComparison.Ordinal);

            var count = Regex.Matches(output, Regex.Escape(WarningFragment)).Count;
            Assert.True(count == 1,
                $"the KNOWN-DEGRADED warning must print exactly once even when the run crashes before the " +
                $"startup flush; printed {count} time(s). exit={exit}\n{output}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void ExplicitDifferentMinor_PrintsKnownDegradedWarning_ExactlyOnce()
    {
        var engineVersion = BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engineVersion == null,
            "no baked-in BcEngineVersion on this build — nothing to compare a selection against.");
        TestArtifacts.SkipIf(EngineVariants.Discover(AppContext.BaseDirectory).Count > 0,
            "this install ships engine variants, and the warning is gated off for that shape (#2037).");

        var (root, selected) = BuildArtifactsRootWithADifferentMinor(engineVersion!);
        var cacheDir = TestScratch.Dir("al-runner-explicit-minor-warning-once-cache");
        try
        {
            var (exit, output) = Run(root, cacheDir, "--no-auto-provision", "--bc-version", selected.ToString());

            Assert.True(exit == 0, $"expected a clean run against the aliased engine artifacts. exit={exit}\n{output}");
            Assert.Contains($"[bc] selected BC {selected} (", output, StringComparison.Ordinal);

            var count = Regex.Matches(output, Regex.Escape(WarningFragment)).Count;
            Assert.True(count == 1,
                $"the KNOWN-DEGRADED warning must print exactly once per invocation; printed {count} time(s).\n{output}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
