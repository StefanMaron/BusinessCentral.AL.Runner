// #4850: every bundle of one --server request is loaded before any of them runs tests, so a later
// bundle's subscriber is bound when an earlier bundle raises the event, as in a CLI run over one
// root and as on a tenant with every app installed. A bundle outside the request is not installed.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerMultiBundleSubscriberTests
{
    private static readonly Guid AppP = new("3b0e6f52-0000-4c38-9e25-000000004850");
    private static readonly Guid AppS = new("3b0e6f52-0000-4c38-9e25-000000004851");

    private static (string P, string S) WriteFixture(string root)
    {
        var p = WriteApp(Path.Combine(root, "reachP"), AppP, "Reach P", 62830, 62839, null);
        File.WriteAllText(Path.Combine(p, "Pub.al"), """
        codeunit 62830 "Reach Pub"
        {
            procedure Raise(): Text
            var
                Tag: Text;
            begin
                OnReach(Tag);
                exit(Tag);
            end;

            [IntegrationEvent(false, false)]
            local procedure OnReach(var Tag: Text) begin end;
        }
        codeunit 62831 "Reach P Subs"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Reach Pub", 'OnReach', '', false, false)]
            local procedure OnReach(var Tag: Text) begin Tag += 'P'; end;
        }
        codeunit 62832 "Reach P Tests"
        {
            Subtype = Test;
            [Test]
            procedure Probe()
            var
                Pub: Codeunit "Reach Pub";
            begin
                Error('REACHED[%1]', Pub.Raise());
            end;
        }
        """);
        var s = WriteApp(Path.Combine(root, "reachS"), AppS, "Reach S", 62840, 62849, (AppP, "Reach P"));
        File.WriteAllText(Path.Combine(s, "Subs.al"), """
        codeunit 62840 "Reach S Subs"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Reach Pub", 'OnReach', '', false, false)]
            local procedure OnReach(var Tag: Text) begin Tag += 'S'; end;
        }
        codeunit 62841 "Reach S Tests"
        {
            Subtype = Test;
            [Test]
            procedure Probe()
            var
                Pub: Codeunit "Reach Pub";
            begin
                Error('REACHED[%1]', Pub.Raise());
            end;
        }
        """);
        return (p, s);
    }

    private static string WriteApp(string dir, Guid appId, string name, int from, int to, (Guid Id, string Name)? dependsOn)
    {
        Directory.CreateDirectory(dir);
        var deps = dependsOn is { } d
            ? $$"""[ { "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "AL Runner", "version": "1.0.0.0" } ]"""
            : "[]";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "{{appId}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": {{deps}}, "platform": "1.0.0.0", "idRanges": [ { "from": {{from}}, "to": {{to}} } ], "runtime": "14.0" }
        """);
        return dir;
    }

    private static string RunTests(params string[] dirs)
        => JsonSerializer.Serialize(new { command = "runTests", sourcePaths = dirs, packagePaths = Array.Empty<string>() });

    /// <summary>Each probe's subscriber tags, sorted: the probes report through their error message.</summary>
    private static async Task<Dictionary<string, string>> ProbeAsync(CliServer server, string label, params string[] dirs)
    {
        var lines = await server.SendRequestStreamingAsync(RunTests(dirs));
        var (events, _) = ProtocolV2Streaming.Split(lines);
        var reached = new Dictionary<string, string>();
        foreach (var e in events)
        {
            var name = e.GetProperty("name").GetString()!;
            var message = e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var start = message.IndexOf("REACHED[", StringComparison.Ordinal);
            var end = start < 0 ? -1 : message.IndexOf(']', start);
            Assert.True(end > start, $"{label}: {name} did not report its subscribers | " + string.Join(" | ", lines));
            reached[name] = string.Concat(message[(start + 8)..end].Order());
        }
        Assert.True(reached.Count == dirs.Length, $"{label}: expected {dirs.Length} probes | " + string.Join(" | ", lines));
        return reached;
    }

    private static async Task AssertSequence(CliServer server, string p, string s)
    {
        // P runs first (S depends on it), and S's subscriber must already be bound.
        var both = await ProbeAsync(server, "[P,S]", p, s);
        Assert.Equal("PS", both["Codeunit62832.Probe"]);
        Assert.Equal("PS", both["Codeunit62841.Probe"]);

        // S is not part of this request, so it is not installed: its module from the previous
        // request must not answer the event.
        var alone = await ProbeAsync(server, "[P]", p);
        Assert.Equal("P", alone["Codeunit62832.Probe"]);

        // Listed in reverse and after a reload: each subscriber still fires exactly once.
        var again = await ProbeAsync(server, "[S,P]", s, p);
        Assert.Equal("PS", again["Codeunit62832.Probe"]);
        Assert.Equal("PS", again["Codeunit62841.Probe"]);
    }

    [SkippableFact]
    public async Task Server_LaterBundlesSubscriber_SeesAnEarlierBundlesEvent_AndOnlyWhileInTheRequest()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-multi-bundle-subscriber");
        Directory.CreateDirectory(root);
        var (p, s) = WriteFixture(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
        await AssertSequence(server, p, s);
    }

    /// <summary>The same sequence cold, then again in a second server on the same cache root, where
    /// every module is a cache HIT.</summary>
    [SkippableFact]
    public async Task Server_LaterBundlesSubscriber_ColdThenWarmOnOneCacheRoot()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-multi-bundle-subscriber-warm");
        Directory.CreateDirectory(root);
        var (p, s) = WriteFixture(root);
        var cache = TestScratch.Dir("al-runner-server-multi-bundle-subscriber-cache");
        for (var pass = 0; pass < 2; pass++)
        {
            await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
            await AssertSequence(server, p, s);
        }
    }

    /// <summary>#4931: a deferred bundle writes ONE phase-log row, carrying its load and its run.</summary>
    [SkippableFact]
    public async Task Server_MultiBundleRequest_WritesOnePhaseLogRowPerBundle()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-multi-bundle-phaselog");
        Directory.CreateDirectory(root);
        var (p, s) = WriteFixture(root);
        var log = Path.Combine(root, "phases.jsonl");
        await using (var server = await CliServer.StartAsync(new[] { "--no-cache" },
                         extraEnv: new Dictionary<string, string> { ["AL_RUNNER_PHASE_LOG"] = log }))
            await ProbeAsync(server, "[P,S]", p, s);

        var rows = File.ReadAllLines(log).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("kind").GetString() == "bundle").ToList();
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.GetProperty("bundle_index").GetInt32()));
        Assert.All(rows, r =>
        {
            Assert.True(r.GetProperty("emit_ms").GetInt64() > 0, $"load turn missing: {r}");
            Assert.True(r.GetProperty("run_ms").GetInt64() > 0, $"run turn missing: {r}");
        });
    }
}
