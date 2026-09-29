// ServerAffectedSelectionIncludeFailingTests — #4978: under affectedOnly, a test that failed in the
// recording run is selected by the coverage it recorded up to the failure, unless the request sets
// includeFailing:true. Mechanism: docs/server-mode.md#affectedonly-and-previously-failing-tests.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionIncludeFailingTests
{
    private const string HelperA = """
        codeunit 60451 "IncFail Helper A SX"
        {
            procedure ValueA(): Integer
            var
                X: Integer;
            begin
                X := {{EDIT}};
                exit(1);
            end;
        }
        """;

    private const string HelperB = """
        codeunit 60452 "IncFail Helper B SX"
        {
            procedure ValueB(): Integer
            var
                Y: Integer;
            begin
                Y := {{EDIT}};
                exit(2);
            end;
        }
        """;

    // FailsAfterA runs helper A, then fails by design: its recorded coverage is the test's own
    // statements plus helper A, and helper B is nowhere in it.
    private const string Tests = """
        codeunit 60460 "IncFail Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure FailsAfterA()
            var
                H: Codeunit "IncFail Helper A SX";
            begin
                if H.ValueA() = 1 then
                    Error('FailsAfterA fails by design');
            end;

            [Test]
            procedure PassesB()
            var
                H: Codeunit "IncFail Helper B SX";
            begin
                if H.ValueB() <> 2 then
                    Error('PassesB failed');
            end;
        }
        """;

    // Runs helper A, then outlives the per-test timeout the server is started with. A watchdog
    // timeout abandons the rest of the run, so the passing test is declared first.
    private const string SlowTests = """
        codeunit 60460 "IncFail Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure PassesB()
            var
                H: Codeunit "IncFail Helper B SX";
            begin
                if H.ValueB() <> 2 then
                    Error('PassesB failed');
            end;

            [Test]
            procedure TimesOutAfterA()
            var
                H: Codeunit "IncFail Helper A SX";
            begin
                if H.ValueA() = 1 then
                    Sleep(4000);
            end;
        }
        """;

    private static string Bundle(string testsSource)
    {
        var dir = TestScratch.Dir("al-runner-server-affected-incfail");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "2853a187-f1c7-4c3b-9b56-c2f4bb2d1d8d",
          "name": "Server Affected IncludeFailing Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60450, "to": 60469 } ],
          "runtime": "14.0"
        }
        """);
        EditHelper(dir, "A", 0);
        EditHelper(dir, "B", 0);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), testsSource);
        return dir;
    }

    // Each edit changes an executable statement and keeps the helper's return value, so the
    // selection sees a changed procedure and no test changes outcome because of it.
    private static void EditHelper(string bundle, string which, int edit)
        => File.WriteAllText(Path.Combine(bundle, $"Helper{which}.Codeunit.al"),
            (which == "A" ? HelperA : HelperB).Replace("{{EDIT}}", edit.ToString(), StringComparison.Ordinal));

    private static string Request(string bundle, bool? includeFailing)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
            ["includeFailing"] = includeFailing,
        }.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status, int Skipped,
        int? SkippedFailing, bool ForcedFull, string Raw);

    private static async Task<Observed> Send(CliServer server, string bundle, bool? includeFailing)
    {
        var lines = await server.SendRequestStreamingAsync(Request(bundle, includeFailing));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines);
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        int? skippedFailing = selection.TryGetProperty("skippedFailing", out var sf) ? sf.GetInt32() : null;
        var status = events.ToDictionary(
            e => e.GetProperty("name").GetString()!, e => e.GetProperty("status").GetString()!, StringComparer.Ordinal);
        return new Observed(status.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), status,
            selection.GetProperty("skipped").GetInt32(), skippedFailing,
            selection.GetProperty("forcedFull").GetBoolean(), raw);
    }

    [SkippableFact]
    public async Task EditOutsideAFailingTestsCoverage_SkipsIt_UnlessIncludeFailing()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle(Tests);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle, includeFailing: null);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal("fail", baseline.Status["Codeunit60460.FailsAfterA"]);
        Assert.Equal("pass", baseline.Status["Codeunit60460.PassesB"]);

        // Default: helper B is not in the failing test's recorded coverage, so it is skipped and counted.
        EditHelper(bundle, "B", 1);
        var narrowed = await Send(server, bundle, includeFailing: null);
        Assert.False(narrowed.ForcedFull, narrowed.Raw);
        Assert.Equal(new[] { "Codeunit60460.PassesB" }, narrowed.Ran);
        Assert.Equal(1, narrowed.Skipped);
        Assert.Equal(1, narrowed.SkippedFailing);

        // Opt-in: the failing test runs again whatever changed. It kept its failing status while skipped.
        EditHelper(bundle, "B", 2);
        var included = await Send(server, bundle, includeFailing: true);
        Assert.False(included.ForcedFull, included.Raw);
        Assert.Equal(new[] { "Codeunit60460.FailsAfterA", "Codeunit60460.PassesB" }, included.Ran);
        Assert.Equal(0, included.Skipped);
        Assert.Equal(0, included.SkippedFailing);
        Assert.Equal("fail", included.Status["Codeunit60460.FailsAfterA"]);
    }

    [SkippableFact]
    public async Task EditToCodeAFailingTestRanBeforeFailing_SelectsIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle(Tests);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle, includeFailing: null);
        Assert.Equal("fail", baseline.Status["Codeunit60460.FailsAfterA"]);

        // Helper A ran before the failure, so the failing test's coverage holds it.
        EditHelper(bundle, "A", 1);
        var observed = await Send(server, bundle, includeFailing: false);
        Assert.False(observed.ForcedFull, observed.Raw);
        Assert.Equal(new[] { "Codeunit60460.FailsAfterA" }, observed.Ran);
        Assert.Equal("fail", observed.Status["Codeunit60460.FailsAfterA"]);
        Assert.Equal(1, observed.Skipped);
        Assert.Equal(0, observed.SkippedFailing);
    }

    // A timed-out test was stopped by the clock, not by the code it ran, and its body may still be
    // running: its coverage is not a complete record, so it stays unknown and always runs.
    [SkippableFact]
    public async Task TimedOutTest_StaysUnknown_AndRunsOnAnUnrelatedEdit()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle(SlowTests);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" },
            extraEnv: new Dictionary<string, string> { ["AL_RUNNER_TEST_TIMEOUT_SEC"] = "1" });

        var baseline = await Send(server, bundle, includeFailing: null);
        Assert.NotEqual("pass", baseline.Status["Codeunit60460.TimesOutAfterA"]);
        Assert.Contains("timeout", baseline.Raw, StringComparison.OrdinalIgnoreCase);

        EditHelper(bundle, "B", 1);
        var observed = await Send(server, bundle, includeFailing: false);
        Assert.False(observed.ForcedFull, observed.Raw);
        Assert.Equal(new[] { "Codeunit60460.PassesB", "Codeunit60460.TimesOutAfterA" }, observed.Ran);
        Assert.Equal(0, observed.SkippedFailing);
    }
}
