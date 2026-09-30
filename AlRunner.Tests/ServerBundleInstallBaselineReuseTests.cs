// ServerBundleInstallBaselineReuseTests — issue #5060.
//
// A warm --server request for an unchanged bundle restores the install baseline the previous
// request captured, instead of re-running the bundle's own Install triggers and re-capturing.
// The observable is the bundle's own Install trigger: InstallTriggerRunner logs one
// `InstallTrigger Codeunit<N>` line per firing under AL_RUNNER_PERF=1, so "the seed was not
// redone" is that line being absent from a request's slice of stderr. The AL tests read the
// seeded row back by value in two codeunits, so a reuse that restored nothing, restored a
// stale row, or restored another bundle's baseline fails them.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerBundleInstallBaselineReuseTests
{
    private const string HitLine = "InstallBaseline.BundleCache HIT";
    private const string SeedLine = "TestExecutor.InitialInstallSeed";

    private static readonly Dictionary<string, string> PerfEnv = new() { ["AL_RUNNER_PERF"] = "1" };

    private static string InstallTriggerLine(int offset) => $"InstallTrigger Codeunit{50601 + offset} ";

    [SkippableFact]
    public async Task SecondIdenticalRequest_ReusesTheInstallBaseline_AndEveryCodeunitStillStartsFromTheSeed()
    {
        TestArtifacts.SkipIfMissing();
        // Two independent bundles in one request, so each HIT has to publish its own baseline:
        // the codeunit boundaries restore whichever baseline was published last.
        var a = CreateBundle(0, seed: "7777", useNumberSequence: false);
        var b = CreateBundle(5, seed: "3333", useNumberSequence: false);
        try
        {
            await using var server = await CliServer.StartAsync(extraEnv: PerfEnv);

            var first = await RunAsync(server, a, b);
            Assert.Equal(1, Count(first, InstallTriggerLine(0)));
            Assert.Equal(1, Count(first, InstallTriggerLine(5)));
            Assert.Equal(0, Count(first, HitLine));
            Assert.Equal(2, Count(first, "InstallBaseline.BundleCache MISS"));

            var second = await RunAsync(server, a, b);
            Assert.Equal(0, Count(second, InstallTriggerLine(0)));
            Assert.Equal(0, Count(second, InstallTriggerLine(5)));
            Assert.Equal(2, Count(second, HitLine));

            // Only Codeunit isolation is keyed: the other modes can observe what an Install
            // trigger leaves outside the store before the first restore.
            var testIsolation = await RunAsync(server, new[] { a, b }, isolation: "test");
            Assert.Equal(1, Count(testIsolation, InstallTriggerLine(0)));
            Assert.Equal(0, Count(testIsolation, HitLine));
            Assert.Contains("InstallBaseline.BundleCache NOKEY isolation Test", testIsolation);
        }
        finally
        {
            try { Directory.Delete(a, recursive: true); } catch { }
            try { Directory.Delete(b, recursive: true); } catch { }
        }
    }

    /// <summary>The stale-reuse direction: an edit that changes what the Install trigger writes
    /// must re-run it, and the tests must see the new row.</summary>
    [SkippableFact]
    public async Task EditToTheInstallTrigger_RedoesTheSeed_AndTheTestsSeeTheNewRow()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = CreateBundle(0, seed: "7777", useNumberSequence: false);
        try
        {
            await using var server = await CliServer.StartAsync(extraEnv: PerfEnv);

            var first = await RunAsync(server, bundle);
            Assert.Equal(1, Count(first, InstallTriggerLine(0)));

            var source = Path.Combine(bundle, "Bundle.al");
            File.WriteAllText(source, File.ReadAllText(source).Replace("7777", "4242"));

            var edited = await RunAsync(server, bundle);
            Assert.Equal(1, Count(edited, InstallTriggerLine(0)));
            Assert.Equal(0, Count(edited, HitLine));

            var again = await RunAsync(server, bundle);
            Assert.Equal(0, Count(again, InstallTriggerLine(0)));
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
        var bundle = CreateBundle(0, seed: "7777", useNumberSequence: true);
        try
        {
            await using var server = await CliServer.StartAsync(extraEnv: PerfEnv);

            // The second request's own AL test fails if its sequence was not created, which is
            // what a reuse would do.
            var first = await RunAsync(server, bundle);
            var second = await RunAsync(server, bundle);
            Assert.Equal(1, Count(second, InstallTriggerLine(0)));
            Assert.Equal(0, Count(second, HitLine));
            Assert.Contains("not-stored: the seed used a NumberSequence", first);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    private static Task<string> RunAsync(CliServer server, params string[] bundles)
        => RunAsync(server, bundles, "codeunit");

    /// <summary>Send one runTests request, assert every test passed, and return the request's
    /// own slice of stderr once every bundle's seed line is in it (the seed line is logged after
    /// every line these tests assert on, absent ones included).</summary>
    private static async Task<string> RunAsync(CliServer server, string[] bundles, string isolation)
    {
        var mark = server.StdErrMark;
        var response = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = bundles,
            testIsolation = isolation,
        }));
        var (events, summary) = ProtocolV2Streaming.Split(response);
        var stderr = await server.StdErrSinceAsync(mark, SeedLine);
        for (var wait = 0; Count(stderr, SeedLine) < bundles.Length && wait < 400; wait++)
        {
            await Task.Delay(25);
            stderr = server.StdErrSince(mark);
        }
        Assert.True(summary.GetProperty("failed").GetInt32() == 0 && summary.GetProperty("errors").GetInt32() == 0,
            "a runTests request failed:\n" + string.Join("\n", response) + "\n--- stderr ---\n" + stderr);
        Assert.Equal(3 * bundles.Length, summary.GetProperty("passed").GetInt32());
        Assert.All(events, e => Assert.Equal("pass", e.GetProperty("status").GetString()));
        Assert.Equal(bundles.Length, Count(stderr, SeedLine));
        return stderr;
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>A bundle with one table, an Install trigger seeding one row with
    /// <paramref name="seed"/>, and three tests in two codeunits reading it back. Object ids are
    /// 50600 + <paramref name="offset"/> onward, so two bundles can share a request.</summary>
    private static string CreateBundle(int offset, string seed, bool useNumberSequence)
    {
        var directory = TestScratch.Dir("al-runner-5060-bundle-baseline");
        Directory.CreateDirectory(directory);
        int id(int n) => 50600 + offset + n;
        File.WriteAllText(Path.Combine(directory, "app.json"), $$"""
        {
          "id": "5a0f5060-1b2c-4d3e-8f40-5060506050{{60 + offset}}",
          "name": "Runner Tests - Bundle Baseline Reuse {{offset}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{id(0)}}, "to": {{id(4)}} } ],
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
        table {{id(0)}} "IBR Setup {{offset}}"
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

        codeunit {{id(1)}} "IBR Install {{offset}}"
        {
            Subtype = Install;

            trigger OnInstallAppPerCompany()
            var
                Setup: Record "IBR Setup {{offset}}";
            begin
                Setup."Key" := '';
                Setup.Counter := {{seed}};
                Setup.Insert();
                {{sequenceInstall}}
            end;
        }

        codeunit {{id(2)}} "IBR Tests A {{offset}}"
        {
            Subtype = Test;

            [Test]
            procedure BumpStartsFromTheSeed()
            var
                Setup: Record "IBR Setup {{offset}}";
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
                Setup: Record "IBR Setup {{offset}}";
            begin
                if Setup.Count() <> 1 then
                    Error('expected exactly the seeded row, found %1', Setup.Count());
            end;
        }

        codeunit {{id(3)}} "IBR Tests B {{offset}}"
        {
            Subtype = Test;

            [Test]
            procedure SecondCodeunitAlsoStartsFromTheSeed()
            var
                Setup: Record "IBR Setup {{offset}}";
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
