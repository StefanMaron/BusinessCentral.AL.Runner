using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1616: --server's `runTests` had no way to ask for per-method test isolation
/// (the CLI's `--test-isolation method` equivalent), so tests that depend on a
/// per-method reset cross-pollute under --server even though the identical CLI
/// invocation passes. These spawn the real runner and need the BC artifact caches
/// present (~/.bcartifacts.cache); when absent, they skip rather than fail.
/// </summary>
[Collection("server-serial")]
public class ServerTestIsolationTests
{
    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return false;
        return Directory.Exists(Path.Combine(home, ".bcartifacts.cache", "sandbox"));
    }

    // Two [Test] procs in the SAME codeunit both insert a row with the SAME primary
    // key. Under TestIsolation.Codeunit (the default — no reset between methods
    // inside one codeunit), the second Insert must fail with a duplicate-key error.
    // Under TestIsolation.Test ("test"/"method"), state resets before every [Test]
    // proc, so both Insert calls succeed independently.
    private static string MakeIsolationBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-server-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "f3a4b5c6-d7e8-4f90-a1b2-c3d4e5f60718",
          "name": "Runner Extras - Server Isolation Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 60170, "to": 60179 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "IsoTable.Table.al"), """
        table 60170 "Server Isolation Probe Tbl"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "IsoTest.Codeunit.al"), """
        codeunit 60170 "Server Isolation Probe SX"
        {
            Subtype = Test;

            [Test]
            procedure InsertsFixedKey_First()
            var
                Rec: Record "Server Isolation Probe Tbl";
            begin
                Rec.Init();
                Rec."Code" := 'FIXED';
                Rec.Insert();
            end;

            [Test]
            procedure InsertsFixedKey_Second()
            var
                Rec: Record "Server Isolation Probe Tbl";
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

    [Fact]
    public async Task RunTests_NoTestIsolationField_DefaultsToCodeunit_SecondInsertFails()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle = MakeIsolationBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-isolation-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var r = await server.SendAsync(Req(bundle, testIsolation: null));
        var d = JsonSerializer.Deserialize<JsonElement>(r);

        Assert.Equal(2, d.GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("passed").GetInt32());
        Assert.Equal(1, d.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task RunTests_TestIsolationMethod_BothInsertsSucceed()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle = MakeIsolationBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-isolation-cache2", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var r = await server.SendAsync(Req(bundle, testIsolation: "method"));
        var d = JsonSerializer.Deserialize<JsonElement>(r);

        Assert.Equal(2, d.GetProperty("total").GetInt32());
        Assert.Equal(2, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task RunTests_TestIsolationDoesNotStickAcrossRequests()
    {
        // Request 1 asks for "method" (both pass). Request 2 (fresh bundle copy,
        // same server process) omits testIsolation and must fall back to the
        // server's own default (Codeunit) — not silently inherit "method" from
        // request 1.
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle1 = MakeIsolationBundle();
        var bundle2 = MakeIsolationBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-isolation-cache3", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var r1 = await server.SendAsync(Req(bundle1, testIsolation: "method"));
        var d1 = JsonSerializer.Deserialize<JsonElement>(r1);
        Assert.Equal(2, d1.GetProperty("passed").GetInt32());

        var r2 = await server.SendAsync(Req(bundle2, testIsolation: null));
        var d2 = JsonSerializer.Deserialize<JsonElement>(r2);
        Assert.Equal(1, d2.GetProperty("passed").GetInt32());
        Assert.Equal(1, d2.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task RunTests_UnknownTestIsolationMode_ReturnsError()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle = MakeIsolationBundle();
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-isolation-cache4", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var r = await server.SendAsync(Req(bundle, testIsolation: "bogus"));
        var d = JsonSerializer.Deserialize<JsonElement>(r);
        Assert.True(d.TryGetProperty("error", out var err));
        Assert.Contains("bogus", err.GetString());
    }
}
