using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionTests
{
    private static string MakeBundle(string helperABody)
    {
        var dir = TestScratch.Dir("al-runner-server-affected");
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

    // #2539: procedure granularity. Two tests cover the SAME object but call DIFFERENT
    // procedures of it — before #2539, affectedOnly's coverage keys are whole-object, so
    // editing EITHER procedure reran BOTH tests (AffectedOnly_ChangedObjectRunsOnlyIntersectingTests
    // above already proves object-level narrowing works across DIFFERENT objects; this proves
    // narrowing works WITHIN one object, across its procedures).
    private static string MakeMultiProcBundle()
    {
        var dir = TestScratch.Dir("al-runner-server-affected-scope");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c357c8d5-5f6a-4f52-9e06-6f42ca7e9999",
          "name": "Server Affected Scope Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60260, "to": 60269 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Multi.Codeunit.al"), """
        codeunit 60260 "Affected Multi Proc SX"
        {
            procedure ValueA(): Integer
            begin
                exit(1);
            end;

            procedure ValueB(): Integer
            begin
                exit(2);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), """
        codeunit 60261 "Affected Multi Proc Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure OnlyA()
            var
                H: Codeunit "Affected Multi Proc SX";
            begin
                if H.ValueA() <> 1 then
                    Error('OnlyA failed');
            end;

            [Test]
            procedure OnlyB()
            var
                H: Codeunit "Affected Multi Proc SX";
            begin
                if H.ValueB() <> 2 then
                    Error('OnlyB failed');
            end;
        }
        """);
        return dir;
    }

    [SkippableFact]
    public async Task AffectedOnly_ProcedureGranularity_EditingOneProcedureRunsOnlyItsTest()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeMultiProcBundle();
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));

        // Edit ONLY ValueA's begin..end body — a genuine, executable statement change, still
        // returning 1 so OnlyA keeps passing. Deliberately does NOT touch ValueA's own local
        // `var` section: a local declaration sits outside the procedure's BodyRange exactly
        // like an object-level one, so it widens too (a MORE conservative posture than the
        // issue's rule 2 strictly requires, but a safe one) — this test isolates the
        // executable-statement case specifically.
        File.WriteAllText(Path.Combine(bundle, "Multi.Codeunit.al"), """
        codeunit 60260 "Affected Multi Proc SX"
        {
            procedure ValueA(): Integer
            begin
                if 1 = 1 then;
                exit(1);
            end;

            procedure ValueB(): Integer
            begin
                exit(2);
            end;
        }
        """);

        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        var (events, summary) = ProtocolV2Streaming.Split(lines);

        Assert.True(summary.TryGetProperty("selection", out var selection), string.Join(" | ", lines));
        Assert.False(selection.GetProperty("forcedFull").GetBoolean());
        // [THEN] only the test that calls ValueA reruns, even though both tests cover the
        // SAME object — object-level attribution alone (pre-#2539) cannot tell ValueA from
        // ValueB and reruns both.
        Assert.Equal(1, selection.GetProperty("ran").GetInt32());
        Assert.Equal(1, selection.GetProperty("skipped").GetInt32());
        Assert.Single(events);
        Assert.EndsWith(".OnlyA", events[0].GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Equal("pass", events[0].GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task AffectedOnly_ProcedureGranularity_NonStatementEditWidensToWholeObject()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeMultiProcBundle();
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));

        // A NON-STATEMENT edit: an object-level variable declaration, which sits OUTSIDE
        // every procedure's begin..end and carries no StmtHit. This MUST widen to the whole
        // object — the safety-net rule #2539's issue calls out explicitly — so BOTH tests
        // rerun, not just neither or an arbitrary one.
        File.WriteAllText(Path.Combine(bundle, "Multi.Codeunit.al"), """
        codeunit 60260 "Affected Multi Proc SX"
        {
            var
                Dummy: Integer;

            procedure ValueA(): Integer
            begin
                exit(1);
            end;

            procedure ValueB(): Integer
            begin
                exit(2);
            end;
        }
        """);

        var lines = await server.SendRequestStreamingAsync(RunTestsRequest(bundle, affectedOnly: true));
        var (events, summary) = ProtocolV2Streaming.Split(lines);

        Assert.True(summary.TryGetProperty("selection", out var selection), string.Join(" | ", lines));
        Assert.False(selection.GetProperty("forcedFull").GetBoolean());
        Assert.Equal(2, selection.GetProperty("ran").GetInt32());
        Assert.Equal(0, selection.GetProperty("skipped").GetInt32());
        Assert.Equal(2, events.Count);
    }
}
