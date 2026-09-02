using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1804 proving tests for <see cref="SharedCliServer"/>.
///
/// Two claims, deliberately kept as SEPARATE tests rather than folded into
/// one, because they are two different risks and either failing on its own
/// must say so unambiguously — the issue's own framing: "a test proving
/// isolation is preserved... or this trades minutes for intermittent false
/// greens":
///
///  1. Sharing actually saves spawns — <see cref="SharedCliServer.GetAsync"/>
///     called N times costs exactly ONE <c>Process.Start</c>, not N. Asserted
///     against a real, observable spawn counter (<see cref="SharedCliServer.SpawnCount"/>),
///     never inferred from wall-clock timing — timing assertions are flaky on
///     a noisy CI runner and, worse, would not fail if the saving regressed on
///     a fast box (see the issue's own noise-floor measurements: a 25% swing
///     between two runs of the SAME commit is normal here).
///  2. Sharing does not leak state — running the SAME bundle identity (same
///     AppId) twice through the SAME shared server must give the SECOND run
///     the SAME fresh company/database state the FIRST run got, not the
///     first run's leftover rows. This deliberately keeps ONE fixed AppId
///     across both calls (unlike ServerTestIsolationTests/ServerStreamingTests,
///     which now give every call site its own AppId — see SharedCliServer's
///     class doc comment on condition (c)) specifically to exercise
///     <c>DependencyLoader.TryGetByAppId</c>'s real cross-request reuse path:
///     the second call's bundle sits at a different SourcePath but the same
///     AppId, so the server reuses run 1's ALREADY-COMPILED module for run 2
///     (asserted below via the response's own <c>cached</c> field) rather than
///     compiling a second, distinct one. If TestExecutor's per-request company
///     reset only ran once per OS process (rather than once per request) — the
///     exact regression a naive "just don't restart the process" change could
///     introduce, and the exact shape the review that hardened this test was
///     about — this test fails, not passes.
/// </summary>
public class SharedCliServerTests
{
    // Table starts empty and inserts exactly 3 rows — every fresh run of this
    // bundle must report the SAME shape (table starts empty, ends with 3 rows).
    // If a prior run's rows leaked forward, the "table must start EMPTY" guard
    // inside the AL test itself fires and the run FAILS instead of PASSING.
    //
    // Fixed AppId on every call — see the class doc comment on why that is
    // deliberate here (this test exists specifically to prove isolation holds
    // THROUGH DependencyLoader.TryGetByAppId's cross-request module reuse, not
    // to avoid it).
    private static string MakeIsolationProbeBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-shared-server-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c4d5e6f7-0819-4a2b-bc3d-4e5f60718293",
          "name": "Runner Extras - Shared Server Isolation Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60400, "to": 60409 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "ProbeTbl.Table.al"), """
        table 60400 "Shared Server Isolation Tbl"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "ProbeTest.Codeunit.al"), """
        codeunit 60400 "Shared Server Isolation SX"
        {
            Subtype = Test;

            [Test]
            procedure InsertsThreeRows_TableStartsEmpty()
            var
                Rec: Record "Shared Server Isolation Tbl";
                CountBefore: Integer;
            begin
                CountBefore := Rec.Count();
                if CountBefore <> 0 then
                    Error('table must start EMPTY on every fresh run, found %1 row(s) already present', CountBefore);

                Rec.Init(); Rec."Code" := '1'; Rec.Insert();
                Rec.Init(); Rec."Code" := '2'; Rec.Insert();
                Rec.Init(); Rec."Code" := '3'; Rec.Insert();

                if Rec.Count() <> 3 then
                    Error('expected 3 rows after inserting 3, got %1', Rec.Count());
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsReq(string bundleDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundleDir },
            packagePaths = Array.Empty<string>(),
        });

    [SkippableFact]
    public async Task GetAsync_CalledManyTimesSequentially_SpawnsExactlyOneProcess()
    {
        TestArtifacts.SkipIfMissing();

        var shared = new SharedCliServer();
        try
        {
            var s1 = await shared.GetAsync();
            var s2 = await shared.GetAsync();
            var s3 = await shared.GetAsync();

            Assert.Equal(1, shared.SpawnCount);
            // Same OS process, not merely the same C# object reference.
            Assert.Equal(s1.Pid, s2.Pid);
            Assert.Equal(s1.Pid, s3.Pid);
            Assert.Same(s1, s2);
            Assert.Same(s1, s3);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task GetAsync_ConcurrentFirstCalls_StillSpawnsExactlyOneProcess()
    {
        // Guards the fixture's own startup gate: N callers racing GetAsync()
        // for the FIRST time (not the sequential-by-construction case above —
        // xUnit runs facts within one class sequentially, but a class fixture
        // is still just a plain object with no guarantee callers serialize
        // themselves) must still converge on ONE spawn, not one per racer.
        TestArtifacts.SkipIfMissing();

        var shared = new SharedCliServer();
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => shared.GetAsync()).ToArray();
            var servers = await Task.WhenAll(tasks);

            Assert.Equal(1, shared.SpawnCount);
            Assert.True(servers.All(s => s.Pid == servers[0].Pid),
                $"expected every racer to observe the SAME pid, got: {string.Join(",", servers.Select(s => s.Pid))}");
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task TwoRunsOfSameBundleIdentity_ThroughSameSharedServer_BothSeeFreshState_NoLeakedRows()
    {
        TestArtifacts.SkipIfMissing();

        var shared = new SharedCliServer();
        try
        {
            var server = await shared.GetAsync();

            // Run 1 (stands in for "test case 1" in a class sharing this
            // server): inserts 3 rows into a table that must start empty.
            var bundle1 = MakeIsolationProbeBundle();
            var lines1 = await server.SendRequestStreamingAsync(RunTestsReq(bundle1));
            var (_, d1) = ProtocolV2Streaming.Split(lines1);
            Assert.Equal(1, d1.GetProperty("passed").GetInt32());
            Assert.Equal(0, d1.GetProperty("failed").GetInt32());

            // Run 2 (stands in for "test case 2"): SAME shared OS process,
            // SAME app identity/table, brand new bundle directory. If run 1's
            // 3 rows (or any other company state) leaked forward into this
            // request, the AL test's own "must start EMPTY" guard fires and
            // THIS run fails. It must pass — the decisive proof that the
            // second test case through a shared process gets the SAME fresh
            // company reset the first one got, not the first one's leftovers.
            var bundle2 = MakeIsolationProbeBundle();
            var lines2 = await server.SendRequestStreamingAsync(RunTestsReq(bundle2));
            var (_, d2) = ProtocolV2Streaming.Split(lines2);
            Assert.Equal(1, d2.GetProperty("passed").GetInt32());
            Assert.Equal(0, d2.GetProperty("failed").GetInt32());
            // Confirms this test actually exercised the risky path rather than
            // incidentally getting a fresh compile: run 2's SourcePath differs
            // from run 1's, so a `cached:true` here can only come from
            // DependencyLoader.TryGetByAppId serving run 1's already-compiled
            // module (see RunBundleForServer's `cached = reusedAsm != null`) —
            // the exact mechanism named in the class doc comment above.
            Assert.True(d2.GetProperty("cached").GetBoolean(),
                "expected run 2 to be served via AppId-based module reuse (same AppId, " +
                "different SourcePath) — if this is false the test is no longer proving " +
                "isolation survives that specific reuse path.");
            Assert.Equal(1, shared.SpawnCount); // still one process — the isolation is per-REQUEST, not per-process.
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }
}
