// ServerSamePageIdTwoWorkspacesTests — issue #4100.
//
// One --server process serves two unrelated workspaces that both declare page 64090, each over
// its own table and with its own page-variable control. Whichever workspace's TestPage opened
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
    private static void WriteWorkspace(string root, string appId, string tag, int tableId, int testCuId)
    {
        Directory.CreateDirectory(root);
        // No Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Same Page Id {{tag}}",
          "publisher": "Repro4100",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64090, "to": 64109 } ],
          "runtime": "14.0"
        }
        """);
        // Different table, different control name, different page-variable value per workspace:
        // a page object built from the OTHER workspace's type cannot answer this control.
        File.WriteAllText(Path.Combine(root, "Objects.al"), $$"""
        table {{tableId}} "R4100 {{tag}} Rec"
        {
            fields { field(1; "Code"; Code[10]) { } }
            keys { key(PK; "Code") { } }
        }

        page 64090 "R4100 {{tag}} Card"
        {
            PageType = Card;
            SourceTable = "R4100 {{tag}} Rec";
            layout { area(Content) { field({{tag}}VarCtl; {{tag}}Text) { ApplicationArea = All; } } }
            var
                {{tag}}Text: Text[30];
            trigger OnOpenPage()
            begin
                {{tag}}Text := '{{tag}}-value';
            end;
        }

        codeunit {{testCuId}} "R4100 {{tag}} Tests"
        {
            Subtype = Test;

            [Test]
            procedure PageVariableControl()
            var
                TP: TestPage "R4100 {{tag}} Card";
            begin
                TP.OpenView();
                if TP.{{tag}}VarCtl.Value <> '{{tag}}-value' then
                    Error('{{tag}} page control: expected {{tag}}-value, actual %1', TP.{{tag}}VarCtl.Value);
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

    private static async Task RunSessionAsync(string first, string second, string cache, string label)
    {
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        await AssertPassesAsync(server, first, $"{label} request 1");
        await AssertPassesAsync(server, second, $"{label} request 2 (same page id, other workspace)");
    }

    private static (string Root, string X, string Y) Fixture(string name)
    {
        var root = TestScratch.Dir(name);
        var x = Path.Combine(root, "xws");
        var y = Path.Combine(root, "yws");
        WriteWorkspace(x, "e4100410-0410-4410-8410-041004100001", "X", 64101, 64102);
        WriteWorkspace(y, "e4100410-0410-4410-8410-041004100002", "Y", 64103, 64104);
        return (root, x, y);
    }

    [SkippableFact]
    public async Task XThenY_BothBuildTheirOwnPage_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, y) = Fixture("al-runner-server-same-page-id-4100-xy");
        var cache = Path.Combine(root, "cache");
        try
        {
            await RunSessionAsync(x, y, cache, "X->Y cold");
            await RunSessionAsync(x, y, cache, "X->Y warm");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task YThenX_BothBuildTheirOwnPage_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, y) = Fixture("al-runner-server-same-page-id-4100-yx");
        var cache = Path.Combine(root, "cache");
        try
        {
            await RunSessionAsync(y, x, cache, "Y->X cold");
            await RunSessionAsync(y, x, cache, "Y->X warm");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
