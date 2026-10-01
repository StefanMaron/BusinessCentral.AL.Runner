// #5183: runTests' `test` and `excludeTests` — the CLI's --test / --filter and --exclude-test for one
// request. Runner-specific (a request field of this server), so it lives here and not in the
// al-language corpus. Mechanism and refusals: docs/server-mode.md#test-and-excludetests.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

// Facts that need no startup flag of their own share one --server (SharedCliServer, rule (c): one
// app id per call site). The startup-default fact needs its own flag, so it starts its own server.
public sealed class ServerTestSelectionFieldTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerTestSelectionFieldTests(SharedCliServer fixture) => _fixture = fixture;

    private const string AlphaTests = """
        codeunit 65401 "SelFld Alpha Tests"
        {
            Subtype = Test;

            [Test]
            procedure AlphaCheck()
            begin
                if 1 + 1 <> 2 then
                    Error('alpha broke');
            end;
        }
        """;

    private const string BetaTests = """
        codeunit 65402 "SelFld Beta Tests"
        {
            Subtype = Test;

            [Test]
            procedure BetaCheck()
            begin
                if 2 + 2 <> 4 then
                    Error('beta broke');
            end;
        }
        """;

    private static string Bundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5183000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Server Test Selection SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 65400, "to": 65419 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Alpha.Codeunit.al"), AlphaTests);
        File.WriteAllText(Path.Combine(dir, "Beta.Codeunit.al"), BetaTests);
        return dir;
    }

    private sealed record Response(string[] Ran, JsonElement Summary, string Raw, string? Error)
    {
        public int ExitCode => Summary.GetProperty("exitCode").GetInt32();
        public string[] Warnings => Summary.TryGetProperty("warnings", out var w)
            ? w.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();
    }

    private static async Task<Response> Send(CliServer server, string bundle, Action<Dictionary<string, object?>>? fields = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
        };
        fields?.Invoke(request);
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(240));
        var raw = string.Join(" | ", lines);
        var last = JsonSerializer.Deserialize<JsonElement>(lines[^1]);
        if (last.TryGetProperty("error", out var err))
            return new Response(Array.Empty<string>(), default, raw, err.GetString());
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var ran = events.Select(e => e.GetProperty("name").GetString()!.Split('.').Last()).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        return new Response(ran, summary, raw, null);
    }

    private static readonly string[] Both = { "AlphaCheck", "BetaCheck" };

    /// <summary>`test` narrows this request to the matching test and exit 0; the next request, which
    /// omits the field, runs everything again — a request's selection never outlives it.</summary>
    [SkippableFact]
    public async Task Test_NarrowsOneRequest_AndTheNextRequestWithoutItRunsEverything()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-test-field", "000000000001");
        var server = await _fixture.GetAsync();

        var narrowed = await Send(server, bundle, r => r["test"] = "alphacheck");
        Assert.Equal(new[] { "AlphaCheck" }, narrowed.Ran);
        Assert.True(narrowed.ExitCode == 0, narrowed.Raw);

        var plain = await Send(server, bundle);
        Assert.Equal(Both, plain.Ran);
        Assert.True(plain.ExitCode == 0, plain.Raw);

        var other = await Send(server, bundle, r => r["test"] = "BetaCheck");
        Assert.Equal(new[] { "BetaCheck" }, other.Ran);

        var again = await Send(server, bundle);
        Assert.Equal(Both, again.Ran);
    }

    /// <summary>`excludeTests` skips a whole qualified test name for one request and, like the CLI
    /// flag, never matches a partial name; the next request without it runs everything.</summary>
    [SkippableFact]
    public async Task ExcludeTests_SkipsTheNamedTestForOneRequest_AndNeverAPartialName()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-exclude-field", "000000000002");
        var server = await _fixture.GetAsync();

        var qualified = await FirstQualifiedName(server, bundle, "AlphaCheck");

        var excluded = await Send(server, bundle, r => r["excludeTests"] = new[] { qualified });
        Assert.Equal(new[] { "BetaCheck" }, excluded.Ran);
        Assert.True(excluded.ExitCode == 0, excluded.Raw);

        // A prefix of the codeunit name must not swallow it (the CLI flag's rule).
        var partial = await Send(server, bundle, r => r["excludeTests"] = new[] { qualified.Split('.')[0][..^1] });
        Assert.Equal(Both, partial.Ran);

        var plain = await Send(server, bundle);
        Assert.Equal(Both, plain.Ran);
    }

    private static async Task<string> FirstQualifiedName(CliServer server, string bundle, string method)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = new[] { bundle },
            ["packagePaths"] = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(request, TimeSpan.FromSeconds(240));
        var (events, _) = ProtocolV2Streaming.Split(lines);
        return events.Select(e => e.GetProperty("name").GetString()!).First(n => n.EndsWith("." + method, StringComparison.Ordinal));
    }

    /// <summary>A pattern that selects nothing is the CLI's exit 6 with its diagnostic, not a clean
    /// 0-test run; a pattern whose only match `excludeTests` removed is NOT a no-match; and the next
    /// request is judged on its own.</summary>
    [SkippableFact]
    public async Task Test_SelectingNothing_IsExit6_ButNotWhenExcludeTestsRemovedTheMatch()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-test-nomatch", "000000000003");
        var server = await _fixture.GetAsync();

        var none = await Send(server, bundle, r => r["test"] = "NoSuchTestExists");
        Assert.Empty(none.Ran);
        Assert.True(none.ExitCode == 6, none.Raw);
        Assert.Contains(none.Warnings, w => w.Contains("--test 'NoSuchTestExists' selected no test in this run", StringComparison.Ordinal));

        var qualified = await FirstQualifiedName(server, bundle, "AlphaCheck");
        var removed = await Send(server, bundle, r => { r["test"] = "AlphaCheck"; r["excludeTests"] = new[] { qualified }; });
        Assert.Empty(removed.Ran);
        Assert.True(removed.ExitCode == 0, removed.Raw);
        Assert.Empty(removed.Warnings);

        var plain = await Send(server, bundle);
        Assert.Equal(Both, plain.Ran);
        Assert.True(plain.ExitCode == 0, plain.Raw);
        Assert.Empty(plain.Warnings);
    }

    /// <summary>`test` / `excludeTests` with affectedOnly are refused, as the CLI refuses --affected with
    /// --test: nothing runs, and the next request is unaffected.</summary>
    [SkippableFact]
    public async Task Selection_WithAffectedOnly_IsRefusedAndNothingRuns()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-test-affected", "000000000004");
        var server = await _fixture.GetAsync();

        var refused = await Send(server, bundle, r => { r["test"] = "AlphaCheck"; r["affectedOnly"] = true; });
        Assert.NotNull(refused.Error);
        Assert.Contains("cannot be combined with affectedOnly", refused.Error, StringComparison.Ordinal);
        Assert.Empty(refused.Ran);

        var refusedExclude = await Send(server, bundle, r => { r["excludeTests"] = new[] { "Codeunit1" }; r["affectedOnly"] = true; });
        Assert.NotNull(refusedExclude.Error);

        var plain = await Send(server, bundle);
        Assert.Equal(Both, plain.Ran);
    }

    /// <summary>A server started with --test keeps it as the default for a request that omits the field,
    /// a request's own `test` replaces it for that request only, and the default is back for the next.</summary>
    [SkippableFact]
    public async Task StartupTestFlag_IsTheDefault_ARequestFieldReplacesItForOneRequest()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-test-startup", "000000000005");
        await using var server = await CliServer.StartAsync(new[] { "--test", "AlphaCheck" });

        var first = await Send(server, bundle);
        Assert.Equal(new[] { "AlphaCheck" }, first.Ran);

        var replaced = await Send(server, bundle, r => r["test"] = "BetaCheck");
        Assert.Equal(new[] { "BetaCheck" }, replaced.Ran);

        var back = await Send(server, bundle);
        Assert.Equal(new[] { "AlphaCheck" }, back.Ran);
    }
}
