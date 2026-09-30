using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4952: a <c>runTests</c>/<c>execute</c> request field the server does not read used to be
/// dropped by the deserializer, so a client asking for something the protocol cannot do got an
/// ordinary run of something else. These spawn the real runner (one shared server process).
/// </summary>
public class ServerRequestFieldTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerRequestFieldTests(SharedCliServer fixture) => _fixture = fixture;

    // The reporter's shape: the procedure answers 1 only under SYM, the test asserts 1, so a
    // request whose symbols were honoured would pass and one whose symbols were dropped fails.
    // `variant` gives every call site its own AppId and id range (a server caches by AppId).
    private static string MakeBundle(int variant)
    {
        var dir = TestScratch.Dir("al-runner-server-request-fields");
        Directory.CreateDirectory(dir);
        var baseId = 71230 + variant * 2;
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "b7c1e2d3-4f5a-4b6c-8d7e-9f0a1b2c3d4{{variant:x1}}",
          "name": "Runner Extras - Server Request Fields {{variant}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 1}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Answer.Codeunit.al"), $$"""
        codeunit {{baseId}} "Request Fields Answer {{variant}}"
        {
            procedure Value(): Integer
            begin
        #if SYM
                exit(1);
        #else
                exit(2);
        #endif
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "AnswerTest.Codeunit.al"), $$"""
        codeunit {{baseId + 1}} "Request Fields Test {{variant}}"
        {
            Subtype = Test;

            [Test]
            procedure ValueIsTheNoSymbolBuild()
            var
                Answer: Codeunit "Request Fields Answer {{variant}}";
            begin
                if Answer.Value() <> 2 then
                    Error('expected the no-symbol build, got %1', Answer.Value());
            end;
        }
        """);
        return dir;
    }

    [SkippableFact]
    public async Task RunTests_PreprocessorSymbolsField_IsRefusedNamingTheFieldAndDefine()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();

        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { MakeBundle(0) },
            preprocessorSymbols = new[] { "SYM" },
        });
        var d = JsonSerializer.Deserialize<JsonElement>(await server.SendAsync(req));

        Assert.True(d.TryGetProperty("error", out var err), $"expected a refusal, got: {d}");
        var msg = err.GetString()!;
        Assert.Contains("'preprocessorSymbols'", msg);
        Assert.Contains("--define", msg);
        Assert.False(d.TryGetProperty("type", out _), $"a refusal is not a summary: {d}");

        // The refusal left the daemon serving: the same bundle without the field runs.
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { MakeBundle(1) },
        }));
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        Assert.False(summary.TryGetProperty("warnings", out _), $"nothing inapplicable was sent: {summary}");
    }

    [SkippableFact]
    public async Task Execute_UnknownField_IsRefused()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();

        var req = JsonSerializer.Serialize(new
        {
            command = "execute",
            code = "codeunit 71239 \"Request Fields Scratch\" { trigger OnRun() begin end; }",
            noCache = true,
        });
        var d = JsonSerializer.Deserialize<JsonElement>(await server.SendAsync(req));

        Assert.True(d.TryGetProperty("error", out var err), $"expected a refusal, got: {d}");
        Assert.Contains("'noCache'", err.GetString()!);
        Assert.False(d.TryGetProperty("exitCode", out _), $"a refusal did not run anything: {d}");
    }

    [SkippableFact]
    public async Task RunTests_KnownFieldOnlyExecuteReads_RunsAndWarnsInTheSummary()
    {
        TestArtifacts.SkipIfMissing();
        var server = await _fixture.GetAsync();

        // ALchemist sends captureValues/iterationTracking on every runtests; refusing them would
        // break it, so the run goes ahead and the summary says they did nothing.
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new
        {
            command = "runtests",
            sourcePaths = new[] { MakeBundle(2) },
            captureValues = true,
            iterationTracking = true,
        }));
        var (_, summary) = ProtocolV2Streaming.Split(lines);

        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.True(summary.TryGetProperty("warnings", out var w), $"expected warnings: {summary}");
        var warnings = w.EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, s => s.StartsWith("'captureValues' ", StringComparison.Ordinal));
        Assert.Contains(warnings, s => s.StartsWith("'iterationTracking' ", StringComparison.Ordinal));
    }
}
