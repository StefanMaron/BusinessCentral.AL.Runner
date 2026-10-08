// ServerAffectedSelectionReportExtensionTests — #5088: under affectedOnly, a changed reportextension
// selects the tests that ran its base report, instead of running everything. Mechanism:
// docs/server-mode.md#affectedonly-and-report-extensions.
// Runs under --isolation test, like ServerAffectedSelectionPageExtensionTests: these assert per-test
// narrowing inside one codeunit, which the default Codeunit isolation widens to the whole codeunit.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionReportExtensionTests
{
    private const string Blob = """
        table 60721 "RExt Blob SX"
        {
            fields
            {
                field(1; PK; Integer) { }
                field(2; Data; Blob) { }
            }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    // SaveAs refuses a processing-only report and Run refuses to render a layout, so the two have their own reports.
    private static string Report(int id, string name, bool processingOnly = true)
        => $"report {id} \"{name}\"\n{{\n    ProcessingOnly = {(processingOnly ? "true" : "false")};\n    UseRequestPage = false;\n"
           + "    dataset\n    {\n        dataitem(Item; \"RExt Blob SX\")\n        {\n"
           + "            column(PK; PK) { }\n        }\n    }\n}\n";

    // Without a trigger the extension carries no code of its own: the tests that ran its base report are
    // then selected by the base alone, not also by the extension's own entered-scope key.
    private static string Extension(int id, string name, string baseReport, string? trigger = null, string columns = "PK2")
        => $"reportextension {id} \"{name}\" extends \"{baseReport}\"\n{{\n"
           + "    dataset\n    {\n        add(Item)\n        {\n"
           + string.Concat(columns.Split(',').Select(c => $"            column({c}; PK) {{ }}\n"))
           + "        }\n    }\n"
           + (trigger == null ? "" : "    trigger OnPreReport()\n    begin\n" + trigger + "    end;\n")
           + "}\n";

    // A request page the reportextension adds a field to; the handler reads that field's caption.
    private const string PageReport = """
        report 60730 "RExt Page SX"
        {
            ProcessingOnly = true;
            dataset { dataitem(Item; "RExt Blob SX") { column(PK; PK) { } } }
            requestpage { layout { area(Content) { field(BaseOpt; BaseOpt) { Caption = 'Base'; ApplicationArea = All; } } } }
            var
                BaseOpt: Boolean;
        }
        """;

    private static string PageExtension(string caption = "Alpha")
        => "reportextension 60731 \"RExt Page Ext SX\" extends \"RExt Page SX\"\n{\n"
           + "    requestpage { layout { addlast(Content) { field(ExtOpt; ExtOpt) { Caption = '" + caption
           + "'; ApplicationArea = All; } } } }\n    var\n        ExtOpt: Boolean;\n}\n";

    private const string ProbeExt = "        Error('PROBE-EXT');\n";
    private const string ProbeNew = "        Error('PROBE-NEW');\n";

    // Every route a test can run the extended report by.
    private const string Tests = """
        codeunit 60725 "RExt Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure RunsById()
            begin
                Report.Run(Report::"RExt Report SX", false, false);
            end;

            [Test]
            procedure RunsModalById()
            begin
                Report.RunModal(Report::"RExt Report SX", false);
            end;

            [Test]
            procedure RunsReportVariable()
            var
                R: Report "RExt Report SX";
            begin
                R.UseRequestPage(false);
                R.Run();
            end;

            // After the others: running a report writes the last error, so a test before the last selected one
            // is pulled in too (docs/server-mode.md#affectedonly-and-session-state).
            [Test]
            procedure RunsOther()
            begin
                Report.Run(Report::"RExt Other SX", false, false);
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;
        }
        """;

    // The report is a test codeunit's global: no one test builds it.
    private const string GlobalTests = """
        codeunit 60727 "RExt Global Tests SX"
        {
            Subtype = Test;

            var
                G: Report "RExt Report SX";

            [Test]
            procedure RunsGlobalReport()
            begin
                G.UseRequestPage(false);
                G.Run();
            end;
        }
        """;

    private static readonly string[] RunBaseReport = { "RunsById", "RunsModalById", "RunsReportVariable" };
    private static readonly string[] All = { "RunsById", "RunsModalById", "RunsOther", "RunsReportVariable", "Unrelated" };

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5088000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "ReportExtension Selection SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60721, "to": 60731 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Blob.Table.al", Blob);
        Write(dir, "Report.Report.al", Report(60722, "RExt Report SX"));
        Write(dir, "Other.Report.al", Report(60723, "RExt Other SX"));
        Write(dir, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX"));
        Write(dir, "Tests.Codeunit.al", Tests);
        return dir;
    }

    // SaveAs and a request page read and write SingleInstance System codeunits, so each joins any selection
    // another test brings (docs/server-mode.md#affectedonly-and-session-state): each route gets a bundle of its own.
    private const string SaveAsTests = """
        codeunit 60725 "RExt Save Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure SavesAsXml()
            var
                Holder: Record "RExt Blob SX" temporary;
                OutStr: OutStream;
            begin
                Holder.Data.CreateOutStream(OutStr);
                Report.SaveAs(Report::"RExt Data SX", '', ReportFormat::Xml, OutStr);
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;
        }
        """;

    // The handler reads the caption of a field the reportextension adds to the request page.
    private const string RequestPageTests = """
        codeunit 60725 "RExt Page Tests SX"
        {
            Subtype = Test;

            [Test]
            [HandlerFunctions('RequestPageHandler')]
            procedure ViaRunRequestPage()
            begin
                if Report.RunRequestPage(Report::"RExt Page SX") = '' then;
            end;

            [Test]
            [HandlerFunctions('RequestPageHandler')]
            procedure ViaRun()
            begin
                Report.Run(Report::"RExt Page SX", true, false);
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;

            [RequestPageHandler]
            procedure RequestPageHandler(var RP: TestRequestPage "RExt Page SX")
            begin
                if RP.ExtOpt.Caption <> 'Alpha' then
                    Error('ext caption was %1', RP.ExtOpt.Caption);
                RP.OK().Invoke();
            end;
        }
        """;

    private static string SmallBundle(string prefix, string appIdSuffix, params (string File, string Source)[] files)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5088000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "ReportExtension Selection SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60721, "to": 60731 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Blob.Table.al", Blob);
        foreach (var (file, source) in files) Write(dir, file, source);
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
    public async Task ChangedReportExtension_SelectsTheTestsThatRanItsBaseReport()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-rext", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(All.SequenceEqual(baseline.Ran), baseline.Raw);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        // Nothing changed: narrowed to nothing, so the selection below is not a blanket full run.
        var none = await Send(server, bundle);
        Assert.False(none.ForcedFull, none.Raw);
        Assert.True(none.Ran.Length == 0, none.Raw);

        // Edited: every route that runs the base report runs its trigger, and only those tests run.
        Write(bundle, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX", ProbeExt));
        AssertNarrowed(await Send(server, bundle), RunBaseReport, "PROBE-EXT");
        Write(bundle, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX"));
        AssertNarrowed(await Send(server, bundle), RunBaseReport, null);

        // A whole-object edit (a column the extension adds), which also keys the metadata tables listing it.
        Write(bundle, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX", columns: "PK2,PK3"));
        AssertNarrowed(await Send(server, bundle), RunBaseReport, null);
        Write(bundle, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX"));
        AssertNarrowed(await Send(server, bundle), RunBaseReport, null);

        // Added: no test has built it, so only the registry names its base.
        Write(bundle, "New.ReportExt.al", Extension(60726, "RExt New SX", "RExt Report SX", columns: "PK4"));
        AssertNarrowed(await Send(server, bundle), RunBaseReport, null);

        // Removed: gone from the registry, so only the recording names its base.
        File.Delete(Path.Combine(bundle, "New.ReportExt.al"));
        AssertNarrowed(await Send(server, bundle), RunBaseReport, null);

        // A report no one test built: which tests use it is not recorded, so everything runs.
        // The edit below is inside the trigger, so the extension's own instance (which the global report
        // builds too) is not what forces the run: the base report's rule is.
        Write(bundle, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX", "        exit;\n"));
        Write(bundle, "GlobalTests.Codeunit.al", GlobalTests);
        await Send(server, bundle);
        Write(bundle, "Ext.ReportExt.al", Extension(60724, "RExt Ext SX", "RExt Report SX", ProbeExt));
        var global = await Send(server, bundle);
        Assert.True(global.ForcedFull, global.Raw);
        Assert.Contains("reportextension 60724 extends Report 60722, an instance of which was built outside any one test",
            global.Reason, StringComparison.Ordinal);
        Assert.Contains("PROBE-EXT", global.Line["RunsGlobalReport"], StringComparison.Ordinal);
    }

    // #5007's path: removed while no server runs, so only the persisted baseline names its base.
    [SkippableFact]
    public async Task NextServer_RemovedReportExtension_SelectsTheTestsThatRanItsBaseReport()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-rext-persist", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-rext-persist-cache");

        var baseline = await SendFresh(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(All.SequenceEqual(baseline.Ran), baseline.Raw);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        File.Delete(Path.Combine(bundle, "Ext.ReportExt.al"));
        AssertNarrowed(await SendFresh(cache, bundle), RunBaseReport, null);
    }

    [SkippableFact]
    public async Task ChangedReportExtension_SelectsTheTestThatSavedItsBaseReport()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = SmallBundle("al-runner-server-affected-rext-saveas", "000000000003",
            ("Data.Report.al", Report(60728, "RExt Data SX", processingOnly: false)),
            ("Data.ReportExt.al", Extension(60729, "RExt Data Ext SX", "RExt Data SX")),
            ("Tests.Codeunit.al", SaveAsTests));
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(new[] { "SavesAsXml", "Unrelated" }.SequenceEqual(baseline.Ran), baseline.Raw);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        Write(bundle, "Data.ReportExt.al", Extension(60729, "RExt Data Ext SX", "RExt Data SX", ProbeExt));
        AssertNarrowed(await Send(server, bundle), new[] { "SavesAsXml" }, "PROBE-EXT");
        Write(bundle, "Data.ReportExt.al", Extension(60729, "RExt Data Ext SX", "RExt Data SX"));
        AssertNarrowed(await Send(server, bundle), new[] { "SavesAsXml" }, null);
    }

    [SkippableFact]
    public async Task ChangedReportExtension_SelectsTheTestsThatShowedItsBaseReportsRequestPage()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = SmallBundle("al-runner-server-affected-rext-requestpage", "000000000004",
            ("Page.Report.al", PageReport),
            ("Page.ReportExt.al", PageExtension()),
            ("Tests.Codeunit.al", RequestPageTests));
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.True(new[] { "Unrelated", "ViaRun", "ViaRunRequestPage" }.SequenceEqual(baseline.Ran), baseline.Raw);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        var opened = new[] { "ViaRun", "ViaRunRequestPage" };
        Write(bundle, "Page.ReportExt.al", PageExtension("Changed"));
        AssertNarrowed(await Send(server, bundle), opened, "ext caption was Changed");
        Write(bundle, "Page.ReportExt.al", PageExtension());
        AssertNarrowed(await Send(server, bundle), opened, null);
    }
}
