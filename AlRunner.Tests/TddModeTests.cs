// TddModeTests — issue #1997 (refuse-only baseline) + #2001 (member generation, this
// file's current shape): --tdd infers and generates the missing member an unresolved-
// symbol compile error names, directly into the implementing app's own source, so the
// referencing [Test] procedure actually RUNS instead of vanishing behind a whole-module
// compile failure. Since #5147 a generated procedure's body is empty (it returns its type's
// default) and each such test reports its own result, annotated with the generated members
// it ran against. Where nothing anchors a confident guess, it still falls through to
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
        _scratch = TestScratch.Dir("al-runner-tdd");
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

    private static JsonElement FindIn(List<JsonElement> tests, string nameContains) =>
        tests.Single(t => t.GetProperty("name").GetString()!.Contains(nameContains));

    private static string[] StubsOf(JsonElement test) =>
        test.TryGetProperty("generatedStubs", out var s)
            ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// The core proof (#2001 generation, #5147 behaviour): a missing field / procedure / enum
    /// value is generated into the implementing app's own source and recompiled; a generated
    /// procedure has an EMPTY body returning its type's default; and each test that reaches a
    /// generated member reports its OWN result, never a blanket FAILED, with
    /// <c>generatedStubs</c> naming the exact signatures it reaches. Reaching it through a
    /// helper or a library codeunit: <see cref="GeneratedMemberReachedThroughCalledProcedures_IsAnnotated"/>. Covers criteria 3
    /// (field), 4 (procedure, all three return-type anchors), 5 (enum value), 6 (unrelated
    /// sibling test unaffected and unannotated), 9 (exit 1, not 3).
    ///
    /// The signatures prove generation is real: a wrong inferred type could not have compiled
    /// and would have fallen through to the refuse path
    /// (<see cref="UnresolvableCalls_RefuseRatherThanInvent"/>) instead of being named here.
    /// </summary>
    [SkippableFact]
    public void GeneratedMembers_RunAgainstEmptyStubs_AndReportTheirOwnResult()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{FixturePath}\"");

        Assert.Equal(1, exit); // failed tests, not a compile failure (criterion 9)

        using var doc = JsonDocument.Parse(stdout.Trim());
        var root = doc.RootElement;
        Assert.Equal(9, root.GetProperty("total").GetInt32());
        // Each test reports its own assertion result: the nested-argument test's assertion
        // fails, the two refused tests fail at compile time, and every other test passes.
        Assert.Equal(6, root.GetProperty("passed").GetInt32());
        Assert.Equal(3, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());

        var tests = root.GetProperty("tests").EnumerateArray().ToList();
        JsonElement Find(string nameContains) => FindIn(tests, nameContains);

        // A non-default expectation fails on the test's OWN assertion: the empty stub returned
        // 0, never the generated "is a generated stub" error the stub used to raise.
        var procNested = Find("MissingProcedureNestedArg_FailsOnItsOwnAssertion");
        Assert.Equal("fail", procNested.GetProperty("status").GetString());
        var nestedMsg = procNested.GetProperty("message").GetString()!;
        Assert.Contains("Expected:<100>. Actual:<0>", nestedMsg);
        Assert.DoesNotContain("generated stub", nestedMsg);
        Assert.DoesNotContain("depends on", nestedMsg);
        Assert.Equal(new[] { "Tdd Target Cu: procedure \"CalcSubtotal\"(Arg1: Integer): Integer" }, StubsOf(procNested));

        // An assertion of the default value passes against the empty stub, and says what it ran against.
        var deflt = Find("DefaultReturn_PassesAgainstEmptyStub");
        Assert.Equal("pass", deflt.GetProperty("status").GetString());
        Assert.Equal(new[] { "Tdd Target Cu: procedure \"CountOpen\"(Arg1: Integer): Integer" }, StubsOf(deflt));

        // Criterion 4's other two anchors (assignment target, if-condition) and criteria 3/5:
        // nothing in these tests asserts a non-default value, so they pass — and are annotated.
        void AssertPassAgainst(string testName, string stub)
        {
            var t = Find(testName);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Equal(new[] { stub }, StubsOf(t));
        }
        AssertPassAgainst("MissingProcedure_RunsAgainstGeneratedStub", "Tdd Target Cu: procedure \"CalcTotal\"(Arg1: Integer): Integer");
        AssertPassAgainst("MissingBooleanProcedure_RunsAgainstGeneratedStub", "Tdd Target Cu: procedure \"HasDiscount\"(Arg1: Integer): Boolean");
        AssertPassAgainst("MissingField_RunsAgainstGeneratedField", "Tdd Target Table: field \"Loyalty Points\": Integer");
        AssertPassAgainst("MissingEnumValue_RunsAgainstGeneratedValue", "Tdd Target Enum: enum value \"Archived\" = 1");

        // Criterion 6: an unrelated test in a SIBLING object passes, and carries no annotation.
        var healthy = Find("UnrelatedTest_StillPasses");
        Assert.Equal("pass", healthy.GetProperty("status").GetString());
        Assert.False(healthy.TryGetProperty("generatedStubs", out _), "an unrelated test must not be annotated");

        // Criterion 7 (refuse rather than invent): still FAILED at compile time, naming the symbol.
        Assert.Equal("fail", Find("BareStatementCall_RefusesNotGuesses").GetProperty("status").GetString());
        Assert.Equal("fail", Find("BothSidesUnresolved_RefusesNotGuesses").GetProperty("status").GetString());

        // Criterion 8: the run prints the REAL generated-members list.
        Assert.Contains("--tdd: generated 6 member(s) this run:", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"CalcTotal\"(Arg1: Integer): Integer", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"CalcSubtotal\"(Arg1: Integer): Integer", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"HasDiscount\"(Arg1: Integer): Boolean", stderr);
        Assert.Contains("Tdd Target Cu: procedure \"CountOpen\"(Arg1: Integer): Integer", stderr);
        Assert.Contains("Tdd Target Table: field \"Loyalty Points\": Integer", stderr);
        Assert.Contains("Tdd Target Enum: enum value \"Archived\" = 1", stderr);

        // #5147: then every test that ran against a generated member, with its result — and
        // only those: the unrelated test and the refused (never-run) tests are not listed.
        var listIdx = stderr.IndexOf("--tdd: 6 test(s) ran against generated stubs this run:", StringComparison.Ordinal);
        Assert.True(listIdx >= 0, $"expected the ran-against-stubs list in stderr:\n{stderr}");
        var list = stderr[listIdx..];
        Assert.Contains("Tdd Default Assert Tests.DefaultReturn_PassesAgainstEmptyStub (pass): Tdd Target Cu: procedure \"CountOpen\"(Arg1: Integer): Integer", list);
        Assert.Contains("Tdd Broken Proc Nested Tests.MissingProcedureNestedArg_FailsOnItsOwnAssertion (fail): Tdd Target Cu: procedure \"CalcSubtotal\"", list);
        Assert.DoesNotContain("UnrelatedTest_StillPasses", list);
        Assert.DoesNotContain("RefusesNotGuesses", list);
    }

    /// <summary>
    /// #5147, console output: the per-test line names the generated members after the test's own
    /// message, and the closing list appears after the results — and neither appears for a run
    /// in which no test referenced a generated member (<see cref="TddTwoFolderTests"/>'s
    /// NothingMissing case covers that run's closing line).
    /// </summary>
    [SkippableFact]
    public void ConsoleOutput_NamesGeneratedStubsPerTestAndInTheClosingList()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-console");
        var (stdout, _, exit) = RunRunner("--tdd", "--show-pass", $"--cache \"{alCache}\"", $"\"{FixturePath}\"");
        Assert.Equal(1, exit);

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        int IndexOf(string needle) => Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal));

        var nested = IndexOf("MissingProcedureNestedArg_FailsOnItsOwnAssertion");
        Assert.True(nested >= 0 && lines[nested].TrimStart().StartsWith("FAIL", StringComparison.Ordinal), stdout);
        Assert.Contains("Expected:<100>. Actual:<0>", lines[nested + 1]);
        Assert.Equal("ran against generated stub(s): Tdd Target Cu: procedure \"CalcSubtotal\"(Arg1: Integer): Integer",
            lines[nested + 2].Trim());

        var deflt = IndexOf("DefaultReturn_PassesAgainstEmptyStub");
        Assert.True(deflt >= 0 && lines[deflt].TrimStart().StartsWith("PASS", StringComparison.Ordinal), stdout);
        Assert.Equal("ran against generated stub(s): Tdd Target Cu: procedure \"CountOpen\"(Arg1: Integer): Integer",
            lines[deflt + 1].Trim());

        var healthy = IndexOf("UnrelatedTest_StillPasses");
        Assert.True(healthy >= 0, stdout);
        Assert.DoesNotContain("ran against", lines[healthy + 1]);

        var listIdx = IndexOf("--tdd: 6 test(s) ran against generated stubs this run:");
        Assert.True(listIdx > IndexOf("Tests: "), $"the list must follow the results summary:\n{stdout}");
    }

    private static readonly string HelpersFixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "TddHelpers");

    /// <summary>
    /// #5147 review: the AL0132 sits in a procedure the test calls, not in the [Test] body — a
    /// local helper in the test codeunit, and a procedure of a library codeunit. Each test
    /// reaches the stub all the same, so it is annotated all the same; a test whose helper
    /// reaches no generated member is not.
    /// </summary>
    [SkippableFact]
    public void GeneratedMemberReachedThroughCalledProcedures_IsAnnotated()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-helpers");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{HelpersFixturePath}\"");

        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(3, tests.Count);

        var viaHelper = FindIn(tests, "ViaLocalHelper_RunsAgainstGeneratedStub");
        Assert.Equal("pass", viaHelper.GetProperty("status").GetString());
        Assert.Equal(new[] { "Tdd Helper Target Cu: procedure \"CountClosed\"(Arg1: Integer): Integer" }, StubsOf(viaHelper));

        var viaLibrary = FindIn(tests, "ViaLibraryCodeunit_RunsAgainstGeneratedStub");
        Assert.Equal("pass", viaLibrary.GetProperty("status").GetString());
        Assert.Equal(new[] { "Tdd Helper Target Cu: procedure \"CountPending\"(Arg1: Integer): Integer" }, StubsOf(viaLibrary));

        var unrelated = FindIn(tests, "ViaHelperWithoutStub_IsNotAnnotated");
        Assert.Equal("pass", unrelated.GetProperty("status").GetString());
        Assert.Empty(StubsOf(unrelated));

        Assert.Contains("--tdd: 2 test(s) ran against generated stubs this run:", stderr);
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
    /// Criterion 10 — --tdd must not leak into the default path. The SAME fixture, without
    /// --tdd, still exits 3 and reports EMIT-EXCLUDED rather than TDD-EXCLUDED. A second,
    /// independent proof over a DIFFERENT fixture from EmitExclusionLoudnessTests' — one with
    /// method-body reference errors rather than an unresolvable type, which is the class of
    /// compile failure #1997 is about.
    ///
    /// The assertion that used to carry this — "and runs zero tests" — was #3476's subject and
    /// is gone: the five broken objects are test codeunits nothing else in the module names, so
    /// the survivors run. What still separates the two modes, and what this now asserts, is the
    /// OUTCOME the dropped objects' tests get. --tdd reports them FAILED, because the point of
    /// --tdd is a red test; the default path reports them SKIPPED, because nothing measured
    /// whether they would pass. A default run that produced a FAILED result here would be
    /// asserting something it never ran.
    /// </summary>
    [SkippableFact]
    public void WithoutTdd_ExclusionIsEmitExcludedAndItsTestsAreSkippedNotFailed()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-plain");
        var (stdout, stderr, exit) = RunRunner($"--cache \"{alCache}\"", $"\"{FixturePath}\"");

        Assert.Equal(3, exit);
        Assert.Contains("EMIT-EXCLUDED", stdout + stderr);
        Assert.DoesNotContain("TDD-EXCLUDED", stdout + stderr);

        // The healthy sibling really ran, and the dropped objects' tests are SKIPPED, not
        // FAILED and not absent. `fail: 0` is the one that separates this from a --tdd run.
        Assert.Contains("UnrelatedTest_StillPasses", stdout);
        Assert.Contains("passed 1 ", stdout);
        Assert.Contains("failed 0 ", stdout);
        Assert.Contains("skipped 8 ", stdout);
        Assert.Contains("MissingProcedure_RunsAgainstGeneratedStub", stdout);
    }

    private static readonly string EnumArgsFixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "TddEnumArgs");

    private static JsonElement FindTest(List<JsonElement> tests, string nameContains) => FindIn(tests, nameContains);

    /// <summary>
    /// #5038: an enum-value argument (<c>"Loyalty Tier"::Gold</c>) anchors an
    /// <c>Enum "Loyalty Tier"</c> parameter, alone or next to a literal, and an enum declared
    /// in a namespace the implementing codeunit does not import is written namespace-qualified.
    /// Each test only calls the generated procedure, so it passes against the empty stub and
    /// names the signature it ran against (#5147).
    /// </summary>
    [SkippableFact]
    public void EnumValueArguments_GenerateEnumParameters()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-enum-args");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{EnumArgsFixturePath}\"");

        Assert.Equal(1, exit);
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(8, doc.RootElement.GetProperty("total").GetInt32());

        void AssertGenerated(string testName, string signature, string procName)
        {
            var t = FindTest(tests, testName);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Equal(new[] { $"Tdd Loyalty Cu: procedure {signature}" }, StubsOf(t));
            Assert.Contains($"\"{procName}\"(", signature);
            Assert.Contains($"Tdd Loyalty Cu: procedure {signature}", stderr);
        }

        AssertGenerated("EnumValueArg_GeneratesEnumParameter",
            "\"CalcTier\"(Arg1: Enum \"Loyalty Tier\"): Integer", "CalcTier");
        AssertGenerated("LiteralAndEnumValueArgs_GenerateBothParameters",
            "\"CalcPoints\"(Arg1: Integer; Arg2: Enum \"Loyalty Tier\"): Integer", "CalcPoints");
        AssertGenerated("EnumVariableArg_GeneratesEnumParameter",
            "\"CalcByTier\"(Arg1: Enum \"Loyalty Tier\"): Integer", "CalcByTier");
        AssertGenerated("NamespacedEnumValueArg_GeneratesQualifiedEnumParameter",
            "\"CalcStatus\"(Arg1: Enum TddEnumArgs.Membership.\"Member Status\"): Integer", "CalcStatus");

        // Field sibling: same resolver on an assignment's right-hand side. A pass means the
        // test's own read-back of the enum value held.
        var field = FindTest(tests, "EnumValueAssignment_GeneratesEnumField");
        Assert.Equal("pass", field.GetProperty("status").GetString());
        Assert.Equal(new[] { "Tdd Loyalty Member: field \"Tier\": Enum \"Loyalty Tier\"" }, StubsOf(field));
        Assert.Contains("Tdd Loyalty Member: field \"Tier\": Enum \"Loyalty Tier\"", stderr);

        Assert.Contains("--tdd: generated 7 member(s) this run:", stderr);
    }

    private (List<JsonElement> Tests, string Stderr) RunEnumArgsFixture(string cacheName)
    {
        var alCache = Path.Combine(_scratch, cacheName);
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{EnumArgsFixturePath}\"");
        Assert.Equal(1, exit);
        using var doc = JsonDocument.Parse(stdout.Trim());
        Assert.Equal(8, doc.RootElement.GetProperty("total").GetInt32());
        return (doc.RootElement.GetProperty("tests").EnumerateArray().Select(e => e.Clone()).ToList(), stderr);
    }

    private static void AssertGeneratedStub(List<JsonElement> tests, string stderr,
        string testName, string signature, string procName)
    {
        var t = FindTest(tests, testName);
        Assert.Equal("pass", t.GetProperty("status").GetString());
        Assert.Contains($"\"{procName}\"(", signature);
        Assert.Equal(new[] { $"Tdd Loyalty Cu: procedure {signature}" }, StubsOf(t));
        Assert.Contains($"Tdd Loyalty Cu: procedure {signature}", stderr);
    }

    /// <summary>
    /// #5044: a value an enumextension adds (<c>"Loyalty Tier"::Platinum</c>) is a value of the
    /// base enum, so the parameter is <c>Enum "Loyalty Tier"</c>, not a refusal.
    /// </summary>
    [SkippableFact]
    public void EnumExtensionValueArgument_GeneratesBaseEnumParameter()
    {
        TestArtifacts.SkipIfMissing();
        var (tests, stderr) = RunEnumArgsFixture("al-cache-enumext-arg");
        AssertGeneratedStub(tests, stderr, "EnumExtValueArg_GeneratesBaseEnumParameter",
            "\"CalcBonus\"(Arg1: Enum \"Loyalty Tier\"): Integer", "CalcBonus");
    }

    /// <summary>
    /// #5044: a namespace-qualified enum value at the call site
    /// (<c>TddEnumArgs.Membership."Member Status"::Lapsed</c>) generates the qualified parameter.
    /// </summary>
    [SkippableFact]
    public void QualifiedEnumValueArgument_GeneratesQualifiedEnumParameter()
    {
        TestArtifacts.SkipIfMissing();
        var (tests, stderr) = RunEnumArgsFixture("al-cache-qualified-enum-arg");
        AssertGeneratedStub(tests, stderr, "QualifiedEnumValueArg_GeneratesQualifiedEnumParameter",
            "\"CalcRenewal\"(Arg1: Enum TddEnumArgs.Membership.\"Member Status\"): Integer", "CalcRenewal");
    }

    /// <summary>
    /// #5038: an Option-member argument (<c>Choice::Beta</c>) still refuses — an Option
    /// parameter needs a member list the call site does not fix.
    /// </summary>
    [SkippableFact]
    public void OptionMemberArgument_RefusesRatherThanInvent()
    {
        TestArtifacts.SkipIfMissing();

        var alCache = Path.Combine(_scratch, "al-cache-option-arg");
        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{alCache}\"", "--output-json", $"\"{EnumArgsFixturePath}\"");

        Assert.Equal(1, exit);
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

        var option = FindTest(tests, "OptionMemberArg_RefusesNotGuesses");
        Assert.Equal("fail", option.GetProperty("status").GetString());
        Assert.Contains("did not compile", option.GetProperty("message").GetString());
        Assert.Contains("CalcChoice", option.GetProperty("message").GetString());

        var summaryIdx = stderr.IndexOf("--tdd: generated", StringComparison.Ordinal);
        Assert.True(summaryIdx >= 0, "expected the --tdd generated-members summary line in stderr");
        Assert.DoesNotContain("CalcChoice", stderr[summaryIdx..]);
    }

    /// <summary>Criterion 12, now for --dap: --server takes --tdd per request (#5034,
    /// ServerTddTests); a debug session would run the stubs in place of the app's code, so
    /// --tdd + --dap is rejected, not silently honoured.</summary>
    [SkippableFact]
    public void Tdd_RejectedTogetherWithDap()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{TestBuildConfig.RunArgs(ProjectPath)} --tdd --dap stdio",
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // no requests — the rejection must happen before the daemon loop reads anything
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }

        Assert.Equal(2, p.ExitCode);
        Assert.Contains("--tdd is not supported together with --dap", err);
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
    /// Criterion 11, code-shape half: ComputeAlCacheKey (moved from Program.cs to
    /// AlRunner/ProgramSupport/Dependencies.cs by #2665) must hash the --tdd flag
    /// itself, not only rely on the cache being disabled at runtime — the issue calls
    /// this out as required in the FIRST commit, and a future PR that re-enables
    /// caching under --tdd (e.g. once excluded-object detail has its own sidecar) must
    /// not be able to silently drop this line and still compile. A scrape test (same
    /// technique as CliDocumentationTests' flag scrape) rather than a runtime probe,
    /// because --tdd runs never reach ComputeAlCacheKey while the cache is disabled
    /// (see the test above) — there is no live --print-cache-key path to probe.
    /// </summary>
    [Fact]
    public void ComputeAlCacheKey_HashesTheTddFlag()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "ProgramSupport", "Dependencies.cs"));
        var start = source.IndexOf("internal static string ComputeAlCacheKey(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ComputeAlCacheKey not found in ProgramSupport/Dependencies.cs");
        var end = source.IndexOf("internal static string? CommonDirectory(", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound ComputeAlCacheKey's body (CommonDirectory marker not found after it)");
        var body = source[start..end];

        Assert.Contains("IsTddMode()", body);
        Assert.Contains("tdd:", body);
    }
}
