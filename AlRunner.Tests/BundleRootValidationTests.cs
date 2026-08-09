// BundleRootValidationTests — RED->GREEN guard for issue #1713.
//
// The bug: a positional bundle path that does not exist reached EnumerateSuites /
// EnumerateSuitesBelow unchecked, so `Directory.EnumerateDirectories` threw a raw
// System.IO.DirectoryNotFoundException out of Main. The process died with a .NET
// stack trace and exit code 134 — the same code the CI matrix documents as "crash"
// (`1=test failure, 2=exec fail, 3=compile fail, 134/139=crash`), so a mistyped path
// was indistinguishable from a real runtime crash in a CI log. Worse, the crash landed
// AFTER ~6s of BC patch application, and after a `WARN: no app.json under …` line that
// had already noticed the directory was unusable and continued anyway.
//
// The fix validates the positional roots at argument-parse time (before the BC
// artifact selection, the Cecil re-exec and the patch pass) and fails with the
// documented exit code 2 — "a bundle could not execute (process-level error)", the
// same code every other CLI usage error in Program.cs already returns.
//
// Ghost-test trap avoided in both directions:
//   * a no-op "fix" that only swallows the exception (returning no suites) still fails
//     ExistingAndMissingRoot_* / NonexistentBundlePath_*, because those assert the
//     named message text and exit 2, not merely "did not crash";
//   * an over-eager guard that rejects valid inputs — the obvious way to get this
//     wrong — fails ExistingBundlePath_StillRuns_AndIsNotRejected and
//     Validate_ReturnsNull_* plus SubmoduleHint_IsOmitted_WhenSubmoduleIsCheckedOut.
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why the subprocess tests are serialized with the
// other runner-subprocess integration tests.
[Collection("server-serial")]
public sealed class BundleRootValidationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public BundleRootValidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-bundle-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stdCache = Path.Combine(home, ".local", "share", "al-runner", "artifacts");
        return Directory.Exists(stdCache) && Directory.EnumerateDirectories(stdCache).Any();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var argLine = TestBuildConfig.RunArgs(ProjectPath)
            + " " + string.Join(' ', args.Select(a => $"\"{a}\""));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(entireProcessTree: true); } catch { } }
        p.WaitForExit();
        return (p.ExitCode, so.ToString(), se.ToString());
    }

    // ── Positive direction: the missing path must be named, and the exit code must be
    // the documented 2 — never 134 and never a raw .NET stack trace. ──────────────

    /// <summary>
    /// Builds the issue's exact shape — a doubled prefix under an existing root — inside
    /// this test's temp directory, and returns (existingRoot, missingPath).
    /// <para>Deliberately NOT the literal repo path `tests/al-language/tests/al-language`
    /// from the issue: the corpus really does live at that nested path, so once the
    /// submodule is checked out (as it is in CI, and in any `--recurse-submodules` clone)
    /// that directory EXISTS and the runner runs it. A test asserting "no such directory"
    /// against it passes only where the submodule is missing — which is how it was written,
    /// in a worktree that had no submodule. The shape is what matters, not the name.</para>
    /// </summary>
    private (string ExistingRoot, string Missing) DoubledPrefix()
    {
        var existingRoot = Path.Combine(_root, "tests", "al-language");
        Directory.CreateDirectory(existingRoot);
        return (existingRoot, Path.Combine(existingRoot, "tests", "al-language"));
    }

    [Fact]
    public void NonexistentBundlePath_FailsLoudlyWithExitCode2()
    {
        // Exactly the shape from the issue: a doubled prefix under an existing root.
        var (_, bad) = DoubledPrefix();
        var (exit, stdout, stderr) = RunCli(bad);
        var all = stdout + stderr;

        Assert.Equal(2, exit);
        Assert.Contains($"al-runner: no such directory: {bad}", all);
        Assert.DoesNotContain("DirectoryNotFoundException", all);
        Assert.DoesNotContain("Unhandled exception", all);
        // The failure must land before the BC patch pass — that is the whole point of
        // validating at argument-parse time rather than only inside EnumerateSuites.
        Assert.DoesNotContain("BC runtime patches applied", all);
        Assert.DoesNotContain("WARN: no app.json under", all);
    }

    [Fact]
    public void NonexistentBundlePath_NamesDeepestExistingParent()
    {
        // The line that makes a doubled prefix obvious: the user sees where the path
        // stopped being real. See DoubledPrefix() for why this is a temp tree.
        var (existingRoot, bad) = DoubledPrefix();
        var (_, stdout, stderr) = RunCli(bad);
        var all = stdout + stderr;
        Assert.Contains($"deepest existing parent: {existingRoot}", all);
    }

    // ── Negative direction: a valid path must be completely unaffected. ───────────

    [Fact]
    public void ExistingAndMissingRoot_RejectsOnlyTheMissingOne()
    {
        // Siblings, not nested — so "no such directory: <good>" cannot accidentally match
        // as a prefix of the bad path's own line.
        var good = Path.Combine(_root, "good");
        Directory.CreateDirectory(good);
        var bad = Path.Combine(_root, "definitely-not-here");
        var (exit, stdout, stderr) = RunCli(good, bad);
        var all = stdout + stderr;

        Assert.Equal(2, exit);
        Assert.Contains($"al-runner: no such directory: {bad}", all);
        Assert.DoesNotContain($"no such directory: {good}", all);
    }

    [Fact]
    public void ExistingBundlePath_StillRuns_AndIsNotRejected()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifacts not present"); return; }

        // An existing but empty directory is a valid invocation: the runner must get all
        // the way through to its own "SKIP (no suites)" outcome and exit 0. An over-eager
        // guard (e.g. one that also rejects a directory with no app.json/.al files) turns
        // this green run red, which is exactly the regression this test exists to catch.
        var args = new List<string> { _root };
        args.AddRange(TestBuildConfig.BcVersionArg
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var (exit, stdout, stderr) = RunCli(args.ToArray());
        var all = stdout + stderr;

        Assert.DoesNotContain("no such directory", all);
        Assert.DoesNotContain("not a directory", all);
        Assert.Contains("SKIP (no suites)", all);
        Assert.Equal(0, exit);
    }

    // ── Unit-level coverage of the extracted validator. ───────────────────────────

    [Fact]
    public void Validate_ReturnsNull_ForExistingDirectory()
        => Assert.Null(BundleRootValidation.Validate(new[] { _root }));

    [Fact]
    public void Validate_ReturnsNull_ForNoRoots()
        => Assert.Null(BundleRootValidation.Validate(Array.Empty<string>()));

    [Fact]
    public void Validate_NamesMissingDirectoryAndDeepestExistingParent()
    {
        var missing = Path.Combine(_root, "a", "b", "c");
        var msg = BundleRootValidation.Validate(new[] { missing });

        Assert.NotNull(msg);
        Assert.Contains($"al-runner: no such directory: {missing}", msg);
        Assert.Contains($"deepest existing parent: {_root}", msg);
    }

    [Fact]
    public void Validate_ReportsFileAsNotADirectory()
    {
        var file = Path.Combine(_root, "app.json");
        File.WriteAllText(file, "{}");
        var msg = BundleRootValidation.Validate(new[] { file });

        Assert.NotNull(msg);
        Assert.Contains($"al-runner: not a directory: {file}", msg);
        // A file is not a missing path — do not emit the misleading "no such directory".
        Assert.DoesNotContain("no such directory", msg);
    }

    [Fact]
    public void Validate_WalksPastAGoodRoot_AndReportsTheBadOneAfterIt()
    {
        var good = Path.Combine(_root, "good");
        Directory.CreateDirectory(good);
        var missing = Path.Combine(_root, "gone");
        var msg = BundleRootValidation.Validate(new[] { good, missing });

        Assert.NotNull(msg);
        Assert.Contains($"al-runner: no such directory: {missing}", msg);
        Assert.DoesNotContain($"no such directory: {good}", msg);
    }

    [Fact]
    public void SubmoduleHint_IsEmitted_WhenSubmoduleIsNotCheckedOut()
    {
        // A repo whose .gitmodules declares tests/al-language, with the submodule
        // directory present but EMPTY — exactly what `git clone` without
        // `--recurse-submodules` leaves behind.
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "tests", "al-language"));
        File.WriteAllText(Path.Combine(repo, ".gitmodules"), """
        [submodule "tests/al-language"]
        	path = tests/al-language
        	url = https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests.git
        """);

        var target = Path.Combine(repo, "tests", "al-language", "tests");
        var msg = BundleRootValidation.Validate(new[] { target });

        Assert.NotNull(msg);
        Assert.Contains("git submodule update --init --recursive", msg);
        Assert.Contains(Path.Combine(repo, "tests", "al-language"), msg);
    }

    [Fact]
    public void SubmoduleHint_IsOmitted_WhenSubmoduleIsCheckedOut()
    {
        // Same repo shape, but the submodule HAS content — so the missing path is a
        // typo, not an uninitialised submodule, and the hint would send the user down
        // the wrong road. This is the doubled-prefix case from issue #1713 itself.
        var repo = Path.Combine(_root, "repo2");
        var sub = Path.Combine(repo, "tests", "al-language");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "app.json"), "{}");
        File.WriteAllText(Path.Combine(repo, ".gitmodules"), """
        [submodule "tests/al-language"]
        	path = tests/al-language
        	url = https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests.git
        """);

        var msg = BundleRootValidation.Validate(
            new[] { Path.Combine(sub, "tests", "al-language") });

        Assert.NotNull(msg);
        Assert.DoesNotContain("git submodule update", msg);
    }

    [Fact]
    public void SubmoduleHint_IsOmitted_WhenNoGitmodulesDeclaresThePath()
    {
        var msg = BundleRootValidation.Validate(new[] { Path.Combine(_root, "nope") });
        Assert.NotNull(msg);
        Assert.DoesNotContain("git submodule update", msg);
    }
}
