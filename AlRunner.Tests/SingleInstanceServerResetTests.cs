// SingleInstanceServerResetTests — issue #4781, the bundle-start half.
//
// SingleInstance state now lives for the whole run (SingleInstanceSessionLifetimeTests), so
// the only thing ending it is the reset at the start of TestExecutor.Run. Install triggers run
// before the post-seed reset, so without the bundle-start one a second request's install
// trigger reads the first request's instance. Two runTests requests to one --server process are
// the shape that reaches it; a single CLI bundle cannot.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class SingleInstanceServerResetTests
{
    [SkippableFact]
    public async Task ConsecutiveServerRequests_InstallTriggersStartWithoutThePreviousRunsSingleInstanceState()
    {
        TestArtifacts.SkipIfMissing();
        var bundles = new[] { CreateProbeBundle(), CreateProbeBundle() };
        try
        {
            await using var server = await CliServer.StartAsync();

            foreach (var bundle in bundles)
                AssertSuccessful(await server.SendRequestStreamingAsync(CreateRequest(bundle)));
        }
        finally
        {
            foreach (var bundle in bundles)
                try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    private static string CreateRequest(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
    });

    private static string CreateProbeBundle()
    {
        var directory = TestScratch.Dir("al-runner-si-server-reset");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app.json"), """
        {
          "id": "6d1f3a92-8c47-4b0e-a5d3-2e9f7c1b4a60",
          "name": "Runner Tests - SingleInstance Server Reset",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 64600, "to": 64603 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(directory, "Probe.al"), """
        table 64600 "SISR Seen"
        {
            fields
            {
                field(1; "Key"; Code[10]) { }
                field(2; Seen; Integer) { }
            }
            keys
            {
                key(PK; "Key") { Clustered = true; }
            }
        }

        codeunit 64601 "SISR Single"
        {
            SingleInstance = true;

            var
                Value: Integer;

            procedure Bump()
            begin
                Value += 1;
            end;

            procedure Get(): Integer
            begin
                exit(Value);
            end;
        }

        codeunit 64602 "SISR Install"
        {
            Subtype = Install;

            trigger OnInstallAppPerCompany()
            var
                Single: Codeunit "SISR Single";
                Seen: Record "SISR Seen";
            begin
                Seen."Key" := 'INSTALL';
                Seen.Seen := Single.Get();
                Seen.Insert();
            end;
        }

        codeunit 64603 "SISR Tests"
        {
            Subtype = Test;

            [Test]
            procedure InstallTriggerSawNoEarlierRunsState()
            var
                Single: Codeunit "SISR Single";
                Seen: Record "SISR Seen";
            begin
                Seen.Get('INSTALL');
                if Seen.Seen <> 0 then
                    Error('install trigger saw previous run SingleInstance state: %1', Seen.Seen);
                Single.Bump();
                if Single.Get() <> 1 then
                    Error('expected the bump to land on this run''s instance, got %1', Single.Get());
            end;
        }
        """);
        return directory;
    }

    private static void AssertSuccessful(IReadOnlyList<string> response)
    {
        var (events, summary) = ProtocolV2Streaming.Split(response);
        Assert.True(summary.GetProperty("failed").GetInt32() == 0,
            "a runTests request failed:\n" + string.Join("\n", response));
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.Single(events);
        Assert.All(events, test => Assert.Equal("pass", test.GetProperty("status").GetString()));
    }
}
