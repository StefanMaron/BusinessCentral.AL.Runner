// Issue #5168 — --server writes `[server] request <n> done` on stderr after everything else a
// request writes there, so a client sharing one server can read one request's stderr and make a
// negative assertion on it. docs/server-mode.md#stderr-request-marker.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerRequestDoneMarkerTests
{
    // RunSeed.CreateRandomizeRandom's warning: once per test that calls Randomize() with no seed.
    private const string RandomizeWarning = "called Randomize() without a seed";

    private static string WriteBundle(string name, string appId, int codeunitId, string body)
    {
        var root = TestScratch.Dir($"al-runner-server-marker-5168-{name}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Server Request Marker {{name}}",
          "publisher": "Repro5168",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 511680, "to": 511689 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, $"{name}.Codeunit.al"), $$"""
        codeunit {{codeunitId}} "Srv Marker {{name}}"
        {
            Subtype = Test;

            [Test]
            procedure {{name}}Runs()
            var
                N: Integer;
            begin
                {{body}}
                N := Random(10);
                if (N < 1) or (N > 10) then
                    Error('Random(10) returned %1', N);
            end;
        }
        """);
        return root;
    }

    private static string RunTests(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
    });

    private static void AssertPassed(List<string> lines)
    {
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.True(summary.GetProperty("exitCode").GetInt32() == 0, string.Join(" | ", lines));
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
    }

    [SkippableFact]
    public async Task EachRequestsStderrSlice_EndsAtItsMarker_AndHoldsOnlyItsOwnDiagnostics()
    {
        TestArtifacts.SkipIfMissing();
        var withRandomize = WriteBundle("Reseeds", "c5168a01-0000-4000-8000-000000000001", 511680, "Randomize();");
        var without = WriteBundle("Plain", "c5168a02-0000-4000-8000-000000000002", 511681, "");
        try
        {
            await using var server = await CliServer.StartAsync();

            AssertPassed(await server.SendRequestStreamingAsync(RunTests(withRandomize), TimeSpan.FromSeconds(180)));
            Assert.Equal(1, server.RequestsSent);
            var first = await server.StdErrOfRequestAsync(1);
            Assert.Contains(RandomizeWarning, first);

            // Answered on the side channel: no number and no marker, so the next request is 2.
            var ack = await server.SendAsync("{\"command\":\"cancel\"}");
            Assert.Contains("\"ack\"", ack);
            Assert.Equal(1, server.RequestsSent);

            AssertPassed(await server.SendRequestStreamingAsync(RunTests(without), TimeSpan.FromSeconds(180)));
            var second = await server.StdErrOfLastRequestAsync();
            Assert.DoesNotContain(RandomizeWarning, second);
            Assert.DoesNotContain("[server] request 1 done", second);

            // A refused request and shutdown end with a marker too.
            Assert.Contains("\"error\"", await server.SendAsync("{\"command\":\"noSuchCommand\"}"));
            Assert.DoesNotContain(RandomizeWarning, await server.StdErrOfRequestAsync(3));
            Assert.Contains("shutting down", await server.SendAsync("{\"command\":\"shutdown\"}"));
            await server.StdErrOfRequestAsync(4);
            Assert.True(await server.WaitForExitAsync(TimeSpan.FromSeconds(30)));

            // The documented spelling, written out rather than read from RequestDoneMarker.
            var all = server.StdErr;
            foreach (var n in new[] { 1, 2, 3, 4 })
                Assert.Contains($"\n[server] request {n} done\n", all.Replace("\r\n", "\n"));
            Assert.DoesNotContain("[server] request 5 done", all);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(all, "called Randomize\\(\\) without a seed"));
        }
        finally
        {
            try { Directory.Delete(withRandomize, recursive: true); } catch { }
            try { Directory.Delete(without, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("{\"command\":\"runTests\"}", true)]
    [InlineData("{\"command\":\"cancel\"}", false)]
    [InlineData("{\"command\":\"CANCEL\"}", false)]
    [InlineData("not json", true)]
    [InlineData("", false)]
    public void ClientNumbersRequests_AsTheServerDoes(string line, bool numbered)
    {
        Assert.Equal(numbered, CliServer.IsNumberedRequest(line));
    }
}
