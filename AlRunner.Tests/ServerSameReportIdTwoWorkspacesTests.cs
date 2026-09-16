// ServerSameReportIdTwoWorkspacesTests — issue #4137.
//
// The REPORT arm of #4137, and the reason it is worth writing rather than shape-matching the
// tier onto FindReportType the way FindQueryType's was: BcRuntime.FindReportType is REACHED by
// the stock suite, 9 times in one `tests/runner-extras` run, and already resolves reports that
// live in DEPENDENCY modules (`RSM Precompiled StubMeta Dep`, `RMI Precompiled MaxIteration
// Dep`, `V2_rpd-hermetic-dep`). That is the exact shape the issue names as vulnerable, so the
// finder can be driven from a fixture instead of trusted to behave like its sibling.
//
// Structure copied from ServerSameCodeunitIdTwoWorkspacesTests (#4259), which copied
// ServerSamePageIdTwoWorkspacesTests (#4100): two workspaces declaring ONE report id, four
// requests per session so both load and are then served again, and each session run twice
// against ONE cache root (cold, then warm) because the second reaches request 1 through an
// AL-output cache HIT (.claude/rules/local-test-scope.md).
//
// Why the assertion is on a WRITTEN VALUE. A report that renders is out of scope here, so the
// report is ProcessingOnly and writes a marker row. A wrong report therefore leaves a different
// marker rather than throwing — the silent wrong result #4137 warns about — and the test fails
// with "expected dep-R, actual x-R" instead of with an exception.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerSameReportIdTwoWorkspacesTests
{
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // No Base Application floor in any manifest (.claude/rules/no-base-app-in-csharp-tests.md).
    private static string Manifest(string id, string name, int from, int to, string dependencies = "") => $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "Repro4137R",
          "version": "1.0.0.0",
          "dependencies": [ {{dependencies}} ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{from}}, "to": {{to}} } ],
          "runtime": "14.0"
        }
        """;

    // Workspace X: one app declaring report 64230 with its own marker, plus its own test.
    private static void WriteWorkspaceX(string app)
    {
        Write(Path.Combine(app, "app.json"), Manifest("e4137723-0723-4723-8723-072304137011", "R4137R X", 64230, 64249));
        Write(Path.Combine(app, "X.al"), """
        table 64231 "R4137R X Mark"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; Value; Text[30]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        report 64230 "R4137R X Report"
        {
            ProcessingOnly = true;
            UseRequestPage = false;

            trigger OnPostReport()
            var
                M: Record "R4137R X Mark";
            begin
                M.Init();
                M.Code := 'M';
                M.Value := 'x-R';
                M.Insert();
            end;
        }

        codeunit 64232 "R4137R X Tests"
        {
            Subtype = Test;

            [Test]
            procedure XReportWritesItsOwnMark()
            var
                M: Record "R4137R X Mark";
                R: Report "R4137R X Report";
            begin
                R.Run();
                if not M.Get('M') then
                    Error('x report: no mark row — the report did not run');
                if M.Value <> 'x-R' then
                    Error('x report: expected x-R, actual %1', M.Value);
            end;
        }
        """);
    }

    // Workspace B: report 64230 lives in a sibling source DEPENDENCY and the tests app is the
    // module that executes, so the type is NOT in CurrentTestAssembly — exactly the case the
    // AppDomain fallback mishandles, since a foreign workspace's same-id report is not a stale
    // generation and IsStaleBundleAssembly therefore does not skip it.
    private static void WriteWorkspaceB(string root)
    {
        const string depId = "e4137723-0723-4723-8723-072304137021";
        Write(Path.Combine(root, "dep", "app.json"), Manifest(depId, "R4137R Dep", 64230, 64234));
        Write(Path.Combine(root, "dep", "Dep.al"), """
        table 64231 "R4137R Dep Mark"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; Value; Text[30]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        report 64230 "R4137R Dep Report"
        {
            ProcessingOnly = true;
            UseRequestPage = false;

            trigger OnPostReport()
            var
                M: Record "R4137R Dep Mark";
            begin
                M.Init();
                M.Code := 'M';
                M.Value := 'dep-R';
                M.Insert();
            end;
        }
        """);
        Write(Path.Combine(root, "tests", "app.json"), Manifest("e4137723-0723-4723-8723-072304137022", "R4137R Dep Tests", 64235, 64239,
            $$"""{ "id": "{{depId}}", "name": "R4137R Dep", "publisher": "Repro4137R", "version": "1.0.0.0" }"""));
        Write(Path.Combine(root, "tests", "T.al"), """
        codeunit 64235 "R4137R Dep Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepReportWritesItsOwnMark()
            var
                M: Record "R4137R Dep Mark";
                R: Report "R4137R Dep Report";
            begin
                // A wrong answer here is X's report running for B's request: silent, and only
                // visible because this asserts the VALUE rather than that Run() returned.
                R.Run();
                if not M.Get('M') then
                    Error('dep report: no mark row — X''s report ran instead, writing to its own table');
                if M.Value <> 'dep-R' then
                    Error('dep report: expected dep-R, actual %1', M.Value);
            end;
        }
        """);
    }

    private static string Req(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
    });

    private static async Task AssertPassesAsync(CliServer server, string bundle, string label)
    {
        var lines = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(240));
        var (_, d) = ProtocolV2Streaming.Split(lines);
        Assert.True(d.GetProperty("passed").GetInt32() == 1 && d.GetProperty("failed").GetInt32() == 0,
            $"[{label}] must pass its one test: {string.Join(" | ", lines)}");
    }

    private static async Task RunSessionAsync(string first, string second, string cache, string label)
    {
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        await AssertPassesAsync(server, first, $"{label} request 1");
        await AssertPassesAsync(server, second, $"{label} request 2 (same report id, other workspace)");
        await AssertPassesAsync(server, first, $"{label} request 3");
        await AssertPassesAsync(server, second, $"{label} request 4");
    }

    private static (string Root, string X, string B) Fixture(string name)
    {
        var root = TestScratch.Dir(name);
        var x = Path.Combine(root, "xws", "x");
        WriteWorkspaceX(x);
        WriteWorkspaceB(Path.Combine(root, "ws"));
        return (root, x, Path.Combine(root, "ws", "tests"));
    }

    [SkippableFact]
    public async Task XThenB_BothRunTheirOwnReport_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, b) = Fixture("al-runner-server-same-report-id-4137-xb");
        var cache = Path.Combine(root, "cache");
        try
        {
            await RunSessionAsync(x, b, cache, "X->B cold");
            await RunSessionAsync(x, b, cache, "X->B warm");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The ordering CONTROL, not a second arm of evidence — the same role, and the same caveat,
    /// as BThenX in ServerSameCodeunitIdTwoWorkspacesTests. It is expected to pass with and
    /// without the fix, so it proves nothing on its own; what it guards is that the fix does not
    /// break the ordering that already worked. Any claim of "both orderings pass" as evidence
    /// for the fix counts this arm as a passenger.
    /// </summary>
    [SkippableFact]
    public async Task BThenX_BothRunTheirOwnReport_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, b) = Fixture("al-runner-server-same-report-id-4137-bx");
        var cache = Path.Combine(root, "cache");
        try
        {
            await RunSessionAsync(b, x, cache, "B->X cold");
            await RunSessionAsync(b, x, cache, "B->X warm");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
