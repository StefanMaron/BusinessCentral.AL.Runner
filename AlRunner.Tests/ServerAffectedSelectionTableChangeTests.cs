// ServerAffectedSelectionTableChangeTests — #5008: under affectedOnly, a changed table or
// tableextension selects the tests that held a record of the table. A table with no triggers runs no
// statement, so statement coverage alone cannot select them.
// Mechanism: docs/server-mode.md#affectedonly-and-changed-tables.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionTableChangeTests
{
    private static string Table(string nameField = "field(2; Name; Text[30]) { }", string triggers = "")
        => "table 60670 \"TabSel Tab SX\"\n{\n"
           + $"    fields {{ field(1; PK; Integer) {{ }} {nameField} }}\n"
           + "    keys { key(PK; PK) { Clustered = true; } }\n"
           + triggers
           + "}\n";

    private const string OnInsertProbe = "    trigger OnInsert() begin Error('PROBE-INSERT'); end;\n";
    private const string NameOnValidateProbe = "field(2; Name; Text[30]) { trigger OnValidate() begin Error('PROBE-VALIDATE'); end; }";
    private const string NameInitValue = "field(2; Name; Text[30]) { InitValue = 'X'; }";

    // No test holds a record of it: changing it must select nothing.
    private const string Untouched = """
        table 60671 "TabSel Untouched SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string UntouchedChanged = """
        table 60671 "TabSel Untouched SX"
        {
            fields { field(1; PK; Integer) { } field(2; Extra; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
            trigger OnInsert() begin Error('PROBE-UNTOUCHED'); end;
        }
        """;

    private const string Held = """
        table 60672 "TabSel Held SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string HeldChanged = """
        table 60672 "TabSel Held SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
            trigger OnInsert() begin Error('PROBE-HELD'); end;
        }
        """;

    private static string Extension(string body)
        => "tableextension 60673 \"TabSel Ext SX\" extends \"TabSel Tab SX\"\n{\n" + body + "}\n";

    private const string ExtensionInsertProbe = "    trigger OnBeforeInsert() begin Error('PROBE-EXT'); end;\n";

    private const string UnrelatedCodeunit = """
        codeunit 60674 "TabSel Unrelated SX"
        {
            procedure Value(): Integer
            begin
                exit(7);
            end;
        }
        """;

    private const string Tests = """
        codeunit 60675 "TabSel Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure InsertsRow()
            var
                T: Record "TabSel Tab SX";
            begin
                T.PK := 1;
                T.Insert(true);
            end;

            [Test]
            procedure ValidatesName()
            var
                T: Record "TabSel Tab SX";
            begin
                T.Validate(Name, 'x');
            end;

            [Test]
            procedure ReadsInit()
            var
                T: Record "TabSel Tab SX";
            begin
                T.Init();
                if T.Name <> '' then
                    Error('PROBE-INIT %1', T.Name);
            end;

            [Test]
            procedure Unrelated()
            var
                U: Codeunit "TabSel Unrelated SX";
            begin
                if U.Value() <> 7 then
                    Error('Unrelated failed');
            end;
        }
        """;

    // Its global record is built with the codeunit instance, before any of its tests starts.
    private const string GlobalTests = """
        codeunit 60676 "TabSel Global Tests SX"
        {
            Subtype = Test;

            var
                G: Record "TabSel Held SX";

            [Test]
            procedure AReadsNothing()
            begin
                if 1 + 1 <> 2 then
                    Error('AReadsNothing failed');
            end;

            [Test]
            procedure ReadsHeldGlobal()
            begin
                if not G.IsEmpty() then
                    Error('ReadsHeldGlobal failed');
            end;
        }
        """;

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5008000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Table Change Selection SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60670, "to": 60679 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tab.Table.al"), Table());
        File.WriteAllText(Path.Combine(dir, "Untouched.Table.al"), Untouched);
        File.WriteAllText(Path.Combine(dir, "Held.Table.al"), Held);
        File.WriteAllText(Path.Combine(dir, "Unrelated.Codeunit.al"), UnrelatedCodeunit);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), Tests);
        File.WriteAllText(Path.Combine(dir, "GlobalTests.Codeunit.al"), GlobalTests);
        return dir;
    }

    private static void Write(string bundle, string file, string source)
        => File.WriteAllText(Path.Combine(bundle, file), source);

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status,
        Dictionary<string, string> Line, bool ForcedFull, string? Reason, string Raw);

    private static async Task<Observed> Send(CliServer server, string bundle)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = true,
        });
        var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines);
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        var status = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(), e => e.GetProperty("status").GetString()!,
            StringComparer.Ordinal);
        var line = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(), e => e.GetRawText(), StringComparer.Ordinal);
        return new Observed(status.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), status, line,
            selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            raw);
    }

    private static async Task<Observed> SendFresh(string cache, string bundle)
    {
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        return await Send(server, bundle);
    }

    private static readonly string[] Holders = { "InsertsRow", "ReadsInit", "ValidatesName" };

    private static void AssertNarrowedToHolders(Observed o, string failing, string probe)
    {
        Assert.False(o.ForcedFull, o.Raw);
        Assert.Equal(Holders, o.Ran);
        foreach (var t in Holders)
            Assert.True(o.Status[t] == (t == failing ? "fail" : "pass"), $"{t}: {o.Raw}");
        Assert.Contains(probe, o.Line[failing], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangedTableOrTableExtension_SelectsTheTestsHoldingItsRecords()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-tabsel", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(6, baseline.Ran.Length);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.Empty(unchanged.Ran);

        // The issue's measurement: an added table trigger.
        Write(bundle, "Tab.Table.al", Table(triggers: OnInsertProbe));
        AssertNarrowedToHolders(await Send(server, bundle), "InsertsRow", "PROBE-INSERT");

        // A field trigger, then a field property no trigger is involved in.
        Write(bundle, "Tab.Table.al", Table(nameField: NameOnValidateProbe));
        AssertNarrowedToHolders(await Send(server, bundle), "ValidatesName", "PROBE-VALIDATE");
        Write(bundle, "Tab.Table.al", Table(nameField: NameInitValue));
        AssertNarrowedToHolders(await Send(server, bundle), "ReadsInit", "PROBE-INIT");
        Write(bundle, "Tab.Table.al", Table());
        Assert.Equal(Holders, (await Send(server, bundle)).Ran);

        // A new tableextension: the current registry names its base table.
        Write(bundle, "Ext.TableExt.al", Extension(ExtensionInsertProbe));
        AssertNarrowedToHolders(await Send(server, bundle), "InsertsRow", "PROBE-EXT");

        // Emptied, then removed: the recorded run knew which table it extends.
        Write(bundle, "Ext.TableExt.al", Extension(""));
        var emptied = await Send(server, bundle);
        Assert.False(emptied.ForcedFull, emptied.Raw);
        Assert.Equal(Holders, emptied.Ran);
        Assert.All(emptied.Status.Values, s => Assert.Equal("pass", s));
        File.Delete(Path.Combine(bundle, "Ext.TableExt.al"));
        var removed = await Send(server, bundle);
        Assert.False(removed.ForcedFull, removed.Raw);
        Assert.Equal(Holders, removed.Ran);

        // A table no test holds a record of selects nothing.
        Write(bundle, "Untouched.Table.al", UntouchedChanged);
        var untouched = await Send(server, bundle);
        Assert.False(untouched.ForcedFull, untouched.Raw);
        Assert.Empty(untouched.Ran);

        // A table only a test codeunit's global holds: which of its tests read it is not recorded.
        Write(bundle, "Held.Table.al", HeldChanged);
        var held = await Send(server, bundle);
        Assert.True(held.ForcedFull, held.Raw);
        Assert.Contains("Table 60672 was held outside any one test", held.Reason, StringComparison.Ordinal);
        Assert.Equal(6, held.Ran.Length);
    }

    // #5007's path: the same changes made while no server runs.
    [SkippableFact]
    public async Task NextServer_ChangedTableOrNewTableExtension_SelectsTheTestsHoldingItsRecords()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-tabsel-persist", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-tabsel-persist-cache");

        var baseline = await SendFresh(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(6, baseline.Ran.Length);

        Write(bundle, "Tab.Table.al", Table(nameField: NameOnValidateProbe, triggers: OnInsertProbe));
        var edited = await SendFresh(cache, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        Assert.Equal(Holders, edited.Ran);
        Assert.Contains("PROBE-INSERT", edited.Line["InsertsRow"], StringComparison.Ordinal);
        Assert.Contains("PROBE-VALIDATE", edited.Line["ValidatesName"], StringComparison.Ordinal);
        Assert.Equal("pass", edited.Status["ReadsInit"]);

        Write(bundle, "Tab.Table.al", Table());
        Write(bundle, "Ext.TableExt.al", Extension(ExtensionInsertProbe));
        AssertNarrowedToHolders(await SendFresh(cache, bundle), "InsertsRow", "PROBE-EXT");
    }
}
