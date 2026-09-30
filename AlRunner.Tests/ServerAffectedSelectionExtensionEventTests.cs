// ServerAffectedSelectionExtensionEventTests — #5004: an event declared in a tableextension is
// published under the base table, so a subscriber added to it must select the tests that raised
// it. The recorded key has to name the base table the way the subscriber's attribute does, or the
// selection reads the event as "never raised by a fully seeded publisher" and skips its raisers.
// Mechanism: docs/server-mode.md#affectedonly-and-event-subscribers.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionExtensionEventTests
{
    private const string Table = """
        table 60492 "EvSel Tab SX"
        {
            fields { field(1; PK; Integer) { } field(2; Name; Text[30]) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string TableExtension = """
        tableextension 60496 "EvSel TabExt SX" extends "EvSel Tab SX"
        {
            procedure RaiseExt()
            begin
                OnExtEvent();
            end;

            procedure RaiseOtherExt()
            begin
                OnOtherExtEvent();
            end;

            [IntegrationEvent(false, false)]
            local procedure OnExtEvent()
            begin
            end;

            [IntegrationEvent(false, false)]
            local procedure OnOtherExtEvent()
            begin
            end;
        }
        """;

    private const string Tests = """
        codeunit 60495 "EvSel Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure RaisesExt()
            var
                T: Record "EvSel Tab SX";
            begin
                T.RaiseExt();
            end;

            [Test]
            procedure RaisesOtherExt()
            var
                T: Record "EvSel Tab SX";
            begin
                T.RaiseOtherExt();
            end;
        }
        """;

    private static string Subscriber(bool bound)
        => "codeunit 60493 \"EvSel Sub SX\"\n{\n"
           + "    procedure Helper(): Integer\n    begin\n        exit(1);\n    end;\n"
           + (bound
               ? "\n    [EventSubscriber(ObjectType::Table, Database::\"EvSel Tab SX\", 'OnExtEvent', '', false, false)]\n"
                 + "    local procedure Handle()\n    begin\n        Error('PROBE-EXT');\n    end;\n"
               : "")
           + "}\n";

    private static string Bundle()
    {
        var dir = TestScratch.Dir("al-runner-server-affected-extevent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "0b7c2f4e-5004-4a1e-8c55-3b8f2a9e5004",
          "name": "Server Affected Extension Event Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60490, "to": 60499 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tab.Table.al"), Table);
        File.WriteAllText(Path.Combine(dir, "TabExt.TableExt.al"), TableExtension);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), Tests);
        return dir;
    }

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

    private const string RaisesExt = "Codeunit60495.RaisesExt";

    // The issue's reproducer, under affectedOnly: the added subscriber is in nobody's coverage, so
    // only the recorded raise of OnExtEvent can select RaisesExt — and the subscriber must fire.
    // RaisesOtherExt raised a sibling event of the same tableextension and must stay unselected.
    [SkippableFact]
    public async Task SubscriberAddedToATableExtensionEvent_SelectsAndFailsItsRaiser()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle();
        var subscriberFile = Path.Combine(bundle, "Sub.Codeunit.al");
        File.WriteAllText(subscriberFile, Subscriber(bound: false));
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(2, baseline.Ran.Length);
        Assert.Equal("pass", baseline.Status[RaisesExt]);

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.Empty(unchanged.Ran);

        File.WriteAllText(subscriberFile, Subscriber(bound: true));
        var added = await Send(server, bundle);
        Assert.False(added.ForcedFull, added.Raw);
        Assert.Equal(new[] { RaisesExt }, added.Ran);
        Assert.Equal("fail", added.Status[RaisesExt]);
        Assert.Contains("PROBE-EXT", added.Line[RaisesExt]);
    }
}
