// ServerAffectedSelectionPageExtensionTests — #5025: under affectedOnly, a changed pageextension
// selects the tests that opened its base page, instead of running everything. Mechanism:
// docs/server-mode.md#affectedonly-and-page-extensions.
// Runs under --isolation test, like ServerAffectedSelectionEnteredScopeTests: these assert per-test
// narrowing inside one codeunit, which the default Codeunit isolation widens to the whole codeunit.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionPageExtensionTests
{
    private const string Table = """
        table 60711 "PExt Tab SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private static string Page(int id, string name)
        => $"page {id} \"{name}\"\n{{\n    PageType = Card;\n    SourceTable = \"PExt Tab SX\";\n"
           + "    layout\n    {\n        area(Content)\n        {\n            field(PK; Rec.PK) { }\n        }\n    }\n}\n";

    private static string Extension(int id, string name, string basePage, string body = "")
        => $"pageextension {id} \"{name}\" extends \"{basePage}\"\n{{\n"
           + "    trigger OnOpenPage()\n    begin\n" + body + "    end;\n}\n";

    private const string ProbeExt = "        Error('PROBE-EXT');\n";
    private const string ProbeNew = "        Error('PROBE-NEW');\n";

    // Every route a test can open the extended page by.
    private const string Tests = """
        codeunit 60715 "PExt Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure OpensViaTestPage()
            var
                TP: TestPage "PExt Page SX";
            begin
                TP.OpenView();
                TP.Close();
            end;

            [Test]
            [HandlerFunctions('ModalHandler')]
            procedure RunsModalById()
            begin
                Page.RunModal(Page::"PExt Page SX");
            end;

            [Test]
            [HandlerFunctions('PageHandler')]
            procedure RunsById()
            begin
                Page.Run(Page::"PExt Page SX");
            end;

            [Test]
            [HandlerFunctions('ModalHandler')]
            procedure RunsPageVariable()
            var
                P: Page "PExt Page SX";
            begin
                P.RunModal();
            end;

            [Test]
            procedure OpensOther()
            var
                TP: TestPage "PExt Other SX";
            begin
                TP.OpenView();
                TP.Close();
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;

            [ModalPageHandler]
            procedure ModalHandler(var P: TestPage "PExt Page SX")
            begin
            end;

            [PageHandler]
            procedure PageHandler(var P: TestPage "PExt Page SX")
            begin
            end;
        }
        """;

    // The page is a test codeunit's global: no one test builds it.
    private const string GlobalTests = """
        codeunit 60717 "PExt Global Tests SX"
        {
            Subtype = Test;

            var
                G: Page "PExt Page SX";

            [Test]
            [HandlerFunctions('ModalHandler')]
            procedure RunsGlobalPage()
            begin
                G.RunModal();
            end;

            [ModalPageHandler]
            procedure ModalHandler(var P: TestPage "PExt Page SX")
            begin
            end;
        }
        """;

    private static readonly string[] OpenBasePage = { "OpensViaTestPage", "RunsById", "RunsModalById", "RunsPageVariable" };
    private static readonly string[] All = { "OpensOther", "OpensViaTestPage", "RunsById", "RunsModalById", "RunsPageVariable", "Unrelated" };

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5025000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "PageExtension Selection SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60711, "to": 60718 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Tab.Table.al", Table);
        Write(dir, "Page.Page.al", Page(60712, "PExt Page SX"));
        Write(dir, "Other.Page.al", Page(60713, "PExt Other SX"));
        Write(dir, "Ext.PageExt.al", Extension(60714, "PExt Ext SX", "PExt Page SX"));
        Write(dir, "Tests.Codeunit.al", Tests);
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

    private static void AssertNarrowed(Observed o, string[] tests, string? probe)
    {
        Assert.False(o.ForcedFull, o.Raw);
        Assert.Equal(tests, o.Ran);
        foreach (var t in tests)
        {
            if (probe == null) Assert.True(o.Status[t] == "pass", o.Raw);
            else Assert.Contains(probe, o.Line[t], StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task ChangedPageExtension_SelectsTheTestsThatOpenedItsBasePage()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-pext", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        // Edited: every route that opens the base page runs its trigger, and only those tests run.
        Write(bundle, "Ext.PageExt.al", Extension(60714, "PExt Ext SX", "PExt Page SX", ProbeExt));
        AssertNarrowed(await Send(server, bundle), OpenBasePage, "PROBE-EXT");
        Write(bundle, "Ext.PageExt.al", Extension(60714, "PExt Ext SX", "PExt Page SX"));
        AssertNarrowed(await Send(server, bundle), OpenBasePage, null);

        // Added: no test has built it, so only the registry names its base.
        Write(bundle, "New.PageExt.al", Extension(60716, "PExt New SX", "PExt Other SX", ProbeNew));
        AssertNarrowed(await Send(server, bundle), new[] { "OpensOther" }, "PROBE-NEW");

        // Removed: gone from the registry, so only the recording names its base.
        File.Delete(Path.Combine(bundle, "New.PageExt.al"));
        AssertNarrowed(await Send(server, bundle), new[] { "OpensOther" }, null);

        // A page no one test built: which tests use it is not recorded, so everything runs.
        Write(bundle, "GlobalTests.Codeunit.al", GlobalTests);
        await Send(server, bundle);
        Write(bundle, "Ext.PageExt.al", Extension(60714, "PExt Ext SX", "PExt Page SX", ProbeExt));
        var global = await Send(server, bundle);
        Assert.True(global.ForcedFull, global.Raw);
        Assert.Contains("pageextension 60714 extends Page 60712, an instance of which was built outside any one test",
            global.Reason, StringComparison.Ordinal);
        Assert.Contains("PROBE-EXT", global.Line["RunsGlobalPage"], StringComparison.Ordinal);
    }

    // #5007's path: removed while no server runs, so only the persisted baseline names its base.
    [SkippableFact]
    public async Task NextServer_RemovedPageExtension_SelectsTheTestsThatOpenedItsBasePage()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-pext-persist", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-pext-persist-cache");
        Write(bundle, "Ext.PageExt.al", Extension(60714, "PExt Ext SX", "PExt Page SX", ProbeExt));

        var baseline = await SendFresh(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);
        foreach (var t in OpenBasePage)
            Assert.Contains("PROBE-EXT", baseline.Line[t], StringComparison.Ordinal);

        File.Delete(Path.Combine(bundle, "Ext.PageExt.al"));
        AssertNarrowed(await SendFresh(cache, bundle), OpenBasePage, null);
    }
}
