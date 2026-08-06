using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// End-to-end tests for <c>--server</c> mode: the newline-delimited JSON protocol
/// the VS Code extension depends on, plus the warm in-process same-bundle reload
/// (edit a table, re-run in the SAME process, the change must show — not stale).
///
/// These spawn the real runner and need the BC artifact caches present
/// (~/.bcartifacts.cache). When they are absent the tests skip rather than fail,
/// so CI without artifacts stays green; locally they run for real.
/// </summary>
[Collection("server-serial")]
public class ServerTests
{
    // The fixture bundle: a table whose OnInsert trigger reads xRec and a test
    // that asserts the resulting Counter value. Copied to a temp dir per test so
    // edits never touch the repo.
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..",
        "Fixtures", "RecordTriggerXRec"));

    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return false;
        return Directory.Exists(Path.Combine(home, ".bcartifacts.cache", "sandbox"));
    }

    private static string MakeTempBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    // The request carries the bundle dir; the AL-output cache dir is a server-level
    // CLI flag (--cache), not part of the per-request payload.
    private static string Req(string command, string bundleDir)
        => JsonSerializer.Serialize(new
        {
            command,
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
        });

    [Fact]
    public async Task RunTests_Then_EditTable_Then_RunAgain_PicksUpChange()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache (~/.bcartifacts.cache) not present"); return; }

        var bundle = MakeTempBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-tests-cache", Guid.NewGuid().ToString("N"));
        var tablePath = Path.Combine(bundle, "XRecProbe.Table.al");

        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        // ── Phase 1: first run — must PASS (Counter 0 -> 1), cache MISS ──────────
        var r1 = await server.SendAsync(Req("runTests", bundle));
        var d1 = JsonSerializer.Deserialize<JsonElement>(r1);
        Assert.Equal(1, d1.GetProperty("passed").GetInt32());
        Assert.Equal(0, d1.GetProperty("failed").GetInt32());
        Assert.Equal(0, d1.GetProperty("errors").GetInt32());
        Assert.False(d1.GetProperty("cached").GetBoolean());

        // ── Phase 2: re-run with NO edit — must HIT the cache, still PASS ────────
        var r2 = await server.SendAsync(Req("runTests", bundle));
        var d2 = JsonSerializer.Deserialize<JsonElement>(r2);
        Assert.Equal(1, d2.GetProperty("passed").GetInt32());
        Assert.True(d2.GetProperty("cached").GetBoolean(),
            $"second identical run should be a cache hit. Response: {r2}");

        // ── Phase 3: edit ONLY the table trigger (+1 -> +9). The test codeunit is
        //    unchanged and still asserts Counter == '1', so the run MUST now FAIL.
        //    If the runtime metatable / record-type caches are stale, the old
        //    Record type (+1) would run and the test would falsely PASS. ─────────
        var table = await File.ReadAllTextAsync(tablePath);
        var edited = table.Replace("xRec.\"Counter\" + 1", "xRec.\"Counter\" + 9");
        Assert.NotEqual(table, edited); // guard: the substitution actually applied
        await File.WriteAllTextAsync(tablePath, edited);

        var r3 = await server.SendAsync(Req("runTests", bundle));
        var d3 = JsonSerializer.Deserialize<JsonElement>(r3);
        Assert.False(d3.GetProperty("cached").GetBoolean(),
            $"run after an edit must be a cache miss. Response: {r3}");
        Assert.Equal(0, d3.GetProperty("passed").GetInt32());
        Assert.Equal(1, d3.GetProperty("failed").GetInt32());
        // The failure message proves the NEW trigger ran: Counter became 9, not 1.
        var failMsg = d3.GetProperty("tests")[0].GetProperty("message").GetString() ?? "";
        Assert.Contains("9", failMsg);

        // ── Phase 4: shutdown — response then process exit ──────────────────────
        var rs = await server.SendAsync("{\"command\":\"shutdown\"}");
        var ds = JsonSerializer.Deserialize<JsonElement>(rs);
        Assert.Equal("shutting down", ds.GetProperty("status").GetString());
        Assert.True(await server.WaitForExitAsync(TimeSpan.FromSeconds(10)),
            "server did not exit after shutdown");
    }

    // A standalone bundle whose first codeunit's OnRun calls Error(...) — run-mode
    // execute must actually invoke OnRun, so the AL Error text surfaces as a Fail.
    private static string MakeExecuteBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-exec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b2c3d4e5-f607-4809-ab1c-2d3e4f607182",
          "name": "Runner Extras - Server Execute Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60120, "to": 60129 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), """
        codeunit 60120 "Server Execute Probe SX"
        {
            trigger OnRun()
            begin
                Error('executed-onrun-boom');
            end;
        }
        """);
        return dir;
    }

    [Fact]
    public async Task Execute_RunsOnRun_SurfacesAlError()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }
        var bundle = MakeExecuteBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-exec-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var r = await server.SendAsync(Req("execute", bundle));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        // OnRun ran and threw the AL Error → exitCode 1, one failing "OnRun" result
        // whose message carries the AL Error text (proves real execution, not a stub).
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("OnRun", tests[0].GetProperty("name").GetString()!.Split('.').Last());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("executed-onrun-boom", tests[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Execute_InlineCode_NotSupported_ReturnsError()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }
        await using var server = await CliServer.StartAsync();
        var r = await server.SendAsync("{\"command\":\"execute\",\"code\":\"Message('hi');\"}");
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("inline AL", err.GetString());
    }

    // Reproduces #1658: a request naming an app bundle + its separate test-app
    // bundle (the shape --guide recommends) must run BOTH — not silently drop
    // everything after sourcePaths[0]. App exposes a procedure; the test bundle
    // depends on the app and asserts the procedure's return value, so a green
    // result here PROVES the test bundle actually compiled against the app's
    // real symbols, not just that some non-empty test list came back.
    private static string MakeAppTestPair(out string appDir, out string testDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-multi", Guid.NewGuid().ToString("N"));
        appDir = Path.Combine(root, "App");
        testDir = Path.Combine(root, "Test");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(testDir);

        const string appId = "d1e2f3a4-b5c6-4d7e-8f90-a1b2c3d4e5f6";
        File.WriteAllText(Path.Combine(appDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Runner Extras - Server Multi App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60150, "to": 60159 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(appDir, "AppLogic.Codeunit.al"), """
        codeunit 60150 "Server Multi App Logic SX"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "e2f3a4b5-c6d7-4e8f-90a1-b2c3d4e5f6a7",
          "name": "Runner Extras - Server Multi Test",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{appId}}", "name": "Runner Extras - Server Multi App", "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60160, "to": 60169 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "AppLogicTest.Codeunit.al"), """
        codeunit 60160 "Server Multi Test SX"
        {
            Subtype = Test;

            [Test]
            procedure AnswerIs42()
            var
                Logic: Codeunit "Server Multi App Logic SX";
                Result: Integer;
            begin
                Result := Logic.Answer();
                if Result <> 42 then
                    Error('expected the app codeunit''s real answer 42, got %1', Result);
            end;
        }
        """);
        return root;
    }

    [Fact]
    public async Task RunTests_MultipleSourcePaths_RunsAppAndTestBundle()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        MakeAppTestPair(out var appDir, out var testDir);
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-multi-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { appDir, testDir },
            packagePaths = Array.Empty<string>(),
        });
        var r = await server.SendAsync(req, TimeSpan.FromSeconds(180));
        var d = JsonSerializer.Deserialize<JsonElement>(r);

        // Bundle 1 (the app) has zero tests; bundle 2 (the test app) has exactly
        // one. Honouring only sourcePaths[0] would report total == 0, exitCode 0.
        Assert.Equal(1, d.GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("failed").GetInt32());
        Assert.Equal(0, d.GetProperty("errors").GetInt32());
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("AnswerIs42", tests[0].GetProperty("name").GetString()!.Split('.').Last());
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache (~/.bcartifacts.cache) not present"); return; }
        await using var server = await CliServer.StartAsync();
        var r = await server.SendAsync("{\"command\":\"bogus\"}");
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("bogus", err.GetString());
    }
}
