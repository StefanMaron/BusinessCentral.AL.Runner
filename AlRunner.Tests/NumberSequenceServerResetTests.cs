using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class NumberSequenceServerResetTests
{
    [SkippableFact]
    public async Task ConsecutiveServerRequests_StartWithIndependentSequenceState()
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
        var directory = Path.Combine(
            Path.GetTempPath(), "al-runner-number-sequence-server", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app.json"), """
        {
          "id": "419709b5-6033-4f36-b3da-4491742d5485",
          "name": "Runner Tests - Number Sequence Server Reset",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64590, "to": 64590 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(directory, "Probe.Codeunit.al"), """
        codeunit 64590 "Number Sequence Reset Tests"
        {
            Subtype = Test;

            [Test]
            procedure RequestStartsWithFreshSequenceState()
            begin
                if NumberSequence.Exists('ALRunnerRequestState', false) then
                    Error('NumberSequence state leaked from an earlier server request.');

                NumberSequence.Insert('ALRunnerRequestState', 1, 1, false);
                if not NumberSequence.Exists('ALRunnerRequestState', false) then
                    Error('NumberSequence.Insert did not create request-local state.');
            end;
        }
        """);
        return directory;
    }

    private static void AssertSuccessful(IReadOnlyList<string> response)
    {
        var (events, summary) = ProtocolV2Streaming.Split(response);
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.Single(events);
        Assert.All(events, test => Assert.Equal("pass", test.GetProperty("status").GetString()));
    }
}
