using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionTests
{
    private static string MakeBundle(string helperABody)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-affected", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c357c8d5-5f6a-4f52-9e06-6f42ca7e1b92",
          "name": "Server Affected Selection Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60200, "to": 60249 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "HelperA.Codeunit.al"), helperABody);
        File.WriteAllText(Path.Combine(dir, "HelperB.Codeunit.al"), """
        codeunit 60202 "Affected Helper B SX"
        {
            procedure ValueB(): Integer
            begin
                exit(2);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), """
        codeunit 60210 "Affected Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure OnlyA()
            var
                H: Codeunit "Affected Helper A SX";
            begin
                if H.ValueA() <> 1 then
                    Error('OnlyA failed');
            end;

            [Test]
            procedure OnlyB()
            var
                H: Codeunit "Affected Helper B SX";
            begin
                if H.ValueB() <> 2 then
                    Error('OnlyB failed');
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsRequest(string bundle, bool affectedOnly)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
            affectedOnly,
            perTestCoverage = true,
        });

    [SkippableFact]
    public async Task AffectedOnly_ChangedObjectRunsOnlyIntersectingTests()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeBundle("""
        codeunit 60201 "Affected Helper A SX"
        {
            procedure ValueA(): Integer
            begin
                exit(1);
            end;
        }
        """);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));

        File.WriteAllText(Path.Combine(bundle, "HelperA.Codeunit.al"), """
        codeunit 60201 "Affected Helper A SX"
        {
            procedure ValueA(): Integer
            var
                X: Integer;
            begin
                X := 1;
                exit(X);
            end;
        }
        """);

        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        var (events, summary) = ProtocolV2Streaming.Split(lines);

        Assert.Single(events);
        Assert.EndsWith(".OnlyA", events[0].GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.True(summary.TryGetProperty("selection", out var selection), string.Join(" | ", lines));
        Assert.Equal("affected", selection.GetProperty("mode").GetString());
        Assert.Equal(1, selection.GetProperty("ran").GetInt32());
        Assert.Equal(1, selection.GetProperty("skipped").GetInt32());
        Assert.False(selection.GetProperty("forcedFull").GetBoolean());
        Assert.Contains(selection.GetProperty("changedObjects").EnumerateArray().Select(x => x.GetString()),
            x => x == "Codeunit 60201 Affected Helper A SX");
    }

    [SkippableFact]
    public async Task AffectedOnly_AppJsonChangeForcesFullWithReason()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeBundle("""
        codeunit 60201 "Affected Helper A SX"
        {
            procedure ValueA(): Integer
            begin
                exit(1);
            end;
        }
        """);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        var appJsonPath = Path.Combine(bundle, "app.json");
        File.AppendAllText(appJsonPath, "\n ");

        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        Assert.Equal(2, events.Count);
        Assert.True(summary.TryGetProperty("selection", out var selection), string.Join(" | ", lines));
        Assert.True(selection.GetProperty("forcedFull").GetBoolean());
        Assert.Contains("app.json", selection.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AffectedOnly_WithStartupTestFilter_RanPlusSkippedEqualsFilteredDiscovery()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeBundle("""
        codeunit 60201 "Affected Helper A SX"
        {
            procedure ValueA(): Integer
            begin
                exit(1);
            end;
        }
        """);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache", "--test", "OnlyA" });

        await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        File.WriteAllText(Path.Combine(bundle, "HelperA.Codeunit.al"), """
        codeunit 60201 "Affected Helper A SX"
        {
            procedure ValueA(): Integer
            var
                X: Integer;
            begin
                X := 1;
                exit(X);
            end;
        }
        """);

        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        Assert.Single(events);
        Assert.EndsWith(".OnlyA", events[0].GetProperty("name").GetString(), StringComparison.Ordinal);

        Assert.True(summary.TryGetProperty("selection", out var selection), string.Join(" | ", lines));
        Assert.False(selection.GetProperty("forcedFull").GetBoolean());
        var ran = selection.GetProperty("ran").GetInt32();
        var skipped = selection.GetProperty("skipped").GetInt32();
        Assert.Equal(1, ran + skipped);
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
    }
}
