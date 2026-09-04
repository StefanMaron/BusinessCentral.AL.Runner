using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// End-to-end proof for issue #2669, through the REAL <c>RunLayeredPrePass</c> pipeline under
/// <c>--server</c> — not just the underlying <c>BcCompiler.EmitDepSymbolsIncremental</c> mechanism
/// (see <c>BcCompilerEmitDepSymbolsIncrementalTests</c> for that layer).
///
/// Setup: a dependency app ("Layered RAD Lib") + a separate test app ("Layered RAD Tests") that
/// declares a dependency on it and calls into it — the standard AL-Go app + test-app shape #2669
/// describes, and the exact shape <c>RunLayeredPrePass</c> engages for (the test app becomes an
/// "impl" target the pre-pass must synthesize symbols for).
///
/// <see cref="EditingOneCodeunitInTheDependency_TakesTheRadFastPath_AndTheDependentCompilesAgainstTheNewProcedure"/>
/// edits ONLY the dependency between two warm requests and asserts the pre-pass reports
/// "RAD incremental (fast path)" on the SECOND request (not just "faster" — the actual mechanism
/// engaging), and that the dependent app's test, which calls the NEWLY ADDED procedure, passes —
/// proof the fast-path symbols are not a stale replay, because a stale/incomplete symbol table
/// would fail that compile outright (AL0132/AL0185), not silently disagree.
///
/// <see cref="AddingAnOverloadInTheDependency_StillFallsBackToAFullCompile"/> is this class's
/// negative control and the one #2669's own "Contention" section calls out by name: an edit that
/// ADDS AN OVERLOAD must still force a full compile even through the new fast path, because
/// TryEmitIncremental's own #2548 guard is what stops this mechanism from reintroducing the
/// #2603 cross-app silent-rebind hazard. <see cref="ServerCrossAppOverloadRebindTests"/> is the
/// existing #2603 regression test for the CONSUMING bundle's own fallback gating (untouched by
/// this change); this test is the complementary check that the DEPENDENCY's own re-synthesis
/// still refuses to ship stale symbols for the identical edit shape.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class LayeredDepSymbolsIncrementalServerTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _shared;

    private static readonly string CacheDir = Path.Combine(
        Path.GetTempPath(), "al-runner-layered-rad", Guid.NewGuid().ToString("N"), "al-out");

    /// <summary>Same synchronisation anchor <see cref="LayeredCacheTests"/> uses — emitted by the
    /// pre-pass AFTER every per-impl line, so it is sound to slice on for both a positive
    /// ("RAD incremental" is present) and a negative ("full compile" is NOT present) assertion.</summary>
    private const string PrePassTrailer = "[layered] pre-built";

    public LayeredDepSymbolsIncrementalServerTests(SharedCliServer shared) => _shared = shared;

    private const string LibAppId = "d3e4f5a6-7001-4a11-9111-111111111111";
    private const string TestAppId = "d3e4f5a6-7002-4a11-9222-222222222222";

    private static string LibBefore(int libId) => $$"""
        codeunit {{libId}} "Layered RAD Lib"
        {
            procedure Ping(): Integer
            begin
                exit(1);
            end;
        }
        """;

    /// <summary>Content edit to the SAME file/object: one brand new procedure added.</summary>
    private static string LibWithNewProcedure(int libId) => $$"""
        codeunit {{libId}} "Layered RAD Lib"
        {
            procedure Ping(): Integer
            begin
                exit(1);
            end;

            procedure Pong(): Integer
            begin
                exit(41);
            end;
        }
        """;

    /// <summary>The #2548/#2603 hazard shape: a SECOND overload of `Ping`, not a new name.</summary>
    private static string LibWithOverload(int libId) => $$"""
        codeunit {{libId}} "Layered RAD Lib"
        {
            procedure Ping(): Integer
            begin
                exit(1);
            end;

            procedure Ping(Seed: Integer): Integer
            begin
                exit(Seed);
            end;
        }
        """;

    private static string MakeLibBundle(string root, int libId, string lib)
    {
        var dir = Path.Combine(root, "lib");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{LibAppId}}",
          "name": "Layered RAD Lib",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{libId}}, "to": {{libId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Lib.al"), lib);
        return dir;
    }

    private static string MakeTestBundle(string root, int libId, int testId, string testCodeunit)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}",
          "name": "Layered RAD Tests",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{LibAppId}}", "name": "Layered RAD Lib",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{testId}}, "to": {{testId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), testCodeunit);
        return dir;
    }

    private static string Req(params string[] bundles)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = bundles,
            packagePaths = Array.Empty<string>(),
        });

    [SkippableFact]
    public async Task EditingOneCodeunitInTheDependency_TakesTheRadFastPath_AndTheDependentCompilesAgainstTheNewProcedure()
    {
        TestArtifacts.SkipIfMissing();

        var server = await _shared.GetAsync(new[] { "--cache", CacheDir });

        var root = Path.Combine(Path.GetTempPath(), "al-runner-layered-rad", Guid.NewGuid().ToString("N"));
        const int libId = 60560;
        const int testId = 60570;
        var libDir = MakeLibBundle(root, libId, LibBefore(libId));

        // Request 1's test app calls only Ping — Pong does not exist in the dependency yet.
        var testDirBefore = MakeTestBundle(root, libId, testId, $$"""
            codeunit {{testId}} "Layered RAD Tests Cu"
            {
                Subtype = Test;
                [Test]
                procedure PingOnly()
                var
                    Lib: Codeunit "Layered RAD Lib";
                begin
                    if Lib.Ping() <> 1 then
                        Error('WRONG');
                end;
            }
            """);

        var mark1 = server.StdErrMark;
        var lines1 = await server.SendRequestStreamingAsync(Req(libDir, testDirBefore), TimeSpan.FromSeconds(300));
        var (_, summary1) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(0, summary1.GetProperty("exitCode").GetInt32());
        var out1 = await server.StdErrSinceAsync(mark1, PrePassTrailer);
        // First synthesis of this dependency in this warm process: no baseline yet, so the
        // pre-pass must report a full compile — the SAME message a developer sees today.
        Assert.Contains("[layered] Layered RAD Lib 1.0.0.0: full compile (", out1);
        Assert.DoesNotContain("[layered] Layered RAD Lib 1.0.0.0: RAD incremental", out1);

        // Edit ONLY the dependency: same file, same object, a genuinely new procedure. The test
        // app's OWN source also changes here (to call the new procedure) — that is deliberate:
        // this test is about whether the DEPENDENCY's fast-path symbols are correct enough for a
        // REAL downstream compile to bind against, not about the separate #2603 "consumer's own
        // files are untouched" hazard (covered by AddingAnOverloadInTheDependency_... below and by
        // ServerCrossAppOverloadRebindTests).
        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.al"), LibWithNewProcedure(libId));
        var testDirAfter = MakeTestBundle(root, libId, testId, $$"""
            codeunit {{testId}} "Layered RAD Tests Cu"
            {
                Subtype = Test;
                [Test]
                procedure PingAndPong()
                var
                    Lib: Codeunit "Layered RAD Lib";
                begin
                    if Lib.Ping() + Lib.Pong() <> 42 then
                        Error('WRONG-SUM=' + Format(Lib.Ping() + Lib.Pong()));
                end;
            }
            """);

        var mark2 = server.StdErrMark;
        var lines2 = await server.SendRequestStreamingAsync(Req(libDir, testDirAfter), TimeSpan.FromSeconds(300));
        var (events2, summary2) = ProtocolV2Streaming.Split(lines2);
        var out2 = await server.StdErrSinceAsync(mark2, PrePassTrailer);

        // The RED->GREEN proof: the second synthesis of the SAME dependency in this warm process
        // takes the RAD fast path, not a repeat full compile.
        Assert.Contains("[layered] Layered RAD Lib 1.0.0.0: RAD incremental (fast path)", out2);
        Assert.DoesNotContain("[layered] Layered RAD Lib 1.0.0.0: full compile (", out2);

        // Correctness, not just "took the fast path": the dependent app's test — which only
        // compiles and passes if the fast-path symbols genuinely describe Pong — must succeed
        // with the CORRECT summed value. A stale/incomplete symbol table fails AL0132/AL0185 at
        // compile time (exitCode != 0) or, if it somehow compiled, would not know about Pong at
        // all — either way this assertion catches it.
        Assert.Equal(0, summary2.GetProperty("exitCode").GetInt32());
        var joined2 = string.Join(" | ", lines2);
        Assert.DoesNotContain("WRONG-SUM", joined2, StringComparison.Ordinal);
        var status2 = events2.Count > 0 ? events2[^1].GetProperty("status").GetString() : null;
        Assert.True(status2 == "pass", $"expected the dependent app's test to pass. Got: {joined2}");
    }

    [SkippableFact]
    public async Task AddingAnOverloadInTheDependency_StillFallsBackToAFullCompile()
    {
        TestArtifacts.SkipIfMissing();

        var server = await _shared.GetAsync(new[] { "--cache", CacheDir });

        var root = Path.Combine(Path.GetTempPath(), "al-runner-layered-rad", Guid.NewGuid().ToString("N"));
        const int libId = 60580;
        const int testId = 60590;
        var libDir = MakeLibBundle(root, libId, LibBefore(libId));
        var testDir = MakeTestBundle(root, libId, testId, $$"""
            codeunit {{testId}} "Layered RAD Ovl Tests Cu"
            {
                Subtype = Test;
                [Test]
                procedure Trivial()
                begin
                end;
            }
            """);

        var mark1 = server.StdErrMark;
        var lines1 = await server.SendRequestStreamingAsync(Req(libDir, testDir), TimeSpan.FromSeconds(300));
        var (_, summary1) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(0, summary1.GetProperty("exitCode").GetInt32());
        await server.StdErrSinceAsync(mark1, PrePassTrailer);

        // Edit ONLY the dependency: an ADDED OVERLOAD, the one shape TryEmitIncremental itself
        // refuses to fast-path (#2548) because an unmodified caller elsewhere could silently
        // rebind to it. The test app's own source is UNCHANGED here — deliberately, so nothing
        // about this request forces a full compile except the hazard itself.
        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.al"), LibWithOverload(libId));

        var mark2 = server.StdErrMark;
        var lines2 = await server.SendRequestStreamingAsync(Req(libDir, testDir), TimeSpan.FromSeconds(300));
        var (_, summary2) = ProtocolV2Streaming.Split(lines2);
        Assert.Equal(0, summary2.GetProperty("exitCode").GetInt32());
        var out2 = await server.StdErrSinceAsync(mark2, PrePassTrailer);

        Assert.Contains("[layered] Layered RAD Lib 1.0.0.0: full compile (", out2);
        Assert.DoesNotContain("[layered] Layered RAD Lib 1.0.0.0: RAD incremental", out2);
        Assert.Contains("overload", out2, StringComparison.OrdinalIgnoreCase);
    }
}
