// ServerCompanyInitDrainTests — issue #3561, the server-mode half.
//
// CompanyInitializer's accumulator is run-wide and is drained where Program.cs builds a bucket's
// BucketResult — a path `--server` never takes. So under --server the static list grew for the
// life of the process and NOTHING ever reported it: a client had no surface at all saying its
// tests had run against a company real BC cannot produce, and the second request inherited the
// first's entries. Both halves are asserted here, and the second is what a growing static looks
// like from outside: request 2 carrying request 1's abort as well as its own.
//
// The abort is injected with AL_RUNNER_TEST_FAIL_COMPANY_INIT for the same reason
// PartialCompanyInitializationTests injects it — see that file's header. This class starts its
// OWN server rather than sharing SharedCliServer, because the seam is a startup-time environment
// variable and every other class sharing that fixture must not get it.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerCompanyInitDrainTests
{
    private const string InjectedReason = "CIP-INJECTED-ABORT: InitSourceCodeSetup did not finish";

    private static string MakeBundle(int variant)
    {
        var dir = TestScratch.Dir("al-runner-server-cip");
        Directory.CreateDirectory(dir);
        var baseId = 61860 + variant * 10;
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "a1b2c3d4-e5f6-4708-a9ba-cbdcedfe1f1{{variant:x1}}",
          "name": "Runner Tests Fixture - Server Company Init {{variant}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "ServerCipTest.Codeunit.al"), $$"""
        codeunit {{baseId}} "Server CIP Probe {{variant}}"
        {
            Subtype = Test;

            [Test]
            procedure ArithmeticStillRuns()
            var
                Sum: Integer;
            begin
                Sum := 2 + 40;
                if Sum <> 42 then
                    Error('expected 42, got %1', Sum);
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsReq(string bundleDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
        });

    /// <summary>
    /// Two requests against a server whose company initialization aborts. Each summary carries
    /// the condition — the field a client can read at all, which server mode had none of — and
    /// each carries exactly ONE entry. Request 2 carrying two is the shape of the defect: the
    /// accumulator never drained, so the second response reports the first request's abort as
    /// well. The exit code mirrors the CLI's: nothing about the AL failed, and the database the
    /// AL ran against was not the one asked for.
    /// </summary>
    [SkippableFact]
    public async Task EachRequestReportsItsOwnCompanyInitAbort_AndTheAccumulatorDoesNotGrow()
    {
        TestArtifacts.SkipIfMissing();

        await using var server = await CliServer.StartAsync(extraEnv: new Dictionary<string, string>
        {
            ["AL_RUNNER_TEST_FAIL_COMPANY_INIT"] = InjectedReason,
        });

        var first = await server.SendRequestStreamingAsync(RunTestsReq(MakeBundle(0)));
        var firstSummary = JsonSerializer.Deserialize<JsonElement>(first[^1]);
        Assert.Equal("summary", firstSummary.GetProperty("type").GetString());
        Assert.Equal(1, firstSummary.GetProperty("passed").GetInt32());
        var firstFailures = firstSummary.GetProperty("companyInitFailures").EnumerateArray().ToList();
        Assert.Single(firstFailures);
        Assert.Equal(2, firstFailures[0].GetProperty("codeunitId").GetInt32());
        Assert.Equal("Company-Initialize", firstFailures[0].GetProperty("codeunit").GetString());
        Assert.Equal(InjectedReason, firstFailures[0].GetProperty("message").GetString());
        Assert.Equal(2, firstSummary.GetProperty("exitCode").GetInt32());

        var second = await server.SendRequestStreamingAsync(RunTestsReq(MakeBundle(1)));
        var secondSummary = JsonSerializer.Deserialize<JsonElement>(second[^1]);
        var secondFailures = secondSummary.GetProperty("companyInitFailures").EnumerateArray().ToList();
        Assert.True(secondFailures.Count == 1,
            $"the second request must report its own abort only; {secondFailures.Count} entries means "
            + $"the accumulator was never drained.\n{second[^1]}");
        Assert.Equal(1, secondFailures[0].GetProperty("count").GetInt32());
        Assert.Equal(2, secondSummary.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The negative control: with nothing injected, the same server, the same request shape and
    /// the same bundle produce a summary with no such field at all — so the field means "this
    /// happened", never "the server always says this".
    /// </summary>
    [SkippableFact]
    public async Task ACleanServerRun_CarriesNoCompanyInitField()
    {
        TestArtifacts.SkipIfMissing();

        await using var server = await CliServer.StartAsync();
        var lines = await server.SendRequestStreamingAsync(RunTestsReq(MakeBundle(2)));
        var summary = JsonSerializer.Deserialize<JsonElement>(lines[^1]);

        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.False(summary.TryGetProperty("companyInitFailures", out _));
    }
}
