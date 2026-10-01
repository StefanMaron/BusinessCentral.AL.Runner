// ServerAffectedSelectionUnrecordedStateTests — #5057: session state outside the three kinds #5050
// recorded first: the last error and static .NET state reached through DotNet interop. Both
// directions per kind: an edited writer selects the reader the full run fails, and an edited reader
// brings the writer it reads from. The Randomize seed is pinned as NOT carried between tests.
// Mechanism: docs/server-mode.md#affectedonly-and-session-state.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionUnrecordedStateTests
{
    // Writer.Codeunit.al holds "US Writer" (62482) with Write(); Checker.Codeunit.al holds
    // "US Checker" (62480) with Check(). Each test codeunit reaches only one of them, so an edit to
    // one file changes exactly one test. One test per codeunit: under Codeunit isolation a codeunit
    // with a selected test runs whole (#5048), which would select a test for the wrong reason.
    private const string WriterTests = """
        codeunit 62483 "US Writer Tests"
        {
            Subtype = Test;

            [Test]
            procedure A_Writes()
            var
                W: Codeunit "US Writer";
            begin
                W.Write();
            end;
        }
        """;

    private const string ReaderTests = """
        codeunit 62484 "US Reader Tests"
        {
            Subtype = Test;

            [Test]
            procedure B_Reads()
            var
                C: Codeunit "US Checker";
            begin
                C.Check();
            end;
        }
        """;

    private const string ControlTests = """
        codeunit 62485 "US Control Tests"
        {
            Subtype = Test;

            [Test]
            procedure D_ReadsNothing()
            begin
                if 2 + 2 <> 4 then
                    Error('D failed');
            end;
        }
        """;

    // Runs after B and leaves the state at another value, so a narrowed run that skips A hands B
    // what this test left rather than what A wrote.
    private const string OverwriteTests = """
        codeunit 62486 "US Overwrite Tests"
        {
            Subtype = Test;

            [Test]
            procedure E_Overwrites()
            var
                W: Codeunit "US Overwriter";
            begin
                W.Write();
            end;
        }
        """;

    private sealed record Observed(Dictionary<string, (string Status, string Line)> Tests, bool ForcedFull, string Raw)
    {
        public string[] Ran => Tests.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static string Bundle(string prefix, string suffix, string writer, string checker, string overwriter,
        bool onPrem = false, AlRunner.AppManifest? testRunner = null)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        var dependency = testRunner == null ? "" :
            $$"""{ "id": "{{testRunner.AppId}}", "name": "{{testRunner.Name}}", "publisher": "{{testRunner.Publisher}}", "version": "{{testRunner.Version}}" }""";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5057000-0000-4a11-9111-{{suffix}}",
          "name": "Unrecorded State {{suffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ {{dependency}} ],
          "platform": "{{(testRunner == null ? "1.0.0.0" : "27.0.0.0")}}",
          "idRanges": [ { "from": 62480, "to": 62489 } ],
          {{(onPrem ? "\"target\": \"OnPrem\"," : "")}}
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Writer.Codeunit.al"), writer);
        File.WriteAllText(Path.Combine(dir, "Checker.Codeunit.al"), checker);
        File.WriteAllText(Path.Combine(dir, "WriterTests.Codeunit.al"), WriterTests);
        File.WriteAllText(Path.Combine(dir, "ReaderTests.Codeunit.al"), ReaderTests);
        File.WriteAllText(Path.Combine(dir, "ControlTests.Codeunit.al"), ControlTests);
        File.WriteAllText(Path.Combine(dir, "Overwriter.Codeunit.al"), overwriter);
        File.WriteAllText(Path.Combine(dir, "OverwriteTests.Codeunit.al"), OverwriteTests);
        return dir;
    }

    private static async Task<Observed> Send(CliServer server, string bundle, bool affectedOnly = true)
    {
        var request = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = affectedOnly,
        };
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        var forced = summary.TryGetProperty("selection", out var selection) && selection.GetProperty("forcedFull").GetBoolean();
        var tests = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(),
            e => (e.GetProperty("status").GetString()!, e.GetRawText()), StringComparer.Ordinal);
        return new Observed(tests, forced, raw);
    }

    // E may or may not run (it reads the kind too for DotNet), so the check is: these ran, D did not.
    private static void AssertRan(Observed o, string step, params string[] expected)
        => Assert.True(expected.All(o.Tests.ContainsKey) && !o.Tests.ContainsKey("D_ReadsNothing"),
            $"{step}: ran [{string.Join(", ", o.Ran)}], expected [{string.Join(", ", expected)}] and not D_ReadsNothing:\n{o.Raw}");

    private static void AssertStatus(Observed o, string test, string status, string? text = null)
    {
        Assert.True(o.Tests.TryGetValue(test, out var t) && t.Status == status, $"{test} must be {status}:\n{o.Raw}");
        if (text != null) Assert.Contains(text, t.Line, StringComparison.Ordinal);
    }

    // An edit to the writer changes what B reads: the full run on the edited source fails B, and the
    // narrowed run must have run B and failed it the same way. D, reading nothing, stays out.
    private static async Task AssertReaderSelected(string bundle, string editedWriter, string failure, string writerStatus = "pass")
    {
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        AssertStatus(baseline, "A_Writes", writerStatus);
        AssertStatus(baseline, "B_Reads", "pass");

        File.WriteAllText(Path.Combine(bundle, "Writer.Codeunit.al"), editedWriter);
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "writer edit", "A_Writes", "B_Reads");
        AssertStatus(edited, "B_Reads", "fail", failure);

        var full = await Send(server, bundle, affectedOnly: false);
        AssertStatus(full, "B_Reads", "fail", failure);
        AssertStatus(full, "D_ReadsNothing", "pass");
    }

    // An edit to the reader alone must bring the writer it reads from, so B sees what a full run
    // gives it and passes. Without A, B would read what E left at the end of the previous request.
    private static async Task AssertWriterBrought(string bundle, string editedChecker, string writerStatus = "pass")
    {
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        AssertStatus(baseline, "A_Writes", writerStatus);
        AssertStatus(baseline, "B_Reads", "pass");

        File.WriteAllText(Path.Combine(bundle, "Checker.Codeunit.al"), editedChecker);
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "reader edit", "A_Writes", "B_Reads");
        AssertStatus(edited, "B_Reads", "pass");
    }

    // ── The last error ──────────────────────────────────────────────────────────────────────────

    private static string TrappedErrorWriter(string text, string obj = "62482 \"US Writer\"") => $$"""
        codeunit {{obj}}
        {
            procedure Write()
            begin
                if not TryRaise() then;
            end;

            [TryFunction]
            local procedure TryRaise()
            begin
                Error('{{text}}');
            end;
        }
        """;

    private static string UncaughtErrorWriter(string text) => $$"""
        codeunit 62482 "US Writer"
        {
            procedure Write()
            begin
                Error('{{text}}');
            end;
        }
        """;

    private static string LastErrorChecker(string label = "LASTERR") => $$"""
        codeunit 62480 "US Checker"
        {
            procedure Check()
            begin
                if GetLastErrorText() <> 'LE-ONE' then
                    Error('{{label}}-%1', GetLastErrorText());
            end;
        }
        """;

    /// <summary>A trapped error's text survives the test boundary: editing the helper that raises it
    /// selects the test reading GetLastErrorText, which fails as the full run fails it.</summary>
    [SkippableFact]
    public async Task LastErrorReader_IsSelectedWhenTheRaisingHelperChanges()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-lasterror", "000000000001",
            TrappedErrorWriter("LE-ONE"), LastErrorChecker(), TrappedErrorWriter("LE-OTHER", "62487 \"US Overwriter\""));
        await AssertReaderSelected(bundle, TrappedErrorWriter("LE-TWO"), "LASTERR-LE-TWO");
    }

    /// <summary>The same through the persisted baseline (schema 6): a restarted server on the same
    /// cache selects the reader on its first request, with the AL-output cache warm.</summary>
    [SkippableFact]
    public async Task AcrossARestart_LastErrorReader_IsSelected()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-lasterror-restart", "000000000009",
            TrappedErrorWriter("LE-ONE"), LastErrorChecker(), TrappedErrorWriter("LE-OTHER", "62487 \"US Overwriter\""));
        var cache = TestScratch.Dir("al-runner-server-affected-unrecorded-lasterror-restart-cache");

        await using (var recorder = await CliServer.StartAsync(new[] { "--cache", cache }))
            Assert.True((await Send(recorder, bundle)).ForcedFull);

        File.WriteAllText(Path.Combine(bundle, "Writer.Codeunit.al"), TrappedErrorWriter("LE-TWO"));
        await using var restarted = await CliServer.StartAsync(new[] { "--cache", cache });
        var edited = await Send(restarted, bundle);
        Assert.False(edited.ForcedFull, $"the persisted baseline must let the first request narrow: {edited.Raw}");
        AssertRan(edited, "restarted", "A_Writes", "B_Reads");
        AssertStatus(edited, "B_Reads", "fail", "LASTERR-LE-TWO");
    }

    /// <summary>The other direction: an edited GetLastErrorText reader brings the test that set it.</summary>
    [SkippableFact]
    public async Task LastErrorReaderEdit_BringsTheTestThatRaisedIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-lasterror-rev", "000000000002",
            TrappedErrorWriter("LE-ONE"), LastErrorChecker(), TrappedErrorWriter("LE-OTHER", "62487 \"US Overwriter\""));
        await AssertWriterBrought(bundle, LastErrorChecker("LASTERROR"));
    }

    /// <summary>The same when the writer is a test that FAILS: its uncaught error is the last error
    /// the next test reads, so it is recorded as written by that test, not lost at the boundary.</summary>
    [SkippableFact]
    public async Task LastErrorReaderEdit_BringsTheFailingTestWhoseErrorItReads()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-lasterror-fail", "000000000003",
            UncaughtErrorWriter("LE-ONE"), LastErrorChecker(), TrappedErrorWriter("LE-OTHER", "62487 \"US Overwriter\""));
        await AssertWriterBrought(bundle, LastErrorChecker("LASTERROR"), writerStatus: "fail");
    }

    private const string QuietWriter = """
        codeunit 62482 "US Writer"
        {
            procedure Write()
            begin
            end;
        }
        """;

    /// <summary>A test that failed wrote the last error only because it failed. Once fixed it is not
    /// a writer any more, so an edit to a later reader no longer brings it.</summary>
    [SkippableFact]
    public async Task LastError_AFixedFailingTest_IsNoLongerLinked()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-lasterror-fixed", "000000000008",
            UncaughtErrorWriter("LE-ONE"), LastErrorChecker(), TrappedErrorWriter("LE-OTHER", "62487 \"US Overwriter\""));
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        AssertStatus(baseline, "A_Writes", "fail");

        File.WriteAllText(Path.Combine(bundle, "Writer.Codeunit.al"), QuietWriter);
        var fixedRun = await Send(server, bundle);
        AssertStatus(fixedRun, "A_Writes", "pass");

        File.WriteAllText(Path.Combine(bundle, "Checker.Codeunit.al"), LastErrorChecker("LASTERROR"));
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        Assert.True(edited.Tests.ContainsKey("B_Reads") && !edited.Tests.ContainsKey("A_Writes"),
            $"reader edit: ran [{string.Join(", ", edited.Ran)}], expected B_Reads without A_Writes:\n{edited.Raw}");
    }

    /// <summary>
    /// With Microsoft's Test Runner app loaded, its 130453 "ALTestRunner Reset Environment" clears
    /// the last error before every test method (corpus PR 520 asks BC the same question), so no
    /// test can read what another left: an edit to the raising helper does not select the reader,
    /// which passes in the full run either way.
    /// </summary>
    [SkippableFact]
    public async Task WithTheTestRunnerApp_TheLastErrorIsClearedPerTest_AndLinksNothing()
    {
        var dirs = TestRunnerMgtEventsTests.RequireProvisioned();
        var bundle = Bundle("al-runner-server-affected-unrecorded-lasterror-reset", "00000000000a",
            TrappedErrorWriter("LE-ONE"), EmptyLastErrorChecker, TrappedErrorWriter("LE-OTHER", "62487 \"US Overwriter\""),
            testRunner: dirs.TestRunner);
        await using var server = await CliServer.StartAsync(new[]
            { "--no-cache", "--package-cache", dirs.TestApps, "--package-cache", dirs.PlatformApps });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        AssertStatus(baseline, "B_Reads", "pass");

        File.WriteAllText(Path.Combine(bundle, "Writer.Codeunit.al"), TrappedErrorWriter("LE-TWO"));
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        Assert.True(edited.Tests.ContainsKey("A_Writes") && !edited.Tests.ContainsKey("B_Reads"),
            $"writer edit: ran [{string.Join(", ", edited.Ran)}], expected A_Writes without B_Reads:\n{edited.Raw}");
        AssertStatus(await Send(server, bundle, affectedOnly: false), "B_Reads", "pass");
    }

    /// <summary>
    /// #5091: a --server run loads the Test Runner app by default from its package caches, as the
    /// CLI does (#4816), so 130453 clears the last error before each test method. The official
    /// Windows container (BC 28.4, corpus PR 520) clears it too. Before the fix the server ran
    /// without the app and B read A's error.
    /// </summary>
    [SkippableFact]
    public async Task Server_LoadsTheDefaultTestRunnerApp_SoTheLastErrorIsClearedBetweenMethods()
    {
        var dirs = TestRunnerMgtEventsTests.RequireProvisioned();
        var bundle = RawBundle("al-runner-server-default-test-tool", "00000000000d", 62480,
            ("T.Codeunit.al", """
            codeunit 62480 "US Tool Probe"
            {
                Subtype = Test;
                [Test]
                procedure A_Traps()
                begin
                    asserterror Error('TP-A');
                end;

                [Test]
                procedure B_Reads()
                begin
                    if GetLastErrorText() <> '' then
                        Error('CARRIED-%1', GetLastErrorText());
                end;
            }
            """));
        File.WriteAllText(Path.Combine(bundle, "app.json"),
            File.ReadAllText(Path.Combine(bundle, "app.json")).Replace("\"platform\": \"1.0.0.0\"", "\"platform\": \"27.0.0.0\"", StringComparison.Ordinal));
        await using var server = await CliServer.StartAsync(
            new[] { "--no-cache", "--package-cache", dirs.TestApps, "--package-cache", dirs.PlatformApps },
            configure: psi => DefaultTestToolPin.LoadFrom(psi, dirs.TestApps));

        var run = await Send(server, bundle, affectedOnly: false);
        AssertStatus(run, "A_Traps", "pass");
        AssertStatus(run, "B_Reads", "pass");
    }

    private const string EmptyLastErrorChecker = """
        codeunit 62480 "US Checker"
        {
            procedure Check()
            begin
                if GetLastErrorText() <> '' then
                    Error('NOT-CLEARED-%1', GetLastErrorText());
            end;
        }
        """;

    // ── Static .NET state through DotNet interop ───────────────────────────────────────────────

    private const string EnvDeclaration = """
        dotnet
        {
            assembly("mscorlib")
            {
                type("System.Environment"; "US Env")
                {
                }
            }
        }

        """;

    private static string EnvWriter(string value, string obj = "62482 \"US Writer\"", bool declare = true)
        => (declare ? EnvDeclaration : "") + $$"""
        codeunit {{obj}}
        {
            procedure Write()
            var
                E: DotNet "US Env";
            begin
                E.SetEnvironmentVariable('AL_RUNNER_5057_PROBE', '{{value}}');
            end;
        }
        """;

    private static string EnvChecker(string label = "ENV") => $$"""
        codeunit 62480 "US Checker"
        {
            procedure Check()
            var
                E: DotNet "US Env";
            begin
                if E.GetEnvironmentVariable('AL_RUNNER_5057_PROBE') <> 'one' then
                    Error('{{label}}-%1', E.GetEnvironmentVariable('AL_RUNNER_5057_PROBE'));
            end;
        }
        """;

    /// <summary>A process environment variable set through DotNet survives the test boundary: editing
    /// the helper that sets it selects the test reading it.</summary>
    [SkippableFact]
    public async Task DotNetStaticStateReader_IsSelectedWhenTheWritingHelperChanges()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-dotnet", "000000000004",
            EnvWriter("one"), EnvChecker(), EnvWriter("other", "62487 \"US Overwriter\"", declare: false), onPrem: true);
        await AssertReaderSelected(bundle, EnvWriter("two"), "ENV-two");
    }

    /// <summary>The other direction: an edited DotNet reader brings the DotNet writer before it.</summary>
    [SkippableFact]
    public async Task DotNetStaticStateReaderEdit_BringsTheWriter()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-dotnet-rev", "000000000005",
            EnvWriter("one"), EnvChecker(), EnvWriter("other", "62487 \"US Overwriter\"", declare: false), onPrem: true);
        await AssertWriterBrought(bundle, EnvChecker("ENVIRONMENT"));
    }

    // A DotNet constructor does not go through the invoke path a method call takes and can reach
    // static state, so it is recorded on its own. An enum member is the only static field AL can
    // read (Guid.Empty is AL0132), and a constant holds no state, so it is not recorded.
    private const string OtherDotNetUseTests = """
        dotnet
        {
            assembly("mscorlib")
            {
                type("System.Text.StringBuilder"; "US SB")
                {
                }
                type("System.DateTimeKind"; "US Kind")
                {
                }
            }
        }

        codeunit 62488 "US Ctor Tests"
        {
            Subtype = Test;

            [Test]
            procedure F_ConstructsOnly()
            var
                SB: DotNet "US SB";
            begin
                SB := SB.StringBuilder();
            end;
        }
        """;

    private const string StaticFieldTests = """
        codeunit 62489 "US Field Tests"
        {
            Subtype = Test;

            [Test]
            procedure G_ReadsAnEnumMemberOnly()
            var
                K: DotNet "US Kind";
            begin
                K := K.Utc;
            end;
        }
        """;

    /// <summary>A test whose only DotNet use is a constructor is selected when a DotNet writer before
    /// it changes; one whose only use is an enum member is not.</summary>
    [SkippableFact]
    public async Task DotNetConstructorUse_IsRecorded_AndAnEnumMemberIsNot()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-dotnet-other", "000000000007",
            EnvWriter("one"), EnvChecker(), EnvWriter("other", "62487 \"US Overwriter\"", declare: false), onPrem: true);
        File.WriteAllText(Path.Combine(bundle, "CtorTests.Codeunit.al"), OtherDotNetUseTests);
        File.WriteAllText(Path.Combine(bundle, "FieldTests.Codeunit.al"), StaticFieldTests);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(baseline.Tests.Values.All(t => t.Status == "pass"), baseline.Raw);

        File.WriteAllText(Path.Combine(bundle, "Writer.Codeunit.al"), EnvWriter("two"));
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertRan(edited, "writer edit", "A_Writes", "B_Reads", "F_ConstructsOnly");
        Assert.False(edited.Tests.ContainsKey("G_ReadsAnEnumMemberOnly"), edited.Raw);
    }

    // ── The Randomize seed: not carried between tests ──────────────────────────────────────────

    private static string SeedWriter(int seed, string obj = "62482 \"US Writer\"") => $$"""
        codeunit {{obj}}
        {
            procedure Write()
            begin
                Randomize({{seed}});
            end;
        }
        """;

    // Passes only if the generator A seeded with 42 reached B.
    private const string SeedChecker = """
        codeunit 62480 "US Checker"
        {
            procedure Check()
            var
                V: Integer;
            begin
                V := Random(1000000);
                Randomize(42);
                if V <> Random(1000000) then
                    Error('RANDOM-%1', V);
            end;
        }
        """;

    /// <summary>
    /// Randomize(seed) does not need recording: every test starts from a generator the runner seeds
    /// from the run seed and the test's own identity (RunSeed.BeginTest, #2502), so B draws the same
    /// first value whatever A seeded. If this starts failing, the seed has become session state that
    /// outlives a test and needs a kind in AlSessionStateTracker.
    /// </summary>
    [SkippableFact]
    public async Task RandomizeSeed_DoesNotReachTheNextTest()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unrecorded-seed", "000000000006",
            SeedWriter(42), SeedChecker, SeedWriter(7, "62487 \"US Overwriter\""));
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var seeded42 = await Send(server, bundle, affectedOnly: false);
        AssertStatus(seeded42, "B_Reads", "fail", "RANDOM-");
        File.WriteAllText(Path.Combine(bundle, "Writer.Codeunit.al"), SeedWriter(43));
        var seeded43 = await Send(server, bundle, affectedOnly: false);
        Assert.Equal(
            JsonDocument.Parse(seeded42.Tests["B_Reads"].Line).RootElement.GetProperty("message").GetString(),
            JsonDocument.Parse(seeded43.Tests["B_Reads"].Line).RootElement.GetProperty("message").GetString());
    }

    // ── The review's two counterexamples (stma-review-2 on PR #5080) ──────────────────────────

    private static string RawBundle(string prefix, string suffix, int from, params (string File, string Content)[] files)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "c5057000-0000-4a11-9111-{{suffix}}", "name": "Unrecorded State {{suffix}}", "publisher": "AL Runner",
          "version": "1.0.0.0", "dependencies": [], "platform": "1.0.0.0",
          "idRanges": [ { "from": {{from}}, "to": {{from + 9}} } ], "runtime": "14.0" }
        """);
        foreach (var (file, content) in files) File.WriteAllText(Path.Combine(dir, file), content);
        return dir;
    }

    private static string TrappingTest(int id, string name, string method, string error, string condition = "") => $$"""
        codeunit {{id}} "{{name}}"
        {
            Subtype = Test;
            [Test]
            procedure {{method}}()
            begin
                {{(condition.Length > 0 ? condition + " then" : "")}}
                    if not TryRaise() then;
            end;

            [TryFunction]
            local procedure TryRaise()
            begin
                Error('{{error}}');
            end;
        }
        """;

    private static string CxSetter(int year) => $$"""
        codeunit 62491 "CX Setter"
        {
            procedure Set()
            begin
                WorkDate(DMY2Date(1, 1, {{year}}));
            end;
        }
        """;

    /// <summary>
    /// B traps its own error only while WorkDate is 2030. Editing the setter to 2031 makes B stop
    /// writing the last error, so C (reading it) sees W2's error in a full run. B's stale record
    /// must not end the walk from C before W2.
    /// </summary>
    [SkippableFact]
    public async Task AReaderWhoseInputChanged_DoesNotHideTheRealLastErrorWriter()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = RawBundle("al-runner-server-affected-unrecorded-cx", "00000000000b", 62490,
            ("Setter.Codeunit.al", CxSetter(2030)),
            ("XTests.Codeunit.al", """
            codeunit 62492 "CX X Tests"
            {
                Subtype = Test;
                [Test]
                procedure X_SetsWorkDate()
                var
                    S: Codeunit "CX Setter";
                begin
                    S.Set();
                end;
            }
            """),
            ("W2Tests.Codeunit.al", TrappingTest(62493, "CX W2 Tests", "W2_Traps", "W2-ERR")),
            ("BTests.Codeunit.al", TrappingTest(62494, "CX B Tests", "B_Conditional", "B-ERR", "if WorkDate() = DMY2Date(1, 1, 2030)")),
            ("CTests.Codeunit.al", """
            codeunit 62495 "CX C Tests"
            {
                Subtype = Test;
                [Test]
                procedure C_Reads()
                begin
                    if StrPos(GetLastErrorText(), 'W2') > 0 then
                        Error('C saw %1', GetLastErrorText());
                end;
            }
            """));
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        AssertStatus(baseline, "C_Reads", "pass");
        File.WriteAllText(Path.Combine(bundle, "Setter.Codeunit.al"), CxSetter(2031));
        var narrowed = await Send(server, bundle);
        Assert.False(narrowed.ForcedFull, narrowed.Raw);
        AssertStatus(narrowed, "W2_Traps", "pass");
        AssertStatus(narrowed, "C_Reads", "fail", "C saw W2-ERR");
        AssertStatus(await Send(server, bundle, affectedOnly: false), "C_Reads", "fail", "C saw W2-ERR");
    }

    private static string CyHelper(bool traps) => $$"""
        codeunit 62504 "CY B Helper"
        {
            procedure DoIt()
            begin
                {{(traps ? "if not TryRaise() then;" : "")}}
            end;

            [TryFunction]
            local procedure TryRaise()
            begin
                Error('B-ERR');
            end;
        }
        """;

    private static string CyChecker(string label) => $$"""
        codeunit 62505 "CY C Checker"
        {
            procedure Check()
            begin
                if GetLastErrorText() = 'W2-ERR' then
                    Error('{{label}} saw W2');
            end;
        }
        """;

    /// <summary>
    /// B's helper stops trapping, so B stops writing the last error. Its earlier write must not stay
    /// a definite write through the re-recording union: an edit to C's checker alone must still
    /// bring W2, which C reads in a full run.
    /// </summary>
    [SkippableFact]
    public async Task AWriteOnlyAnEarlierRecordHad_DoesNotEndTheWalk()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = RawBundle("al-runner-server-affected-unrecorded-cy", "00000000000c", 62500,
            ("BHelper.Codeunit.al", CyHelper(true)),
            ("CChecker.Codeunit.al", CyChecker("ONE")),
            ("W2Tests.Codeunit.al", TrappingTest(62501, "CY W2 Tests", "W2_Traps", "W2-ERR")),
            ("BTests.Codeunit.al", """
            codeunit 62502 "CY B Tests"
            {
                Subtype = Test;
                [Test]
                procedure B_UsesHelper()
                var
                    H: Codeunit "CY B Helper";
                begin
                    H.DoIt();
                end;
            }
            """),
            ("CTests.Codeunit.al", """
            codeunit 62503 "CY C Tests"
            {
                Subtype = Test;
                [Test]
                procedure C_Reads()
                var
                    K: Codeunit "CY C Checker";
                begin
                    K.Check();
                end;
            }
            """));
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        AssertStatus(await Send(server, bundle), "C_Reads", "pass");
        File.WriteAllText(Path.Combine(bundle, "BHelper.Codeunit.al"), CyHelper(false));
        AssertStatus(await Send(server, bundle), "C_Reads", "fail", "ONE saw W2");
        File.WriteAllText(Path.Combine(bundle, "CChecker.Codeunit.al"), CyChecker("TWO"));
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        AssertStatus(edited, "W2_Traps", "pass");
        AssertStatus(edited, "C_Reads", "fail", "TWO saw W2");
        AssertStatus(await Send(server, bundle, affectedOnly: false), "C_Reads", "fail", "TWO saw W2");
    }
}
