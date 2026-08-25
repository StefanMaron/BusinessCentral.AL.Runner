// TddModeTests — issue #1997 (refuse-only baseline) + #2001 (member generation, this
// file's current shape): --tdd infers and generates the missing member an unresolved-
// symbol compile error names, directly into the implementing app's own source, so the
// referencing [Test] procedure actually RUNS instead of vanishing behind a whole-module
// compile failure. Where nothing anchors a confident guess, it still falls through to
// #1997's original refuse path (excluded, reported FAILED naming the AL diagnostic).
//
// This is a runner-specific claim (--tdd producing a failed/passed test where BC's
// compiler alone produces a hard error), not a BC-behaviour claim — it belongs here per
// .claude/rules/bc-behavior-tests-go-upstream.md, not in the al-language corpus.
//
// Acceptance criteria covered by this file: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 — the
// full set from #1997, closed out by #2001's generation work.
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
    /// The core proof, updated for issue #2001 (member generation, the deferred half of
    /// #1997) AND for the orchestrator's review of the first version of this PR: a missing
    /// field / procedure / enum value each now get GENERATED into the implementing app's own
    /// source and recompiled, so the referencing [Test] procedure actually RUNS up to the
    /// point of contact with the generated member — but EVERY test whose compile depended on
    /// a generated member reports FAILED, never a pass, regardless of what actually happened
    /// when it ran. Covers criteria 3 (field), 4 (procedure, all three return-type anchors),
    /// 5 (enum value), 6 (unrelated sibling test unaffected), 9 (exit 1, not 3).
    ///
    /// A generated member the implementing app hasn't defined yet is scaffolding, not an
    /// implementation. The first version of this PR let a field/enum-value test PASS once its
    /// generated member made the assignment compile — the reasoning ("real generation
    /// produces a real read/write") was correct as a proof of the runner's OWN mechanism, but
    /// it is also exactly the green-test-lies-about-what-executed failure
    /// .claude/rules/loud-failures.md exists to rule out: a generated field is a fully
    /// functional fake, which is worse than a default return, not better, and it defeats
    /// #1997's whole stated goal — confirming a new test reports red BEFORE touching the
    /// implementing app. A generated PROCEDURE already failed correctly (its stub raises
    /// Error()); the asymmetry — field/enum pass, procedure fails, for the identical "the app
    /// doesn't have this yet" situation — was the bug.
    ///
    /// The proof that generation is real is now in the failure MESSAGE, not the outcome: every
    /// assertion below pins the exact generated signature (concrete inferred type included) a
    /// wrong guess could not have produced, because a wrong guess would have failed to compile
    /// and fallen through to the refuse path (proven separately in
    /// <see cref="UnresolvableCalls_RefuseRatherThanInvent"/>) instead of generating anything
    /// to name.
    /// </summary>
    [SkippableFact]
    public void GeneratedMembers_CompileAndRunButAlwaysReportFailed()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{FixturePath}\"");

        Assert.Equal(1, exit); // failed tests, not a compile failure (criterion 9)

        using var doc = JsonDocument.Parse(stdout.Trim());
        var root = doc.RootElement;
        Assert.Equal(8, root.GetProperty("total").GetInt32());
        // Exactly ONE test in this whole fixture references nothing missing at all
        // (UnrelatedTest_StillPasses) — every other test's compile depended on --tdd
        // generating something, so every other test must report failed. The run-level
        // summary can never read as success while any member was generated.
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(7, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());

        var tests = root.GetProperty("tests").EnumerateArray().ToList();
        JsonElement Find(string nameContains) =>
            tests.Single(t => t.GetProperty("name").GetString()!.Contains(nameContains));

        // Criterion 4 (procedure, assignment-target return-type anchor): the generated
        // CalcTotal(Arg1: Integer): Integer stub compiles and RUNS (it hits its own
        // generated Error()) but is reported failed for depending on generated scaffolding,
        // not merely because the stub raised — the message names the concrete signature.
        var proc = Find("MissingProcedure_ReportsFailedNotVanished");
        Assert.Equal("fail", proc.GetProperty("status").GetString());
        Assert.Contains("depends on", proc.GetProperty("message").GetString());
        Assert.Contains("CalcTotal\"(Arg1: Integer): Integer", proc.GetProperty("message").GetString());

        // Criterion 4 (procedure, NESTED-ARGUMENT return-type anchor — the acceptance
        // table's own `Assert.AreEqual(100, Cu.CalcTotal())` example): return type comes
        // from AreEqual's own second parameter (Integer), not from an assignment.
        var procNested = Find("MissingProcedureNestedArg_ReportsFailedNotVanished");
        Assert.Equal("fail", procNested.GetProperty("status").GetString());
        Assert.Contains("CalcSubtotal\"(Arg1: Integer): Integer", procNested.GetProperty("message").GetString());

        // Criterion 4 (procedure, IF-CONDITION return-type anchor — the acceptance
        // table's `if Cust.HasLoyalty() then` example): return type is Boolean because
        // the call sits directly in an `if ... then` condition.
        var procIf = Find("MissingBooleanProcedure_ReportsFailedNotVanished");
        Assert.Equal("fail", procIf.GetProperty("status").GetString());
        Assert.Contains("HasDiscount\"(Arg1: Integer): Boolean", procIf.GetProperty("message").GetString());

        // Criterion 3 (field) — the corrected behavior: even though the generated Integer
        // field accepts the assignment cleanly and nothing else in the test fails, the
        // result is still FAILED, and the message pins the exact inferred type (Integer) —
        // a wrong guess (e.g. Boolean) could not have compiled and would never appear here.
        var field = Find("MissingField_ReportsFailedNotVanished");
        Assert.Equal("fail", field.GetProperty("status").GetString());
        Assert.Contains("depends on", field.GetProperty("message").GetString());
        Assert.Contains("\"Loyalty Points\": Integer", field.GetProperty("message").GetString());
        Assert.Contains("has not defined yet", field.GetProperty("message").GetString());

        // Criterion 5 (enum value) — same corrected shape: FAILED, message pins the exact
        // generated ordinal.
        var enumVal = Find("MissingEnumValue_ReportsFailedNotVanished");
        Assert.Equal("fail", enumVal.GetProperty("status").GetString());
        Assert.Contains("enum value \"Archived\" = 1", enumVal.GetProperty("message").GetString());

        // Criterion 6: an unrelated test in a SIBLING object, referencing nothing
        // missing, still passes in the same run — the ONLY pass in this fixture.
        var healthy = Find("UnrelatedTest_StillPasses");
        Assert.Equal("pass", healthy.GetProperty("status").GetString());

        // Criterion 7 (refuse rather than invent) — proven in its own test below with the
        // exact AL0132 diagnostics asserted; just confirms both still fail HERE too.
        Assert.Equal("fail", Find("BareStatementCall_RefusesNotGuesses").GetProperty("status").GetString());
        Assert.Equal("fail", Find("BothSidesUnresolved_RefusesNotGuesses").GetProperty("status").GetString());

        // Criterion 8: the run prints the REAL generated-members list — one entry per
        // member actually generated, naming the object and the inferred signature. Every
        // signature below proves a DIFFERENT inference anchor from the acceptance table.
        Assert.Contains("--tdd: generated 5 member(s) this run:", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"CalcTotal\"(Arg1: Integer): Integer", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"CalcSubtotal\"(Arg1: Integer): Integer", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"HasDiscount\"(Arg1: Integer): Boolean", stderr);
        Assert.Contains("Tdd Target Table: field \"Loyalty Points\": Integer", stderr);
        Assert.Contains("Tdd Target Enum: enum value \"Archived\" = 1", stderr);
    }

    /// <summary>
    /// Criterion 7, in isolation: the two "must refuse" cases straight from #1997/#2001's
    /// own text. A bare-statement call (<c>Target.DoThing();</c>) can't distinguish void
    /// from a discarded return value, and an assignment where BOTH sides are unresolved
    /// (<c>Rec."Bar" := GetUnknownValue();</c>) has no anchor on either side. Neither is
    /// generated — both fall through to the pre-existing refuse path (excluded, reported
    /// FAILED naming the AL diagnostic) exactly as they did before generation existed.
    /// </summary>
    [SkippableFact]
    public void UnresolvableCalls_RefuseRatherThanInvent()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-refuse");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{FixturePath}\"");
        Assert.Equal(1, exit);

        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

        var bareStatement = tests.Single(t => t.GetProperty("name").GetString()!.Contains("BareStatementCall_RefusesNotGuesses"));
        Assert.Equal("fail", bareStatement.GetProperty("status").GetString());
        Assert.Contains("DoThing", bareStatement.GetProperty("message").GetString());
        Assert.Contains("did not compile", bareStatement.GetProperty("message").GetString());

        var bothSides = tests.Single(t => t.GetProperty("name").GetString()!.Contains("BothSidesUnresolved_RefusesNotGuesses"));
        Assert.Equal("fail", bothSides.GetProperty("status").GetString());
        // The synthetic result's top-line message names whichever diagnostic TddSupport
        // picked as the OBJECT's first (shared across every [Test] method in that excluded
        // object — unchanged #2000 behaviour, not something this issue touches); the full
        // diagnostic set — including the "Bar" field access this test is actually about —
        // is carried in stackTrace instead (TddSupport.BuildFailedTests' diagText).
        Assert.Contains("Bar", bothSides.GetProperty("stackTrace").GetString());

        // Neither refused member appears in the generated-members list — proves refusal
        // isn't silently generating something anyway under a different name.
        var summaryIdx = stderr.IndexOf("--tdd: generated", StringComparison.Ordinal);
        Assert.True(summaryIdx >= 0, "expected the --tdd generated-members summary line in stderr");
        var summary = stderr[summaryIdx..];
        Assert.DoesNotContain("DoThing", summary);
        Assert.DoesNotContain("\"Bar\"", summary);
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
