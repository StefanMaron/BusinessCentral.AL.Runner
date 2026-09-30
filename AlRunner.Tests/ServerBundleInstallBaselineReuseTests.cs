// ServerBundleInstallBaselineReuseTests — issue #5060.
//
// A warm --server request for an unchanged bundle restores the install baseline the previous
// request captured, instead of re-running the bundle's own Install triggers and re-capturing.
// The observable is the bundle's own Install trigger: TestExecutor logs one
// `InstallTrigger Codeunit<N>` line per firing under AL_RUNNER_PERF=1, so "the seed was not
// redone" is that line being absent from a request's slice of stderr. The AL tests read the
// seeded row back by value in two codeunits, so a reuse that restored nothing, or restored a
// stale row, fails them.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerBundleInstallBaselineReuseTests
{
    private const string InstallTriggerLine = "InstallTrigger Codeunit50601 ";
    private const string HitLine = "InstallBaseline.BundleCache HIT";

    private static readonly Dictionary<string, string> PerfEnv = new() { ["AL_RUNNER_PERF"] = "1" };

    [SkippableFact]
    public async Task SecondIdenticalRequest_ReusesTheInstallBaseline_AndEveryCodeunitStillStartsFromTheSeed()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = CreateBundle(seed: "7777", useNumberSequence: false);
        try
        {
            await using var server = await CliServer.StartAsync(extraEnv: PerfEnv);

            var first = await RunAsync(server, bundle);
            Assert.Equal(1, Count(first, InstallTriggerLine));
            Assert.Equal(0, Count(first, HitLine));
            Assert.Equal(1, Count(first, "InstallBaseline.BundleCache MISS"));

            var second = await RunAsync(server, bundle);
            Assert.Equal(0, Count(second, InstallTriggerLine));
            Assert.Equal(1, Count(second, HitLine));

            // Only Codeunit isolation is keyed: the other modes can observe what an Install
            // trigger leaves outside the store before the first restore.
            var testIsolation = await RunAsync(server, bundle, isolation: "test");
            Assert.Equal(1, Count(testIsolation, InstallTriggerLine));
            Assert.Equal(0, Count(testIsolation, HitLine));
            Assert.Contains("InstallBaseline.BundleCache NOKEY isolation Test", testIsolation);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    /// <summary>The stale-reuse direction: an edit that changes what the Install trigger writes
    /// must re-run it, and the tests must see the new row.</summary>
    [SkippableFact]
    public async Task EditToTheInstallTrigger_RedoesTheSeed_AndTheTestsSeeTheNewRow()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = CreateBundle(seed: "7777", useNumberSequence: false);
        try
        {
            await using var server = await CliServer.StartAsync(extraEnv: PerfEnv);

            var first = await RunAsync(server, bundle);
            Assert.Equal(1, Count(first, InstallTriggerLine));

            var source = Path.Combine(bundle, "Bundle.al");
            File.WriteAllText(source, File.ReadAllText(source).Replace("7777", "4242"));

            var edited = await RunAsync(server, bundle);
            Assert.Equal(1, Count(edited, InstallTriggerLine));
            Assert.Equal(0, Count(edited, HitLine));

            var again = await RunAsync(server, bundle);
            Assert.Equal(0, Count(again, InstallTriggerLine));
            Assert.Equal(1, Count(again, HitLine));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    /// <summary>A NumberSequence lives outside the store and is reset per request, so a seed
    /// that creates one is not stored: a reuse would hand the tests a request with no sequence.</summary>
    [SkippableFact]
    public async Task InstallTriggerUsingANumberSequence_IsNotReused()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = CreateBundle(seed: "7777", useNumberSequence: true);
        try
        {
            await using var server = await CliServer.StartAsync(extraEnv: PerfEnv);

            var first = await RunAsync(server, bundle);
            Assert.Contains("not-stored: the seed used a NumberSequence", first);

            var second = await RunAsync(server, bundle);
            Assert.Equal(1, Count(second, InstallTriggerLine));
            Assert.Equal(0, Count(second, HitLine));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    /// <summary>Send one runTests request, assert all three tests passed, and return the
    /// request's own slice of stderr.</summary>
    private static async Task<string> RunAsync(CliServer server, string bundle, string isolation = "codeunit")
    {
        var mark = server.StdErrMark;
        var response = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundle },
            testIsolation = isolation,
        }));
        var (events, summary) = ProtocolV2Streaming.Split(response);
        // Logged after every line these tests assert on, absent ones included.
        var stderr = await server.StdErrSinceAsync(mark, "TestExecutor.InitialInstallSeed");
        Assert.True(summary.GetProperty("failed").GetInt32() == 0 && summary.GetProperty("errors").GetInt32() == 0,
            "a runTests request failed:\n" + string.Join("\n", response) + "\n--- stderr ---\n" + stderr);
        Assert.Equal(3, summary.GetProperty("passed").GetInt32());
        Assert.All(events, e => Assert.Equal("pass", e.GetProperty("status").GetString()));
        return stderr;
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static string CreateBundle(string seed, bool useNumberSequence)
    {
        var directory = TestScratch.Dir("al-runner-5060-bundle-baseline");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app.json"), """
        {
          "id": "5a0f5060-1b2c-4d3e-8f40-506050605060",
          "name": "Runner Tests - Bundle Baseline Reuse",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 50600, "to": 50609 } ],
          "runtime": "14.0"
        }
        """);
        var sequenceInstall = useNumberSequence
            ? "if not NumberSequence.Exists('IBRSeq') then NumberSequence.Insert('IBRSeq', 1, 1);"
            : "";
        var sequenceCheck = useNumberSequence
            ? "if not NumberSequence.Exists('IBRSeq') then Error('the install trigger''s number sequence is missing');"
            : "";
        File.WriteAllText(Path.Combine(directory, "Bundle.al"), $$"""
        table 50600 "IBR Setup"
        {
            fields
            {
                field(1; "Key"; Code[10]) { }
                field(2; Counter; Integer) { }
            }
            keys
            {
                key(PK; "Key") { Clustered = true; }
            }
        }

        codeunit 50601 "IBR Install"
        {
            Subtype = Install;

            trigger OnInstallAppPerCompany()
            var
                Setup: Record "IBR Setup";
            begin
                Setup."Key" := '';
                Setup.Counter := {{seed}};
                Setup.Insert();
                {{sequenceInstall}}
            end;
        }

        codeunit 50602 "IBR Tests A"
        {
            Subtype = Test;

            [Test]
            procedure BumpStartsFromTheSeed()
            var
                Setup: Record "IBR Setup";
            begin
                Setup.Get('');
                Setup.Counter += 1;
                Setup.Modify();
                if Setup.Counter <> {{seed}} + 1 then
                    Error('expected %1, got %2', {{seed}} + 1, Setup.Counter);
                {{sequenceCheck}}
            end;

            [Test]
            procedure SeedRowIsTheOnlyRow()
            var
                Setup: Record "IBR Setup";
            begin
                if Setup.Count() <> 1 then
                    Error('expected exactly the seeded row, found %1', Setup.Count());
            end;
        }

        codeunit 50603 "IBR Tests B"
        {
            Subtype = Test;

            [Test]
            procedure SecondCodeunitAlsoStartsFromTheSeed()
            var
                Setup: Record "IBR Setup";
            begin
                Setup.Get('');
                Setup.Counter += 1;
                Setup.Modify();
                if Setup.Counter <> {{seed}} + 1 then
                    Error('the first codeunit''s write leaked: expected %1, got %2', {{seed}} + 1, Setup.Counter);
            end;
        }
        """);
        return directory;
    }
}
