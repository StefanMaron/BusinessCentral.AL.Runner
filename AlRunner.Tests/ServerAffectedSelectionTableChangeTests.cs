// ServerAffectedSelectionTableChangeTests — #5008: under affectedOnly, a changed table or
// tableextension selects the tests that held a record of the table. A table with no triggers runs no
// statement, so statement coverage alone cannot select them.
// Mechanism: docs/server-mode.md#affectedonly-and-changed-tables.
// Runs under --isolation test: these assert per-test narrowing inside one codeunit, which the
// default Codeunit isolation widens to the whole codeunit (#5035, ServerAffectedSelectionSharedSetupTests).
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

    // A SingleInstance codeunit's global record: built by the first test that calls it, read by the second.
    private static string SiTable(string nameField = "field(2; Name; Text[30]) { }")
        => "table 60678 \"TabSel Si Tab SX\"\n{\n"
           + $"    fields {{ field(1; PK; Integer) {{ }} {nameField} }}\n"
           + "    keys { key(PK; PK) { Clustered = true; } }\n}\n";

    private const string SiCodeunit = """
        codeunit 60679 "TabSel Si SX"
        {
            SingleInstance = true;

            var
                G: Record "TabSel Si Tab SX";

            procedure NameAfterInit(): Text
            begin
                G.Init();
                exit(G.Name);
            end;
        }
        """;

    private const string SiTests = """
        codeunit 60680 "TabSel Si Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure ViaSingleInstance()
            var
                S: Codeunit "TabSel Si SX";
            begin
                if S.NameAfterInit() <> '' then
                    Error('PROBE-SI %1', S.NameAfterInit());
            end;

            [Test]
            procedure ViaSingleInstance2()
            var
                S: Codeunit "TabSel Si SX";
            begin
                if S.NameAfterInit() <> '' then
                    Error('PROBE-SI %1', S.NameAfterInit());
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
          "idRanges": [ { "from": 60670, "to": 60689 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tab.Table.al"), Table());
        File.WriteAllText(Path.Combine(dir, "Untouched.Table.al"), Untouched);
        File.WriteAllText(Path.Combine(dir, "Held.Table.al"), Held);
        File.WriteAllText(Path.Combine(dir, "Unrelated.Codeunit.al"), UnrelatedCodeunit);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), Tests);
        File.WriteAllText(Path.Combine(dir, "GlobalTests.Codeunit.al"), GlobalTests);
        File.WriteAllText(Path.Combine(dir, "SiTab.Table.al"), SiTable());
        File.WriteAllText(Path.Combine(dir, "Si.Codeunit.al"), SiCodeunit);
        File.WriteAllText(Path.Combine(dir, "SiTests.Codeunit.al"), SiTests);
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
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--cache", cache });
        return await Send(server, bundle);
    }

    private static readonly string[] Holders = { "InsertsRow", "ReadsInit", "ValidatesName" };

    // #5050: the SingleInstance tests run after the holders and read state a changed test could
    // start writing, so any selection brings them (docs/server-mode.md#affectedonly-and-session-state).
    private static readonly string[] Selected =
        Holders.Concat(new[] { "ViaSingleInstance", "ViaSingleInstance2" }).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static void AssertNarrowedToHolders(Observed o, string failing, string probe)
    {
        Assert.False(o.ForcedFull, o.Raw);
        Assert.Equal(Selected, o.Ran);
        foreach (var t in Selected)
            Assert.True(o.Status[t] == (t == failing ? "fail" : "pass"), $"{t}: {o.Raw}");
        Assert.Contains(probe, o.Line[failing], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangedTableOrTableExtension_SelectsTheTestsHoldingItsRecords()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-tabsel", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(8, baseline.Ran.Length);
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
        Assert.Equal(Selected, (await Send(server, bundle)).Ran);

        // A new tableextension: the current registry names its base table.
        Write(bundle, "Ext.TableExt.al", Extension(ExtensionInsertProbe));
        AssertNarrowedToHolders(await Send(server, bundle), "InsertsRow", "PROBE-EXT");

        // Emptied, then removed: the recorded run knew which table it extends.
        Write(bundle, "Ext.TableExt.al", Extension(""));
        var emptied = await Send(server, bundle);
        Assert.False(emptied.ForcedFull, emptied.Raw);
        Assert.Equal(Selected, emptied.Ran);
        Assert.All(emptied.Status.Values, s => Assert.Equal("pass", s));
        File.Delete(Path.Combine(bundle, "Ext.TableExt.al"));
        var removed = await Send(server, bundle);
        Assert.False(removed.ForcedFull, removed.Raw);
        Assert.Equal(Selected, removed.Ran);

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
        Assert.Equal(8, held.Ran.Length);

        // The same through a SingleInstance global: only ViaSingleInstance built the record, and
        // both read it, so narrowing would run one of the two that now fail.
        Write(bundle, "SiTab.Table.al", SiTable("field(2; Name; Text[30]) { InitValue = 'X'; }"));
        var singleInstance = await Send(server, bundle);
        Assert.True(singleInstance.ForcedFull, singleInstance.Raw);
        Assert.Contains("Table 60678 was held outside any one test", singleInstance.Reason, StringComparison.Ordinal);
        foreach (var t in new[] { "ViaSingleInstance", "ViaSingleInstance2" })
        {
            Assert.True(singleInstance.Status[t] == "fail", $"{t}: {singleInstance.Raw}");
            Assert.Contains("PROBE-SI", singleInstance.Line[t], StringComparison.Ordinal);
        }

        // A new page: no test built it (#5011), so it selects nothing.
        Write(bundle, "Page.Page.al", "page 60677 \"TabSel Page SX\"\n{\n    SourceTable = \"TabSel Tab SX\";\n}\n");
        var page = await Send(server, bundle);
        Assert.False(page.ForcedFull, page.Raw);
        Assert.Empty(page.Ran);

        // A pageextension: its base page is not recorded, so it runs everything.
        Write(bundle, "PageExt.PageExt.al", "pageextension 60681 \"TabSel PageExt SX\" extends \"TabSel Page SX\"\n{\n}\n");
        var pageExt = await Send(server, bundle);
        Assert.True(pageExt.ForcedFull, pageExt.Raw);
        Assert.Contains("PageExtension 60681 changed", pageExt.Reason, StringComparison.Ordinal);
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
        Assert.Equal(8, baseline.Ran.Length);

        Write(bundle, "Tab.Table.al", Table(nameField: NameOnValidateProbe, triggers: OnInsertProbe));
        var edited = await SendFresh(cache, bundle);
        Assert.False(edited.ForcedFull, edited.Raw);
        Assert.Equal(Selected, edited.Ran);
        Assert.Contains("PROBE-INSERT", edited.Line["InsertsRow"], StringComparison.Ordinal);
        Assert.Contains("PROBE-VALIDATE", edited.Line["ValidatesName"], StringComparison.Ordinal);
        Assert.Equal("pass", edited.Status["ReadsInit"]);

        Write(bundle, "Tab.Table.al", Table());
        var reverted = await SendFresh(cache, bundle);
        Assert.Equal(Selected, reverted.Ran);
        Assert.All(reverted.Status.Values, s => Assert.Equal("pass", s));

        // The extension alone, so only its own mapping to the table can select.
        Write(bundle, "Ext.TableExt.al", Extension(ExtensionInsertProbe));
        AssertNarrowedToHolders(await SendFresh(cache, bundle), "InsertsRow", "PROBE-EXT");
    }
}
