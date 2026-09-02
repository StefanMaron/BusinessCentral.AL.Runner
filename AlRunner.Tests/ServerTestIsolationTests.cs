using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1616: --server's `runTests` had no way to ask for per-method test isolation
/// (the CLI's `--test-isolation method` equivalent), so tests that depend on a
/// per-method reset cross-pollute under --server even though the identical CLI
/// invocation passes. These spawn the real runner and need BC artifacts provisioned
/// (see TestArtifacts); when absent they report Skipped with a reason, not Passed.
///
/// See DefineFlagIntegrationTests for why this used to be
/// [Collection("server-serial")] and no longer is — #1809.
///
/// #1804: all four facts share ONE server process via SharedCliServer.
///
/// #1804 review follow-up: this class's bundle generator now takes a
/// <c>variant</c> so each of the five call sites (four facts, one of them —
/// RunTests_TestIsolationDoesNotStickAcrossRequests — calling it twice)
/// produces a DISTINCT app ID and object-ID range, not the one originally
/// hardcoded id shared by all of them. That is not cosmetic:
/// <c>DependencyLoader.TryGetByAppId</c> (see DependencyLoader.cs) caches a
/// compiled module by AppId for the lifetime of the SERVER PROCESS, and
/// returns the cached module for any LATER request whose bundle reports a
/// matching AppId at a DIFFERENT SourcePath — regardless of whether that
/// bundle's actual source content differs. Sharing one server process across
/// facts (this class's whole point) means every fact's request now runs
/// through that same process-lifetime cache, so a shared AppId across facts
/// would mean fact 2+ silently gets fact 1's compiled module back instead of
/// its own — harmless today only because every call site happened to produce
/// byte-identical content, and a live bug the moment anyone edits one call
/// site's table/codeunit body without also touching the others. Distinct
/// AppIds per call site route every fact through a genuine fresh compile
/// instead of resting on that coincidence.
/// </summary>
public class ServerTestIsolationTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerTestIsolationTests(SharedCliServer fixture) => _fixture = fixture;

    // Two [Test] procs in the SAME codeunit both insert a row with the SAME primary
    // key. Under TestIsolation.Codeunit (the default — no reset between methods
    // inside one codeunit), the second Insert must fail with a duplicate-key error.
    // Under TestIsolation.Test ("test"/"method"), state resets before every [Test]
    // proc, so both Insert calls succeed independently.
    //
    // `variant` gives each call site its own AppId (last hex digit) and its own
    // object-ID range (offset by variant*10 from the base 60170) — see the class
    // doc comment for why a shared AppId across call sites is unsafe once facts
    // share a server process.
    private static string MakeIsolationBundle(int variant)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var baseId = 60170 + variant * 10;
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "f3a4b5c6-d7e8-4f90-a1b2-c3d4e5f6071{{variant:x1}}",
          "name": "Runner Extras - Server Isolation Probe {{variant}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "IsoTable.Table.al"), $$"""
        table {{baseId}} "Server Isolation Probe Tbl {{variant}}"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "IsoTest.Codeunit.al"), $$"""
        codeunit {{baseId}} "Server Isolation Probe SX {{variant}}"
        {
            Subtype = Test;

            [Test]
            procedure InsertsFixedKey_First()
            var
                Rec: Record "Server Isolation Probe Tbl {{variant}}";
            begin
                Rec.Init();
                Rec."Code" := 'FIXED';
                Rec.Insert();
            end;

            [Test]
            procedure InsertsFixedKey_Second()
            var
                Rec: Record "Server Isolation Probe Tbl {{variant}}";
            begin
                Rec.Init();
                Rec."Code" := 'FIXED';
                Rec.Insert();
            end;
        }
        """);
        return dir;
    }

    private static string Req(string bundleDir, string? testIsolation)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
            testIsolation,
        });

    [SkippableFact]
    public async Task RunTests_NoTestIsolationField_DefaultsToCodeunit_SecondInsertFails()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeIsolationBundle(variant: 0);
        var server = await _fixture.GetAsync();

        var lines = await server.SendRequestStreamingAsync(Req(bundle, testIsolation: null));
        var (_, d) = ProtocolV2Streaming.Split(lines);

        Assert.Equal(2, d.GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("passed").GetInt32());
        Assert.Equal(1, d.GetProperty("failed").GetInt32());
    }

    [SkippableFact]
    public async Task RunTests_TestIsolationMethod_BothInsertsSucceed()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeIsolationBundle(variant: 1);
        var server = await _fixture.GetAsync();

        var lines = await server.SendRequestStreamingAsync(Req(bundle, testIsolation: "method"));
        var (_, d) = ProtocolV2Streaming.Split(lines);

        Assert.Equal(2, d.GetProperty("total").GetInt32());
        Assert.Equal(2, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("failed").GetInt32());
    }

    [SkippableFact]
    public async Task RunTests_TestIsolationDoesNotStickAcrossRequests()
    {
        // Request 1 asks for "method" (both pass). Request 2 (fresh bundle copy,
        // same server process) omits testIsolation and must fall back to the
        // server's own default (Codeunit) — not silently inherit "method" from
        // request 1.
        TestArtifacts.SkipIfMissing();

        var bundle1 = MakeIsolationBundle(variant: 2);
        var bundle2 = MakeIsolationBundle(variant: 3);
        var server = await _fixture.GetAsync();

        var lines1 = await server.SendRequestStreamingAsync(Req(bundle1, testIsolation: "method"));
        var (_, d1) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(2, d1.GetProperty("passed").GetInt32());

        var lines2 = await server.SendRequestStreamingAsync(Req(bundle2, testIsolation: null));
        var (_, d2) = ProtocolV2Streaming.Split(lines2);
        Assert.Equal(1, d2.GetProperty("passed").GetInt32());
        Assert.Equal(1, d2.GetProperty("failed").GetInt32());
    }

    [SkippableFact]
    public async Task RunTests_UnknownTestIsolationMode_ReturnsError()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = MakeIsolationBundle(variant: 4);
        var server = await _fixture.GetAsync();

        var r = await server.SendAsync(Req(bundle, testIsolation: "bogus"));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("bogus", err.GetString());
    }
}
