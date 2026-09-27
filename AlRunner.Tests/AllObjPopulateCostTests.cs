// AllObjPopulateCostTests — issue #4851. Every AllObj (2000000038) data-access handout used to
// re-walk the whole object inventory, Base and System Application included, only to insert
// nothing; Test Runner's per-test AllObj lookups made that most of a warm corpus run. These
// tests pin two things together, because a memo that is cheap and wrong is worse than the cost:
//
//   * the COST: many handouts in one run cost one inventory walk and one fill per store, counted through the
//     AL_RUNNER_PERF lines the runner logs for each (a count, not a duration, so it cannot flake
//     on a loaded box and cannot be satisfied by a fast machine);
//   * the ANSWERS: every lookup the fixture makes asserts a concrete value, positive and
//     negative, on both a cold and a warm run against one --cache root, and a --server process
//     that is handed a changed bundle answers for the new objects, not the memoised ones.
//
// Runner-specific, so no corpus test: real BC has no inventory walk to count, and the AL answers
// asserted here are the ones corpus AllObj codeunits already pin on a service tier.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public class AllObjPopulateCostTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const int BaseId = 62720;
    private const int Lookups = 25;

    private static readonly Regex HandoutLine = new(@"^PERF AllObj\.Handout$", RegexOptions.Multiline);
    private static readonly Regex TopUpLine = new(@"^PERF AllObj\.TopUp (\d+) row\(s\)$", RegexOptions.Multiline);
    private static readonly Regex WalkLine = new(@"^PERF AllObj\.InventoryWalk (\d+) row\(s\)$", RegexOptions.Multiline);

    private static (string output, int exit) RunRunner(string cacheDir, string app)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        args.Append(" --cache \"").Append(cacheDir).Append('"');
        args.Append(" \"").Append(app).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            Environment = { ["AL_RUNNER_PERF"] = "1" },
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// One app: a table and a page whose names carry <paramref name="tag"/>, and two test codeunits
    /// that each look AllObj up <see cref="Lookups"/> times through a fresh local record, the shape
    /// of Test Runner's per-test lookup. With <paramref name="withSecondTable"/> a second table
    /// exists, and the tests assert it is listed; without it, that it is not.
    /// </summary>
    private static void WriteFixture(string dir, string tag, bool withSecondTable)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "6a4851e0-0000-4c38-9e25-000000004851",
          "name": "IT4851 AllObj Populate Cost",
          "publisher": "IssueTest4851",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{BaseId}}, "to": {{BaseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        var second = withSecondTable
            ? $$"""
            table {{BaseId + 2}} "IT4851 {{tag}} Second"
            {
                fields { field(1; "Code"; Code[20]) { } }
                keys { key(PK; "Code") { Clustered = true; } }
            }
            """
            : "";
        // Objects in the id range: table, page, the helper and two test codeunits, plus the
        // second table when present.
        var inRange = withSecondTable ? 6 : 5;
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), $$"""
        table {{BaseId}} "IT4851 {{tag}} Widget"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        {{second}}

        page {{BaseId + 1}} "IT4851 {{tag}} Card"
        {
            PageType = Card;
            SourceTable = "IT4851 {{tag}} Widget";
        }

        codeunit {{BaseId + 3}} "IT4851 Lookup"
        {
            procedure CheckOnce()
            var
                AllObj: Record AllObj;
            begin
                if not AllObj.Get(AllObj."Object Type"::Table, {{BaseId}}) then
                    Error('AllObj must list table {{BaseId}}');
                if AllObj."Object Name" <> 'IT4851 {{tag}} Widget' then
                    Error('table {{BaseId}} is named <%1>, expected <IT4851 {{tag}} Widget>', AllObj."Object Name");
                if AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 9}}) then
                    Error('AllObj must NOT list table {{BaseId + 9}}');
                if AllObj.Get(AllObj."Object Type"::Page, {{BaseId}}) then
                    Error('AllObj must NOT list page {{BaseId}}: the id is a table''s, not a page''s');
                if AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 2}}) <> {{(withSecondTable ? "true" : "false")}} then
                    Error('AllObj listing of table {{BaseId + 2}} must be {{withSecondTable}}');

                AllObj.Reset();
                AllObj.SetRange("Object Type", AllObj."Object Type"::Page);
                AllObj.SetRange("Object ID", {{BaseId + 1}});
                if not AllObj.FindFirst() then
                    Error('a filtered read must find page {{BaseId + 1}}');
                if AllObj."Object Name" <> 'IT4851 {{tag}} Card' then
                    Error('page {{BaseId + 1}} is named <%1>, expected <IT4851 {{tag}} Card>', AllObj."Object Name");

                AllObj.Reset();
                AllObj.SetRange("Object ID", {{BaseId}}, {{BaseId + 9}});
                if AllObj.Count() <> {{inRange}} then
                    Error('AllObj lists %1 objects in {{BaseId}}..{{BaseId + 9}}, expected {{inRange}}', AllObj.Count());
            end;

            procedure CheckMany()
            var
                i: Integer;
            begin
                for i := 1 to {{Lookups}} do
                    CheckOnce();
            end;
        }

        codeunit {{BaseId + 4}} "IT4851 Tests A"
        {
            Subtype = Test;

            [Test]
            procedure ManyLookupsA()
            var
                Lookup: Codeunit "IT4851 Lookup";
            begin
                Lookup.CheckMany();
            end;
        }

        codeunit {{BaseId + 5}} "IT4851 Tests B"
        {
            Subtype = Test;

            [Test]
            procedure ManyLookupsB()
            var
                Lookup: Codeunit "IT4851 Lookup";
            begin
                Lookup.CheckMany();
            end;
        }
        """);
    }

    /// <summary>
    /// The cost claim, on a cold and then a warm run against one cache root. Two test codeunits,
    /// so two providers across a boundary, each handed out dozens of times: the inventory is
    /// walked once per key, not once per handout.
    /// </summary>
    [SkippableFact]
    public void ManyHandouts_WalkTheInventoryOnce_AndEveryLookupStillAnswers_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-4851-cost");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app, "V1", withSecondTable: false);
            var cache = Path.Combine(root, "cache");

            foreach (var pass in new[] { "cold", "warm" })
            {
                var (output, exit) = RunRunner(cache, app);

                // [THEN] Every lookup answered: both tests passed, so each of the 2 x 25 rounds of
                // positive and negative assertions held.
                Assert.True(exit == 0 && output.Contains("2P/0F/0E"),
                    $"{pass} run: expected both tests to pass (exit {exit}), got:\n{output}");

                var handouts = HandoutLine.Matches(output).Count;
                var walks = WalkLine.Matches(output).Count;
                var topUps = TopUpLine.Matches(output).Count;

                // [THEN] The loop reached the populate path on every lookup. Without this the
                // walk bound below could hold because nothing was handed out at all.
                Assert.True(handouts >= 2 * Lookups,
                    $"{pass} run: expected at least {2 * Lookups} AllObj handouts, got {handouts}:\n{output}");

                // [THEN] The rows were built once for the run: one visibility, one inventory.
                // Before #4851 every handout walked, so this equalled the handout count.
                Assert.True(walks == 1,
                    $"{pass} run: {walks} inventory walk(s) for {handouts} handout(s); expected 1:\n{output}");

                // [THEN] And each store was filled once, on its first handout: one per test
                // codeunit, whose boundary drops the store. The other handouts added nothing.
                Assert.True(topUps == 2,
                    $"{pass} run: {topUps} top-up(s) for {handouts} handout(s); expected 2, one per codeunit:\n{output}");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string RunTests(string dir)
        => JsonSerializer.Serialize(new { command = "runTests", sourcePaths = new[] { dir }, packagePaths = Array.Empty<string>() });

    /// <summary>
    /// The invalidation claim. One --server process, two requests on the same bundle directory:
    /// the second renames both objects and adds a table. Its AL asserts the new names and the new
    /// table, so an inventory memoised across the reload fails it by name.
    /// </summary>
    [SkippableFact]
    public async Task Server_ASecondRequestWithChangedObjects_AnswersFromTheNewObjects()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-4851-server");
        try
        {
            var app = Path.Combine(root, "app");
            WriteFixture(app, "V1", withSecondTable: false);

            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

            var lines1 = await server.SendRequestStreamingAsync(RunTests(app));
            var (events1, _) = ProtocolV2Streaming.Split(lines1);
            Assert.Equal(2, events1.Count);
            foreach (var e in events1)
                Assert.True(e.GetProperty("status").GetString() == "pass", string.Join(" | ", lines1));

            WriteFixture(app, "V2", withSecondTable: true);

            var lines2 = await server.SendRequestStreamingAsync(RunTests(app));
            var (events2, _) = ProtocolV2Streaming.Split(lines2);
            Assert.Equal(2, events2.Count);
            foreach (var e in events2)
                Assert.True(e.GetProperty("status").GetString() == "pass", string.Join(" | ", lines2));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
