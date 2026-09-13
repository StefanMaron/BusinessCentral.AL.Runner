// ServerSamePageIdTwoWorkspacesTests — issue #4100.
//
// One --server process serves two unrelated workspaces that both declare page 64090, each over
// its own table and with its own page-variable control; in workspace B the page is in a sibling
// source dependency (the issue's reproducer). Whichever workspace's TestPage opened
// second got no AL page object, and its page-variable control refused with
// testpage-control-binding. Each order is its own session, and each session runs against ONE
// cache root twice (cold, then warm), because the second session reaches request 1 through an
// AL-output cache HIT (.claude/rules/local-test-scope.md).
//
// Dedicated server per session, never SharedCliServer.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerSamePageIdTwoWorkspacesTests
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
          "publisher": "Repro4100",
          "version": "1.0.0.0",
          "dependencies": [ {{dependencies}} ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{from}}, "to": {{to}} } ],
          "runtime": "14.0"
        }
        """;

    // Workspace X: one app declaring page 64090 over its own table, with its own tests.
    private static void WriteWorkspaceX(string app)
    {
        Write(Path.Combine(app, "app.json"), Manifest("e4100410-0410-4410-8410-041004100011", "R4100 X", 64090, 64109));
        Write(Path.Combine(app, "X.al"), """
        table 64101 "R4100 X Rec"
        {
            fields { field(1; "Code"; Code[10]) { } }
            keys { key(PK; "Code") { } }
        }

        page 64090 "R4100 X Card"
        {
            PageType = Card;
            SourceTable = "R4100 X Rec";
            layout { area(Content) { field(XVarCtl; XText) { ApplicationArea = All; } } }
            var
                XText: Text[30];
            trigger OnOpenPage()
            begin
                XText := 'x-A';
            end;
        }

        codeunit 64102 "R4100 X Tests"
        {
            Subtype = Test;

            [Test]
            procedure XPageControl()
            var
                TP: TestPage "R4100 X Card";
            begin
                TP.OpenView();
                if TP.XVarCtl.Value <> 'x-A' then
                    Error('x page control: expected x-A, actual %1', TP.XVarCtl.Value);
                TP.Close();
            end;
        }
        """);
    }

    // Workspace B: page 64090 lives in a sibling source dependency and the tests app is the
    // module that executes, so the page type is NOT in CurrentTestAssembly. Its later requests
    // reuse both modules (DependencyLoader's identity-match branch) rather than reloading them.
    private static void WriteWorkspaceB(string root)
    {
        const string depId = "e4100410-0410-4410-8410-041004100021";
        Write(Path.Combine(root, "dep", "app.json"), Manifest(depId, "R4100 Dep", 64090, 64094));
        Write(Path.Combine(root, "dep", "Dep.al"), """
        table 64090 "R4100 Dep Rec"
        {
            fields { field(1; "Code"; Code[10]) { } }
            keys { key(PK; "Code") { } }
        }

        page 64090 "R4100 Dep Card"
        {
            PageType = Card;
            SourceTable = "R4100 Dep Rec";
            layout { area(Content) { field(DepVarCtl; DepText) { ApplicationArea = All; } } }
            var
                DepText: Text[30];
            trigger OnOpenPage()
            begin
                DepText := 'dep-A';
            end;
        }
        """);
        Write(Path.Combine(root, "tests", "app.json"), Manifest("e4100410-0410-4410-8410-041004100022", "R4100 Dep Tests", 64095, 64099,
            $$"""{ "id": "{{depId}}", "name": "R4100 Dep", "publisher": "Repro4100", "version": "1.0.0.0" }"""));
        Write(Path.Combine(root, "tests", "T.al"), """
        codeunit 64095 "R4100 Dep Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepPageControl()
            var
                TP: TestPage "R4100 Dep Card";
            begin
                TP.OpenView();
                if TP.DepVarCtl.Value <> 'dep-A' then
                    Error('dep page control: expected dep-A, actual %1', TP.DepVarCtl.Value);
                TP.Close();
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
            $"[{label}] must pass its one TestPage test: {string.Join(" | ", lines)}");
    }

    // Four requests: each workspace loads once, then each is served again. The second pair is
    // the one that failed after a partial fix (a reused dependency module was not preferred).
    private static async Task RunSessionAsync(string first, string second, string cache, string label)
    {
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        await AssertPassesAsync(server, first, $"{label} request 1");
        await AssertPassesAsync(server, second, $"{label} request 2 (same page id, other workspace)");
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
    public async Task XThenB_BothBuildTheirOwnPage_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, b) = Fixture("al-runner-server-same-page-id-4100-xb");
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

    [SkippableFact]
    public async Task BThenX_BothBuildTheirOwnPage_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, b) = Fixture("al-runner-server-same-page-id-4100-bx");
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
