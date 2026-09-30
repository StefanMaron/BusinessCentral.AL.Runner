// ServerAffectedSelectionUnknownRecordTests — #5059: a test that ran to completion but is unknown
// because its statements cannot be attributed to an object keeps its session-state record, so an
// unchanged request brings only the earlier writers of what it read. A timed-out test keeps none.
// Mechanism: docs/server-mode.md#affectedonly-and-session-state.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionUnknownRecordTests
{
    // Fills the SingleInstance store (codeunit 61910) the reader reads.
    private static string WriterA(int value = 42) => $$"""
        codeunit 61901 "SU Writer A"
        {
            Subtype = Test;

            [Test]
            procedure WritesStore()
            var
                S: Codeunit "SS Store";
            begin
                S.Put({{value}});
            end;
        }
        """;

    // Writes session state the reader never reads.
    private const string WriterB = """
        codeunit 61902 "SU Writer B"
        {
            Subtype = Test;

            [Test]
            procedure WritesWorkDate()
            begin
                WorkDate(20200101D);
            end;
        }
        """;

    // Unknown only because its own statements sit in a file declaring two objects, which maps to no
    // single object (#5003). If #5003 lands, move the reader to another unattributable shape.
    private const string UnmappableReader = """
        codeunit 61903 "SU Reader"
        {
            Subtype = Test;

            [Test]
            procedure ReadsStore()
            var
                S: Codeunit "SS Store";
            begin
                if S.Get() <> 42 then
                    Error('READS-%1', S.Get());
            end;
        }

        codeunit 61904 "SU Other"
        {
            procedure Nothing()
            begin
            end;
        }
        """;

    // Unknown only because it outlives the per-test timeout the server is started with. A watchdog
    // timeout abandons the rest of the run, so it is the last test by object id.
    private const string TimingOutReader = """
        codeunit 61903 "SU Reader"
        {
            Subtype = Test;

            [Test]
            procedure ReadsStoreThenTimesOut()
            var
                S: Codeunit "SS Store";
            begin
                if S.Get() <> 42 then
                    Error('READS-%1', S.Get());
                Sleep(4000);
            end;
        }
        """;

    private static string Bundle(string prefix, string appIdSuffix, string reader)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5059000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Unknown Record SU {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61900, "to": 61919 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Store.Codeunit.al"), ServerAffectedSelectionSessionStateTests.Store());
        File.WriteAllText(Path.Combine(dir, "WriterA.Codeunit.al"), WriterA());
        File.WriteAllText(Path.Combine(dir, "WriterB.Codeunit.al"), WriterB);
        File.WriteAllText(Path.Combine(dir, "Reader.Codeunit.al"), reader);
        return dir;
    }

    private sealed record Observed(Dictionary<string, (string Status, string Line)> Tests, bool ForcedFull, string Raw)
    {
        public string[] Ran => Tests.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static async Task<Observed> Send(CliServer server, string bundle)
    {
        var request = new Dictionary<string, object>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
        };
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        var forced = summary.TryGetProperty("selection", out var selection) && selection.GetProperty("forcedFull").GetBoolean();
        var tests = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(),
            e => (e.GetProperty("status").GetString()!, e.GetRawText()), StringComparer.Ordinal);
        return new Observed(tests, forced, raw);
    }

    private static void AssertRan(Observed o, string step, params string[] expected)
        => Assert.True(expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(o.Ran),
            $"{step}: ran [{string.Join(", ", o.Ran)}], expected [{string.Join(", ", expected)}]:\n{o.Raw}");

    /// <summary>
    /// Unchanged request: the unknown reader runs, as always, and brings the test that wrote the
    /// store it read, not the test that wrote WorkDate. An edit to that writer still selects the
    /// reader, which then fails as a full run fails it.
    /// </summary>
    [SkippableFact]
    public async Task UnknownByUnmappableStatements_BringsOnlyTheWriterOfWhatItRead()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unknown-record", "000000000001", UnmappableReader);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(baseline.Tests.Values.All(t => t.Status == "pass"), baseline.Raw);

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        AssertRan(unchanged, "unchanged", "ReadsStore", "WritesStore");
        Assert.True(unchanged.Tests.Values.All(t => t.Status == "pass"), unchanged.Raw);

        File.WriteAllText(Path.Combine(bundle, "WriterA.Codeunit.al"), WriterA(43));
        var edited = await Send(server, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        // With a change every earlier writer comes along, whatever the records say.
        AssertRan(edited, "writer edit", "ReadsStore", "WritesStore", "WritesWorkDate");
        Assert.True(edited.Tests.TryGetValue("ReadsStore", out var r) && r.Status == "fail", edited.Raw);
        Assert.Contains("READS-43", r.Line, StringComparison.Ordinal);
    }

    /// <summary>The same record through the persisted baseline, read by a restarted server.</summary>
    [SkippableFact]
    public async Task UnknownByUnmappableStatements_KeepsItsRecordAcrossARestart()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unknown-record-restart", "000000000002", UnmappableReader);
        var cache = TestScratch.Dir("al-runner-server-affected-unknown-record-restart-cache");

        await using (var recorder = await CliServer.StartAsync(new[] { "--cache", cache }))
            Assert.True((await Send(recorder, bundle)).ForcedFull);

        await using (var restarted = await CliServer.StartAsync(new[] { "--cache", cache }))
        {
            var unchanged = await Send(restarted, bundle);
            Assert.False(unchanged.ForcedFull, $"the persisted baseline must let the first request narrow: {unchanged.Raw}");
            AssertRan(unchanged, "restarted", "ReadsStore", "WritesStore");
        }

        // The baseline a narrowed run persisted, read again from the same cache root.
        await using var again = await CliServer.StartAsync(new[] { "--cache", cache });
        var second = await Send(again, bundle);
        Assert.False(second.ForcedFull, second.Raw);
        AssertRan(second, "second restart", "ReadsStore", "WritesStore");
    }

    /// <summary>
    /// A timed-out test was stopped by the clock and its record may be partial, so it keeps none
    /// and brings every earlier writer, the WorkDate writer included.
    /// </summary>
    [SkippableFact]
    public async Task TimedOutReader_KeepsNoRecord_AndBringsEveryEarlierWriter()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-unknown-record-timeout", "000000000003", TimingOutReader);
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" },
            extraEnv: new Dictionary<string, string> { ["AL_RUNNER_TEST_TIMEOUT_SEC"] = "1" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.NotEqual("pass", baseline.Tests["ReadsStoreThenTimesOut"].Status);
        Assert.Equal("pass", baseline.Tests["WritesStore"].Status);
        Assert.Equal("pass", baseline.Tests["WritesWorkDate"].Status);

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        AssertRan(unchanged, "unchanged", "ReadsStoreThenTimesOut", "WritesStore", "WritesWorkDate");
    }
}
