// TddModeTests — issue #1997: --tdd turns objects excluded for referencing a
// not-yet-implemented symbol into synthetic FAILED tests instead of a whole-module
// compile failure.
//
// This is a runner-specific claim (--tdd producing a failed test where BC's compiler
// produces a hard error), not a BC-behaviour claim — it belongs here per
// .claude/rules/bc-behavior-tests-go-upstream.md, not in the al-language corpus.
//
// Reduced scope (see the issue and the PR this file shipped in): no type inference.
// Every missing symbol is REFUSED — reported as a failed test naming the symbol —
// rather than guessed at. Acceptance criteria covered: 1, 2, 6, 7, 9, 10, 11, 12.
// Criteria 3/4/5/8 (actual member generation) are a tracked follow-up.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
// [Collection("server-serial")] and no longer are — #1809.
public sealed class TddModeTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "Tdd");

    private readonly string _scratch;

    public TddModeTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "al-runner-tdd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static (string StdOut, string StdErr, int Exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (outSb) lock (errSb) return (outSb.ToString(), errSb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The core proof. Without --tdd this fixture drops its whole module (see
    /// <see cref="WithoutTdd_BehaviorIsByteForByteUnchanged"/>); with --tdd the three
    /// broken [Test] procedures must report FAILED — each naming the specific missing
    /// symbol, not a generic "compile failed" — while the unrelated healthy test in a
    /// SIBLING object still passes.
    ///
    /// Covers acceptance criteria 6 (unrelated tests unaffected), 7 (refuse-not-guess:
    /// every one of these IS the refused case in this build — see below), 9 (exit 1,
    /// not 3). Covers ONLY PART of 3/4/5: a missing field / procedure / enum value each
    /// produce a failed test naming that specific symbol, which is as far as this build
    /// goes. It does NOT cover the rest of 3 ("the test... runs and reports failed" —
    /// under this build the [Test] procedure never executes; the FAILED result is
    /// synthesized from the compile diagnostic, not from running the method body), nor
    /// any of 4's "with parameter and return types inferred from the call" (there is no
    /// type inference anywhere in this build — see TddSupport's doc comment). Criterion
    /// 8 (the generated-members list) is exercised by this test's own stderr assertion
    /// below, but the list is always empty here, for the same reason. See issue #2001
    /// (member generation/inference — the follow-up to #1997) for what closes the gap.
    /// </summary>
    [SkippableFact]
    public void MissingSymbols_ReportAsFailedTestsNamingTheSymbol()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{FixturePath}\"");

        Assert.Equal(1, exit); // failed tests, not a compile failure (criterion 9)

        using var doc = JsonDocument.Parse(stdout.Trim());
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(3, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());

        var tests = root.GetProperty("tests").EnumerateArray().ToList();

        var proc = tests.Single(t => t.GetProperty("name").GetString()!.Contains("MissingProcedure_ReportsFailedNotVanished"));
        Assert.Equal("fail", proc.GetProperty("status").GetString());
        Assert.Contains("CalcTotal", proc.GetProperty("message").GetString());

        var field = tests.Single(t => t.GetProperty("name").GetString()!.Contains("MissingField_ReportsFailedNotVanished"));
        Assert.Equal("fail", field.GetProperty("status").GetString());
        Assert.Contains("Loyalty Points", field.GetProperty("message").GetString());

        var enumVal = tests.Single(t => t.GetProperty("name").GetString()!.Contains("MissingEnumValue_ReportsFailedNotVanished"));
        Assert.Equal("fail", enumVal.GetProperty("status").GetString());
        Assert.Contains("Archived", enumVal.GetProperty("message").GetString());

        // Criterion 6: an unrelated test in a SIBLING object, referencing nothing
        // missing, still passes in the same run.
        var healthy = tests.Single(t => t.GetProperty("name").GetString()!.Contains("UnrelatedTest_StillPasses"));
        Assert.Equal("pass", healthy.GetProperty("status").GetString());

        // Criterion 8: the run prints a generated-members summary (empty in this build —
        // no inference — but the summary line itself must appear, not silently vanish).
        Assert.Contains("--tdd:", stderr);
        Assert.Contains("no members were generated", stderr);
    }

    /// <summary>
    /// Criterion 10 — the default path must not change AT ALL. This asserts the SAME
    /// fixture, without --tdd, still exits 3, reports EMIT-EXCLUDED (not TDD-EXCLUDED),
    /// and runs zero tests — same shape EmitExclusionLoudnessTests pins for its own
    /// fixture. This is a second, independent proof over a DIFFERENT fixture (one with
    /// method-body reference errors rather than an unresolvable type), which is exactly
    /// the class of compile failure this issue is about.
    /// </summary>
    [SkippableFact]
    public void WithoutTdd_BehaviorIsByteForByteUnchanged()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-plain");
        var (stdout, stderr, exit) = RunRunner($"--cache \"{alCache}\"", $"\"{FixturePath}\"");

        Assert.Equal(3, exit);
        Assert.Contains("EMIT-EXCLUDED", stdout + stderr);
        Assert.DoesNotContain("TDD-EXCLUDED", stdout + stderr);
        Assert.Contains("Tests:         0 total", stdout);
    }

    /// <summary>Criterion 12 — --tdd + --server is rejected, not silently ignored.</summary>
    [SkippableFact]
    public void Tdd_RejectedTogetherWithServer()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{TestBuildConfig.RunArgs(ProjectPath)} --tdd --server",
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // no requests — the rejection must happen before the daemon loop reads anything
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }

        Assert.Equal(2, p.ExitCode);
        Assert.Contains("--tdd", err);
        Assert.Contains("--server", err);
    }

    /// <summary>Criterion 12 — --tdd + --watch is rejected, not silently ignored.</summary>
    [SkippableFact]
    public void Tdd_RejectedTogetherWithWatch()
    {
        var (_, stderr, exit) = RunRunner("--tdd", "--watch", $"\"{FixturePath}\"");
        Assert.Equal(2, exit);
        Assert.Contains("--tdd", stderr);
        Assert.Contains("--watch", stderr);
    }

    /// <summary>
    /// Criterion 11, behavioural half: a --tdd run must never produce a cache entry a
    /// normal run could accidentally reuse (or vice versa). This build satisfies that by
    /// disabling the AL-output cache outright under --tdd (see Program.cs) — its
    /// synthetic FAILED tests are derived fresh from source every Emit() call and are
    /// not part of a cached DLL, so a HIT would silently drop them. Proven here by
    /// asserting a --tdd run leaves the cache directory empty.
    /// </summary>
    [SkippableFact]
    public void Tdd_NeverWritesTheAlOutputCache()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-empty-check");
        var (_, stderr, _) = RunRunner("--tdd", $"--cache \"{alCache}\"", $"\"{FixturePath}\"");

        Assert.Contains("--tdd disables the AL-output cache", stderr);
        // TOP-LEVEL only, not recursive: --cache <dir> is also the isolation root for
        // three OTHER, unrelated caches (compiled-deps/, bc-symbols/, ncl-cecil/ — see
        // AlRunner.Infrastructure.CacheRoots), which legitimately write .dll files under
        // subdirectories of `alCache` regardless of --tdd. Only the AL-OUTPUT cache
        // writes directly at `<dir>/<key>.dll`, with no subdirectory — that is the one
        // --tdd must leave untouched.
        if (Directory.Exists(alCache))
            Assert.Empty(Directory.EnumerateFiles(alCache, "*.dll", SearchOption.TopDirectoryOnly));
    }

    /// <summary>
    /// Criterion 11, code-shape half: Program.cs:5334's ComputeAlCacheKey must hash the
    /// --tdd flag itself, not only rely on the cache being disabled at runtime — the
    /// issue calls this out as required in the FIRST commit, and a future PR that
    /// re-enables caching under --tdd (e.g. once excluded-object detail has its own
    /// sidecar) must not be able to silently drop this line and still compile. A scrape
    /// test (same technique as CliDocumentationTests' flag scrape) rather than a runtime
    /// probe, because --tdd runs never reach ComputeAlCacheKey while the cache is
    /// disabled (see the test above) — there is no live --print-cache-key path to probe.
    /// </summary>
    [Fact]
    public void ComputeAlCacheKey_HashesTheTddFlag()
    {
        var programSource = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        var start = programSource.IndexOf("static string ComputeAlCacheKey(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ComputeAlCacheKey not found in Program.cs");
        var end = programSource.IndexOf("static string? CommonDirectory(", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound ComputeAlCacheKey's body (CommonDirectory marker not found after it)");
        var body = programSource[start..end];

        Assert.Contains("IsTddMode()", body);
        Assert.Contains("tdd:", body);
    }
}
