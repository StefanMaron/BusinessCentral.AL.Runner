using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class NumberSequenceServerResetTests
{
    [SkippableFact]
    public async Task ConsecutiveServerRequests_StartWithIndependentSequenceState()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = CreateProbeBundle();
        try
        {
            var request = JsonSerializer.Serialize(new
            {
                command = "runTests",
                sourcePaths = new[] { bundle },
                packagePaths = Array.Empty<string>(),
            });

            await using var server = await CliServer.StartAsync();

            AssertSuccessful(await server.SendRequestStreamingAsync(request));
            AssertSuccessful(await server.SendRequestStreamingAsync(request));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

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
          "application": "1.0.0.0",
          "idRanges": [ { "from": 64590, "to": 64590 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(directory, "Probe.Codeunit.al"), """
        codeunit 64590 "Number Sequence Reset Tests"
        {
            Subtype = Test;

            [Test]
            procedure DefaultAndGlobalScopesAllocateConfiguredValues()
            begin
                NumberSequence.Insert('ALRunnerDefaultScope', 5, 2);
                if NumberSequence.Next('ALRunnerDefaultScope') <> 5 then
                    Error('Default-scope NumberSequence did not allocate its seed.');

                NumberSequence.Insert('ALRunnerGlobalScope', 10, 3, false);
                if NumberSequence.Next('ALRunnerGlobalScope', false) <> 10 then
                    Error('Global NumberSequence did not allocate its seed.');
                if NumberSequence.Next('ALRunnerGlobalScope', false) <> 13 then
                    Error('Global NumberSequence did not apply its increment.');
            end;

            [Test]
            procedure CurrentRestartAndDeleteWork()
            begin
                NumberSequence.Insert('ALRunnerLifecycle', 10, 3, false);
                if NumberSequence.Current('ALRunnerLifecycle', false) <> 10 then
                    Error('Current did not expose the configured seed.');

                NumberSequence.Restart('ALRunnerLifecycle', 50, false);
                if NumberSequence.Next('ALRunnerLifecycle', false) <> 50 then
                    Error('Restart did not supply the next value.');

                NumberSequence.Delete('ALRunnerLifecycle', false);
                if NumberSequence.Exists('ALRunnerLifecycle', false) then
                    Error('Delete did not remove the sequence.');
            end;

            [Test]
            procedure RangeReportsIncrementAndReservesValues()
            var
                First: BigInteger;
                Increment: BigInteger;
            begin
                NumberSequence.Insert('ALRunnerRange', 10, 3, false);
                First := NumberSequence.Range('ALRunnerRange', 4, Increment, false);
                if First <> 10 then
                    Error('Range did not return the configured seed.');
                if Increment <> 3 then
                    Error('Range did not report the configured increment.');
                if NumberSequence.Current('ALRunnerRange', false) <> 19 then
                    Error('Range did not reserve the requested values.');
            end;

            [Test]
            procedure DuplicateInsertRaisesCatchableError()
            begin
                NumberSequence.Insert('ALRunnerDuplicate', 1, 1, false);

                asserterror NumberSequence.Insert('ALRunnerDuplicate', 1, 1, false);
                if StrPos(GetLastErrorText(), 'already exists') = 0 then
                    Error('Duplicate Insert returned the wrong error: %1', GetLastErrorText());
            end;

            [Test]
            procedure MissingNextRaisesCatchableError()
            var
                Value: BigInteger;
            begin
                asserterror Value := NumberSequence.Next('ALRunnerMissing', false);
                if StrPos(GetLastErrorText(), 'does not exist') = 0 then
                    Error('Missing Next returned the wrong error: %1', GetLastErrorText());
            end;
        }
        """);
        return directory;
    }

    private static void AssertSuccessful(IReadOnlyList<string> response)
    {
        var (events, summary) = ProtocolV2Streaming.Split(response);
        Assert.Equal(5, summary.GetProperty("passed").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("errors").GetInt32());
        Assert.Equal(0, summary.GetProperty("exitCode").GetInt32());
        Assert.Equal(5, events.Count);
        Assert.All(events, test => Assert.Equal("pass", test.GetProperty("status").GetString()));
    }
}
