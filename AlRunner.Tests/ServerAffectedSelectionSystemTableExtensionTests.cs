// ServerAffectedSelectionSystemTableExtensionTests — #5454: an extension of a report or page whose
// base object lists a SYSTEM table (Integer) is recompiled by the incremental emit instead of
// falling back to a full compile. The base object's data item names its table as `#<appid>#Integer`,
// which BC resolves through the packaged module's dependencies (RadSelfBaselineLoader.GetDependencies).
// The control is the same edit on a user table: ServerAffectedSelectionReportExtensionTests.
// Runs under --isolation test, like ServerAffectedSelectionObjectKindTests: it asserts a narrowed run.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionSystemTableExtensionTests
{
    private const string Report = """
        report 60772 "SysExt Report SX"
        {
            ProcessingOnly = true;
            dataset
            {
                dataitem(Int; Integer)
                {
                    DataItemTableView = where(Number = const(1));
                    column(Num; Number) { }
                }
            }
        }
        """;

    private static string ReportExtension(string extraColumn = "", int compared = 1)
        => "reportextension 60773 \"SysExt Report Ext SX\" extends \"SysExt Report SX\"\n{\n"
           + "    dataset\n    {\n        add(Int)\n        {\n            column(Num2; Number) { }\n" + extraColumn
           + "        }\n        modify(Int)\n        {\n            trigger OnAfterAfterGetRecord()\n            begin\n"
           + $"                if Number = {compared} then;\n            end;\n        }}\n    }}\n}}\n";

    private const string Page = """
        page 60774 "SysExt Page SX"
        {
            PageType = List;
            SourceTable = Integer;
            layout { area(Content) { repeater(R) { field(Num; Rec.Number) { ApplicationArea = All; } } } }
        }
        """;

    private static string PageExtension(string extraField = "")
        => "pageextension 60775 \"SysExt Page Ext SX\" extends \"SysExt Page SX\"\n{\n"
           + "    layout { addlast(R) { field(Num2; Rec.Number) { ApplicationArea = All; }\n" + extraField + "} }\n}\n";

    private const string Tests = """
        codeunit 60776 "SysExt Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure RunsReport()
            begin
                Report.Run(Report::"SysExt Report SX", false, false);
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;
        }
        """;

    private static readonly string[] All = { "RunsReport", "Unrelated" };

    private static string Bundle()
    {
        var dir = TestScratch.Dir("al-runner-server-affected-systable-ext");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c5454000-0000-4a11-9111-000000000001",
          "name": "System Table Extension SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60772, "to": 60779 } ],
          "runtime": "14.0"
        }
        """);
        Write(dir, "Report.Report.al", Report);
        Write(dir, "ReportExt.ReportExt.al", ReportExtension());
        Write(dir, "Page.Page.al", Page);
        Write(dir, "PageExt.PageExt.al", PageExtension());
        Write(dir, "Tests.Codeunit.al", Tests);
        return dir;
    }

    private static void Write(string bundle, string file, string source)
        => File.WriteAllText(Path.Combine(bundle, file), source);

    private sealed record Observed(string[] Ran, Dictionary<string, string> Status, bool ForcedFull, string? Reason, string Raw);

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
        return new Observed(status.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), status,
            selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            raw);
    }

    // Not forced full means the incremental emit took the edit: its fallback reports the throw as the reason.
    // The steps are collected rather than asserted one by one, so a regression names every edit that fell back.
    private static string? Problem(string step, Observed o, string[] ran)
        => !o.ForcedFull && o.Reason == null && ran.SequenceEqual(o.Ran) && o.Status.Values.All(v => v == "pass")
            ? null
            : $"{step}: forcedFull={o.ForcedFull} reason={o.Reason} ran=[{string.Join(",", o.Ran)}] raw={o.Raw}";

    [SkippableFact]
    public async Task ExtensionEditsOnASystemTableDataItem_AreEmittedIncrementally()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle();
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(All, baseline.Ran);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        var problems = new List<string?>();

        // A column the reportextension adds on the Integer data item (the issue's shape).
        Write(bundle, "ReportExt.ReportExt.al", ReportExtension("            column(Num3; Number) { }\n"));
        problems.Add(Problem("reportextension column", await Send(server, bundle), new[] { "RunsReport" }));

        // The same reportextension's modify() trigger reads a field of the Integer data item.
        Write(bundle, "ReportExt.ReportExt.al", ReportExtension("            column(Num3; Number) { }\n", compared: 2));
        problems.Add(Problem("reportextension trigger", await Send(server, bundle), new[] { "RunsReport" }));

        // A field a pageextension adds to a page whose source table is Integer. No test opens the page.
        Write(bundle, "PageExt.PageExt.al", PageExtension("field(Num3; Rec.Number) { ApplicationArea = All; }\n"));
        problems.Add(Problem("pageextension field", await Send(server, bundle), Array.Empty<string>()));

        Assert.True(problems.All(p => p == null), string.Join("\n", problems.Where(p => p != null)));
    }
}
