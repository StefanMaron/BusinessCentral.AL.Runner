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
    private static readonly Regex ReuseLine = new(@"^PERF AllObj\.Reuse$", RegexOptions.Multiline);
    private static readonly Regex CaptionHandoutLine = new(@"^PERF AllObjWithCaption\.Handout$", RegexOptions.Multiline);
    private static readonly Regex CaptionTopUpLine = new(@"^PERF AllObjWithCaption\.TopUp (\d+) row\(s\)$", RegexOptions.Multiline);
    private static readonly Regex CaptionWalkLine = new(@"^PERF AllObjWithCaption\.InventoryWalk (\d+) row\(s\)$", RegexOptions.Multiline);
    private static readonly Regex CaptionReuseLine = new(@"^PERF AllObjWithCaption\.Reuse$", RegexOptions.Multiline);

    internal static (string output, int exit) RunRunner(string cacheDir, string app)
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
            Caption = 'IT4851 {{tag}} Card Caption';
            SourceTable = "IT4851 {{tag}} Widget";
        }

        codeunit {{BaseId + 3}} "IT4851 Lookup"
        {
            procedure CheckOnce()
            var
                AllObj: Record AllObj;
                WithCaption: Record AllObjWithCaption;
            begin
                if not WithCaption.Get(WithCaption."Object Type"::Page, {{BaseId + 1}}) then
                    Error('AllObjWithCaption must list page {{BaseId + 1}}');
                if WithCaption."Object Caption" <> 'IT4851 {{tag}} Card Caption' then
                    Error('page {{BaseId + 1}} caption is <%1>, expected <IT4851 {{tag}} Card Caption>', WithCaption."Object Caption");
                if WithCaption.Get(WithCaption."Object Type"::Table, {{BaseId + 9}}) then
                    Error('AllObjWithCaption must NOT list table {{BaseId + 9}}');

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

                foreach (var (table, handoutLine, walkLine, topUpLine, reuseLine) in new[]
                         {
                             ("AllObj", HandoutLine, WalkLine, TopUpLine, ReuseLine),
                             ("AllObjWithCaption", CaptionHandoutLine, CaptionWalkLine, CaptionTopUpLine, CaptionReuseLine),
                         })
                {
                    var handouts = handoutLine.Matches(output).Count;
                    var walks = walkLine.Matches(output).Count;
                    var topUps = topUpLine.Matches(output).Count;
                    var reuses = reuseLine.Matches(output).Count;

                    // [THEN] The loop reached the populate path on every lookup. Without this the
                    // bounds below could hold because nothing was handed out at all.
                    Assert.True(handouts >= 2 * Lookups,
                        $"{pass} run: expected at least {2 * Lookups} {table} handouts, got {handouts}:\n{output}");

                    // [THEN] The rows were built once for the run: one visibility, one inventory.
                    // Before #4851 (#4859 for AllObjWithCaption) every handout walked.
                    Assert.True(walks == 1,
                        $"{pass} run: {walks} {table} inventory walk(s) for {handouts} handout(s); expected 1:\n{output}");

                    // [THEN] One store was filled, by the first codeunit; the second codeunit was
                    // handed that store back at its boundary instead of a new one (#4859).
                    Assert.True(topUps == 1 && reuses == 1,
                        $"{pass} run: {topUps} {table} top-up(s) and {reuses} reuse(s) for {handouts} handout(s); "
                        + $"expected 1 and 1:\n{output}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Two symmetric test codeunits that each check the stores are clean, then write to both
    /// tables: an Insert of a row no object backs, and a Modify of a real one. Codeunit order is
    /// not id order, so whichever runs second is the one that would read a leaked write.
    /// </summary>
    private static void WriteWriteLeakFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "6a4851e0-0000-4c38-9e25-000000004859",
          "name": "IT4859 AllObj Write Leak",
          "publisher": "IssueTest4859",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{BaseId + 20}}, "to": {{BaseId + 29}} } ],
          "runtime": "14.0"
        }
        """);
        string Tests(int id, string letter) => $$"""
        codeunit {{id}} "IT4859 Leak Tests {{letter}}"
        {
            Subtype = Test;

            [Test]
            procedure AWriteDoesNotReachTheNextCodeunit{{letter}}()
            var
                Probe: Codeunit "IT4859 Leak Probe";
            begin
                Probe.CheckClean();
                Probe.Write();
            end;
        }
        """;
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), $$"""
        table {{BaseId + 20}} "IT4859 Leak Widget"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        table {{BaseId + 24}} "IT4859 Leak Renamed"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit {{BaseId + 21}} "IT4859 Leak Probe"
        {
            procedure CheckClean()
            var
                AllObj: Record AllObj;
                WithCaption: Record AllObjWithCaption;
            begin
                if AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 28}}) then
                    Error('LEAK: AllObj lists table {{BaseId + 28}}, which only a previous codeunit inserted');
                if not AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 20}}) then
                    Error('AllObj must list table {{BaseId + 20}}');
                if AllObj."Object Name" <> 'IT4859 Leak Widget' then
                    Error('LEAK: table {{BaseId + 20}} is named <%1>, as a previous codeunit modified it', AllObj."Object Name");
                if WithCaption.Get(WithCaption."Object Type"::Table, {{BaseId + 28}}) then
                    Error('LEAK: AllObjWithCaption lists table {{BaseId + 28}}, which only a previous codeunit inserted');
                if not WithCaption.Get(WithCaption."Object Type"::Table, {{BaseId + 20}}) then
                    Error('AllObjWithCaption must list table {{BaseId + 20}}');
                if WithCaption."Object Caption" <> 'IT4859 Leak Widget' then
                    Error('LEAK: table {{BaseId + 20}} caption is <%1>, as a previous codeunit modified it', WithCaption."Object Caption");
                // #4877: a Rename must be seen as a write too.
                if not AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 24}}) then
                    Error('LEAK: AllObj lost table {{BaseId + 24}}, which a previous codeunit renamed');
                if AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 27}}) then
                    Error('LEAK: AllObj lists table {{BaseId + 27}}, a previous codeunit''s rename target');
                if not WithCaption.Get(WithCaption."Object Type"::Table, {{BaseId + 24}}) then
                    Error('LEAK: AllObjWithCaption lost table {{BaseId + 24}}, which a previous codeunit renamed');
            end;

            procedure Write()
            var
                AllObj: Record AllObj;
                WithCaption: Record AllObjWithCaption;
            begin
                AllObj.Init();
                AllObj."Object Type" := AllObj."Object Type"::Table;
                AllObj."Object ID" := {{BaseId + 28}};
                AllObj."Object Name" := 'Written';
                if AllObj.Insert() then;
                if AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 20}}) then begin
                    AllObj."Object Name" := 'Modified';
                    if AllObj.Modify() then;
                end;
                WithCaption.Init();
                WithCaption."Object Type" := WithCaption."Object Type"::Table;
                WithCaption."Object ID" := {{BaseId + 28}};
                if WithCaption.Insert() then;
                if WithCaption.Get(WithCaption."Object Type"::Table, {{BaseId + 20}}) then begin
                    WithCaption."Object Caption" := 'Modified';
                    if WithCaption.Modify() then;
                end;
                if AllObj.Get(AllObj."Object Type"::Table, {{BaseId + 24}}) then
                    if AllObj.Rename(AllObj."Object Type"::Table, {{BaseId + 27}}) then;
                if WithCaption.Get(WithCaption."Object Type"::Table, {{BaseId + 24}}) then
                    if WithCaption.Rename(WithCaption."Object Type"::Table, {{BaseId + 27}}) then;
            end;
        }

        {{Tests(BaseId + 22, "A")}}

        {{Tests(BaseId + 23, "B")}}
        """);
    }

    /// <summary>
    /// A store AL wrote to is never carried into the next codeunit (#4859). Without the write
    /// check, the second codeunit reads the first one's Insert and Modify.
    /// </summary>
    [SkippableFact]
    public void AWrittenStore_IsNotCarriedIntoTheNextCodeunit_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-4859-writeleak");
        try
        {
            var app = Path.Combine(root, "app");
            WriteWriteLeakFixture(app);
            var cache = Path.Combine(root, "cache");

            foreach (var pass in new[] { "cold", "warm" })
            {
                var (output, exit) = RunRunner(cache, app);
                Assert.True(exit == 0 && output.Contains("2P/0F/0E"),
                    $"{pass} run: expected both tests to pass (exit {exit}), got:\n{output}");
                // [THEN] The written stores were not reused — neither table reports a reuse.
                Assert.True(ReuseLine.Matches(output).Count == 0 && CaptionReuseLine.Matches(output).Count == 0,
                    $"{pass} run: a written store was reused:\n{output}");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // Every AL write path, one per row: (name, AL statements). Each path owns table
    // MatrixBase + i; the Insert paths insert id MatrixBase + 20 + i, the Rename path renames to
    // MatrixBase + 30 + i. A path whose write the store's write note misses leaks into the next
    // codeunit, and the probe names the path.
    private const int MatrixBase = 62760;
    // Every write tolerates a leaked store (a missing row, a key already taken), so each
    // codeunit writes whatever it found; the Commit after it keeps the write through the test's
    // own failure, so a leak is visible to every codeunit that follows.
    private static readonly (string Name, string Write)[] WritePaths =
    {
        ("RecordInsert", "AllObj.Init(); AllObj.\"Object Type\" := AllObj.\"Object Type\"::Table; AllObj.\"Object ID\" := {INS}; AllObj.\"Object Name\" := 'X'; if AllObj.Insert() then;"),
        ("RecordModify", "if AllObj.Get(AllObj.\"Object Type\"::Table, {OWN}) then begin AllObj.\"Object Name\" := 'X'; AllObj.Modify(); end;"),
        ("RecordDelete", "if AllObj.Get(AllObj.\"Object Type\"::Table, {OWN}) then AllObj.Delete();"),
        ("RecordRename", "if AllObj.Get(AllObj.\"Object Type\"::Table, {OWN}) then if AllObj.Rename(AllObj.\"Object Type\"::Table, {REN}) then;"),
        ("RecordRefInsert", "Ref.Open(Database::AllObj); Ref.Init(); Ref.Field(1).Value := AllObj.\"Object Type\"::Table; Ref.Field(3).Value := {INS}; Ref.Field(4).Value := 'X'; if Ref.Insert() then;"),
        ("RecordRefModify", "if AllObj.Get(AllObj.\"Object Type\"::Table, {OWN}) then begin Ref.GetTable(AllObj); Ref.Field(4).Value := 'X'; Ref.Modify(); end;"),
        ("RecordRefDelete", "if AllObj.Get(AllObj.\"Object Type\"::Table, {OWN}) then begin Ref.GetTable(AllObj); Ref.Delete(); end;"),
        ("ModifyAll", "AllObj.SetRange(\"Object Type\", AllObj.\"Object Type\"::Table); AllObj.SetRange(\"Object ID\", {OWN}); AllObj.ModifyAll(\"Object Name\", 'X');"),
        ("DeleteAll", "AllObj.SetRange(\"Object Type\", AllObj.\"Object Type\"::Table); AllObj.SetRange(\"Object ID\", {OWN}); AllObj.DeleteAll();"),
    };

    private static void WriteWritePathFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "6a4859e0-0000-4c38-9e25-000000004877",
          "name": "IT4859 AllObj Write Paths",
          "publisher": "IssueTest4859",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{MatrixBase}}, "to": {{MatrixBase + 99}} } ],
          "runtime": "14.0"
        }
        """);
        var al = new StringBuilder();
        var checks = new StringBuilder();
        var writes = new StringBuilder();
        for (var i = 0; i < WritePaths.Length; i++)
        {
            var (name, write) = WritePaths[i];
            int own = MatrixBase + i, ins = MatrixBase + 20 + i, ren = MatrixBase + 30 + i;
            al.Append($$"""
            table {{own}} "IT4859 Path {{i}}"
            {
                fields { field(1; "Code"; Code[20]) { } }
                keys { key(PK; "Code") { Clustered = true; } }
            }

            """);
            checks.Append($$"""
                    AllObj.Reset();
                    if not AllObj.Get(AllObj."Object Type"::Table, {{own}}) then
                        Leaks += 'LEAK via {{name}}: table {{own}} is gone. '
                    else
                        if AllObj."Object Name" <> 'IT4859 Path {{i}}' then
                            Leaks += 'LEAK via {{name}}: table {{own}} is renamed in place. ';
                    if AllObj.Get(AllObj."Object Type"::Table, {{ins}}) then
                        Leaks += 'LEAK via {{name}}: table {{ins}} was inserted by a previous codeunit. ';
                    if AllObj.Get(AllObj."Object Type"::Table, {{ren}}) then
                        Leaks += 'LEAK via {{name}}: table {{ren}} is a previous codeunit rename target. ';

            """);
            writes.Append($$"""
                procedure Write{{name}}()
                var
                    AllObj: Record AllObj;
                    Ref: RecordRef;
                begin
                    {{write.Replace("{OWN}", own.ToString()).Replace("{INS}", ins.ToString()).Replace("{REN}", ren.ToString())}}
                end;

            """);
            foreach (var letter in new[] { "A", "B" })
                al.Append($$"""
                codeunit {{MatrixBase + 40 + 2 * i + (letter == "A" ? 0 : 1)}} "IT4859 {{name}} {{letter}}"
                {
                    Subtype = Test;

                    [Test]
                    procedure {{name}}{{letter}}()
                    var
                        Probe: Codeunit "IT4859 Path Probe";
                        Leaks: Text;
                    begin
                        // Read, write and commit, and only then fail: a codeunit that stopped
                        // before its write, or whose failure rolled it back, would hide its path.
                        Leaks := Probe.Leaks();
                        Probe.Write{{name}}();
                        Commit();
                        if Leaks <> '' then
                            Error(Leaks);
                    end;
                }

                """);
        }
        al.Append($$"""
        codeunit {{MatrixBase + 90}} "IT4859 Path Probe"
        {
            // Every leak, not the first: a codeunit reports all paths that leaked into it.
            procedure Leaks() Leaks: Text
            var
                AllObj: Record AllObj;
            begin
        {{checks}}    end;

        {{writes}}}
        """);
        File.WriteAllText(Path.Combine(dir, "Fixture.al"), al.ToString());
    }

    /// <summary>
    /// Every AL write path to AllObj, each written by two codeunits that first check the table is
    /// clean, so whichever of a pair runs first is followed by some codeunit that reads its write.
    /// A path the write note misses leaks, and the probe names it (#4859, #4877).
    /// </summary>
    [SkippableFact]
    public void EveryWritePath_KeepsItsStoreOutOfTheNextCodeunit_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-4859-writepaths");
        try
        {
            var app = Path.Combine(root, "app");
            WriteWritePathFixture(app);
            var cache = Path.Combine(root, "cache");
            var tests = 2 * WritePaths.Length;

            foreach (var pass in new[] { "cold", "warm" })
            {
                var (output, exit) = RunRunner(cache, app);
                Assert.True(exit == 0 && output.Contains($"{tests}P/0F/0E"),
                    $"{pass} run: expected all {tests} write-path tests to pass (exit {exit}), got:\n{output}");
                Assert.True(ReuseLine.Matches(output).Count == 0,
                    $"{pass} run: a written AllObj store was reused:\n{output}");
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
