// ServerAlOutputCacheCompileInputsTests — #5368, the `--server` half: RunBundleForServer reads and
// writes the same AL-output cache entries as the CLI's bundle loop, so it has to check the same
// record of what the compile read. A fresh server process per step, so a HIT is the cache's doing
// and not the process reusing a module it already loaded; `cached` in the summary says which.
// The CLI half and the record's rules: AlOutputCacheCompileInputsTests.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAlOutputCacheCompileInputsTests
{
    private static string Bundle()
    {
        var dir = TestScratch.Dir("al-runner-server-cache-inputs");
        Directory.CreateDirectory(Path.Combine(dir, "Layouts"));
        File.WriteAllText(Path.Combine(dir, "app.json"), """
            { "id": "c5368000-0000-4a11-9111-0000000000b1", "name": "Server Cache Inputs", "publisher": "AL Runner",
              "version": "1.0.0.0", "dependencies": [], "idRanges": [ { "from": 60830, "to": 60839 } ], "runtime": "14.0" }
            """);
        File.WriteAllText(Path.Combine(dir, "Tab.al"), """
            table 60830 "SCI Tab"
            {
                fields { field(1; PK; Integer) { } }
                keys { key(PK; PK) { Clustered = true; } }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "ReportA.al"), """
            report 60831 "SCI Report A"
            {
                DefaultRenderingLayout = L1;
                dataset { dataitem(T; "SCI Tab") { column(PK; PK) { } } }
                rendering { layout(L1) { Type = RDLC; LayoutFile = 'Layouts/A.rdlc'; } }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
            codeunit 60832 "SCI Tests"
            {
                Subtype = Test;

                [Test]
                procedure Arithmetic()
                begin
                    if 1 + 2 <> 3 then
                        Error('arithmetic must work');
                end;
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Layouts", "A.rdlc"), "<Report>v1</Report>");
        return dir;
    }

    private sealed record Observed(bool Cached, bool AllPassed, string Raw);

    private static async Task<Observed> RunOnFreshServer(string cache, string bundle)
    {
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        var request = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var cached = summary.TryGetProperty("cached", out var c) && c.ValueKind == JsonValueKind.True;
        var passed = events.Count > 0 && events.All(e => e.GetProperty("status").GetString() == "pass");
        return new Observed(cached, passed, string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr);
    }

    [SkippableFact]
    public async Task ADeletedLayout_IsNotServedFromTheCache_AndAnEditedOneRecompiles()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle();
        var cache = TestScratch.Dir("al-runner-server-cache-inputs-cache");

        var cold = await RunOnFreshServer(cache, bundle);
        Assert.True(!cold.Cached && cold.AllPassed, cold.Raw);

        var control = await RunOnFreshServer(cache, bundle);
        Assert.True(control.Cached && control.AllPassed, $"an unchanged bundle must be served from the cache: {control.Raw}");

        File.WriteAllText(Path.Combine(bundle, "Layouts", "A.rdlc"), "<Report>v2</Report>");
        var edited = await RunOnFreshServer(cache, bundle);
        Assert.True(!edited.Cached && edited.AllPassed, edited.Raw);

        // The edit republished the entry: the new state is a HIT.
        var again = await RunOnFreshServer(cache, bundle);
        Assert.True(again.Cached && again.AllPassed, again.Raw);

        File.Delete(Path.Combine(bundle, "Layouts", "A.rdlc"));
        var deleted = await RunOnFreshServer(cache, bundle);
        Assert.False(deleted.Cached, deleted.Raw);
        Assert.False(deleted.AllPassed, deleted.Raw);
        Assert.Contains("AL1081", deleted.Raw, StringComparison.Ordinal);
    }
}
