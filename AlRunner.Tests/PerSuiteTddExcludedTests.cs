// #5307 — `--tdd --per-suite` and `--tdd` disagreed about a test codeunit the compiler dropped. The bundled
// loop answers a drop under --tdd with TDD-EXCLUDED (the codeunit's [Test] procedures reported FAILED,
// exit 1: a red test is the point of that flag, docs/emit-exclusion-triage.md); the --per-suite loop asked
// only the non-tdd question (#5305), so the same drop was SKIPPED and exit 3. The same loop also never
// collected the members --tdd generated, so its closing block said "no test referenced a missing symbol"
// over a run whose test had just passed against a generated stub.
//
// Runner-only claim: how this CLI reports a drop, with the bundled `--tdd` run of the same directory as the
// oracle. Every fact spawns the real CLI (the wiring is Program.cs's) and shares its runs through a Lazy,
// so each layout is spawned once however many facts read it.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class PerSuiteTddExcludedTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private sealed record Spawned(int Exit, string Output, string Junit)
    {
        /// <summary>The per-test rows (`--show-pass` prints every outcome), as "OUTCOME Codeunit.Method".</summary>
        public IReadOnlyList<string> Rows => Regex
            .Matches(Output, @"^(PASS|SKIP|FAIL|ERROR) +(\S.*?)(?: \((?:[^)]*, )?\d+ ?ms\))?\s*$", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .OrderBy(r => r, StringComparer.Ordinal).ToList();

        /// <summary>The message under each synthetic FAILED row: the object and the AL diagnostic that dropped it.</summary>
        public IReadOnlyList<string> RowMessages => Regex
            .Matches(Output, @"^ +--tdd: .* did not compile — .*$", RegexOptions.Multiline)
            .Select(m => m.Value.Trim()).OrderBy(r => r, StringComparer.Ordinal).ToList();

        /// <summary>The summary's counts, which carry no time.</summary>
        public string Counts => Regex.Match(Output, @"^Tests: .*?(?=\s+Time:|$)", RegexOptions.Multiline).Value;

        /// <summary>--tdd's closing block and the per-test annotation, which name the generated members.</summary>
        public IReadOnlyList<string> TddLines => Regex
            .Matches(Output, @"^(--tdd: (generated|no members|\d+ test\(s\) reach)|\s+reaches generated stub|\s+(Impl:|Gen Test\.)).*$",
                RegexOptions.Multiline)
            .Select(m => m.Value).ToList();

        /// <summary>The report the caller asked for: "classname.name:outcome" per case.</summary>
        public IReadOnlyList<string> JunitCases
        {
            get
            {
                Assert.True(File.Exists(Junit), $"--output-junit was not written: {Junit}");
                return XDocument.Load(Junit).Descendants("testcase")
                    .Select(c => $"{c.Attribute("classname")!.Value}.{c.Attribute("name")!.Value}:" +
                        (c.Elements("failure").Any() ? "failure" : c.Elements("error").Any() ? "error"
                            : c.Elements("skipped").Any() ? "skipped" : "pass"))
                    .OrderBy(r => r, StringComparer.Ordinal).ToList();
            }
        }
    }

    // A variable of a PAGE nobody declares: BC answers AL0185 and drops the object. (A missing CODEUNIT or TABLE is no
    // longer the unrecoverable case under --tdd: it is generated, #5431, #5445.)
    private static string Broken(int id, string name, int tests = 1)
    {
        var sb = new StringBuilder();
        sb.Append($"codeunit {id} \"{name}\"\n{{\n    Subtype = Test;\n\n");
        sb.Append("    [Test]\n    procedure Works()\n    var\n        Api: Page \"TD Excl Does Not Exist\";\n");
        sb.Append("    begin\n        Api.Run();\n    end;\n");
        for (var n = 2; n <= tests; n++)
            sb.Append($"\n    [Test]\n    procedure Works{n}()\n    begin\n    end;\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string Healthy(int id, string name, string method, string body = "") => $$"""
        codeunit {{id}} "{{name}}"
        {
            Subtype = Test;

            [Test]
            procedure {{method}}()
            begin
                {{body}}
            end;
        }
        """;

    private static void Suite(string root, string name, int idFrom, params (string File, string Source)[] files)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "5307{{idFrom:x4}}-0000-4000-8000-000000000000",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 99}} } ],
          "runtime": "14.0"
        }
        """);
        foreach (var (file, source) in files) File.WriteAllText(Path.Combine(dir, file), source);
    }

    private static string NewRoot(string name) => TestScratch.Dir("al-runner-persuite-tdd-" + name);

    // The issue's layout: suiteA holds a dropped codeunit of two tests beside a healthy one; suiteB is healthy.
    private static string IssueRoot()
    {
        var root = NewRoot("issue");
        Suite(root, "suiteA", 61000,
            ("Broken.al", Broken(61001, "TD Zero", tests: 2)),
            ("Good.al", Healthy(61002, "TD Good A", "FineA")));
        Suite(root, "suiteB", 62000, ("Good.al", Healthy(62002, "TD Good B", "FineB")));
        return root;
    }

    // Every position a drop can take: after a healthy suite (B); beside a survivor that reaches the dropped
    // codeunit by its object id, which BC does not check (C); a suite whose ONLY object drops (D); a healthy
    // suite after all of them (E).
    private static string ManyRoot()
    {
        var root = NewRoot("many");
        Suite(root, "suiteA", 64000, ("Good.al", Healthy(64001, "TD Good A", "FineA")));
        Suite(root, "suiteB", 64100,
            ("Broken.al", Broken(64101, "TD Zero B")),
            ("Good.al", Healthy(64102, "TD Good B", "FineB")));
        Suite(root, "suiteC", 64200,
            ("Broken.al", Broken(64201, "TD Zero C", tests: 2)),
            ("Reach.al", Healthy(64202, "TD Reach C", "ReachesDropped", "Codeunit.Run(64201);")));
        Suite(root, "suiteD", 64300, ("Broken.al", Broken(64301, "TD Zero D")));
        Suite(root, "suiteE", 64400, ("Good.al", Healthy(64401, "TD Good E", "FineE")));
        return root;
    }

    private static string CleanRoot()
    {
        var root = NewRoot("clean");
        Suite(root, "suiteA", 61000, ("Good.al", Healthy(61002, "TD Good A", "FineA")));
        Suite(root, "suiteB", 62000, ("Good.al", Healthy(62002, "TD Good B", "FineB")));
        return root;
    }

    // A test that calls a procedure its subject does not have yet: --tdd generates it, so the test PASSES.
    private static string GeneratedRoot()
    {
        var root = NewRoot("generated");
        Suite(root, "suiteA", 61000,
            ("Impl.al", """
                codeunit 61010 "Impl"
                {
                    procedure Existing(): Integer
                    begin
                        exit(1);
                    end;
                }
                """),
            ("T.al", """
                codeunit 61011 "Gen Test"
                {
                    Subtype = Test;

                    [Test]
                    procedure UsesMissing()
                    var
                        I: Codeunit "Impl";
                        R: Integer;
                    begin
                        R := I.NotYetWritten();
                    end;
                }
                """));
        Suite(root, "suiteB", 62000, ("Good.al", Healthy(62002, "TD Good B", "FineB")));
        return root;
    }

    // A source dependency in its own folder: the test folder's compile asks for a member of the app folder,
    // which --tdd generates there and then compiles the cycle again.
    private static string[] CrossBundleRoots()
    {
        var root = NewRoot("cross-bundle");
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "AppTest"));
        File.WriteAllText(Path.Combine(root, "App", "app.json"), """
            { "id": "53070001-0000-4000-8000-000000000000", "name": "XbApp", "publisher": "AL Runner", "version": "1.0.0.0",
              "dependencies": [], "idRanges": [ { "from": 61000, "to": 61099 } ], "runtime": "14.0" }
            """);
        File.WriteAllText(Path.Combine(root, "App", "Impl.al"), """
            codeunit 61010 "Impl"
            {
                procedure Existing(): Integer
                begin
                    exit(1);
                end;
            }
            """);
        File.WriteAllText(Path.Combine(root, "AppTest", "app.json"), """
            { "id": "53070002-0000-4000-8000-000000000000", "name": "XbTest", "publisher": "AL Runner", "version": "1.0.0.0",
              "dependencies": [ { "id": "53070001-0000-4000-8000-000000000000", "name": "XbApp", "publisher": "AL Runner", "version": "1.0.0.0" } ],
              "idRanges": [ { "from": 62000, "to": 62099 } ], "runtime": "14.0" }
            """);
        File.WriteAllText(Path.Combine(root, "AppTest", "T.al"), """
            codeunit 62011 "Gen Test"
            {
                Subtype = Test;

                [Test]
                procedure UsesMissing()
                var
                    I: Codeunit "Impl";
                    R: Integer;
                begin
                    R := I.NotYetWritten();
                end;
            }
            """);
        return new[] { Path.Combine(root, "App"), Path.Combine(root, "AppTest") };
    }

    private sealed record Pair(string[] Roots, Spawned PerSuite, Spawned Bundled);

    private static Pair Both(Func<string> layout, string tag) => Both(() => new[] { layout() }, tag);

    private static Pair Both(Func<string[]> layout, string tag)
    {
        var roots = layout();
        var cache = TestScratch.Dir("al-runner-persuite-tdd-cache-" + tag);
        return new Pair(roots, Run(roots, "--tdd --per-suite", cache, tag + "-ps"), Run(roots, "--tdd", cache, tag + "-b"));
    }

    private static readonly Lazy<Pair> Issue = new(() => Both(IssueRoot, "issue"));
    private static readonly Lazy<Pair> Many = new(() => Both(ManyRoot, "many"));
    private static readonly Lazy<Pair> Clean = new(() => Both(CleanRoot, "clean"));
    private static readonly Lazy<Pair> Generated = new(() => Both(GeneratedRoot, "generated"));
    private static readonly Lazy<Pair> CrossBundle = new(() => Both(CrossBundleRoots, "cross-bundle"));

    // The control: the issue's layout WITHOUT --tdd, so the difference below is the flag and nothing else.
    private static readonly Lazy<Spawned> IssueWithoutTdd = new(() =>
    {
        return Run(Issue.Value.Roots, "--per-suite", TestScratch.Dir("al-runner-persuite-tdd-cache-plain"), "plain");
    });

    // The shared --jobs fixture of #5256 / #5262 (one dropped codeunit of 3 tests, 3 healthy of 2), as one suite.
    private static readonly Lazy<(int Exit, string Output, string Junit)> SharedUnderJobs = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-persuite-tdd-jobs");
        var junit = Path.Combine(scratch, "junit.xml");
        var fixture = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "JobsUnitClaimExcluded");
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--tdd --per-suite --show-pass --cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 " +
            $"--output-junit \"{junit}\" \"{fixture}\"",
            lowSplitFloor: true);
        return (exit, output, junit);
    });

    /// <summary>
    /// The issue's table: a dropped test codeunit is FAILED rows and exit 1 under --per-suite, as it is
    /// bundled, and the rows, the counts, the message under each row and the report's cases are the same.
    /// </summary>
    [SkippableFact]
    public void ADroppedTestCodeunit_IsReportedFailed_AsTheBundledTddRunDoes()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = Issue.Value;

        Assert.Equal(1, perSuite.Exit);
        Assert.Equal(
            new[] { "FAIL TD Zero.Works", "FAIL TD Zero.Works2", "PASS Codeunit61002.FineA", "PASS Codeunit62002.FineB" },
            perSuite.Rows);
        Assert.Matches(@"^Tests: 4\s+passed 2\s+failed 2\s+errors 0$", perSuite.Counts);
        Assert.Equal(2, perSuite.RowMessages.Count);
        Assert.All(perSuite.RowMessages, m => Assert.Contains("error AL0185", m));
        Assert.Contains("TDD-EXCLUDED", perSuite.Output);
        Assert.DoesNotContain("EMIT-EXCLUDED", perSuite.Output);
        Assert.DoesNotContain("SUITE ERRORS", perSuite.Output);

        // The oracle: the bundled --tdd run of the same directory.
        Assert.Equal(1, bundled.Exit);
        Assert.Equal(bundled.Rows, perSuite.Rows);
        Assert.Equal(bundled.Counts, perSuite.Counts);
        Assert.Equal(bundled.RowMessages, perSuite.RowMessages);
        Assert.Equal(bundled.JunitCases, perSuite.JunitCases);
        Assert.Equal(
            new[] { "TD Zero.Works2:failure", "TD Zero.Works:failure" },   // ordinal: '2' sorts before ':'
            perSuite.JunitCases.Where(c => c.StartsWith("TD Zero.")));
    }

    /// <summary>
    /// The closing line says what happened: every missing symbol was reported as a failed test. It used to say
    /// "no test was reported failed for a missing symbol — the error(s) reported above stopped the run first".
    /// </summary>
    [SkippableFact]
    public void TheClosingLine_SaysTheMissingSymbolsWereReportedAsFailedTests()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = Issue.Value;

        Assert.Contains("--tdd: no members were generated this run — every missing symbol was reported as a failed test instead", perSuite.Output);
        Assert.DoesNotContain("stopped the run first", perSuite.Output);
        Assert.Equal(bundled.TddLines, perSuite.TddLines);
    }

    /// <summary>
    /// The control that makes the first fact about the flag: the same directory under --per-suite WITHOUT
    /// --tdd is still #5305's answer, SKIPPED rows and exit 3.
    /// </summary>
    [SkippableFact]
    public void WithoutTdd_ThePerSuiteDrop_IsStillSkippedAndExitThree()
    {
        TestArtifacts.SkipIfMissing();
        var plain = IssueWithoutTdd.Value;

        Assert.Equal(3, plain.Exit);
        Assert.Contains("suiteA: EMIT-EXCLUDED", plain.Output);
        Assert.DoesNotContain("TDD-EXCLUDED", plain.Output);
        Assert.Equal(
            new[] { "PASS Codeunit61002.FineA", "PASS Codeunit62002.FineB" },
            plain.Rows.Where(r => r.StartsWith("PASS ")));
        Assert.Equal(2, plain.Rows.Count(r => r.StartsWith("SKIP ")));
        Assert.DoesNotContain(plain.Rows, r => r.StartsWith("FAIL "));
    }

    /// <summary>
    /// A drop in a later suite, two suites dropping, a suite whose only object drops, a survivor that reaches
    /// a dropped codeunit by id, and a healthy suite last: every one answers as the bundled run does, and
    /// each suite is the unit (a drop in one changes nothing about the next).
    /// </summary>
    [SkippableFact]
    public void DropsInSeveralSuites_AreEachReportedFailed_AndTheSuitesAfterThemStillRun()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = Many.Value;

        Assert.Equal(1, perSuite.Exit);
        Assert.Contains("FAIL TD Zero B.Works", perSuite.Rows);
        Assert.Contains("FAIL TD Zero C.Works", perSuite.Rows);
        Assert.Contains("FAIL TD Zero C.Works2", perSuite.Rows);
        Assert.Contains("FAIL TD Zero D.Works", perSuite.Rows);          // the suite whose only object dropped
        Assert.Contains("PASS Codeunit64001.FineA", perSuite.Rows);
        Assert.Contains("PASS Codeunit64102.FineB", perSuite.Rows);      // the survivor beside a drop ran
        Assert.Contains("PASS Codeunit64401.FineE", perSuite.Rows);      // the suite after every drop ran
        Assert.DoesNotContain("EMIT-ZERO", perSuite.Output);
        Assert.DoesNotContain(": EMIT-EXCLUDED", perSuite.Output);  // the survivor's error text names "EMIT-EXCLUDED" too

        Assert.Equal(bundled.Exit, perSuite.Exit);
        Assert.Equal(bundled.Rows, perSuite.Rows);
        Assert.Equal(bundled.Counts, perSuite.Counts);
        Assert.Equal(bundled.RowMessages, perSuite.RowMessages);
        Assert.Equal(bundled.JunitCases, perSuite.JunitCases);
    }

    /// <summary>
    /// A survivor that reaches a dropped codeunit by id is not refused (that is the non-tdd answer): it runs,
    /// and fails naming the codeunit and the AL error that dropped it (#5266), as the bundled run does.
    /// </summary>
    [SkippableFact]
    public void ASurvivorThatReachesADroppedCodeunit_FailsNamingTheError_AsTheBundledRunDoes()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = Many.Value;

        var row = perSuite.Rows.Single(r => r.EndsWith(".ReachesDropped"));
        Assert.StartsWith("FAIL ", row);
        Assert.Contains("\"TD Zero C\"", perSuite.Output);
        Assert.Contains("was dropped from it because it did not compile: error AL0185", perSuite.Output);
        Assert.Equal(bundled.Rows.Single(r => r.EndsWith(".ReachesDropped")), row);
        Assert.Equal(
            Regex.Matches(bundled.Output, "was dropped from it because it did not compile").Count,
            Regex.Matches(perSuite.Output, "was dropped from it because it did not compile").Count);
    }

    /// <summary>
    /// The control that must not change: nothing dropped, nothing generated. Same rows, same counts, exit 0,
    /// and the closing line still says no test referenced a missing symbol.
    /// </summary>
    [SkippableFact]
    public void ARunWithNothingDropped_IsUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = Clean.Value;

        Assert.Equal(0, perSuite.Exit);
        Assert.Equal(new[] { "PASS Codeunit61002.FineA", "PASS Codeunit62002.FineB" }, perSuite.Rows);
        Assert.Matches(@"^Tests: 2\s+passed 2\s+failed 0\s+errors 0$", perSuite.Counts);
        Assert.DoesNotContain("TDD-EXCLUDED", perSuite.Output);
        Assert.Contains("--tdd: no members were generated this run — no test referenced a missing symbol.", perSuite.Output);
        Assert.Equal(bundled.Rows, perSuite.Rows);
        Assert.Equal(bundled.TddLines, perSuite.TddLines);
    }

    /// <summary>
    /// --tdd generates a member a test needs; --per-suite compiles it in, so the test passes, and the closing
    /// block must list the member and the row must say it reached a stub. It used to say "no members were
    /// generated this run — no test referenced a missing symbol" over a test that had just passed against one.
    /// </summary>
    [SkippableFact]
    public void AGeneratedMember_IsListedAndAnnotated_UnderPerSuite_AsItIsBundled()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = Generated.Value;

        Assert.Equal(0, perSuite.Exit);
        Assert.Contains("PASS Codeunit61011.UsesMissing", perSuite.Rows);
        Assert.Contains("--tdd: generated 1 member(s) this run:", perSuite.Output);
        Assert.Contains("Impl: procedure \"NotYetWritten\"(): Integer", perSuite.Output);
        Assert.Contains("reaches generated stub(s): Impl: procedure \"NotYetWritten\"(): Integer", perSuite.Output);
        Assert.DoesNotContain("no test referenced a missing symbol", perSuite.Output);

        Assert.Equal(0, bundled.Exit);
        Assert.Equal(bundled.TddLines, perSuite.TddLines);
        Assert.Equal(bundled.Rows, perSuite.Rows);
    }

    /// <summary>
    /// The member is generated into ANOTHER folder of the run, so the cycle compiles again. The pass that is
    /// thrown away used to end in an EMIT-ZERO suite error under --per-suite (its test folder's only object was
    /// dropped); it is TDD-EXCLUDED as in the bundled run, and the final pass lists the member and annotates the
    /// row exactly as the bundled run does.
    /// </summary>
    [SkippableFact]
    public void AMemberGeneratedIntoAnotherFolder_IsReportedUnderPerSuite_AsItIsBundled()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled) = CrossBundle.Value;

        Assert.Equal(0, perSuite.Exit);
        Assert.Contains("TDD-EXCLUDED", perSuite.Output);
        Assert.DoesNotContain("EMIT-ZERO", perSuite.Output);
        Assert.Contains("--tdd: generated 1 member(s) this run:", perSuite.Output);
        Assert.Contains("reaches generated stub(s): Impl: procedure \"NotYetWritten\"(): Integer", perSuite.Output);
        Assert.Contains("PASS Codeunit62011.UsesMissing", perSuite.Rows);

        Assert.Equal(0, bundled.Exit);
        Assert.Equal(bundled.Rows, perSuite.Rows);
        Assert.Equal(bundled.TddLines, perSuite.TddLines);
    }

    /// <summary>
    /// Under `--jobs` both workers compile the suite and find the same drop; one claim per dropped object
    /// (#5262) makes one worker report its FAILED rows, so the aggregate counts them once, as one process
    /// does: 9 tests with 3 failed, exit 1, and the report holds each case once.
    /// </summary>
    [SkippableFact]
    public void ASharedSuiteUnderJobs_CountsTheDroppedFailedTestsOnce_AndExitsOne()
    {
        TestArtifacts.SkipIfMissing();
        var (exit, output, junit) = SharedUnderJobs.Value;

        Assert.Equal(1, exit);
        Assert.Contains("is shared by 2 worker(s)", output);
        Assert.Contains("Tests: 9   passed 6   failed 3   errors 0   skipped 0", output);
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.Equal(1, Regex.Matches(output, @"this worker claimed 1 of 1").Count);
        Assert.Equal(1, Regex.Matches(output, @"this worker claimed 0 of 1").Count);
        Assert.Equal(1, Regex.Matches(output, "FAIL +Jobs Excl Dropped.Dropped_A").Count);

        Assert.True(File.Exists(junit), $"--output-junit was not written: {junit}");
        var cases = XDocument.Load(junit).Descendants("testcase").ToList();
        Assert.Equal(9, cases.Count);
        Assert.Equal(
            new[] { "Dropped_A", "Dropped_B", "Dropped_C" },
            cases.Where(c => c.Elements("failure").Any()).Select(c => c.Attribute("name")!.Value)
                .OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    private static Spawned Run(string[] roots, string flags, string cache, string tag)
    {
        var junit = Path.Combine(TestScratch.Dir("al-runner-persuite-tdd-junit"), tag + ".xml");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" {string.Join(" ", roots.Select(r => $"\"{r}\""))} --show-pass {flags} --cache \"{Path.GetFullPath(cache)}\" --output-junit \"{junit}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return new Spawned(p.ExitCode, sb.ToString(), junit);
    }
}
