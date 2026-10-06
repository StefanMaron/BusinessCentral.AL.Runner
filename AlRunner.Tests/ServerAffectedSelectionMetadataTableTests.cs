// ServerAffectedSelectionMetadataTableTests — #5084: under affectedOnly, a test that reads object
// metadata through a virtual table (AllObj, Table Metadata, Page Control Field, ...) is selected when
// an object it lists is added, removed or redeclared. Such a test records only the virtual table's own
// `tbl|Table|<id>` key, which no object change used to key.
// Mechanism: docs/server-mode.md#affectedonly-and-metadata-virtual-tables.
// Runs under --isolation test, like ServerAffectedSelectionTableChangeTests: these assert per-test
// narrowing inside one codeunit, which the default Codeunit isolation widens to the whole codeunit.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionMetadataTableTests
{
    private const string Table = """
        table 60790 "MetaSel Tab SX"
        {
            fields { field(1; PK; Integer) { } field(2; Extra; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string Page = """
        page 60791 "MetaSel Page SX"
        {
            PageType = Card;
            SourceTable = "MetaSel Tab SX";
            layout
            {
                area(Content)
                {
                    field(PK; Rec.PK) { }
                }
            }
        }
        """;

    private const string PageExtension = """
        pageextension 60795 "MetaSel PageExt SX" extends "MetaSel Page SX"
        {
            layout
            {
                addlast(Content)
                {
                    field(ExtraCtl; Rec.Extra) { }
                }
            }
        }
        """;

    private const string NewTable = """
        table 60796 "MetaSel New Tab SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string NewCodeunit = "codeunit 60797 \"MetaSel New SX\"\n{\n}\n";

    private static string Unrelated(string body = "exit(7);")
        => "codeunit 60793 \"MetaSel Unrelated SX\"\n{\n    procedure Value(): Integer\n    begin\n        "
           + body + "\n    end;\n}\n";

    // Each test reads ONE metadata virtual table, for an object that exists only while a step adds it.
    private const string Tests = """
        codeunit 60792 "MetaSel Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure ReadsAllObj()
            var
                O: Record AllObj;
            begin
                if O.Get(O."Object Type"::Codeunit, 60797) then
                    Error('PROBE-ALLOBJ');
            end;

            [Test]
            procedure ReadsAllObjWithCaption()
            var
                O: Record AllObjWithCaption;
            begin
                if O.Get(O."Object Type"::Codeunit, 60797) then
                    Error('PROBE-ALLOBJCAPTION');
            end;

            [Test]
            procedure ReadsCodeunitMetadata()
            var
                C: Record "CodeUnit Metadata";
            begin
                if C.Get(60797) then
                    Error('PROBE-CODEUNITMETA');
            end;

            [Test]
            procedure ReadsTableMetadata()
            var
                T: Record "Table Metadata";
            begin
                if T.Get(60796) then
                    Error('PROBE-TABLEMETA');
            end;

            [Test]
            procedure ReadsPageControlField()
            var
                P: Record "Page Control Field";
            begin
                P.SetRange(PageNo, 60791);
                if P.Count() <> 1 then
                    Error('PROBE-PCF %1', P.Count());
            end;

            [Test]
            procedure Unrelated()
            var
                U: Codeunit "MetaSel Unrelated SX";
            begin
                if U.Value() <> 7 then
                    Error('Unrelated failed');
            end;
        }
        """;

    // Its global record of a metadata table is built with the codeunit instance, before any test starts.
    private const string GlobalTests = """
        codeunit 60798 "MetaSel Global Tests SX"
        {
            Subtype = Test;

            var
                G: Record "Table Metadata";

            [Test]
            procedure ReadsHeldGlobal()
            begin
                if G.Get(60796) then
                    Error('PROBE-GLOBAL');
            end;
        }
        """;

    private static readonly string[] All =
    {
        "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsCodeunitMetadata", "ReadsPageControlField", "ReadsTableMetadata", "Unrelated",
    };

    private static string Bundle(string prefix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c5084000-0000-4a11-9111-000000000001",
          "name": "Metadata Table Selection SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60790, "to": 60799 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Tab.Table.al", Table);
        Write(dir, "Page.Page.al", Page);
        Write(dir, "Unrelated.Codeunit.al", Unrelated());
        Write(dir, "Tests.Codeunit.al", Tests);
        return dir;
    }

    private static void Write(string bundle, string file, string source)
        => File.WriteAllText(Path.Combine(bundle, file), source);

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status,
        Dictionary<string, string> Line, bool ForcedFull, string? Reason, string Raw);

    private static async Task<Observed> Send(CliServer server, string bundle, bool affectedOnly = true)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
            ["affectedOnly"] = affectedOnly,
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

    // Selected exactly `expected`; the one test per virtual table in `failing` reads the changed
    // metadata and answers differently, which is what a skipped run would have hidden.
    private static void AssertSelected(Observed o, string[] expected, params (string Test, string Probe)[] failing)
    {
        Assert.False(o.ForcedFull, o.Raw);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), o.Ran);
        foreach (var t in expected)
        {
            var probe = failing.Where(f => f.Test == t).Select(f => f.Probe).FirstOrDefault();
            if (probe == null) Assert.True(o.Status[t] == "pass", $"{t}: {o.Raw}");
            else
            {
                Assert.True(o.Status[t] == "fail", $"{t}: {o.Raw}");
                Assert.Contains(probe, o.Line[t], StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task ChangedObject_SelectsTheTestsReadingTheMetadataVirtualTablesThatListIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-metasel");
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.Empty(unchanged.Ran);

        // An added codeunit is a row of AllObj, AllObjWithCaption and CodeUnit Metadata, and of no other table read here.
        Write(bundle, "New.Codeunit.al", NewCodeunit);
        AssertSelected(await Send(server, bundle),
            new[] { "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsCodeunitMetadata" },
            ("ReadsAllObj", "PROBE-ALLOBJ"), ("ReadsAllObjWithCaption", "PROBE-ALLOBJCAPTION"),
            ("ReadsCodeunitMetadata", "PROBE-CODEUNITMETA"));

        // Removed: the row goes, and the same tests answer the other way.
        File.Delete(Path.Combine(bundle, "New.Codeunit.al"));
        AssertSelected(await Send(server, bundle), new[] { "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsCodeunitMetadata" });

        // An added table: AllObj, AllObjWithCaption and Table Metadata, not CodeUnit Metadata.
        Write(bundle, "NewTab.Table.al", NewTable);
        AssertSelected(await Send(server, bundle),
            new[] { "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsTableMetadata" },
            ("ReadsTableMetadata", "PROBE-TABLEMETA"));
        File.Delete(Path.Combine(bundle, "NewTab.Table.al"));
        AssertSelected(await Send(server, bundle), new[] { "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsTableMetadata" });

        // A pageextension adds a control to a page's rows in Page Control Field, and is an object itself.
        Write(bundle, "Ext.PageExt.al", PageExtension);
        AssertSelected(await Send(server, bundle),
            new[] { "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsPageControlField" },
            ("ReadsPageControlField", "PROBE-PCF 2"));
        File.Delete(Path.Combine(bundle, "Ext.PageExt.al"));
        AssertSelected(await Send(server, bundle), new[] { "ReadsAllObj", "ReadsAllObjWithCaption", "ReadsPageControlField" });

        // A change inside one procedure moves no row of any metadata table: only its callers run.
        Write(bundle, "Unrelated.Codeunit.al", Unrelated("exit(7 + 0);"));
        AssertSelected(await Send(server, bundle), new[] { "Unrelated" });

        // A metadata table only a test codeunit's global holds: which of its tests read it is not recorded.
        Write(bundle, "GlobalTests.Codeunit.al", GlobalTests);
        Assert.Contains("ReadsHeldGlobal", (await Send(server, bundle)).Ran);
        Write(bundle, "NewTab.Table.al", NewTable);
        var held = await Send(server, bundle);
        Assert.True(held.ForcedFull, held.Raw);
        Assert.Contains("a record of metadata table 2000000136 was held outside any one test", held.Reason, StringComparison.Ordinal);
        Assert.Equal(All.Concat(new[] { "ReadsHeldGlobal" }).OrderBy(x => x, StringComparer.Ordinal), held.Ran);
        foreach (var (t, probe) in new[] { ("ReadsTableMetadata", "PROBE-TABLEMETA"), ("ReadsHeldGlobal", "PROBE-GLOBAL") })
        {
            Assert.True(held.Status[t] == "fail", $"{t}: {held.Raw}");
            Assert.Contains(probe, held.Line[t], StringComparison.Ordinal);
        }
    }
}
