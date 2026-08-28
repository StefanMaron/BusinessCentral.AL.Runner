// HomeDirectoryMissingLoudFailureTests — issue #2114.
//
// When $HOME names a directory that does NOT exist, .NET's
// Environment.GetFolderPath(SpecialFolder.UserProfile) silently returns "" instead of
// throwing (verified empirically against a probe binary: HOME=/missing -> "",
// HOME=/existing-empty -> the real path — unlike AutoProvisionDefaultTests' isolated-home
// fixture, which always mkdir's the dir first and so never exercises this branch).
// Every artifact/cache-root resolver then does Path.Combine(home, ".local", "share", ...),
// and Path.Combine("", "a", "b") == "a/b" — a bare RELATIVE path. That relative path used
// to survive every File.Exists/Directory.Exists probe downstream by silently resolving
// against the CWD, and only failed deep inside AssemblyLoadContext.LoadFromAssemblyPath (one
// of the few APIs that demands an absolute path) — by which point it was an unhandled
// exception that took the process down with SIGABRT and a core dump (exit 134) instead of a
// diagnostic, verified on `main` at c94c2f02 (see the issue body for the exact repro and
// stack trace).
//
// This spawns the REAL runner binary with $HOME pointed at a directory that is deliberately
// NEVER created, mirroring the issue's own reproduction command exactly
// (`--bc-version <ver> --no-auto-provision`, no bundle argument). Per
// .claude/rules/bc-behavior-tests-go-upstream.md this is a runner-mechanism claim (how the
// CLI resolves its own cache roots and reports the failure), not a BC-behaviour claim, so it
// belongs here, not in the al-language corpus.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class HomeDirectoryMissingLoudFailureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A version that will never exist on the CDN/cache — irrelevant here since the process
    // must fail at HOME resolution, well before it ever gets to asking "does this version
    // exist" (mirrors AutoProvisionDefaultTests' NonexistentVersion for the same reason).
    private const string SomeVersion = "1.2.3.4";

    private static (int ExitCode, string StdErr) RunWithHome(string homeValue, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append($" --bc-version {SomeVersion}");
        args.Append(" --no-auto-provision");
        foreach (var a in extraArgs) args.Append(' ').Append(a);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["HOME"] = homeValue;
        // Windows resolves SpecialFolder.UserProfile from USERPROFILE, not HOME — clear it
        // so this test exercises the same "profile resolution fails" branch on every OS this
        // suite runs on, not just POSIX.
        psi.Environment["USERPROFILE"] = homeValue;

        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 60s against a missing $HOME.");
        }
        proc.WaitForExit();
        lock (errSb) return (proc.ExitCode, errSb.ToString());
    }

    /// <summary>
    /// RED on unfixed `main`: $HOME names a directory that was never created, so
    /// Environment.GetFolderPath silently returns "" and every downstream
    /// Path.Combine("", ".local", ...) call produces a bare relative path. From this test's
    /// WorkingDirectory (the repo root, which has no coincidental `.local/share/al-runner/`
    /// tree of its own), the OLD code's Directory.Exists probe simply finds nothing and
    /// prints a generic "BC artifact root not found: .local/share/al-runner/artifacts. …"
    /// message — never mentioning $HOME, never saying an absolute path is required. This
    /// test's assertions are specific enough to fail against that generic message. GREEN
    /// after the fix: AlRunnerPaths.UserHome (issue #2114) rejects the non-rooted resolution
    /// before any Path.Combine happens, naming $HOME's raw value and requiring an absolute
    /// path — exit code stays a documented 2 (matching every other loud BcArtifacts
    /// failure in this file), never a raw .NET crash.
    /// </summary>
    [Fact]
    public void MissingHomeDirectory_FailsLoud_NamingHomeValue_NeverCrashesWithRawStack()
    {
        var missingHome = Path.Combine(Path.GetTempPath(), "al-runner-2114-missing-home", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missingHome)); // never created — the exact trigger

        var (exit, stderr) = RunWithHome(missingHome);

        // Documented, controlled exit — never the SIGABRT-driven 134 the issue reports.
        Assert.Equal(2, exit);

        // The loud diagnostic names the raw $HOME value and requires an absolute path —
        // the generic pre-fix "BC artifact root not found: .local/share/al-runner/artifacts"
        // message contains neither.
        Assert.Contains(missingHome, stderr);
        Assert.Contains("HOME", stderr);
        Assert.Contains("absolute", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--artifact-path", stderr);

        // Never a raw, undiagnosed .NET crash trace — the exact shape the issue reports
        // (System.ArgumentException from AssemblyLoadContext.LoadFromAssemblyPath, deep
        // inside a Cecil rewrite the user has no way to act on).
        Assert.DoesNotContain("System.ArgumentException", stderr);
        Assert.DoesNotContain("AssemblyLoadContext", stderr);
        Assert.DoesNotContain("is not an absolute path", stderr);
    }

    /// <summary>
    /// Differential half of the issue's own proof: $HOME existing (created ahead of time)
    /// but otherwise empty must behave exactly as it always has — a loud, path-naming
    /// "BC artifact root not found" message, unrelated to the new $HOME-specific
    /// diagnostic. Confirms the #2114 fix is additive: it only changes behaviour for the
    /// specific "profile resolution returned a non-rooted value" case, never for an
    /// ordinary empty-but-real cache directory.
    /// </summary>
    [Fact]
    public void ExistingEmptyHomeDirectory_BehaviourUnchanged_GenericArtifactRootMessage()
    {
        var existingHome = Path.Combine(Path.GetTempPath(), "al-runner-2114-existing-home", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existingHome);
        try
        {
            var (exit, stderr) = RunWithHome(existingHome);

            Assert.Equal(2, exit);
            Assert.Contains("BC artifact root not found", stderr);
            Assert.Contains("al-runner provision", stderr);
            // The new $HOME-specific diagnostic must NOT fire for a perfectly valid
            // (if empty) home directory — that would be a false positive.
            Assert.DoesNotContain("could not resolve an absolute home directory", stderr);
        }
        finally
        {
            try { Directory.Delete(existingHome, recursive: true); } catch { }
        }
    }
}
