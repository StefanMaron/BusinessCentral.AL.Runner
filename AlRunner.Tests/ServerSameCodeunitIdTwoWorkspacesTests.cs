// ServerSameCodeunitIdTwoWorkspacesTests — issue #4137.
//
// #4100 fixed RunnerPageInstance.FindPageType: a --server process that had already served
// another workspace declaring the same page id built the OTHER workspace's Page{id}. The fix
// tries BcRuntime.CurrentBundleAssemblies() — the loading bundle's own modules — before the
// AppDomain scan.
//
// #4137 is the same shape at the finders that fix did not touch, because no test reached them.
// This file reaches the CODEUNIT one: CodeunitPatches' type lookup tries _currentTestAssembly
// and then the whole AppDomain, so an object living in a DEPENDENCY module (not in
// CurrentTestAssembly) falls through to a scan where a foreign workspace's same-id type can
// answer first. The foreign type is not a stale generation, so IsStaleBundleAssembly does not
// skip it.
//
// Why a codeunit rather than the page: a wrong page answers with a control-binding refusal,
// which is loud. A wrong CODEUNIT runs someone else's business logic and returns a value — the
// silent wrong result #4137 warns about. The assertion is on the VALUE, so the test fails with
// "expected dep-A, actual x-A" rather than with an exception.
//
// Same structure as ServerSamePageIdTwoWorkspacesTests (#4100): four requests per session so
// both workspaces load and are then served again, and each session runs twice against ONE cache
// root (cold, then warm) because the second reaches request 1 through an AL-output cache HIT
// (.claude/rules/local-test-scope.md). Dedicated server per session, never SharedCliServer.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerSameCodeunitIdTwoWorkspacesTests
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
          "publisher": "Repro4137",
          "version": "1.0.0.0",
          "dependencies": [ {{dependencies}} ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{from}}, "to": {{to}} } ],
          "runtime": "14.0"
        }
        """;

    // Workspace X: one app declaring codeunit 64190 with its own answer, plus its own test.
    private static void WriteWorkspaceX(string app)
    {
        Write(Path.Combine(app, "app.json"), Manifest("e4137413-0413-4413-8413-041304137011", "R4137 X", 64190, 64209));
        Write(Path.Combine(app, "X.al"), """
        codeunit 64190 "R4137 Worker"
        {
            procedure Answer(): Text[30]
            begin
                exit('x-A');
            end;
        }

        codeunit 64191 "R4137 X Tests"
        {
            Subtype = Test;

            [Test]
            procedure XWorkerAnswers()
            var
                W: Codeunit "R4137 Worker";
            begin
                if W.Answer() <> 'x-A' then
                    Error('x worker: expected x-A, actual %1', W.Answer());
            end;
        }
        """);
    }

    // Workspace B: codeunit 64190 lives in a sibling source DEPENDENCY and the tests app is the
    // module that executes, so the type is NOT in CurrentTestAssembly — which is exactly the
    // case the AppDomain fallback mishandles.
    private static void WriteWorkspaceB(string root)
    {
        const string depId = "e4137413-0413-4413-8413-041304137021";
        Write(Path.Combine(root, "dep", "app.json"), Manifest(depId, "R4137 Dep", 64190, 64194));
        Write(Path.Combine(root, "dep", "Dep.al"), """
        codeunit 64190 "R4137 Dep Worker"
        {
            procedure Answer(): Text[30]
            begin
                exit('dep-A');
            end;
        }
        """);
        Write(Path.Combine(root, "tests", "app.json"), Manifest("e4137413-0413-4413-8413-041304137022", "R4137 Dep Tests", 64195, 64199,
            $$"""{ "id": "{{depId}}", "name": "R4137 Dep", "publisher": "Repro4137", "version": "1.0.0.0" }"""));
        Write(Path.Combine(root, "tests", "T.al"), """
        codeunit 64195 "R4137 Dep Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepWorkerAnswers()
            var
                W: Codeunit "R4137 Dep Worker";
            begin
                // A wrong answer here is X's codeunit running for B's request: silent, and only
                // visible because this asserts the VALUE rather than that the call succeeded.
                if W.Answer() <> 'dep-A' then
                    Error('dep worker: expected dep-A, actual %1', W.Answer());
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
        await AssertPassesAsync(server, second, $"{label} request 2 (same codeunit id, other workspace)");
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
    public async Task XThenB_BothCallTheirOwnCodeunit_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, b) = Fixture("al-runner-server-same-codeunit-id-4137-xb");
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
    public async Task BThenX_BothCallTheirOwnCodeunit_ColdThenWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (root, x, b) = Fixture("al-runner-server-same-codeunit-id-4137-bx");
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
