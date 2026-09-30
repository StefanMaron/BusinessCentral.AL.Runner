// ServerAffectedSelectionEventSubscriberTests — #4988: under affectedOnly, a changed event
// subscriber selects the tests that raised its event in the recording run. A subscriber is reached
// through the event, not a call, so statement coverage alone cannot select those tests.
// Mechanism: docs/server-mode.md#affectedonly-and-event-subscribers.
// Runs under --isolation test: these assert per-test narrowing inside one codeunit, which the
// default Codeunit isolation widens to the whole codeunit (#5035, ServerAffectedSelectionSharedSetupTests).
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionEventSubscriberTests
{
    private const string Publisher = """
        codeunit 60490 "EvSel Pub SX"
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

    // One object per file: coverage attributes a statement to its file's single object.
    private const string UnrelatedCodeunit = """
        codeunit 60491 "EvSel Unrelated SX"
        {
            procedure Value(): Integer
            begin
                exit(7);
            end;
        }
        """;

    private const string Table = """
        table 60492 "EvSel Tab SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string Tests = """
        codeunit 60495 "EvSel Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure RaisesWork()
            var
                P: Codeunit "EvSel Pub SX";
            begin
                P.DoWork();
            end;

            [Test]
            procedure RaisesOther()
            var
                P: Codeunit "EvSel Pub SX";
            begin
                P.DoOther();
            end;

            [Test]
            procedure InsertsRow()
            var
                T: Record "EvSel Tab SX";
            begin
                T.PK := 1;
                T.Insert(false);
            end;

            [Test]
            procedure Unrelated()
            var
                U: Codeunit "EvSel Unrelated SX";
            begin
                if U.Value() <> 7 then
                    Error('Unrelated failed');
            end;
        }
        """;

    // A subscriber codeunit whose body is a placeholder, so the fixture can bind it to anything.
    private static string Subscriber(string attribute, string parameters, string body)
        => "codeunit 60493 \"EvSel Sub SX\"\n{\n"
           + "    procedure Helper(): Integer\n    begin\n        exit(1);\n    end;\n"
           + (attribute.Length == 0 ? "" :
               $"\n    {attribute}\n    local procedure Handle({parameters})\n    begin\n        {body}\n    end;\n")
           + "}\n";

    private const string OnWork = "[EventSubscriber(ObjectType::Codeunit, Codeunit::\"EvSel Pub SX\", 'OnDoWork', '', false, false)]";
    private const string OnOther = "[EventSubscriber(ObjectType::Codeunit, Codeunit::\"EvSel Pub SX\", 'OnDoOther', '', false, false)]";
    private const string OnInsert = "[EventSubscriber(ObjectType::Table, Database::\"EvSel Tab SX\", 'OnAfterInsertEvent', '', false, false)]";
    private const string Probe = "Error('SUBSCRIBER-PROBE');";

    private static string Bundle()
    {
        var dir = TestScratch.Dir("al-runner-server-affected-evsub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "6b1b8e0a-4c3e-4f59-9a0e-2f3d1e4c4988",
          "name": "Server Affected Event Subscriber Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60490, "to": 60499 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Pub.Codeunit.al"), Publisher);
        File.WriteAllText(Path.Combine(dir, "Unrelated.Codeunit.al"), UnrelatedCodeunit);
        File.WriteAllText(Path.Combine(dir, "Tab.Table.al"), Table);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), Tests);
        return dir;
    }

    private static void WriteSubscriber(string bundle, string source)
        => File.WriteAllText(Path.Combine(bundle, "Sub.Codeunit.al"), source);

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status,
        Dictionary<string, string> Line, bool ForcedFull, string Raw);

    private static async Task<Observed> Send(CliServer server, string bundle)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
        });
        var lines = await server.SendRequestStreamingAsync(request);
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines);
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        var status = events.ToDictionary(
            e => e.GetProperty("name").GetString()!, e => e.GetProperty("status").GetString()!, StringComparer.Ordinal);
        var line = events.ToDictionary(
            e => e.GetProperty("name").GetString()!, e => e.GetRawText(), StringComparer.Ordinal);
        return new Observed(status.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), status, line,
            selection.GetProperty("forcedFull").GetBoolean(), raw);
    }

    private const string Work = "Codeunit60495.RaisesWork";
    private const string Other = "Codeunit60495.RaisesOther";
    private const string Insert = "Codeunit60495.InsertsRow";

    // The issue's measurement: an added subscriber is in nobody's coverage. Then the same subscriber
    // removed again: its event is still the key, taken from the binding the recording run saw.
    [SkippableFact]
    public async Task AddedThenRemovedSubscriber_SelectsTheTestsThatRaisedItsEvent()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle();
        WriteSubscriber(bundle, Subscriber("", "", ""));
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(4, baseline.Ran.Length);

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.Empty(unchanged.Ran);

        WriteSubscriber(bundle, Subscriber(OnWork, "", Probe));
        var added = await Send(server, bundle);
        Assert.False(added.ForcedFull, added.Raw);
        Assert.Equal(new[] { Work }, added.Ran);
        Assert.Equal("fail", added.Status[Work]);
        Assert.Contains("SUBSCRIBER-PROBE", added.Line[Work]);

        // Emptied: selected through coverage, which now holds the subscriber's statement.
        WriteSubscriber(bundle, Subscriber(OnWork, "", ""));
        var emptied = await Send(server, bundle);
        Assert.Equal(new[] { Work }, emptied.Ran);
        Assert.Equal("pass", emptied.Status[Work]);

        // Removed: an empty body leaves nothing in coverage, so only the recorded binding selects.
        WriteSubscriber(bundle, Subscriber("", "", ""));
        var removed = await Send(server, bundle);
        Assert.False(removed.ForcedFull, removed.Raw);
        Assert.Equal(new[] { Work }, removed.Ran);
        Assert.Equal("pass", removed.Status[Work]);

        WriteSubscriber(bundle, Subscriber(OnWork, "", ""));
        var readded = await Send(server, bundle);
        Assert.Equal(new[] { Work }, readded.Ran);

        // Filled: same binding, and the empty body that ran is in no coverage — the subscriber's
        // own changed code is what selects.
        WriteSubscriber(bundle, Subscriber(OnWork, "", Probe));
        var filled = await Send(server, bundle);
        Assert.False(filled.ForcedFull, filled.Raw);
        Assert.Equal(new[] { Work }, filled.Ran);
        Assert.Equal("fail", filled.Status[Work]);
    }

    // A rebound subscriber selects the raisers of its old event and of its new one. The old
    // binding's body is empty, so no test's coverage holds it: only the old binding selects
    // RaisesWork. Then a table trigger event: selected by the tests that inserted into the table.
    [SkippableFact]
    public async Task ReboundAndTriggerEventSubscribers_SelectOldAndNewRaisers()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle();
        WriteSubscriber(bundle, Subscriber(OnWork, "", ""));
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal("pass", baseline.Status[Work]);

        WriteSubscriber(bundle, Subscriber(OnOther, "", Probe));
        var rebound = await Send(server, bundle);
        Assert.False(rebound.ForcedFull, rebound.Raw);
        Assert.Equal(new[] { Other, Work }, rebound.Ran);
        Assert.Equal("pass", rebound.Status[Work]);
        Assert.Equal("fail", rebound.Status[Other]);
        Assert.Contains("SUBSCRIBER-PROBE", rebound.Line[Other]);

        WriteSubscriber(bundle, Subscriber(OnInsert, "var Rec: Record \"EvSel Tab SX\"", Probe));
        var trigger = await Send(server, bundle);
        Assert.False(trigger.ForcedFull, trigger.Raw);
        // RaisesOther: the subscriber leaving OnDoOther is a change to its binding too.
        Assert.Equal(new[] { Insert, Other }, trigger.Ran);
        Assert.Equal("fail", trigger.Status[Insert]);
        Assert.Contains("SUBSCRIBER-PROBE", trigger.Line[Insert]);
        Assert.Equal("pass", trigger.Status[Other]);
    }
}
