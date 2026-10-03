// #5118 — --server and an EMIT-EXCLUDED test codeunit.
//
// When BC cannot compile one test codeunit of a module, the CLI drops it, runs the surviving
// codeunits, reports the dropped codeunit's [Test] procedures as SKIPPED, and ends with a suite
// error EMIT-EXCLUDED and exit code 3 (#3476). --server used to return exit code 3 and no test at
// all for the same module. The first fact runs one module through the CLI and through --server
// and compares the result rows, so the CLI is the oracle for what "the same" means.
//
// The rest keep what the CLI refuses refused: a survivor that reaches the dropped codeunit by id
// (the module is not run), a module in which nothing survives (loud, never a pass with zero
// tests), a dropped library codeunit beside a droppable test codeunit (a real compile error is
// never run around), a dropped profile beside a dropped test codeunit, and `execute`, which keeps
// the refusal.

using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerEmitExcludedSurvivorsTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerEmitExcludedSurvivorsTests(SharedCliServer fixture) => _fixture = fixture;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // One AppId and one id range per bundle: the shared server keeps a compiled module per AppId
    // for its whole life (SharedCliServer's rule c).
    private static void WriteApp(string root, string appId, int from)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Server Emit Excluded Survivors {{from}}",
          "publisher": "Repro5118",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{from}}, "to": {{from + 9}} } ],
          "runtime": "14.0"
        }
        """);
    }

    /// <summary>A test codeunit of two tests that bind and run.</summary>
    private static string Healthy(int id) => $$"""
        codeunit {{id}} "Srv Excl Healthy {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure Healthy_A()
            begin
                if 1 + 1 <> 2 then
                    Error('arithmetic broke');
            end;

            [Test]
            procedure Healthy_B()
            begin
                if 2 + 2 <> 4 then
                    Error('arithmetic broke');
            end;
        }
        """;

    /// <summary>A test codeunit of two tests that cannot bind: BC drops it from the module.</summary>
    private static string Broken(int id) => $$"""
        codeunit {{id}} "Srv Excl Broken {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure Broken_A()
            var
                Missing: Codeunit "This Codeunit Does Not Exist At All";
            begin
                Missing.DoSomething();
            end;

            [Test]
            procedure Broken_B()
            begin
            end;
        }
        """;

    /// <summary>The same codeunit, with the offending reference removed.</summary>
    private static string Fixed(int id) => $$"""
        codeunit {{id}} "Srv Excl Broken {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure Broken_A()
            begin
            end;

            [Test]
            procedure Broken_B()
            begin
            end;
        }
        """;

    private static string Req(string bundle, bool affectedOnly = false) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
        affectedOnly = affectedOnly ? (bool?)true : null,
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    private sealed record Answer(
        int Exit, int Total, int Passed, IReadOnlyList<(string Name, string Status)> Rows, string ErrorText, string Raw);

    private async Task<Answer> Ask(string bundle, bool affectedOnly = false)
    {
        var server = await _fixture.GetAsync(ServerArgs());
        var lines = await server.SendRequestStreamingAsync(Req(bundle, affectedOnly), TimeSpan.FromSeconds(300));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var rows = events
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("status").GetString()!))
            .OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
        var text = summary.TryGetProperty("compilationErrors", out var groups)
            ? string.Join(" | ", groups.EnumerateArray()
                .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())))
            : "";
        return new Answer(summary.GetProperty("exitCode").GetInt32(), summary.GetProperty("total").GetInt32(),
            summary.GetProperty("passed").GetInt32(), rows, text, string.Join("\n", lines));
    }

    private static IEnumerable<string> ServerArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps) ? new[] { "--package-cache", platformApps } : Array.Empty<string>();
    }

    private static (int Exit, IReadOnlyList<(string Name, string Status)> Rows, string Output) RunCli(string bundle)
    {
        var scratch = TestScratch.Dir("al-runner-server-excl-survivors-cli");
        var platformApps = TestArtifacts.PlatformAppsDir();
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"\"{bundle}\" --show-pass --cache \"{Path.Combine(scratch, "cache")}\""
            + (Directory.Exists(platformApps) ? $" --package-cache \"{platformApps}\"" : ""),
            lowSplitFloor: false);
        var rows = Regex.Matches(output, @"^(PASS|FAIL|ERROR|SKIP)\s+(.+?) \(\d+ms\)", RegexOptions.Multiline)
            .Select(m => (m.Groups[2].Value, m.Groups[1].Value switch
            {
                "PASS" => "pass", "SKIP" => "skipped", "FAIL" => "fail", _ => "error",
            }))
            .OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
        return (exit, rows, output);
    }

    private static string Describe(IEnumerable<(string Name, string Status)> rows) =>
        string.Join(", ", rows.Select(r => $"{r.Name}={r.Status}"));

    /// <summary>
    /// The oracle: the CLI's rows for a module with one dropped codeunit are the server's rows. Before the
    /// fix the server answered exit code 3, total 0 and no row at all.
    /// </summary>
    [SkippableFact]
    public async Task SafeDrop_ServerReportsTheRowsTheCliReports()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-oracle");
        WriteApp(root, "5118a001-0000-4000-8000-000000000001", 62300);
        File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), Healthy(62301));
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62302));
        try
        {
            var (cliExit, cliRows, cliOutput) = RunCli(root);
            Assert.Equal(3, cliExit);
            // The oracle itself: two survivors passed, the dropped codeunit's two tests are SKIPPED rows.
            Assert.Equal(2, cliRows.Count(r => r.Status == "pass"));
            Assert.Equal(2, cliRows.Count(r => r.Status == "skipped"));
            Assert.Equal(4, cliRows.Count);

            var server = await Ask(root);
            Assert.True(server.Rows.SequenceEqual(cliRows),
                $"server rows [{Describe(server.Rows)}] differ from the CLI's [{Describe(cliRows)}]\n{server.Raw}\n--- CLI ---\n{cliOutput}");
            Assert.Equal(3, server.Exit);
            Assert.Equal(4, server.Total);
            Assert.Equal(2, server.Passed);
            Assert.Contains("EMIT-EXCLUDED", server.ErrorText);
            Assert.Contains("AL0185", server.ErrorText);
            Assert.Contains("Srv Excl Broken 62302", server.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// One server process, one directory, four requests: the drop is reported every time the module is
    /// compiled with it, and is not sticky in either direction. A request that finds the same source
    /// must not serve the module without saying a codeunit is missing, and an edit that fixes the dropped
    /// codeunit must run its tests.
    /// </summary>
    [SkippableFact]
    public async Task SafeDrop_IsReportedOnEveryRequest_AndFixingTheObjectEndsIt()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-sticky");
        WriteApp(root, "5118a002-0000-4000-8000-000000000002", 62310);
        File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), Healthy(62311));
        var broken = Path.Combine(root, "Broken.Codeunit.al");
        File.WriteAllText(broken, Broken(62312));
        const string Healthies = "Codeunit62311.Healthy_A=pass, Codeunit62311.Healthy_B=pass";
        try
        {
            var first = await Ask(root);
            var expectedDropped = $"{Healthies}, Srv Excl Broken 62312.Broken_A=skipped, Srv Excl Broken 62312.Broken_B=skipped";
            Assert.Equal(expectedDropped, Describe(first.Rows));
            Assert.Equal(3, first.Exit);
            Assert.Contains("EMIT-EXCLUDED", first.ErrorText);

            var second = await Ask(root);
            Assert.Equal(Describe(first.Rows), Describe(second.Rows));
            Assert.Equal(3, second.Exit);
            Assert.Contains("EMIT-EXCLUDED", second.ErrorText);

            File.WriteAllText(broken, Fixed(62312));
            var fixedRun = await Ask(root);
            Assert.Equal(0, fixedRun.Exit);
            Assert.Equal(4, fixedRun.Total);
            Assert.Equal(4, fixedRun.Passed);
            Assert.DoesNotContain("EMIT-EXCLUDED", fixedRun.ErrorText);
            Assert.DoesNotContain(fixedRun.Rows, r => r.Status == "skipped");

            File.WriteAllText(broken, Broken(62312));
            var broken2 = await Ask(root);
            Assert.Equal(Describe(first.Rows), Describe(broken2.Rows));
            Assert.Equal(3, broken2.Exit);

            // The change model's replay of an unchanged module must not hide the drop either.
            var selA = await Ask(root, affectedOnly: true);
            var selB = await Ask(root, affectedOnly: true);
            Assert.Equal(3, selA.Exit);
            Assert.Equal(3, selB.Exit);
            Assert.Contains("EMIT-EXCLUDED", selB.ErrorText);
            Assert.Equal(2, selB.Rows.Count(r => r.Status == "skipped"));

            // And an edit to a surviving codeunit, which the change model would otherwise compile alone
            // and merge into the last module, whose record of what was dropped it does not carry.
            File.AppendAllText(Path.Combine(root, "Healthy.Codeunit.al"), "\n// edited\n");
            var selC = await Ask(root, affectedOnly: true);
            Assert.Equal(3, selC.Exit);
            Assert.Contains("EMIT-EXCLUDED", selC.ErrorText);
            Assert.Equal(2, selC.Rows.Count(r => r.Status == "skipped"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A module compiled with a dropped codeunit is not handed to a second directory holding the same source and
    /// the same app identity: that request compiles it itself and reports the drop, rather than running the
    /// survivors with nothing saying a codeunit is missing.
    /// </summary>
    [SkippableFact]
    public async Task SafeDrop_AnotherDirectoryWithTheSameSource_IsReportedToo()
    {
        TestArtifacts.SkipIfMissing();
        var rootA = TestScratch.Dir("al-runner-server-excl-survivors-dir-a");
        var rootB = TestScratch.Dir("al-runner-server-excl-survivors-dir-b");
        try
        {
            foreach (var root in new[] { rootA, rootB })
            {
                WriteApp(root, "5118a005-0000-4000-8000-000000000005", 62340);
                File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), Healthy(62341));
                File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62342));
            }
            var a = await Ask(rootA);
            var b = await Ask(rootB);
            Assert.Equal(2, a.Rows.Count(r => r.Status == "skipped"));
            Assert.Equal(Describe(a.Rows), Describe(b.Rows));
            Assert.Equal(3, b.Exit);
            Assert.Contains("EMIT-EXCLUDED", b.ErrorText);
        }
        finally
        {
            try { Directory.Delete(rootA, recursive: true); } catch { }
            try { Directory.Delete(rootB, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A dropped profile declares no [Test] procedure and nothing to run, so the CLI runs the module around it
    /// and reports no suite error (#2238). The server refused the whole module for it as well.
    /// </summary>
    [SkippableFact]
    public async Task DroppedProfile_ServerRunsTheModuleWithoutASuiteError()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-profile");
        WriteApp(root, "5118a006-0000-4000-8000-000000000006", 62350);
        File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), Healthy(62351));
        // BC's own emitter crashes on a profile whose RoleCenter page is declared nowhere, and the retry
        // loop drops it (BcCompilerProfileEmitCrashTests).
        File.WriteAllText(Path.Combine(root, "BadProfile.al"), BadProfile);
        try
        {
            var (cliExit, cliRows, cliOutput) = RunCli(root);
            Assert.Equal(0, cliExit);
            Assert.Equal(2, cliRows.Count(r => r.Status == "pass"));

            var a = await Ask(root);
            Assert.True(a.Rows.SequenceEqual(cliRows), $"server rows [{Describe(a.Rows)}] differ from the CLI's [{Describe(cliRows)}]\n{a.Raw}");
            Assert.Equal(0, a.Exit);
            Assert.Equal("", a.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private const string BadProfile = """
        profile "Srv Excl Bad Profile"
        {
            Caption = 'Srv Excl Bad Profile';
            RoleCenter = "Srv Excl Nonexistent Page";
        }
        """;

    /// <summary>
    /// The profile carve-out holds only when EVERY dropped object is a profile. A broken profile beside a broken
    /// test codeunit must not turn the codeunit's two tests into nothing: both paths refuse the module with exit
    /// code 3 and a reason naming both objects, where a profile-only reading would run the healthy codeunit and
    /// report a green with the two dropped tests absent.
    /// </summary>
    [SkippableFact]
    public async Task ProfilePlusDroppedTestCodeunit_ServerAnswersAsTheCli_AndTheDroppedTestsAreReported()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-mixed");
        WriteApp(root, "5118a007-0000-4000-8000-000000000007", 62360);
        File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), Healthy(62361));
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62362));
        File.WriteAllText(Path.Combine(root, "BadProfile.al"), BadProfile);
        try
        {
            var (cliExit, cliRows, cliOutput) = RunCli(root);
            Assert.Equal(3, cliExit);
            var server = await Ask(root);
            Assert.True(server.Rows.SequenceEqual(cliRows),
                $"server rows [{Describe(server.Rows)}] differ from the CLI's [{Describe(cliRows)}]\n{server.Raw}\n--- CLI ---\n{cliOutput}");
            Assert.Equal(3, server.Exit);
            // The module is refused on both paths: nothing runs, and the reason names both dropped objects.
            Assert.Empty(cliRows);
            Assert.Equal(0, server.Total);
            Assert.Contains("The module was NOT run", server.ErrorText);
            Assert.Contains("Srv Excl Bad Profile", server.ErrorText);
            Assert.Contains("Srv Excl Broken 62362", server.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A real compile error is never run around: a library (non-test) codeunit that cannot bind is dropped
    /// beside a droppable test codeunit, and a survivor may call it, so no test runs, as on the CLI.
    /// </summary>
    [SkippableFact]
    public async Task DroppedLibraryCodeunitBesideADroppableTestCodeunit_IsRefusedAndNothingRuns()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-library");
        WriteApp(root, "5118a008-0000-4000-8000-000000000008", 62370);
        File.WriteAllText(Path.Combine(root, "Healthy.Codeunit.al"), Healthy(62371));
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62372));
        File.WriteAllText(Path.Combine(root, "Library.Codeunit.al"), """
        codeunit 62373 "Srv Excl Library"
        {
            procedure Helper()
            var
                Missing: Codeunit "This Codeunit Does Not Exist At All";
            begin
                Missing.DoSomething();
            end;
        }
        """);
        try
        {
            var (cliExit, cliRows, cliOutput) = RunCli(root);
            Assert.Equal(3, cliExit);
            Assert.DoesNotContain(cliRows, r => r.Status == "pass");
            var server = await Ask(root);
            Assert.True(server.Rows.SequenceEqual(cliRows),
                $"server rows [{Describe(server.Rows)}] differ from the CLI's [{Describe(cliRows)}]\n{server.Raw}\n--- CLI ---\n{cliOutput}");
            Assert.Equal(3, server.Exit);
            Assert.DoesNotContain(server.Rows, r => r.Status == "pass");
            Assert.Contains("The module was NOT run", server.ErrorText);
            Assert.Contains("Srv Excl Library", server.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// `execute` has no row to show a skipped test in, so a dropped test codeunit still refuses its request
    /// (docs/server-mode.md): exit code 3, the reason says so, and nothing is run.
    /// </summary>
    [SkippableFact]
    public async Task Execute_ADroppedTestCodeunit_StillRefusesTheRequest()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-execute");
        WriteApp(root, "5118a009-0000-4000-8000-000000000009", 62380);
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62382));
        File.WriteAllText(Path.Combine(root, "Runner.Codeunit.al"), """
        codeunit 62381 "Srv Excl Execute"
        {
            trigger OnRun()
            begin
            end;
        }
        """);
        try
        {
            var server = await _fixture.GetAsync(ServerArgs());
            var line = await server.SendAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["command"] = "execute", ["sourcePaths"] = new[] { root },
            }), TimeSpan.FromSeconds(240));
            using var doc = JsonDocument.Parse(line);
            var text = string.Join(" | ", doc.RootElement.GetProperty("compilationErrors").EnumerateArray()
                .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));
            Assert.True(doc.RootElement.GetProperty("exitCode").GetInt32() == 3, line);
            Assert.Contains("EMIT-EXCLUDED", text);
            Assert.Contains("The module was NOT run: this request cannot report", text);
            Assert.Empty(doc.RootElement.GetProperty("tests").EnumerateArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// What the CLI refuses stays refused. A survivor that reaches the dropped codeunit by id compiles, so
    /// running the module would call a codeunit it does not contain: no test runs and the reason names both.
    /// </summary>
    [SkippableFact]
    public async Task UnsafeDrop_SurvivorReachesTheDroppedCodeunitById_IsRefusedAndNothingRuns()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-byid");
        WriteApp(root, "5118a003-0000-4000-8000-000000000003", 62320);
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62322));
        File.WriteAllText(Path.Combine(root, "Reaches.Codeunit.al"), """
        codeunit 62321 "Srv Excl Reaches"
        {
            Subtype = Test;

            [Test]
            procedure ReachesTheDroppedOneById()
            begin
                if Codeunit.Run(62322) then;
            end;
        }
        """);
        try
        {
            var a = await Ask(root);
            Assert.Equal(3, a.Exit);
            Assert.Equal(0, a.Total);
            Assert.Empty(a.Rows);
            Assert.Contains("EMIT-EXCLUDED", a.ErrorText);
            Assert.Contains("The module was NOT run", a.ErrorText);
            Assert.Contains("Reaches.Codeunit.al", a.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A module in which nothing survives is a loud failure with the AL diagnostic, not a pass with zero tests.
    /// </summary>
    [SkippableFact]
    public async Task NothingSurvives_IsALoudFailureNotAnEmptyPass()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-excl-survivors-none");
        WriteApp(root, "5118a004-0000-4000-8000-000000000004", 62330);
        File.WriteAllText(Path.Combine(root, "Broken.Codeunit.al"), Broken(62331));
        try
        {
            var a = await Ask(root);
            Assert.Equal(3, a.Exit);
            Assert.Equal(0, a.Total);
            Assert.Contains("AL0185", a.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
