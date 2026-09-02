using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Proves the layered-build workspace cache is keyed PER dependency package, so
/// editing one impl does not rebuild the unchanged siblings.
///
/// Setup: app C depends on impls A and B (both source-only, so the layered
/// pre-pass builds them into the workspace cache). After a clean first run we edit
/// ONLY A and re-run: B must report a `[layered] cache HIT` (untouched), while A
/// re-emits. With a combined workspace key, editing A orphans B's cache too and B
/// re-emits — which this test fails on.
///
/// #2377: the two runs are two `runTests` requests to ONE warm <c>--server</c>
/// process (<see cref="SharedCliServer"/>) rather than two CLI spawns. The claim is
/// unchanged because it is the SAME code that answers it either way:
/// <c>RunAllBundlesForServer</c> calls <c>RunLayeredPrePass</c> for any request with
/// more than one sourcePath, exactly as the CLI's bundled loop does, and the pre-pass
/// emits the same `[layered] WROTE` / `[layered] cache HIT` lines (server mode routes
/// them to stderr, which <see cref="CliServer.StdErrSinceAsync"/> slices per request).
/// The `--cache` dir the workspace key lives under is a server STARTUP flag, so both
/// requests share one — which is what the CLI version did too, passing the same
/// cacheDir to both spawns. Measured: 219.7s as two spawns, ~25s as boot + two
/// requests, on the same loaded box.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
/// </summary>
public class LayeredCacheTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _shared;

    /// <summary>
    /// The one `--cache` dir this class's server is started with. Per test-run (a fresh
    /// GUID), so a workspace cache left behind by an earlier invocation can never answer
    /// for this one — the same guarantee the CLI version got from building its cacheDir
    /// under a fresh scratch root.
    /// </summary>
    private static readonly string CacheDir = Path.Combine(
        Path.GetTempPath(), "al-runner-layered-cache", Guid.NewGuid().ToString("N"), "al-out");

    /// <summary>
    /// Emitted by the layered pre-pass AFTER every per-impl WROTE/HIT line, so it is a
    /// sound synchronisation point for this class's negative assertion
    /// ("B must not have been re-WROTE"): once this is in the slice, every line the
    /// pre-pass had to say about A and B is already in it too.
    /// </summary>
    private const string PrePassTrailer = "[layered] pre-built";

    public LayeredCacheTests(SharedCliServer shared) => _shared = shared;

    private static void WriteApp(string dir, string id, string name, int idFrom,
        string codeunit, string? dependsOnJson = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [{{dependsOnJson ?? ""}}],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), codeunit);
    }

    private static string Req(params string[] bundles)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = bundles,
            packagePaths = Array.Empty<string>(),
        });

    [SkippableFact]
    public async Task EditingOneImpl_DoesNotRebuildSiblingImpl()
    {
        TestArtifacts.SkipIfMissing();

        var server = await _shared.GetAsync(new[] { "--cache", CacheDir });

        var root = Path.Combine(Path.GetTempPath(), "al-runner-layered-cache", Guid.NewGuid().ToString("N"));
        var aDir = Path.Combine(root, "A");
        var bDir = Path.Combine(root, "B");
        var cDir = Path.Combine(root, "C");
        // Unique tokens so the first run is always a fresh WROTE, never a HIT from a
        // previous test invocation (the workspace cache is keyed on file content).
        var tokA = Guid.NewGuid().ToString("N");
        var tokB = Guid.NewGuid().ToString("N");
        var idA = "c1000000-0000-4000-8000-000000000a01";
        var idB = "c1000000-0000-4000-8000-000000000b01";
        var idC = "c1000000-0000-4000-8000-000000000c01";

        WriteApp(aDir, idA, "Layered A LC", 60130,
            $"codeunit 60130 \"Layered A Cu LC\"\n{{\n    // marker {tokA}\n    procedure A_Ping(): Integer begin exit(1); end;\n}}\n");
        WriteApp(bDir, idB, "Layered B LC", 60135,
            $"codeunit 60135 \"Layered B Cu LC\"\n{{\n    // marker {tokB}\n    procedure B_Ping(): Integer begin exit(2); end;\n}}\n");
        var depsJson =
            $"{{ \"id\": \"{idA}\", \"name\": \"Layered A LC\", \"publisher\": \"AL Runner\", \"version\": \"1.0.0.0\" }}," +
            $"{{ \"id\": \"{idB}\", \"name\": \"Layered B LC\", \"publisher\": \"AL Runner\", \"version\": \"1.0.0.0\" }}";
        WriteApp(cDir, idC, "Layered C LC", 60140,
            "codeunit 60140 \"Layered C Cu LC\"\n{\n    Subtype = Test;\n    [Test] procedure Trivial() begin end;\n}\n",
            dependsOnJson: depsJson);

        // Run 1 — both impls fresh-built.
        var mark1 = server.StdErrMark;
        var lines1 = await server.SendRequestStreamingAsync(Req(aDir, bDir, cDir), TimeSpan.FromSeconds(300));
        var (_, summary1) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(0, summary1.GetProperty("exitCode").GetInt32());
        var out1 = await server.StdErrSinceAsync(mark1, PrePassTrailer);
        Assert.Contains("[layered] WROTE Layered A LC", out1);
        Assert.Contains("[layered] WROTE Layered B LC", out1);

        // Edit ONLY A.
        await File.WriteAllTextAsync(Path.Combine(aDir, "Probe.Codeunit.al"),
            $"codeunit 60130 \"Layered A Cu LC\"\n{{\n    // marker {tokA}-EDITED\n    procedure A_Ping(): Integer begin exit(99); end;\n}}\n");

        // Run 2 — A re-emits, B must be a cache HIT (the fix); a combined key would
        // rebuild B too.
        var mark2 = server.StdErrMark;
        var lines2 = await server.SendRequestStreamingAsync(Req(aDir, bDir, cDir), TimeSpan.FromSeconds(300));
        var (_, summary2) = ProtocolV2Streaming.Split(lines2);
        Assert.Equal(0, summary2.GetProperty("exitCode").GetInt32());
        var out2 = await server.StdErrSinceAsync(mark2, PrePassTrailer);
        Assert.Contains("[layered] WROTE Layered A LC", out2);
        Assert.Contains("[layered] cache HIT Layered B LC", out2);
        Assert.DoesNotContain("[layered] WROTE Layered B LC", out2);
    }
}
