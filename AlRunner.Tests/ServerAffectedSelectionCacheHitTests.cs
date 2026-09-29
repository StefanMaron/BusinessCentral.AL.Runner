// ServerAffectedSelectionCacheHitTests — #4971 and #4972: affectedOnly selection when a bundle
// is served from the AL-output cache (`--cache`) instead of being compiled. Every other
// affectedOnly test runs `--no-cache`, which is why neither defect was visible to them.
//
// The invariant under test: the change model reports what changed relative to the code the
// stored per-test coverage was measured on. Mechanism: docs/server-mode.md#affectedonly-and-the-al-output-cache.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionCacheHitTests
{
    private const string HelperOriginal = """
    codeunit 60401 "CacheHit Helper SX"
    {
        procedure Value(): Integer
        begin
            exit(1);
        end;
    }
    """;

    // The probe: every test that calls the helper fails with this exact message, so the set
    // of tests the edited code breaks is known by construction.
    private const string HelperProbe = """
    codeunit 60401 "CacheHit Helper SX"
    {
        procedure Value(): Integer
        begin
            Error('PROBE-4971');
            exit(1);
        end;
    }
    """;

    private static readonly string[] HelperTests = { "UsesHelperOne", "UsesHelperTwo" };

    private const string TestsBody = """
    codeunit 60410 "CacheHit Tests SX"
    {
        Subtype = Test;

        [Test]
        procedure UsesHelperOne()
        var
            H: Codeunit "CacheHit Helper SX";
        begin
            if H.Value() <> 1 then
                Error('UsesHelperOne failed');
        end;

        [Test]
        procedure UsesHelperTwo()
        var
            H: Codeunit "CacheHit Helper SX";
        begin
            if H.Value() + 1 <> 2 then
                Error('UsesHelperTwo failed');
        end;

        [Test]
        procedure Independent()
        begin
            if 2 + 2 <> 4 then
                Error('Independent failed');
        end;
    }
    """;

    private static string SingleBundle(string root, string appIdSuffix)
    {
        var dir = Path.Combine(root, "bundle");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c4971000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "CacheHit Single SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60400, "to": 60419 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Helper.al"), HelperOriginal);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), TestsBody);
        return dir;
    }

    private static (string App, string TestApp) AppAndTestApp(string root, string appIdSuffix)
    {
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "app.json"), $$"""
        {
          "id": "c4971001-0000-4a11-9111-{{appIdSuffix}}",
          "name": "CacheHit App SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60400, "to": 60409 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(app, "Helper.al"), HelperOriginal);

        var testApp = Path.Combine(root, "test-app");
        Directory.CreateDirectory(testApp);
        File.WriteAllText(Path.Combine(testApp, "app.json"), $$"""
        {
          "id": "c4971002-0000-4a11-9222-{{appIdSuffix}}",
          "name": "CacheHit Test App SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "c4971001-0000-4a11-9111-{{appIdSuffix}}", "name": "CacheHit App SX {{appIdSuffix}}",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60410, "to": 60419 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testApp, "Tests.al"), TestsBody);
        return (app, testApp);
    }

    private static string Request(string[] sourcePaths, bool affectedOnly)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths,
            packagePaths = Array.Empty<string>(),
            affectedOnly,
        });

    private sealed record Outcome(Dictionary<string, (string Status, string Message)> Tests, List<JsonElement> Selections, bool Cached, string Raw);

    private static async Task<Outcome> Send(CliServer server, string[] sourcePaths, bool affectedOnly = true)
    {
        var lines = await server.SendRequestStreamingAsync(Request(sourcePaths, affectedOnly));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var tests = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var name = e.GetProperty("name").GetString()!;
            var method = name[(name.LastIndexOf('.') + 1)..];
            var message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : "";
            tests[method] = (e.GetProperty("status").GetString()!, message);
        }
        var selections = new List<JsonElement>();
        if (summary.TryGetProperty("selection", out var s)) selections.Add(s);
        var cached = summary.TryGetProperty("cached", out var c) && c.ValueKind == JsonValueKind.True;
        return new Outcome(tests, selections, cached, string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr);
    }

    /// <summary>The probe is in the loaded code: every helper-calling test must RUN, and fail with it.</summary>
    private static void AssertProbeSelected(Outcome o, string step)
    {
        foreach (var t in HelperTests)
        {
            Assert.True(o.Tests.TryGetValue(t, out var r),
                $"{step}: '{t}' fails on this code (it calls the probed helper) but affectedOnly skipped it: {o.Raw}");
            Assert.True(r.Status is "fail" or "error", $"{step}: '{t}' {r.Status}: {o.Raw}");
            Assert.Contains("PROBE-4971", r.Message, StringComparison.Ordinal);
        }
    }

    private static void AssertAllPass(Outcome o, string step)
    {
        foreach (var (name, r) in o.Tests)
            Assert.True(r.Status == "pass", $"{step}: '{name}' {r.Status} {r.Message}: {o.Raw}");
    }

    private static async Task EditRevertReEdit(CliServer server, string[] sourcePaths, string helperPath, int rounds)
    {
        var first = await Send(server, sourcePaths);
        Assert.Equal(3, first.Tests.Count);
        AssertAllPass(first, "initial");

        for (var round = 1; round <= rounds; round++)
        {
            File.WriteAllText(helperPath, HelperProbe);
            AssertProbeSelected(await Send(server, sourcePaths), $"round {round} edit");

            File.WriteAllText(helperPath, HelperOriginal);
            var reverted = await Send(server, sourcePaths);
            AssertAllPass(reverted, $"round {round} revert");
            foreach (var t in HelperTests)
                Assert.True(reverted.Tests.ContainsKey(t),
                    $"round {round} revert: '{t}' failed on the previous request and must rerun: {reverted.Raw}");

            File.WriteAllText(helperPath, HelperProbe);
            AssertProbeSelected(await Send(server, sourcePaths), $"round {round} re-edit");

            File.WriteAllText(helperPath, HelperOriginal);
            AssertAllPass(await Send(server, sourcePaths), $"round {round} final revert");
        }
    }

    /// <summary>#4971, one bundle: edit → revert → re-edit, twice, so every compile after the first is a cache HIT.</summary>
    [SkippableFact]
    public async Task AffectedOnly_EditRevertReEdit_WithCache_SingleBundle_SelectsBrokenTests()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-affected-cachehit");
        var bundle = SingleBundle(root, "000000000001");
        var cache = TestScratch.Dir("al-runner-server-affected-cachehit-cache");
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        await EditRevertReEdit(server, new[] { bundle }, Path.Combine(bundle, "Helper.al"), rounds: 2);
    }

    /// <summary>#4971, the reported shape: the probed helper lives in the app, the tests in the test app.</summary>
    [SkippableFact]
    public async Task AffectedOnly_EditRevertReEdit_WithCache_AppAndTestApp_SelectsBrokenTests()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-affected-cachehit-multi");
        var (app, testApp) = AppAndTestApp(root, "000000000002");
        var cache = TestScratch.Dir("al-runner-server-affected-cachehit-multi-cache");
        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        await EditRevertReEdit(server, new[] { app, testApp }, Path.Combine(app, "Helper.al"), rounds: 2);
    }

    /// <summary>
    /// #4971's sibling on a second write path: a request WITHOUT affectedOnly recompiles the
    /// bundle, which moves the change model's baseline but records no coverage. The next
    /// affectedOnly request must not compare against the moved baseline with the old coverage.
    /// </summary>
    [SkippableFact]
    public async Task AffectedOnly_AfterAPlainRequestCompiledAnEdit_SelectsBrokenTests()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-affected-plain-between");
        var bundle = SingleBundle(root, "000000000003");
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
        var paths = new[] { bundle };

        AssertAllPass(await Send(server, paths), "initial");
        File.WriteAllText(Path.Combine(bundle, "Helper.al"), HelperProbe);
        var plain = await Send(server, paths, affectedOnly: false);
        Assert.Equal(3, plain.Tests.Count);

        var affected = await Send(server, paths);
        AssertProbeSelected(affected, "affectedOnly after a plain request");
    }

    /// <summary>
    /// #4972: a server started on a cache that already holds the bundle must still narrow from
    /// its second request, and narrow correctly after an edit.
    /// </summary>
    [SkippableFact]
    public async Task AffectedOnly_ServerStartedOnWarmCache_NarrowsFromSecondRequest()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-affected-warmcache");
        var (app, testApp) = AppAndTestApp(root, "000000000004");
        var paths = new[] { app, testApp };
        var cache = TestScratch.Dir("al-runner-server-affected-warmcache-cache");

        await using (var warmer = await CliServer.StartAsync(new[] { "--cache", cache }))
            AssertAllPass(await Send(warmer, paths), "warm-up process");

        await using var server = await CliServer.StartAsync(new[] { "--cache", cache });
        var first = await Send(server, paths);
        Assert.Equal(3, first.Tests.Count);
        AssertAllPass(first, "first request on the warm cache");

        var second = await Send(server, paths);
        var sel = Assert.Single(second.Selections);
        Assert.False(sel.GetProperty("forcedFull").GetBoolean(),
            $"an unchanged second request must narrow, not force a full run: {second.Raw}");
        Assert.True(sel.GetProperty("skipped").GetInt32() > 0, $"nothing changed, so tests must be skipped: {second.Raw}");
        // The first request compiled and so re-established the baseline; the unchanged second one is a HIT again.
        Assert.True(second.Cached, $"an unchanged request after the baseline exists must be served from the cache: {second.Raw}");

        File.WriteAllText(Path.Combine(app, "Helper.al"), HelperProbe);
        var edited = await Send(server, paths);
        AssertProbeSelected(edited, "edit after warm start");
        Assert.False(edited.Tests.ContainsKey("Independent"),
            $"the test that does not call the helper must be skipped: {edited.Raw}");
    }
}
