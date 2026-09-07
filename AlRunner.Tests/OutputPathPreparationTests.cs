// OutputPathPreparationTests — issue #2403.
//
// The defect: --out (and, in the same shape, --output-junit and --coverage-out) opened its
// file only AFTER the run finished, so a missing parent directory surfaced as an unhandled
// DirectoryNotFoundException at the last step — exit 134, a stack trace, and the whole run's
// classification thrown away. Measured on the Microsoft Tests-SINGLESERVER bucket: 103
// seconds and 834 classified results, lost to a mistyped directory.
//
// Two claims are proved here, and they are different claims:
//
//   * a path whose parent does not exist WORKS — the directory is created and the file is
//     written with real content, so the common case stops being an error at all;
//   * a path that genuinely cannot be prepared is refused AT ARGUMENT-PARSE TIME, before any
//     work happens, with a diagnostic naming the flag and the path and exit 2 — never an
//     unhandled exception, and never after paying for the run.
//
// The subprocess tests below deliberately assert the *timing* of the refusal (no run summary
// on stdout), not just its existence: failing late with a nice message would still cost the
// run, which is the entire complaint in the issue.

using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class OutputPathPreparationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // Fixture with no "application" property (see no-base-app-in-csharp-tests.md) — the
    // Base Application floor costs ~70s cold per spawn and nothing here needs it. Its one
    // test passes, so a non-zero exit below is unambiguously about the output path.
    private static string Bundle => Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    private static string PackageCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".al-runner", "platform-apps");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner"))
            + " " + string.Join(' ', args.Select(a => $"\"{a}\""));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Both pipes drained concurrently — the runner writes heavily to stderr, and reading
        // stdout to the end first deadlocks once that buffer fills. Same reasoning as
        // CliDocumentationTests.RunCli.
        using var proc = Process.Start(psi)!;
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(240_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within 240s for: {string.Join(' ', args)}");
        }
        proc.WaitForExit();
        lock (outSb) lock (errSb) return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    // TestScratch.FlatDir, not Path.GetTempPath() by hand: ScratchDirs then records an owner
    // for the directory, so a killed test host's leftovers are reclaimed rather than leaked
    // (#2706/#2743, enforced by ScratchDirOwnershipGuardTests). FlatDir reserves the path but
    // deliberately does not create the leaf, so the CreateDirectory stays.
    private static string FreshTempDir()
    {
        var dir = TestScratch.FlatDir("al-runner-outpath-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- TryPrepare: the unit-level contract -------------------------------------------

    /// <summary>Positive: a parent that does not exist is CREATED, several levels deep, and
    /// the call reports success. This is the case the issue ranks first.</summary>
    [Fact]
    public void TryPrepare_CreatesAMissingParentDirectory()
    {
        var root = FreshTempDir();
        try
        {
            var target = Path.Combine(root, "a", "b", "c", "results.json");
            Assert.False(Directory.Exists(Path.GetDirectoryName(target)!));

            Assert.Null(OutputPaths.TryPrepare("--out", target));

            Assert.True(Directory.Exists(Path.GetDirectoryName(target)!));
            // The FILE is not created — only its parent. A pre-created empty file would be
            // read by a consumer as this run's (empty) output.
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A bare filename has no directory part. GetDirectoryName answers "" rather
    /// than null for it, and treating that as a directory to create would throw — so this
    /// pins that the no-op branch is taken and reports success.</summary>
    [Fact]
    public void TryPrepare_AcceptsABareFilenameWithNoDirectoryPart()
    {
        Assert.Null(OutputPaths.TryPrepare("--out", "results.json"));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "results.json")));
    }

    /// <summary>Negative: a FILE sitting where the parent directory must go cannot be made
    /// into a directory. The diagnostic names the flag and the offending path — both, because
    /// a run can pass three different output flags and "not a usable output path" alone would
    /// not say which one to fix.</summary>
    [Fact]
    public void TryPrepare_RefusesWhenAFileOccupiesTheParentPath_AndNamesTheFlagAndPath()
    {
        var root = FreshTempDir();
        try
        {
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var target = Path.Combine(blocker, "results.json");

            var message = OutputPaths.TryPrepare("--output-junit", target);

            Assert.NotNull(message);
            Assert.Contains("--output-junit", message);
            Assert.Contains(target, message);
            Assert.Contains("is not a usable output path", message);
            // Says what to DO, not merely what went wrong.
            Assert.Contains("parent directory can be created and written to", message!);
            // The framework message already ends in '.'; the sentence must not read "..".
            Assert.DoesNotContain("..", message!.Replace(target, "").Replace("--output-junit", ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Negative: a structurally impossible path is refused by the same route rather
    /// than escaping as an exception from Path.GetFullPath.</summary>
    [Fact]
    public void TryPrepare_RefusesAStructurallyImpossiblePath()
    {
        var message = OutputPaths.TryPrepare("--out", "\0bad");

        Assert.NotNull(message);
        Assert.Contains("--out", message);
        Assert.Contains("is not a usable output path", message);
    }

    // ---- TryWrite: the run's results survive a failed write -----------------------------

    /// <summary>Positive: the delegate runs, the file lands with the delegate's own content,
    /// and the parent is created on the way — TryWrite is not merely a try/catch wrapper.</summary>
    [Fact]
    public void TryWrite_CreatesTheParentAndRunsTheWrite()
    {
        var root = FreshTempDir();
        try
        {
            var target = Path.Combine(root, "made", "up", "report.txt");

            var message = OutputPaths.TryWrite("--out", target, () => File.WriteAllText(target, "payload"));

            Assert.Null(message);
            Assert.Equal("payload", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Negative, and the heart of the issue: a throwing write is RETURNED as a
    /// diagnostic, not propagated. Propagating is what discarded 834 results — the caller
    /// cannot print a summary, write its other outputs, or set an exit code if the exception
    /// unwinds out of top-level statements.</summary>
    [Fact]
    public void TryWrite_ReturnsTheDiagnosticInsteadOfLettingTheWriteThrow()
    {
        var root = FreshTempDir();
        try
        {
            var target = Path.Combine(root, "report.txt");

            var message = OutputPaths.TryWrite("--coverage-out", target,
                () => throw new UnauthorizedAccessException("Access to the path is denied."));

            Assert.NotNull(message);
            Assert.Contains("--coverage-out", message);
            Assert.Contains("Access to the path is denied", message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- End to end through the CLI ------------------------------------------------------

    /// <summary>The reproducer from the issue, now green: --out into a directory that does not
    /// exist produces the classification file, with the run's real content, and exit 0.</summary>
    [Fact]
    public void Cli_OutIntoAMissingDirectory_WritesTheClassificationAndExitsZero()
    {
        var root = FreshTempDir();
        try
        {
            var target = Path.Combine(root, "run2", "results.json");

            var (exit, stdout, stderr) = RunCli(Bundle, "--package-cache", PackageCache, "--out", target);

            Assert.True(File.Exists(target), $"no classification file.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            // Concrete content, not just existence: the file is the run's classification.
            var json = File.ReadAllText(target);
            Assert.Contains("\"total_failures\": 0", json);
            Assert.Contains("\"classifications\"", json);
            Assert.Contains($"Classification → {target}", stdout);
            Assert.Equal(0, exit);
            Assert.DoesNotContain("DirectoryNotFoundException", stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The sibling flag, same shape — --output-junit was the second of the three
    /// writers that opened its file only after the run.</summary>
    [Fact]
    public void Cli_OutputJunitIntoAMissingDirectory_WritesTheXmlAndExitsZero()
    {
        var root = FreshTempDir();
        try
        {
            var target = Path.Combine(root, "reports", "junit.xml");

            var (exit, stdout, stderr) = RunCli(Bundle, "--package-cache", PackageCache, "--output-junit", target);

            Assert.True(File.Exists(target), $"no JUnit file.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            var xml = File.ReadAllText(target);
            Assert.Contains("<testsuites", xml);
            Assert.Contains("tests=\"1\"", xml);
            Assert.Equal(0, exit);
            Assert.DoesNotContain("DirectoryNotFoundException", stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Negative, end to end, and the claim that makes this a fix rather than a
    /// papering-over: an unusable --out is refused BEFORE the run. Asserted by the absence of
    /// the run summary on stdout — a late refusal with a nice message would still have cost
    /// the whole run, which is the actual complaint in #2403.</summary>
    [Fact]
    public void Cli_UnusableOutPath_IsRefusedBeforeTheRun_WithExitTwo()
    {
        var root = FreshTempDir();
        try
        {
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var target = Path.Combine(blocker, "results.json");

            var (exit, stdout, stderr) = RunCli(Bundle, "--package-cache", PackageCache, "--out", target);

            Assert.Equal(2, exit);
            Assert.Contains("--out", stderr);
            Assert.Contains("is not a usable output path", stderr);
            Assert.Contains(target, stderr);
            // Not an unhandled exception: no stack, and not the 134 that one produces.
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.DoesNotContain("DirectoryNotFoundException", stderr);
            // Nothing ran: the summary banner the runner always prints is absent.
            Assert.DoesNotContain("test run summary", stdout);
            Assert.DoesNotContain("Tests:", stdout);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- --output-json must not report an exit code the process did not use --------------

    /// <summary>Reads the <c>exitCode</c> field out of the runner's --output-json document.
    /// Kept as a small parse rather than a substring match so the assertion is about the
    /// field's VALUE — a `Contains("\"exitCode\": 2")` would also be satisfied by the string
    /// turning up anywhere else in the report.</summary>
    private static int JsonExitCodeField(string stdout)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("exitCode").GetInt32();
    }

    /// <summary>
    /// The JSON document's <c>exitCode</c> field exists so a JSON-only consumer learns the real
    /// outcome even when the process exits 0 (--no-strict-exit). That makes it a claim about
    /// the run, and it was serialized BEFORE the lostOutputs escalation — so a --out write that
    /// failed produced a document saying <c>exitCode: 0</c> while the process itself exited 2.
    /// A consumer reading only the JSON, which is exactly who the field is for, was told the
    /// run succeeded and the file it asked for was on disk. Neither was true.
    ///
    /// <para>A directory sitting AT the target path is what defers the failure to write time:
    /// preflight creates and checks the PARENT, which exists and is fine here, so the run
    /// happens in full and only the final write fails — the same shape as a directory that
    /// disappears mid-run, and reproducible without permissions games.</para>
    /// </summary>
    [Fact]
    public void Cli_OutputJson_ReportsTheSameExitCodeTheProcessUses_WhenTheOutWriteFails()
    {
        var root = FreshTempDir();
        try
        {
            // A DIRECTORY where the file should go: parent exists (preflight passes), the
            // write at the end throws.
            var target = Path.Combine(root, "results.json");
            Directory.CreateDirectory(target);

            var (exit, stdout, stderr) = RunCli(
                Bundle, "--package-cache", PackageCache, "--output-json", "--out", target);

            // The process escalates to 2 — the run was fine, the artifact is not on disk.
            Assert.Equal(2, exit);
            Assert.Contains("could not write --out", stderr);

            // ...and the JSON says the same thing. This is the assertion that was RED.
            Assert.Equal(2, JsonExitCodeField(stdout));

            // The document is still the ONLY thing on stdout — deferring the print past the
            // write must not let the "Classification →"/diagnostic lines leak into it.
            Assert.StartsWith("{", stdout.TrimStart());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The other direction, so the test above cannot be satisfied by hardcoding 2: a
    /// --out write that SUCCEEDS leaves the field at 0, and the file is really there.</summary>
    [Fact]
    public void Cli_OutputJson_ReportsZero_WhenTheOutWriteSucceeds()
    {
        var root = FreshTempDir();
        try
        {
            var target = Path.Combine(root, "ok", "results.json");

            var (exit, stdout, stderr) = RunCli(
                Bundle, "--package-cache", PackageCache, "--output-json", "--out", target);

            Assert.Equal(0, exit);
            Assert.Equal(0, JsonExitCodeField(stdout));
            Assert.True(File.Exists(target), $"no classification file.\nSTDERR:\n{stderr}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
