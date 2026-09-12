// ServerCrossBundleReuseRegistryReplayTests — issue #3250.
//
// A --server session that loads app X from directory A in request 1 and the same app X from
// directory B in request 2 takes RunBundleForServer's cross-bundle reuse (#1683/#1892) in
// request 2: B is served A's already-loaded module. ResetForNewBundleReload cleared the
// emit-derived registries at the top of request 2, and the reuse path skipped the only block
// that replays them, so the module ran with no enum metadata and no query symbols.
//
// Dedicated server per session, never SharedCliServer: one AppId at two SourcePaths poisons
// the shared AppId cache (ServerAppVersionBumpTests says the same).
//
// Each session runs twice against ONE cache root, because the two runs reach request 2
// through different request-1 branches: cold is an emit MISS, warm is an AL-output cache HIT
// (.claude/rules/local-test-scope.md). The --no-cache case has no sidecar on disk at all.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerCrossBundleReuseRegistryReplayTests
{
    private const string AppId = "e3250325-0325-4325-8325-032503250325";
    private const string ReuseLine = "reusing that module instead of recompiling";

    private static void WriteBundle(string root)
    {
        Directory.CreateDirectory(root);
        // No Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md). Both
        // directories get the byte-identical manifest: same id, name, publisher and version,
        // which is what makes request 2 a reuse rather than a #1850 collision.
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{AppId}}",
          "name": "Reuse Replay 3250",
          "publisher": "Repro3250",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62400, "to": 62409 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Objects.al"), """
        enum 62400 "R3250 Color"
        {
            Extensible = false;
            value(0; Red) { Caption = 'Crimson Red'; }
            value(1; Blue) { Caption = 'Deep Blue'; }
        }

        table 62401 "R3250 Row"
        {
            fields
            {
                field(1; "No."; Integer) { }
                field(2; Amount; Integer) { }
            }
            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }

        query 62402 "R3250 Rows"
        {
            elements
            {
                dataitem(Row; "R3250 Row")
                {
                    column(No; "No.") { }
                    column(Amount; Amount) { }
                }
            }
        }
        """);
        File.WriteAllText(Path.Combine(root, "Tests.Codeunit.al"), """
        codeunit 62403 "R3250 Replay Tests"
        {
            Subtype = Test;

            [Test]
            procedure EnumCaptionComesFromTheRegistry()
            var
                Color: Enum "R3250 Color";
            begin
                Color := Color::Blue;
                if Format(Color) <> 'Deep Blue' then
                    Error('Format(enum) answered <%1>, expected <Deep Blue>', Format(Color));
            end;

            [Test]
            procedure QueryReadsItsOwnColumns()
            var
                Row: Record "R3250 Row";
                Rows: Query "R3250 Rows";
            begin
                Row.Init();
                Row."No." := 1;
                Row.Amount := 7;
                Row.Insert();
                Rows.Open();
                if not Rows.Read() then
                    Error('query returned no row');
                if Rows.Amount <> 7 then
                    Error('query column Amount answered <%1>, expected <7>', Rows.Amount);
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

    private static async Task<(List<string> Lines, string StdErr)> RunSessionAsync(string dirA, string dirB, IEnumerable<string> args, string label)
    {
        await using var server = await CliServer.StartAsync(args);

        var lines1 = await server.SendRequestStreamingAsync(Req(dirA), TimeSpan.FromSeconds(240));
        var joined1 = string.Join(" | ", lines1);
        var (_, d1) = ProtocolV2Streaming.Split(lines1);
        // Request 1 is the control: the same AL, the same assertions, green. A red here is a
        // general failure of the fixture, not this issue.
        Assert.True(d1.GetProperty("passed").GetInt32() == 2 && d1.GetProperty("failed").GetInt32() == 0,
            $"[{label}] request 1 (directory A) must pass both tests: {joined1}");

        var mark = server.StdErrMark;
        var lines2 = await server.SendRequestStreamingAsync(Req(dirB), TimeSpan.FromSeconds(240));
        // Without the reuse line the request did not exercise #3250 at all — a green would be
        // a plain compile, and a red could be a collision or a cache defect. The diagnostic is
        // on stderr, not the protocol stream; StdErrSinceAsync times out when it never arrives.
        var stderr2 = await server.StdErrSinceAsync(mark, ReuseLine);
        return (lines2, stderr2);
    }

    private static void AssertRequest2((List<string> Lines, string StdErr) run, string label)
    {
        var (lines2, stderr2) = run;
        var joined2 = string.Join(" | ", lines2);
        Assert.Contains(ReuseLine, stderr2, StringComparison.Ordinal);
        var (_, d2) = ProtocolV2Streaming.Split(lines2);
        Assert.True(d2.GetProperty("passed").GetInt32() == 2 && d2.GetProperty("failed").GetInt32() == 0,
            $"[{label}] request 2 (directory B, reused module) must pass both tests: {joined2}");
    }

    [SkippableFact]
    public async Task ReusedModule_ReplaysEnumAndQueryRegistries_ColdThenWarmCache()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-reuse-replay-3250");
        var dirA = Path.Combine(root, "checkout-a");
        var dirB = Path.Combine(root, "checkout-b");
        var cache = Path.Combine(root, "cache");
        WriteBundle(dirA);
        WriteBundle(dirB);
        try
        {
            AssertRequest2(await RunSessionAsync(dirA, dirB, new[] { "--cache", cache }, "cold"), "cold");
            // The warm session is only a different branch if the cold one left an entry to HIT.
            Assert.NotEmpty(Directory.EnumerateFiles(cache, "*.enum-registry.json", SearchOption.AllDirectories));
            AssertRequest2(await RunSessionAsync(dirA, dirB, new[] { "--cache", cache }, "warm"), "warm");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task ReusedModule_ReplaysEnumAndQueryRegistries_NoCache()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-reuse-replay-3250-nocache");
        var dirA = Path.Combine(root, "checkout-a");
        var dirB = Path.Combine(root, "checkout-b");
        WriteBundle(dirA);
        WriteBundle(dirB);
        try
        {
            AssertRequest2(await RunSessionAsync(dirA, dirB, new[] { "--no-cache" }, "no-cache"), "no-cache");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
