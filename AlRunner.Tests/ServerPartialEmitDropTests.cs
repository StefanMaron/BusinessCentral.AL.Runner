// #5559 — --server runs the same PARTIAL-EMIT-DROP guard the CLI's bundled loop does.
//
// BC can silently drop an emitted object with no diagnostic, and a dropped FILE is named by one object only
// (ServerEmitExcludedSurvivorsTests has the profile-and-codeunit shapes). What both paths share is the census:
// the sources declare an object the compile neither emitted nor reported dropping. These facts force the
// plain silent drop with AL_RUNNER_TEST_DROP_EMITTED_OBJECT, which removes one emitted source by name, so the
// server's answer is measured with no retry loop, no profile and no label in the way.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerPartialEmitDropTests : IClassFixture<SharedCliServer>
{
    private const string Victim = "Srv Excl Healthy 62411";

    private readonly SharedCliServer _fixture;

    public ServerPartialEmitDropTests(SharedCliServer fixture) => _fixture = fixture;

    private async Task<(int Exit, int Total, int Passed, string ErrorText)> Ask(string bundle, bool affectedOnly = false)
    {
        var server = await _fixture.GetAsync(
            ServerEmitExcludedSurvivorsTests.ServerArgs(),
            new Dictionary<string, string> { ["AL_RUNNER_TEST_DROP_EMITTED_OBJECT"] = Victim });
        var lines = await server.SendRequestStreamingAsync(
            ServerEmitExcludedSurvivorsTests.Req(bundle, affectedOnly), TimeSpan.FromSeconds(300));
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        var text = summary.TryGetProperty("compilationErrors", out var groups)
            ? string.Join(" | ", groups.EnumerateArray()
                .SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString())))
            : "";
        return (summary.GetProperty("exitCode").GetInt32(), summary.GetProperty("total").GetInt32(),
            summary.GetProperty("passed").GetInt32(), text);
    }

    private static string Module(string scratchName, string appId, int from, params int[] codeunitIds)
    {
        var root = TestScratch.Dir(scratchName);
        ServerEmitExcludedSurvivorsTests.WriteApp(root, appId, from);
        foreach (var id in codeunitIds)
            File.WriteAllText(Path.Combine(root, $"Healthy{id}.Codeunit.al"), ServerEmitExcludedSurvivorsTests.Healthy(id));
        return root;
    }

    /// <summary>
    /// The server refuses a module whose compile lost an object nobody reported, on every request and under the
    /// change model's replay, and says which objects the sources declare.
    /// </summary>
    [SkippableFact]
    public async Task ASilentlyDroppedObject_IsRefused_OnEveryRequest_AndUnderReplay()
    {
        TestArtifacts.SkipIfMissing();
        var root = Module("al-runner-server-partial-drop", "5559b001-0000-4000-8000-000000000001", 62410, 62411, 62412);
        try
        {
            foreach (var affectedOnly in new[] { false, false, true, true })
            {
                var a = await Ask(root, affectedOnly);
                Assert.Equal(3, a.Exit);
                Assert.Equal(0, a.Total);
                Assert.Contains("PARTIAL-EMIT-DROP", a.ErrorText);
                Assert.Contains("2 object(s) declared, only 1 emitted", a.ErrorText);
                Assert.Contains(Victim, a.ErrorText);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The control: a module that does not declare the removed object runs, so the refusal above is the census, not the seam.</summary>
    [SkippableFact]
    public async Task AModuleDeclaringNothingMissing_RunsItsTests()
    {
        TestArtifacts.SkipIfMissing();
        var root = Module("al-runner-server-partial-drop-control", "5559b002-0000-4000-8000-000000000002", 62420, 62421);
        try
        {
            var a = await Ask(root);
            Assert.Equal(0, a.Exit);
            Assert.Equal(2, a.Total);
            Assert.Equal(2, a.Passed);
            Assert.DoesNotContain("PARTIAL-EMIT-DROP", a.ErrorText);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
