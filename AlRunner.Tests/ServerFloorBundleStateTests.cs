using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5182: a <c>--server</c> request for a bundle whose app.json declares an <c>application</c>
/// floor must leave nothing behind that changes the NEXT request's answer. The shared suite
/// server's canary (AL table, SingleInstance codeunit, NumberSequence) is the detector.
/// </summary>
public class ServerFloorBundleStateTests
{
    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name));

    /// <summary>The three fixtures #5182 measured, each declaring an <c>application</c> floor with no dependency entry.</summary>
    [SkippableTheory]
    [InlineData("CrossMajorNote")]
    [InlineData("SubscriberScanAudit")]
    [InlineData("BcFloorSkip/healthy-suite")]
    public async Task TableBundle_FloorBundle_TableBundle_ThirdRequestStillReadsItsOwnTable(string floorFixture)
    {
        TestArtifacts.SkipIfMissing();
        var args = new List<string>();
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
        await using var server = await CliServer.StartAsync(args);
        var bundle = SharedServerCanary.WriteBundle();
        try
        {
            var first = await SharedServerCanary.RunAsync(server, bundle);
            SharedServerCanary.AssertBaseline(first);

            var floorRequest = JsonSerializer.Serialize(new
            {
                command = "runTests",
                sourcePaths = new[] { Fixture(floorFixture) },
                packagePaths = Array.Empty<string>(),
            });
            var floor = ServerRunResult.Parse(await server.SendRequestStreamingAsync(floorRequest), "");
            Assert.True(floor.Failed == 0 && floor.Errors == 0 && floor.Passed >= 1, floor.ToString());

            var third = await SharedServerCanary.RunAsync(server, bundle);
            Assert.True(first == third,
                $"the request after a floor bundle answered differently\n--- first ---\n{first}\n--- third ---\n{third}\n--- server stderr ---\n{server.StdErr}");
        }
        finally { try { Directory.Delete(bundle, recursive: true); } catch { } }
    }
}
