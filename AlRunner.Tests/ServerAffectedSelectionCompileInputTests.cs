// ServerAffectedSelectionCompileInputTests — #5087: under affectedOnly, a changed layout file selects
// the tests that built its report, and a deleted one is a compile failure as it is on a cold run.
// The change model hashed only `*.al`, so an edit to a layout file alone selected nothing.
// Rules and the population: docs/server-mode.md#affectedonly-and-files-the-compile-reads.
// Runs under --isolation test: these assert per-test narrowing inside one codeunit, which the
// default Codeunit isolation widens to the whole codeunit.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionCompileInputTests
{
    private const string TabSource = """
        table 60801 "CI Tab SX"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private const string ReportASource = """
        report 60802 "CI Report A SX"
        {
            DefaultRenderingLayout = L1;
            dataset { dataitem(T; "CI Tab SX") { column(PK; PK) { } } }
            rendering { layout(L1) { Type = RDLC; LayoutFile = 'Layouts/A.rdlc'; } }
        }
        """;

    private const string ReportBSource = """
        report 60803 "CI Report B SX"
        {
            DefaultRenderingLayout = L1;
            dataset { dataitem(T; "CI Tab SX") { column(PK; PK) { } } }
            rendering { layout(L1) { Type = Word; LayoutFile = 'Layouts/B.docx'; } }
        }
        """;

    private const string TestsSource = """
        codeunit 60810 "CI Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure BuildsReportA()
            var
                R: Report "CI Report A SX";
            begin
                R.UseRequestPage(false);
                if R.UseRequestPage() then
                    Error('BuildsReportA failed');
            end;

            [Test]
            procedure BuildsReportB()
            var
                R: Report "CI Report B SX";
            begin
                R.UseRequestPage(false);
                if R.UseRequestPage() then
                    Error('BuildsReportB failed');
            end;

            [Test]
            procedure Unrelated()
            begin
                if 1 + 1 <> 2 then
                    Error('Unrelated failed');
            end;
        }
        """;

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(Path.Combine(dir, "Layouts"));
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5087000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Compile Input SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60801, "to": 60819 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tab.al"), TabSource);
        File.WriteAllText(Path.Combine(dir, "ReportA.al"), ReportASource);
        File.WriteAllText(Path.Combine(dir, "ReportB.al"), ReportBSource);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), TestsSource);
        File.WriteAllText(Path.Combine(dir, "Layouts", "A.rdlc"), "<Report>A v1</Report>");
        File.WriteAllText(Path.Combine(dir, "Layouts", "B.docx"), "B v1");
        return dir;
    }

    private sealed record Observed(Dictionary<string, string> Status, bool ForcedFull, string? Reason, string[] Changed, string Raw)
    {
        public string[] Ran => Status.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

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
        var status = events.ToDictionary(
            e => e.GetProperty("name").GetString()!.Split('.').Last(), e => e.GetProperty("status").GetString()!,
            StringComparer.Ordinal);
        if (!summary.TryGetProperty("selection", out var selection))
            return new Observed(status, false, null, Array.Empty<string>(), raw);
        return new Observed(status, selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            selection.GetProperty("changedObjects").EnumerateArray().Select(c => c.GetString()!).ToArray(), raw);
    }

    [SkippableFact]
    public async Task EditedLayoutFile_SelectsTheTestsThatBuiltItsReport_AndDeletedOneFailsTheCompile()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-compile-input", "000000000001");
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--no-cache" });

        var baseline = await Send(server, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);
        Assert.Equal(new[] { "BuildsReportA", "BuildsReportB", "Unrelated" }, baseline.Ran);
        Assert.All(baseline.Status.Values, s => Assert.Equal("pass", s));

        // Nothing edited: nothing runs, so a selection below is the edit's doing.
        var unchanged = await Send(server, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.True(unchanged.Ran.Length == 0, unchanged.Raw);

        File.WriteAllText(Path.Combine(bundle, "Layouts", "A.rdlc"), "<Report>A v2</Report>");
        var rdlc = await Send(server, bundle);
        Assert.False(rdlc.ForcedFull, rdlc.Raw);
        Assert.Equal(new[] { "Report 60802 CI Report A SX" }, rdlc.Changed);
        Assert.Contains("BuildsReportA", rdlc.Ran);
        Assert.DoesNotContain("Unrelated", rdlc.Ran);

        File.WriteAllText(Path.Combine(bundle, "Layouts", "B.docx"), "B v2");
        var word = await Send(server, bundle);
        Assert.False(word.ForcedFull, word.Raw);
        Assert.Equal(new[] { "Report 60803 CI Report B SX" }, word.Changed);
        Assert.Contains("BuildsReportB", word.Ran);
        Assert.DoesNotContain("Unrelated", word.Ran);

        // A cold compile of this tree refuses it (AL1081, a layout the report names is missing).
        File.Delete(Path.Combine(bundle, "Layouts", "A.rdlc"));
        var deleted = await Send(server, bundle);
        Assert.DoesNotContain("\"status\":\"pass\"", deleted.Raw, StringComparison.Ordinal);
        Assert.Contains("AL1081", deleted.Raw, StringComparison.Ordinal);
    }

    private static async Task<Observed> SendFresh(string cache, string bundle)
    {
        await using var server = await CliServer.StartAsync(new[] { "--isolation", "test", "--cache", cache });
        return await Send(server, bundle);
    }

    // #4979's path: an edit made while no server runs, so only the persisted baseline can name it.
    [SkippableFact]
    public async Task NextServer_LayoutEditedWhileNoServerRan_RunsEverythingAndSaysWhichFileChanged()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-affected-compile-input-persist", "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-compile-input-persist-cache");

        var baseline = await SendFresh(cache, bundle);
        Assert.True(baseline.ForcedFull, baseline.Raw);

        var unchanged = await SendFresh(cache, bundle);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.True(unchanged.Ran.Length == 0, unchanged.Raw);

        File.WriteAllText(Path.Combine(bundle, "Layouts", "A.rdlc"), "<Report>A v2</Report>");
        var edited = await SendFresh(cache, bundle);
        Assert.True(edited.ForcedFull, edited.Raw);
        Assert.Equal(new[] { "BuildsReportA", "BuildsReportB", "Unrelated" }, edited.Ran);
        Assert.Contains("reads changed", edited.Reason, StringComparison.Ordinal);
        Assert.Contains("A.rdlc", edited.Reason, StringComparison.Ordinal);
    }
}
