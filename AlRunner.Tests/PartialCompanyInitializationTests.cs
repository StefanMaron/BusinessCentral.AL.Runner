// PartialCompanyInitializationTests — issue #3538.
//
// What is being pinned
// --------------------
// When Base App codeunit 2 "Company-Initialize" does not run to completion, the runner keeps
// the rows it did write (CompanyInitializer's KNOWN INCOMPLETE note explains why: measured at
// +5 tests over not running it at all) and the tests still run. Before #3538 that was ALL that
// happened: a `[warn]` line on stderr, a summary that recorded nothing, `--output-json` /
// `--out` / JUnit documents identical to a clean run's, and exit 0. The report that produced
// this issue had ~2800 tests pass that way against a half-initialized database.
//
// Real BC cannot produce that state. Codeunit 2's OnRun commits exactly ONCE, at its end
// (`src/Foundation/Company/CompanyInitialize.Codeunit.al` in Microsoft's Base Application app,
// 28.1.49838.50256: one `Commit()` in 806 lines, the last statement of OnRun), so a service
// tier either has the whole set of setup rows or the write that created them rolled back. A
// run carrying a partial company is therefore a property of the RUN, not of any test — see
// docs/partial-company-initialization.md for the decision and the exit-code rule.
//
// Why the abort is injected
// -------------------------
// Whether codeunit 2 aborts depends on the Base Application build: #3054 measured it aborting
// on five of eight BC legs, and on the last green `main` matrix run (34216039259) it aborted on
// none of them. So there is no fixture that reliably reproduces it, and reproducing it by
// loading the Base Application floor would cost ~70s per runner invocation for a condition the
// floor does not guarantee anyway (.claude/rules/no-base-app-in-csharp-tests.md). The seam
// replaces the `NavCodeunit_RunCodeunit` call INSIDE the try block, so the catch path under
// test — the exception unwrapping, the message, the accumulation, the cache carry — is the
// real one, and the injected text is what the assertions below look for.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialCompanyInitializationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "CompanyInitPartial");

    // The value the seam turns into the exception message, so every assertion below is against
    // text that could only have come through the real catch path.
    private const string InjectedReason = "CIP-INJECTED-ABORT: InitSourceCodeSetup did not finish";

    private sealed record Run(string Output, int Exit);

    private static Run RunRunner(string cacheDir, bool injectAbort,
        string? outPath = null, string? junitPath = null, bool outputJson = false)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{cacheDir}\"");
        if (outPath != null) args.Append($" --out \"{outPath}\"");
        if (junitPath != null) args.Append($" --output-junit \"{junitPath}\"");
        if (outputJson) args.Append(" --output-json");
        args.Append($" \"{FixturePath}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (injectAbort) psi.Environment["AL_RUNNER_TEST_FAIL_COMPANY_INIT"] = InjectedReason;
        else psi.Environment.Remove("AL_RUNNER_TEST_FAIL_COMPANY_INIT");

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return new Run(sb.ToString(), p.ExitCode);
    }

    private static string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-cip-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The positive case, everything a caller reading a single plain run can see: the tests
    /// still ran and still passed, the summary names the abort and its cause, and the process
    /// does not exit 0. `--out` carries it as its own record kind, and the JUnit document says
    /// what the counts in it do not.
    /// </summary>
    [SkippableFact]
    public void PartialCompanyInit_IsRecordedInEverySurface_AndTheRunIsNotClean()
    {
        TestArtifacts.SkipIfMissing();

        var cache = NewCacheDir();
        var outPath = Path.Combine(cache, "classification.json");
        var junitPath = Path.Combine(cache, "junit.xml");
        var run = RunRunner(cache, injectAbort: true, outPath: outPath, junitPath: junitPath);

        // The results are NOT discarded — that is the half of the decision this test protects.
        Assert.Contains("Tests:         1 total", run.Output);
        Assert.Contains("pass:        1", run.Output);

        // Exit 2 exactly: not 0 (the run is not clean), and not 1, which would say a test
        // failed when none did.
        Assert.True(run.Exit == 2,
            $"a partially initialized company must exit 2 — nothing about the AL failed, the "
            + $"database was not the one asked for. exit={run.Exit}\n{run.Output}");

        // The summary block, next to the totals, naming the codeunit and the reason.
        Assert.Contains("Company initialization: INCOMPLETE", run.Output);
        Assert.Contains("codeunit 2 \"Company-Initialize\" did not complete", run.Output);
        Assert.Contains(InjectedReason, run.Output);

        // ...and the escalation line, which is what tells a scripted caller why the exit code
        // moved without a single test failing.
        Assert.Contains("[warn] company-init:", run.Output);

        // --out: its own record kind, so a triage pass reading the file sees the condition
        // before it starts chasing the failures it may explain.
        var classification = JsonDocument.Parse(File.ReadAllText(outPath)).RootElement;
        var companyInit = classification.GetProperty("all_failures").EnumerateArray()
            .Where(f => f.GetProperty("kind").GetString() == "company-init")
            .ToList();
        Assert.Single(companyInit);
        Assert.Equal(2, companyInit[0].GetProperty("codeunitId").GetInt32());
        Assert.Equal("Company-Initialize", companyInit[0].GetProperty("codeunit").GetString());
        Assert.Equal(InjectedReason, companyInit[0].GetProperty("message").GetString());

        // JUnit: a comment, following the convention #2919 set for the sibling condition — the
        // counts are whatever the tests earned and a synthetic suite would move them.
        var junit = File.ReadAllText(junitPath);
        Assert.Contains("company initialization did NOT complete", junit);
        Assert.Contains(InjectedReason, junit);
        // ...and it is still a well-formed document a consumer can parse.
        Assert.Equal("testsuites", XDocument.Parse(junit).Root!.Name.LocalName);
    }

    /// <summary>
    /// `--output-json` is the surface the issue named: a consumer reading only that document
    /// could not tell this run from a clean one. The field is structured (codeunit id, type,
    /// message), and `exitCode` in the document agrees with the process — the two disagreeing
    /// is the defect #2403 fixed one condition over.
    /// </summary>
    [SkippableFact]
    public void PartialCompanyInit_IsInTheOutputJsonDocument_WithTheRealExitCode()
    {
        TestArtifacts.SkipIfMissing();

        var cache = NewCacheDir();
        var run = RunRunner(cache, injectAbort: true, outputJson: true);
        Assert.Equal(2, run.Exit);

        var start = run.Output.IndexOf('{');
        Assert.True(start >= 0, $"no JSON document on stdout:\n{run.Output}");
        var doc = JsonDocument.Parse(run.Output[start..]).RootElement;

        Assert.Equal(1, doc.GetProperty("passed").GetInt32());
        Assert.Equal(0, doc.GetProperty("failed").GetInt32());
        Assert.Equal(2, doc.GetProperty("exitCode").GetInt32());

        var failures = doc.GetProperty("companyInitFailures").EnumerateArray().ToList();
        Assert.Single(failures);
        Assert.Equal(2, failures[0].GetProperty("codeunitId").GetInt32());
        Assert.Equal("Company-Initialize", failures[0].GetProperty("codeunit").GetString());
        Assert.Equal("InvalidOperationException", failures[0].GetProperty("exceptionType").GetString());
        Assert.Equal(InjectedReason, failures[0].GetProperty("message").GetString());
    }

    /// <summary>
    /// The negative control, and the one that makes every assertion above mean something: with
    /// nothing injected, the same fixture in the same runner exits 0 and carries none of it.
    /// A `companyInitFailures` field emitted unconditionally, or a summary block printed on
    /// every run, would fail here.
    /// </summary>
    [SkippableFact]
    public void CleanCompanyInit_CarriesNoneOfIt_AndExitsZero()
    {
        TestArtifacts.SkipIfMissing();

        var cache = NewCacheDir();
        var outPath = Path.Combine(cache, "classification.json");
        var junitPath = Path.Combine(cache, "junit.xml");
        var run = RunRunner(cache, injectAbort: false, outPath: outPath, junitPath: junitPath);

        Assert.True(run.Exit == 0, $"a clean run must still exit 0. exit={run.Exit}\n{run.Output}");
        Assert.DoesNotContain("Company initialization: INCOMPLETE", run.Output);
        Assert.DoesNotContain("[warn] company-init:", run.Output);
        Assert.DoesNotContain("did not complete", run.Output);

        var classification = File.ReadAllText(outPath);
        Assert.DoesNotContain("company-init", classification);
        Assert.DoesNotContain("company initialization did NOT complete", File.ReadAllText(junitPath));

        // And the JSON document omits the field entirely rather than carrying an empty array,
        // so an existing consumer's document is unchanged.
        var jsonRun = RunRunner(cache, injectAbort: false, outputJson: true);
        Assert.Equal(0, jsonRun.Exit);
        var doc = JsonDocument.Parse(jsonRun.Output[jsonRun.Output.IndexOf('{')..]).RootElement;
        Assert.False(doc.TryGetProperty("companyInitFailures", out _));
    }

    /// <summary>
    /// The warm-cache arm. The dependency-company baseline is cached in memory and on disk, and
    /// a HIT skips company initialization entirely — so a condition recorded only where the
    /// codeunit actually ran would vanish on the second run and a repeat run would report a
    /// clean database. That is precisely how the emit-exclusion loss came back green on a warm
    /// cache (#3476). Both runs share one private `--cache` directory, so the second is a real
    /// cross-process warm run.
    /// </summary>
    [SkippableFact]
    public void PartialCompanyInit_SurvivesAWarmCache()
    {
        TestArtifacts.SkipIfMissing();

        var cache = NewCacheDir();
        var cold = RunRunner(cache, injectAbort: true);
        Assert.Equal(2, cold.Exit);
        Assert.Contains("Company initialization: INCOMPLETE", cold.Output);

        var warm = RunRunner(cache, injectAbort: true);
        Assert.True(warm.Exit == 2,
            $"the second run reuses the compiled output and may reuse the dependency baseline; "
            + $"the company it runs against is still partial. exit={warm.Exit}\n{warm.Output}");
        Assert.Contains("Company initialization: INCOMPLETE", warm.Output);
        Assert.Contains(InjectedReason, warm.Output);
    }
}
