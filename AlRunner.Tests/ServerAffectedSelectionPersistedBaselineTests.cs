// ServerAffectedSelectionPersistedBaselineTests — #4979 part 1: the affectedOnly baseline one server
// recorded is used by the next server started on the same cache, so its first request narrows.
// Mechanism: docs/server-mode.md#affectedonly-across-server-processes.
// Runs under --isolation test: these assert per-test narrowing inside one codeunit, which the
// default Codeunit isolation widens to the whole codeunit (#5035, ServerAffectedSelectionSharedSetupTests).
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionPersistedBaselineTests
{
    private const string Helper = """
        codeunit 60601 "Persist Helper SX"
        {
            procedure Value(): Integer
            begin
                exit(1);
            end;
        }
        """;

    private const string HelperProbe = """
        codeunit 60601 "Persist Helper SX"
        {
            procedure Value(): Integer
            begin
                Error('PROBE-4979');
                exit(1);
            end;
        }
        """;

    // Reached by id only, so deleting its file still compiles: the test that ran it now fails.
    private const string ById = """
        codeunit 60602 "Persist ById SX"
        {
            trigger OnRun()
            var
                X: Integer;
            begin
                X := 1;
            end;
        }
        """;

    private const string Tests = """
        codeunit 60610 "Persist Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure UsesHelperOne()
            var
                H: Codeunit "Persist Helper SX";
            begin
                if H.Value() <> 1 then
                    Error('UsesHelperOne failed');
            end;

            [Test]
            procedure UsesHelperTwo()
            var
                H: Codeunit "Persist Helper SX";
            begin
                if H.Value() + 1 <> 2 then
                    Error('UsesHelperTwo failed');
            end;

            [Test]
            procedure RunsById()
            begin
                Codeunit.Run(60602);
            end;

            [Test]
            procedure Independent()
            begin
                if 2 + 2 <> 4 then
                    Error('Independent failed');
            end;
        }
        """;

    private const string Publisher = """
        codeunit 60620 "Persist Pub SX"
        {
            procedure DoWork()
            begin
                OnDoWork();
            end;

            procedure DoOther()
            begin
                OnDoOther();
            end;

            [IntegrationEvent(false, false)]
            local procedure OnDoWork()
            begin
            end;

            [IntegrationEvent(false, false)]
            local procedure OnDoOther()
            begin
            end;
        }
        """;

    private const string EventTests = """
        codeunit 60625 "Persist EvTests SX"
        {
            Subtype = Test;

            [Test]
            procedure RaisesWork()
            var
                P: Codeunit "Persist Pub SX";
            begin
                P.DoWork();
            end;

            [Test]
            procedure RaisesOther()
            var
                P: Codeunit "Persist Pub SX";
            begin
                P.DoOther();
            end;
        }
        """;

    private static string Subscriber(bool bound) =>
        "codeunit 60621 \"Persist Sub SX\"\n{\n"
        + "    procedure Helper(): Integer\n    begin\n        exit(1);\n    end;\n"
        + (bound
            ? "\n    [EventSubscriber(ObjectType::Codeunit, Codeunit::\"Persist Pub SX\", 'OnDoWork', '', false, false)]\n"
              + "    local procedure Handle()\n    begin\n        Error('SUBSCRIBER-PROBE');\n    end;\n"
            : "")
        + "}\n";

    private static string Bundle(string prefix, string appIdSuffix, params (string File, string Source)[] files)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c4979000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Persisted Baseline SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60600, "to": 60639 } ],
          "runtime": "14.0"
        }
        """);
        foreach (var (file, source) in files) File.WriteAllText(Path.Combine(dir, file), source);
        return dir;
    }

    private sealed record Observed(Dictionary<string, (string Status, string Line)> Tests, bool ForcedFull,
        string? Reason, int SkippedFailing, string Raw);

    private static async Task<Observed> Send(string cache, string bundle)
    {
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--cache", cache });
        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
            affectedOnly = true,
        });
        var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        var tests = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(),
            e => (e.GetProperty("status").GetString()!, e.GetRawText()), StringComparer.Ordinal);
        return new Observed(tests, selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            selection.TryGetProperty("skippedFailing", out var sf) ? sf.GetInt32() : -1, raw);
    }

    private static string StoreFile(string cache)
        => Assert.Single(Directory.GetFiles(Path.Combine(cache, "affected-baseline"), "*.json"));

    /// <summary>
    /// The issue's acceptance: an edit made while no server runs is selected by the next server's
    /// first request, which narrows. A file deleted in between selects the test that ran it. The
    /// server after that, with nothing edited, runs nothing.
    /// </summary>
    [SkippableFact]
    public async Task NextServer_FirstRequestNarrowsToWhatTheEditBreaks_ThenRunsNothingWhenUnchanged()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-persist", "000000000001",
            ("Helper.al", Helper), ("ById.al", ById), ("Tests.al", Tests));
        var cache = TestScratch.Dir("al-runner-server-affected-persist-cache");

        var baseline = await Send(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(4, baseline.Tests.Count);
        Assert.All(baseline.Tests.Values, t => Assert.True(t.Status == "pass", baseline.Raw));

        File.WriteAllText(Path.Combine(bundle, "Helper.al"), HelperProbe);
        File.Delete(Path.Combine(bundle, "ById.al"));

        var edited = await Send(cache, bundle);
        Assert.False(edited.ForcedFull, $"the persisted baseline must let the first request narrow: {edited.Raw}");
        Assert.Equal(new[] { "RunsById", "UsesHelperOne", "UsesHelperTwo" },
            edited.Tests.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var t in new[] { "UsesHelperOne", "UsesHelperTwo" })
        {
            Assert.True(edited.Tests[t].Status == "fail", edited.Raw);
            Assert.Contains("PROBE-4979", edited.Tests[t].Line, StringComparison.Ordinal);
        }
        Assert.True(edited.Tests["RunsById"].Status != "pass", edited.Raw);

        var unchanged = await Send(cache, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.True(unchanged.Tests.Count == 0, $"nothing changed since the last server: {unchanged.Raw}");
        Assert.Equal(3, unchanged.SkippedFailing);
    }

    /// <summary>#4988's shape across processes: a subscriber added while no server runs selects the test that raises its event.</summary>
    [SkippableFact]
    public async Task NextServer_SubscriberAdded_SelectsTheTestThatRaisesItsEvent()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-persist-ev", "000000000002",
            ("Pub.al", Publisher), ("Sub.al", Subscriber(bound: false)), ("EvTests.al", EventTests));
        var cache = TestScratch.Dir("al-runner-server-affected-persist-ev-cache");

        var baseline = await Send(cache, bundle);
        Assert.Equal(2, baseline.Tests.Count);

        File.WriteAllText(Path.Combine(bundle, "Sub.al"), Subscriber(bound: true));
        var added = await Send(cache, bundle);
        Assert.False(added.ForcedFull, added.Raw);
        Assert.Equal(new[] { "RaisesWork" }, added.Tests.Keys);
        Assert.Equal("fail", added.Tests["RaisesWork"].Status);
        Assert.Contains("SUBSCRIBER-PROBE", added.Tests["RaisesWork"].Line, StringComparison.Ordinal);
    }

    /// <summary>A store that cannot be read, or was written by another schema, is no baseline: never a narrowed run.</summary>
    [SkippableFact]
    public async Task TruncatedOrOtherSchemaStore_ForcesAFullRunWithAReason()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-persist-corrupt", "000000000003",
            ("Helper.al", Helper), ("ById.al", ById), ("Tests.al", Tests));
        var cache = TestScratch.Dir("al-runner-server-affected-persist-corrupt-cache");

        await Send(cache, bundle);
        var store = StoreFile(cache);
        var bytes = File.ReadAllBytes(store);
        File.WriteAllBytes(store, bytes[..(bytes.Length / 2)]);

        var truncated = await Send(cache, bundle);
        Assert.True(truncated.ForcedFull, truncated.Raw);
        Assert.Equal(4, truncated.Tests.Count);
        Assert.Contains("persisted per-test coverage baseline is unusable", truncated.Reason, StringComparison.Ordinal);

        // The run above rewrote the store; bump its schema.
        var text = File.ReadAllText(store);
        var schema = $"\"Schema\":{AlRunner.Infrastructure.AffectedBaselineStore.SchemaVersion},";
        Assert.Contains(schema, text, StringComparison.Ordinal);
        File.WriteAllText(store, text.Replace(schema, "\"Schema\":999,", StringComparison.Ordinal));

        var otherSchema = await Send(cache, bundle);
        Assert.True(otherSchema.ForcedFull, otherSchema.Raw);
        Assert.Equal(4, otherSchema.Tests.Count);
        Assert.Contains("schema version 999", otherSchema.Reason, StringComparison.Ordinal);
    }
}
