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

    // #1640/#2074 (second slice; --coverage was the first, #1922): 'captureValues:true'
    // on `execute` reports ONE entry per statement EXECUTION that changed a top-level
    // OnRun local's value, in execution order — not a single end-of-test snapshot (see
    // AlValueCapture's file header for the #2074 redesign). Four assignment statements,
    // two variables, no loop: Counter is assigned twice (41, then 42) and Msg is assigned
    // twice ('before', then 'after'), interleaved. Asserting the FULL ordered series
    // (not just each variable's final value) is what proves this is a per-execution
    // series and not a snapshot — a snapshot-based implementation would collapse this to
    // two entries, both attributed to statement 3 (see AlStatementTableTests's corollary
    // fix for the OLD, coarser attribution this replaces).
    [SkippableFact]
    public async Task Execute_CaptureValues_True_ReportsOneEntryPerStatementExecutionInOrder()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            captureValues = true,
            code = "codeunit 60171 \"CV Capture SX\" { trigger OnRun() var Counter: Integer; " +
                   "Msg: Text; begin Counter := 41; Msg := 'before'; Counter := 42; " +
                   "Msg := 'after'; end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("pass", tests[0].GetProperty("status").GetString());

        Assert.True(tests[0].TryGetProperty("capturedValues", out var captured),
            $"expected capturedValues on the response: {r}");
        var entries = captured.EnumerateArray()
            .Select(e => (
                Name: e.GetProperty("variableName").GetString(),
                Value: e.GetProperty("value"),
                StatementId: e.GetProperty("statementId").GetInt32(),
                ScopeName: e.GetProperty("scopeName").GetString()))
            .ToList();

        // The whole point of #2074: FOUR executions, not two collapsed final values —
        // Counter's FIRST assignment (41) is now visible, which the old single-snapshot
        // shape could never report.
        Assert.Equal(4, entries.Count);
        Assert.Equal(new[] { "Counter", "Msg", "Counter", "Msg" }, entries.Select(e => e.Name).ToArray());
        Assert.Equal(41, entries[0].Value.GetInt32());
        Assert.Equal("before", entries[1].Value.GetString());
        Assert.Equal(42, entries[2].Value.GetInt32());
        Assert.Equal("after", entries[3].Value.GetString());
        Assert.All(entries, e => Assert.Equal("OnRun", e.ScopeName));

        // statementId is attributed to the ACTUAL producing statement (0-based, 4
        // statements total) — Counter's SECOND assignment is statement 2, NOT the
        // scope's last statement (3), which is what the pre-#2074 design always
        // reported regardless of which variable or which assignment produced it.
        Assert.Equal(0, entries[0].StatementId);
        Assert.Equal(1, entries[1].StatementId);
        Assert.Equal(2, entries[2].StatementId);
        Assert.Equal(3, entries[3].StatementId);
    }

    // Negative direction: the SAME codeunit shape, but captureValues omitted (false by
    // default). capturedValues must be ABSENT — not an empty array, not present with
    // stale/default data — proving the flag actually gates the feature rather than it
    // being always-on (which the positive test alone could not distinguish).
    [SkippableFact]
    public async Task Execute_CaptureValues_Omitted_NoCapturedValuesField()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 60172 \"CV NoCapture SX\" { trigger OnRun() var Counter: Integer; " +
                   "begin Counter := 42; end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("pass", tests[0].GetProperty("status").GetString());
        Assert.False(tests[0].TryGetProperty("capturedValues", out _),
            $"capturedValues must be absent when captureValues wasn't requested: {r}");
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

    // #1931: the old classifier only matched `trimmed.StartsWith("codeunit"/
    // "table")`, so a `page`/`enum`/`report` (or any other AL object type) fell
    // through to the bare-statement branch and got nested INSIDE a scratch
    // codeunit's OnRun trigger body — invalid AL, a syntax error pointing at the
    // wrapper. Each fact below pairs the object type under test with a companion
    // codeunit in the SAME inline `code` string (BC's own parser handles
    // multiple objects per file — see ParseObjectText probe backing this PR).
    // Under the fix, IsFullAlObjectDeclaration recognises the whole text as
    // object declarations via BC's own parser (not a keyword list) and uses it
    // verbatim, so both objects compile as siblings and RunFirstCodeunitOnRun
    // finds and runs the companion's OnRun — a distinct computed value in the
    // failure message is proof the trigger genuinely ran, not that compilation
    // merely didn't error.
    [SkippableFact]
    public async Task Execute_InlineCode_FullPageDefinition_CompanionCodeunitRuns()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "page 60180 \"Inline Full Page SX\" { layout { area(content) { } } } " +
                   "codeunit 60181 \"Inline Full Page Companion SX\" { trigger OnRun() begin " +
                   "Error('page-companion-ran %1', 200 + 1); end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.False(d.TryGetProperty("compilationErrors", out _), $"unexpected compile error: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("page-companion-ran 201", tests[0].GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task Execute_InlineCode_FullEnumDefinition_CompanionCodeunitRuns()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "enum 60182 \"Inline Full Enum SX\" { value(0; A) { } } " +
                   "codeunit 60183 \"Inline Full Enum Companion SX\" { trigger OnRun() begin " +
                   "Error('enum-companion-ran %1', 300 + 1); end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.False(d.TryGetProperty("compilationErrors", out _), $"unexpected compile error: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("enum-companion-ran 301", tests[0].GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task Execute_InlineCode_FullReportDefinition_CompanionCodeunitRuns()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "report 60184 \"Inline Full Report SX\" { dataset { } } " +
                   // #2152: this codeunit name was 32 characters ("...Companion SX") — over
                   // AL's 30-character object-identifier limit (AL0305) — invalid AL real BC
                   // has always rejected. It compiled here regardless because --server's
                   // compile path (unlike the default bundled CLI path #2154 already fixed)
                   // did not check post-emit AL error diagnostics; #2152 closed that gap and
                   // this surfaced immediately. Shortened, not worked around.
                   "codeunit 60185 \"Inline Report Companion SX\" { trigger OnRun() begin " +
                   "Error('report-companion-ran %1', 400 + 1); end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.False(d.TryGetProperty("compilationErrors", out _), $"unexpected compile error: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("report-companion-ran 401", tests[0].GetProperty("message").GetString());
    }

    // #1931: `TrimStart()` leaves a leading `//` comment in place, so the old
    // `StartsWith("codeunit")` check never matched a codeunit preceded by one —
    // it silently fell through to wrapping, nesting a full object declaration
    // inside a trigger body. BC's own parser treats the comment as leading
    // trivia on the `codeunit` token, so IsFullAlObjectDeclaration recognises it
    // regardless. The computed failure value again proves real execution.
    [SkippableFact]
    public async Task Execute_InlineCode_CommentPrefixedCodeunit_UsedVerbatim()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "// setup note the caller left on their inline snippet\n" +
                   "codeunit 60186 \"Inline Comment Prefixed CU SX\" { trigger OnRun() begin " +
                   "Error('comment-prefixed-ran %1', 500 + 1); end; }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected error response: {r}");
        Assert.False(d.TryGetProperty("compilationErrors", out _), $"unexpected compile error: {r}");
        Assert.Equal(1, d.GetProperty("exitCode").GetInt32());
        var tests = d.GetProperty("tests");
        Assert.Equal(1, tests.GetArrayLength());
        Assert.Equal("fail", tests[0].GetProperty("status").GetString());
        Assert.Contains("comment-prefixed-ran 501", tests[0].GetProperty("message").GetString());
    }

    // #1931 negative: a malformed-but-recognisable object (here a `report`
    // whose dataset references a table that doesn't exist) is still used
    // verbatim, so the compile diagnostic that comes back is the REAL semantic
    // error about the caller's actual object — it names the caller's own
    // undeclared table text — rather than a generic "unexpected token 'report'"
    // syntax error a mis-wrap into the scratch OnRun body would have produced.
    // That distinguishes "recognised as an object, then failed to compile" from
    // "not recognised, wrapped, then failed to compile for an unrelated reason".
    [SkippableFact]
    public async Task Execute_InlineCode_MalformedObject_CompileErrorNamesRealProblem()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();
        var r = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "report 60187 \"Inline Malformed Report SX\" { dataset { " +
                   "dataitem(NoSuchTable; \"This Table Does Not Exist SX\") { } } }"
        }));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.False(d.TryGetProperty("error", out _), $"unexpected top-level error response: {r}");
        Assert.NotEqual(0, d.GetProperty("exitCode").GetInt32());
        Assert.True(d.TryGetProperty("compilationErrors", out var compileErrors),
            $"expected compilationErrors on a compile failure: {r}");
        Assert.True(compileErrors.GetArrayLength() > 0);
        var allErrorText = string.Join(" | ", compileErrors.EnumerateArray()
            .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())));
        // Positive: the real semantic diagnostic — the caller's own undeclared
        // table name — is present, so the caller can actually see their bug.
        Assert.Contains("This Table Does Not Exist SX", allErrorText);
        // Negative (the actual RED/GREEN discriminator): a mis-wrap nests the
        // report's `report`/`dataset`/`dataitem` keywords as bare-expression
        // statements inside the scratch OnRun body, and once that body's closing
        // `end;`/`}` is reached the leftover text triggers a FRESH top-level
        // object parse attempt — which fails with AL0198 ("expected one of the
        // application object keywords"). That code never appears when the report
        // is recognised and compiled verbatim, because it is a syntactically
        // complete, single object with nothing left over to mis-parse. Its
        // presence here would mean classification took the wrong branch.
        Assert.DoesNotContain("AL0198", allErrorText);
        var tests = d.GetProperty("tests");
        Assert.Equal(0, tests.GetArrayLength());
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
    //
    // #2152: the App bundle's object ID range was 60150-60159, which collides on
    // the ACTUAL object id (60150) with RecordTriggerXRec's own fixture ("xRec
    // Assert RXT", codeunit 60150) — both loaded into the SAME shared server
    // process across different requests in this class. BC's process-global
    // metadata registry reports the second declaration as AL0264 ("already
    // declared by the extension ..."), a real diagnostic that was already firing
    // and being silently ignored before --server checked post-emit AL error
    // diagnostics; #2152 made it a hard failure and surfaced this pre-existing ID
    // collision immediately. Moved to 60350-60359, well clear of every other
    // idRange in this file (60100-60199, 60120-60129, 60160-60169).
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
          "idRanges": [ { "from": 60350, "to": 60359 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(appDir, "AppLogic.Codeunit.al"), """
        codeunit 60350 "Server Multi App Logic SX"
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
