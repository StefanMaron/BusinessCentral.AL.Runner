// UnlinkedWorkingDirectoryTests — issue #3120.
//
// A process whose working directory has been removed (`cd d && rm -rf d && exec …`) has a
// cwd getcwd(2) cannot name, so Environment.CurrentDirectory throws FileNotFoundException.
// Every bundle path the runner needs is absolute by then, so the run can complete: the
// working directory only ever fed a secondary expectations probe, a bundle's display label
// and coverage's relative filenames. These tests pin that the run does complete, and what
// each of those three says instead.
//
// Not a corpus claim: an unreadable working directory is an OS/process state AL can neither
// produce nor observe. The unlinked-cwd launcher is copied from CacheRootStartupFailureTests,
// whose helpers are private to that file.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class UnlinkedWorkingDirectoryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    // ── unit: the helper and the resolver ─────────────────────────────────────────────

    [Fact]
    public void TryGet_WhenTheReaderThrowsFileNotFound_ReturnsNull()
    {
        Assert.Null(WorkingDirectory.TryGet(() => throw new FileNotFoundException("Unable to find the specified file.")));
    }

    [Fact]
    public void TryGet_WhenTheReaderThrowsUnauthorizedAccess_ReturnsNull()
    {
        Assert.Null(WorkingDirectory.TryGet(() => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void TryGet_WhenTheReaderAnswers_ReturnsItsValue()
    {
        Assert.Equal("/some/dir", WorkingDirectory.TryGet(() => "/some/dir"));
    }

    [Fact]
    public void DisplayPath_WithAReadableWorkingDirectory_IsRelativeToIt()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "/", "wd"));
        Assert.Equal($"bundles{sep}a", WorkingDirectory.DisplayPath(Path.Combine(root, "bundles", "a"), root));
    }

    [Fact]
    public void DisplayPath_WithNoWorkingDirectory_IsTheAbsolutePathUnchanged()
    {
        var abs = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "/", "wd", "bundles", "a"));
        Assert.Equal(abs, WorkingDirectory.DisplayPath(abs, currentDirectory: null));
    }

    [Fact]
    public void Resolve_WithNoWorkingDirectory_StillWalksUpFromTheBundle()
    {
        var root = TestScratch.Dir("al-runner-unlinked-cwd-resolve");
        var expectations = Path.Combine(root, "tests", "expectations");
        var bundle = Path.Combine(root, "suites", "one");
        Directory.CreateDirectory(expectations);
        Directory.CreateDirectory(bundle);

        Assert.Equal(expectations, ExpectationsDirectoryResolution.Resolve(new[] { bundle }, currentDirectory: null));
    }

    [Fact]
    public void Resolve_WithNoWorkingDirectoryAndNoManifest_ReturnsNull()
    {
        var bundle = TestScratch.Dir("al-runner-unlinked-cwd-resolve-none");
        Directory.CreateDirectory(bundle);

        Assert.Null(ExpectationsDirectoryResolution.Resolve(new[] { bundle }, currentDirectory: null));
    }

    [Fact]
    public void NotFoundMessage_WithAReadableWorkingDirectory_NamesTheCwdCandidate()
    {
        var cwd = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "/", "wd"));
        var message = ExpectationsDirectoryResolution.BuildNotFoundMessage(cwd, bundleCount: 2);

        Assert.Contains($"probed {Path.Combine(cwd, "tests", "expectations")} and the ancestor tree of 2 bundle path(s)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("working directory could not be read", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NotFoundMessage_WithNoWorkingDirectory_SaysItWasNotProbed()
    {
        var message = ExpectationsDirectoryResolution.BuildNotFoundMessage(currentDirectory: null, bundleCount: 1);

        Assert.Contains("probed the ancestor tree of 1 bundle path(s)", message, StringComparison.Ordinal);
        Assert.Contains("the working directory could not be read, so it was not probed", message, StringComparison.Ordinal);
        Assert.Contains("Pass --expectations DIR", message, StringComparison.Ordinal);
    }

    // ── end to end: a run from an unlinked working directory ─────────────────────────

    /// <summary>
    /// The reported crash (Program.cs expectations auto-probe, exit 134) and the next one a
    /// run met once past it (the per-bundle label, also exit 134). The loaded-from line is
    /// the discriminator for the probe: it proves the bundle-ancestor walk found the repo's
    /// manifest rather than the probe being skipped.
    /// </summary>
    [SkippableFact]
    public void AutoProbe_FromAnUnlinkedWorkingDirectory_FindsTheBundleAncestorManifestAndRunCompletes()
    {
        SkipUnlessLinux();
        TestArtifacts.SkipIfMissing();

        var (exit, output) = RunFromUnlinkedWorkingDirectory(new[] { FixtureDir, "--no-cache" });

        AssertNotAnUnhandledCrash(exit, output);
        Assert.Contains(
            $"[expectations] loaded ", output, StringComparison.Ordinal);
        Assert.Contains(
            $" from {Path.Combine(RepoRoot, "tests", "expectations")}", output, StringComparison.Ordinal);
        Assert.True(exit == 0 && output.Contains("pass:        1", StringComparison.Ordinal),
            $"the run must complete from an unlinked working directory:\n{output}");
        // The bundle label falls back to the absolute path.
        Assert.Contains($"[1/1] {FixtureDir} ", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The "no manifest found" branch, which read the working directory a second time to
    /// name its candidate. Reached only with a bundle that has no tests/expectations
    /// ancestor, so the fixture is copied out of the repository.
    /// </summary>
    [SkippableFact]
    public void AutoProbe_FromAnUnlinkedWorkingDirectory_WithNoManifestAnywhere_SaysTheWorkingDirectoryWasNotProbed()
    {
        SkipUnlessLinux();
        TestArtifacts.SkipIfMissing();

        var bundle = CopyFixtureOutOfTheRepository("al-runner-unlinked-cwd-nomanifest");
        Assert.Null(ExpectationsDirectoryResolution.Resolve(new[] { bundle }, currentDirectory: null));

        var (exit, output) = RunFromUnlinkedWorkingDirectory(new[] { bundle, "--no-cache" });

        AssertNotAnUnhandledCrash(exit, output);
        Assert.Contains("[expectations] no tests/expectations manifest found", output, StringComparison.Ordinal);
        Assert.Contains("the working directory could not be read, so it was not probed", output, StringComparison.Ordinal);
        Assert.True(exit == 0 && output.Contains("pass:        1", StringComparison.Ordinal),
            $"the run must complete from an unlinked working directory:\n{output}");
    }

    /// <summary>
    /// The coverage source map is built relative to the working directory. With none, the
    /// report names each file by its absolute path, which the report's <c>&lt;source&gt;.</c>
    /// root still resolves.
    /// </summary>
    [SkippableFact]
    public void Coverage_FromAnUnlinkedWorkingDirectory_WritesAbsoluteFilenames()
    {
        SkipUnlessLinux();
        TestArtifacts.SkipIfMissing();

        var bundle = CopyFixtureOutOfTheRepository("al-runner-unlinked-cwd-coverage");
        var coverageDir = TestScratch.Dir("al-runner-unlinked-cwd-coverage-out");
        Directory.CreateDirectory(coverageDir);
        var coverageOut = Path.Combine(coverageDir, "cobertura.xml");

        var (exit, output) = RunFromUnlinkedWorkingDirectory(
            new[] { bundle, "--no-cache", "--coverage", "--coverage-out", coverageOut });

        AssertNotAnUnhandledCrash(exit, output);
        Assert.True(exit == 0 && output.Contains("pass:        1", StringComparison.Ordinal),
            $"the run must complete from an unlinked working directory:\n{output}");
        Assert.True(File.Exists(coverageOut), $"coverage report must be written:\n{output}");
        var xml = File.ReadAllText(coverageOut);
        Assert.Contains(
            $"filename=\"{Path.Combine(bundle, "XRecProbeTests.Codeunit.al").Replace('\\', '/')}\"",
            xml, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static string CopyFixtureOutOfTheRepository(string prefix)
    {
        var bundle = TestScratch.Dir(prefix);
        Directory.CreateDirectory(bundle);
        foreach (var file in Directory.GetFiles(FixtureDir))
            File.Copy(file, Path.Combine(bundle, Path.GetFileName(file)));
        return bundle;
    }

    private static void SkipUnlessLinux()
        => Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            "unlinking a live working directory is a Unix behaviour; Windows refuses to remove it");

    private static void AssertNotAnUnhandledCrash(int exit, string output)
    {
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
        // 128 + SIGABRT(6): what CoreCLR's default unhandled-exception handler produces.
        Assert.NotEqual(134, exit);
    }

    private static (int Exit, string Output) RunFromUnlinkedWorkingDirectory(string[] runnerArgs)
    {
        var doomed = Path.Combine(TestScratch.Dir("al-runner-unlinked-cwd"), "doomed");
        Directory.CreateDirectory(doomed);

        var extra = TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var script = new StringBuilder();
        script.Append("rm -rf ").Append(ShellQuote(doomed)).Append(" && exec dotnet ");
        script.Append(ShellQuote(TestBuildConfig.RunArgs(ProjectPath).Trim('"')));
        foreach (var a in runnerArgs.Concat(extra)) script.Append(' ').Append(ShellQuote(a));

        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = doomed,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script.ToString());

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
