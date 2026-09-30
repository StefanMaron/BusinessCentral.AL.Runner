// ServerAffectedSelectionExtensionEventTests — #5004: an event declared in a tableextension is
// published under the base table, so a subscriber added to it must select the tests that raised
// it. The recorded key has to name the base table the way the subscriber's attribute does, or the
// selection reads the event as "never raised by a fully seeded publisher" and skips its raisers.
// Mechanism: docs/server-mode.md#affectedonly-and-event-subscribers.
// Runs under --isolation test: these assert per-test narrowing inside one codeunit, which the
// default Codeunit isolation widens to the whole codeunit (#5035, ServerAffectedSelectionSharedSetupTests).
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

    // Two unrelated apps in one request each declare a table named "Dup Tab", so the source
    // registry cannot name the tableextension's single base and its raise cannot be keyed.
    private static (string X, string Y) DuplicateBaseNameBundles()
    {
        var x = TestScratch.Dir("al-runner-server-affected-extevent-dupx");
        var y = TestScratch.Dir("al-runner-server-affected-extevent-dupy");
        Directory.CreateDirectory(x);
        Directory.CreateDirectory(y);
        File.WriteAllText(Path.Combine(x, "app.json"), """
        {
          "id": "0b7c2f4e-5004-4a1e-8c55-3b8f2a9e5005",
          "name": "Dup Base Name X",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60740, "to": 60749 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(y, "app.json"), """
        {
          "id": "0b7c2f4e-5004-4a1e-8c55-3b8f2a9e5006",
          "name": "Dup Base Name Y",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60750, "to": 60759 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(x, "Tab.Table.al"), """
            table 60740 "Dup Tab"
            {
                fields { field(1; PK; Integer) { } }
                keys { key(PK; PK) { Clustered = true; } }
            }
            """);
        File.WriteAllText(Path.Combine(x, "TabExt.TableExt.al"), """
            tableextension 60741 "Dup TabExt" extends "Dup Tab"
            {
                procedure RaiseExt()
                begin
                    OnExtEvent();
                end;

                [IntegrationEvent(false, false)]
                local procedure OnExtEvent()
                begin
                end;
            }
            """);
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), """
            codeunit 60743 "Dup Tests"
            {
                Subtype = Test;

                [Test]
                procedure RaisesExt()
                var
                    T: Record "Dup Tab";
                begin
                    T.RaiseExt();
                end;
            }
            """);
        File.WriteAllText(Path.Combine(y, "Tab.Table.al"), """
            table 60750 "Dup Tab"
            {
                fields { field(1; PK; Integer) { } }
                keys { key(PK; PK) { Clustered = true; } }
            }
            """);
        return (x, y);
    }

    private static string DupSubscriber(bool bound)
        => "codeunit 60742 \"Dup Sub\"\n{\n"
           + "    procedure Helper(): Integer\n    begin\n        exit(1);\n    end;\n"
           + (bound
               ? "\n    [EventSubscriber(ObjectType::Table, Database::\"Dup Tab\", 'OnExtEvent', '', false, false)]\n"
                 + "    local procedure Handle()\n    begin\n        Error('PROBE-DUP');\n    end;\n"
               : "")
           + "}\n";

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status,
        Dictionary<string, string> Line, bool ForcedFull, string Raw);

    private static async Task<Observed> Send(CliServer server, params string[] bundles)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = bundles,
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
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

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

    // The raise of an extension event whose base the registry cannot name is recorded under no
    // key, so "fully seeded publisher, event never observed" would skip its raiser. The module
    // must count as not fully seeded instead, forcing a full run in which the subscriber fires.
    [SkippableFact]
    public async Task SubscriberAddedToAnUnkeyableExtensionEvent_ForcesAFullRun()
    {
        TestArtifacts.SkipIfMissing();
        var (x, y) = DuplicateBaseNameBundles();
        var subscriberFile = Path.Combine(x, "Sub.Codeunit.al");
        File.WriteAllText(subscriberFile, DupSubscriber(bound: false));
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, x, y);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal("pass", baseline.Status["Codeunit60743.RaisesExt"]);

        File.WriteAllText(subscriberFile, DupSubscriber(bound: true));
        var added = await Send(server, x, y);
        Assert.True(added.ForcedFull, added.Raw);
        Assert.Contains("could not all be recorded", added.Raw);
        Assert.Equal("fail", added.Status["Codeunit60743.RaisesExt"]);
        Assert.Contains("PROBE-DUP", added.Line["Codeunit60743.RaisesExt"]);
    }
}
