using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// End-to-end tests for <c>--server</c> mode: the newline-delimited JSON protocol
/// the VS Code extension depends on, plus the warm in-process same-bundle reload
/// (edit a table, re-run in the SAME process, the change must show — not stale).
///
/// These spawn the real runner and need BC artifacts provisioned; see TestArtifacts
/// for where those live and why the gate is shared. When artifacts are absent the
/// tests report Skipped with a reason — not Passed, which is what the old private
/// gate produced on every CI run because it probed a path CI never creates.
///
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
///
/// #1804: four of the five facts share ONE server process via SharedCliServer.
/// <see cref="Shutdown_RespondsThenProcessExits"/> is the deliberate exception:
/// it exists specifically to prove the shutdown protocol command tears the
/// process down, so it cannot use the shared instance (doing so would kill the
/// process the other facts in this class still need) and keeps its own
/// dedicated <c>CliServer.StartAsync</c> call. It used to be the tail end of
/// <see cref="RunTests_Then_EditTable_Then_RunAgain_PicksUpChange"/>, which
/// combined a cache-behaviour claim and a process-lifecycle claim in one
/// method; splitting them is what makes the cache-behaviour half safe to move
/// onto a server other facts also use.
///
/// Condition (c) of SharedCliServer's doc comment (distinct AppId per call
/// site sharing this process — see its comment for why a shared AppId is
/// unsafe via <c>DependencyLoader.TryGetByAppId</c>'s cross-request reuse):
/// verified, not just assumed. Each bundle generator's AppId here
/// (<c>MakeTempBundle</c>'s fixture, <c>MakeExecuteBundle</c>,
/// <c>MakeAppTestPair</c>'s two apps) is used by exactly ONE fact in this
/// class, so no two facts sharing this server ever present the same AppId at
/// two different SourcePaths. <c>RunTests_Then_EditTable_Then_RunAgain_PicksUpChange</c>'s
/// three requests reuse the SAME bundle/SourcePath repeatedly on purpose —
/// TryGetByAppId's own same-SourcePath carve-out means that is never treated
/// as a reuse, it is the edit-and-rerun contract the test exists to prove.
/// </summary>
public class ServerTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerTests(SharedCliServer fixture) => _fixture = fixture;

    // The fixture bundle: a table whose OnInsert trigger reads xRec and a test
    // that asserts the resulting Counter value. Copied to a temp dir per test so
    // edits never touch the repo.
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..",
        "Fixtures", "RecordTriggerXRec"));

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

    [SkippableFact]
    public async Task RunTests_Then_EditTable_Then_RunAgain_PicksUpChange()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeTempBundle();
        var tablePath = Path.Combine(bundle, "XRecProbe.Table.al");

        var server = await _fixture.GetAsync();

        // ── Phase 1: first run — must PASS (Counter 0 -> 1), cache MISS ──────────
        var lines1 = await server.SendRequestStreamingAsync(Req("runTests", bundle));
        var (_, d1) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(1, d1.GetProperty("passed").GetInt32());
        Assert.Equal(0, d1.GetProperty("failed").GetInt32());
        Assert.Equal(0, d1.GetProperty("errors").GetInt32());
        Assert.False(d1.GetProperty("cached").GetBoolean());

        // ── Phase 2: re-run with NO edit — must HIT the cache, still PASS ────────
        var lines2 = await server.SendRequestStreamingAsync(Req("runTests", bundle));
        var (_, d2) = ProtocolV2Streaming.Split(lines2);
        Assert.Equal(1, d2.GetProperty("passed").GetInt32());
        Assert.True(d2.GetProperty("cached").GetBoolean(),
            $"second identical run should be a cache hit. Lines: {string.Join(" | ", lines2)}");

        // ── Phase 3: edit ONLY the table trigger (+1 -> +9). The test codeunit is
        //    unchanged and still asserts Counter == '1', so the run MUST now FAIL.
        //    If the runtime metatable / record-type caches are stale, the old
        //    Record type (+1) would run and the test would falsely PASS. ─────────
        var table = await File.ReadAllTextAsync(tablePath);
        var edited = table.Replace("xRec.\"Counter\" + 1", "xRec.\"Counter\" + 9");
        Assert.NotEqual(table, edited); // guard: the substitution actually applied
        await File.WriteAllTextAsync(tablePath, edited);

        var lines3 = await server.SendRequestStreamingAsync(Req("runTests", bundle));
        var (events3, d3) = ProtocolV2Streaming.Split(lines3);
        Assert.False(d3.GetProperty("cached").GetBoolean(),
            $"run after an edit must be a cache miss. Lines: {string.Join(" | ", lines3)}");
        Assert.Equal(0, d3.GetProperty("passed").GetInt32());
        Assert.Equal(1, d3.GetProperty("failed").GetInt32());
        // The failure message proves the NEW trigger ran: Counter became 9, not 1.
        var failMsg = events3[0].GetProperty("message").GetString() ?? "";
        Assert.Contains("9", failMsg);
    }

    // Split out of RunTests_Then_EditTable_Then_RunAgain_PicksUpChange (#1804):
    // that test used to end with this same shutdown check, which meant it could
    // never share a process with the class's other facts (shutting the server
    // down would break whichever fact ran after it). This owns its own
    // dedicated server precisely BECAUSE killing it is the point.
    [SkippableFact]
    public async Task Shutdown_RespondsThenProcessExits()
    {
        TestArtifacts.SkipIfMissing();

        await using var server = await CliServer.StartAsync();
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

    [SkippableFact]
    public async Task Execute_RunsOnRun_SurfacesAlError()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = MakeExecuteBundle();
        var server = await _fixture.GetAsync();

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

    // #1917: 'captureValues' still needs the Cecil instrumentation pass tracked
    // on #1640 — that half of v1 parity stays a loud, structured error.
    [SkippableFact]
    public async Task Execute_CaptureValues_NotSupported_ReturnsError()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(
            "{\"command\":\"execute\",\"code\":\"Message('hi');\",\"captureValues\":true}");
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("captureValues", err.GetString());
    }

    // #1917: inline `code` that is a bare statement list (no leading `codeunit`/
    // `table`) is wrapped in a scratch OnRun trigger and actually executed — the
    // failure message below carries a value AL computed (6 * 7), which a stub
    // that always answered "no error" or always answered the same fixed string
    // could not reproduce. This is the "positive" half of the compile path: the
    // codeunit compiles and its trigger genuinely runs.
    [SkippableFact]
    public async Task Execute_InlineCode_BareStatements_ComputesRealValue()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "Error('computed %1', 6 * 7);"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("computed 42", tests[0].GetProperty("message").GetString());
    }

    // A bare statement list with no error at all must compile+run to a genuine
    // pass — proves the "no exception" path isn't itself a swallowed failure.
    [SkippableFact]
    public async Task Execute_InlineCode_BareStatements_PassesWhenNoError()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "if 6 * 7 <> 42 then Error('math is broken');"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("pass", tests[0].GetProperty("status").GetString());
    }

    // #1917: inline `code` that is already a full AL object definition (starts
    // with "codeunit") is used verbatim, not double-wrapped — matches v1's CLI
    // `-e` shape (fixes #12).
    [SkippableFact]
    public async Task Execute_InlineCode_FullObjectDefinition_UsedVerbatim()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 60170 \"Inline Full Object SX\" { trigger OnRun() begin " +
                   "Error('full-object %1', 100 + 1); end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("full-object 101", tests[0].GetProperty("message").GetString());
    }

    // Negative: inline code that fails to COMPILE must surface a real compiler
    // diagnostic, not be silently swallowed into a false "success".
    [SkippableFact]
    public async Task Execute_InlineCode_CompileError_ReturnsCompilationErrors()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            // UndeclaredInlineVariable is never declared — a real AL compile error.
            code = "if UndeclaredInlineVariable then Error('unreachable');"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected top-level error response: {r}");
        Assert.NotEqual(0, d.GetProperty("exitCode").GetInt32());
        Assert.True(d.TryGetProperty("compilationErrors", out var compileErrors),
            $"expected compilationErrors on a compile failure: {r}");
        Assert.True(compileErrors.GetArrayLength() > 0);
        var tests = d.GetProperty("tests");
        Assert.Equal(0, tests.GetArrayLength());
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

    [SkippableFact]
    public async Task RunTests_MultipleSourcePaths_RunsAppAndTestBundle()
    {
        TestArtifacts.SkipIfMissing();

        MakeAppTestPair(out var appDir, out var testDir);
        var server = await _fixture.GetAsync();

        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { appDir, testDir },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(180));
        var (events, d) = ProtocolV2Streaming.Split(lines);

        // Bundle 1 (the app) has zero tests; bundle 2 (the test app) has exactly
        // one. Honouring only sourcePaths[0] would report total == 0, exitCode 0.
        Assert.Equal(1, d.GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("failed").GetInt32());
        Assert.Equal(0, d.GetProperty("errors").GetInt32());
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        Assert.Single(events);
        Assert.Equal("AnswerIs42", events[0].GetProperty("name").GetString()!.Split('.').Last());
    }

    [SkippableFact]
    public async Task UnknownCommand_ReturnsError()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync("{\"command\":\"bogus\"}");
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("bogus", err.GetString());
    }
}
