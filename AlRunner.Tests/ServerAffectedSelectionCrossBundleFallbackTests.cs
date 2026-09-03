// ServerAffectedSelectionCrossBundleFallbackTests — proves #2492: a warm --server process's
// affectedOnly narrowing must not silently drop a test in one bundle when a SIBLING bundle in
// the SAME multi-sourcePaths request could not be attributed to a change model this cycle.
//
// Root cause (see issue #2492 for the full investigation against a real corpus — the mechanism
// this test isolates, not the RAD "already declared" trigger that surfaced it there)
// ------------------------------------------------------------------------------------------
// Each bundle's own affectedOnly selection is decided from ONLY that bundle's own
// TryEmitIncremental cycle: its own `changedObjects` and its own `changeModelFallbackReason`.
// When bundle A's cycle falls back (RAD failure, an app.json edit, anything
// TryEmitIncremental itself cannot narrow), bundle A correctly runs everything — but a SIBLING
// bundle B, whose OWN files did not change, sees an EMPTY changedObjects set and happily
// narrows using its OWN per-test coverage baseline. If B's covered-objects entry for a test
// does not happen to overlap whatever changed (a real limitation of this corpus's per-test
// coverage attribution — see #2492's own writeup), that test is silently skipped even though
// bundle A could not prove nothing relevant to it changed. A green run then reports fewer
// tests than a from-scratch run, with no error (RunnerOutOfScopeException doesn't apply here —
// this is a selection-narrowing defect, not an unsupported-surface one).
//
// The fix: RunAllBundlesForServer now propagates ANY bundle's changeModelFallbackReason to
// every bundle processed AFTER it in the SAME request (bundles are processed in sourcePaths
// order — dependency app before test app is the supported/documented shape), forcing THEIR
// selection to forcedFull too rather than trusting narrowing it cannot prove correct.
//
// Trigger used here: editing the dependency's Helper.al to declare a SECOND object in the
// same file. TryEmitIncremental's own fast path requires exactly one declared object per
// touched file — one of the issue's own documented forced-fallback conditions — so this
// deterministically makes the dependency's NEXT cycle fall back with a non-null
// changeModelFallbackReason, without touching app identity/version (which collides with the
// layered-prepass's own content-keyed workspace cache under an unrelated guard, #1850). This
// is a DIFFERENT trigger than the RAD "already declared" failure #2492 was actually filed
// against (attempts at a minimal repro for THAT specific trigger did not reproduce in a small
// fixture); this test exercises the selection-propagation fix directly, independent of which
// of TryEmitIncremental's own branches set changeModelFallbackReason.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionCrossBundleFallbackTests
{
    private static string MakeAppBundle(string root, string version)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "a1b2c3d4-7001-4a11-9111-111111111111",
          "name": "CrossBundle Fallback App SX",
          "publisher": "AL Runner",
          "version": "{{version}}",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60380, "to": 60389 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Helper.al"), """
        codeunit 60380 "CrossBundle Helper SX"
        {
            procedure Value(): Integer
            begin
                exit(1);
            end;
        }
        """);
        return dir;
    }

    private static string MakeTestAppBundle(string root)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-7002-4a11-9222-222222222222",
          "name": "CrossBundle Fallback Test App SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "a1b2c3d4-7001-4a11-9111-111111111111", "name": "CrossBundle Fallback App SX",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60390, "to": 60399 } ],
          "runtime": "14.0"
        }
        """);
        // OnlyA deliberately does NOT call into the dependency's Helper codeunit — a
        // self-contained test whose recorded coverage maps cleanly to ONLY its own codeunit
        // (no cross-bundle statement, so no "unmappable -> unknown -> always safely included"
        // escape hatch masking the bug this test proves). That is exactly the shape #2492's
        // real corpus repro's at-risk tests turned out to have: coverage attribution only ever
        // resolves a test's own declaring codeunit, never a helper it calls — see this file's
        // header comment. The dependency is still DECLARED (app.json), which is what makes
        // this a genuine multi-sourcePaths request exercising RunLayeredPrePass.
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
        codeunit 60390 "CrossBundle Fallback Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure OnlyA()
            begin
                if 1 <> 1 then
                    Error('OnlyA failed');
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsRequest(string appDir, string testAppDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { appDir, testAppDir },
            packagePaths = Array.Empty<string>(),
            affectedOnly = true,
            perTestCoverage = true,
        });

    [SkippableFact]
    public async Task AffectedOnly_SiblingBundleFallback_DoesNotSilentlySkipUnrelatedTest()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-affected-crossfallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundle(root, "1.0.0.0");
        var testAppDir = MakeTestAppBundle(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        // Request 1: first cycle for both bundles, necessarily a full run (no baseline yet).
        var lines1 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events1, summary1) = ProtocolV2Streaming.Split(lines1);
        Assert.Single(events1);
        Assert.Equal("pass", events1[0].GetProperty("status").GetString());
        Assert.True(summary1.TryGetProperty("selection", out var selection1), string.Join(" | ", lines1));
        Assert.Equal(1, selection1.GetProperty("ran").GetInt32());
        Assert.Equal(0, selection1.GetProperty("skipped").GetInt32());

        // Make the DEPENDENCY app's Helper.al declare a SECOND object in the same file.
        // TryEmitIncremental's own fast-path requires exactly one declared object per touched
        // file (ClassifyDeclaredObject returns "declares 2 object(s) ... fast path requires
        // exactly 1 per file", the issue's own documented forced-fallback condition) — this
        // app's NEXT cycle falls back with a non-null changeModelFallbackReason, deterministic
        // and without touching app identity/version (which collides with the layered-prepass's
        // own content-keyed workspace cache under a different, unrelated guard — #1850).
        // Unlike the RAD "already declared" trigger #2492 was actually filed against (which a
        // minimal fixture does not reproduce — see this file's header comment), this exercises
        // a DIFFERENT one of TryEmitIncremental's own documented fallback triggers, which is
        // enough: the fix under test reacts to changeModelFallbackReason being non-null,
        // regardless of which of TryEmitIncremental's own branches set it.
        File.WriteAllText(Path.Combine(appDir, "Helper.al"), """
        codeunit 60380 "CrossBundle Helper SX"
        {
            procedure Value(): Integer
            begin
                exit(1);
            end;
        }
        codeunit 60381 "CrossBundle Helper SX 2"
        {
        }
        """);

        // Request 2: same warm server process, same two sourcePaths. The TEST app's own files
        // are untouched, so its own incremental cycle sees zero changes and would, pre-fix,
        // narrow using an empty changed-object set against OnlyA's own recorded coverage —
        // which does not overlap an empty set, so OnlyA gets silently skipped despite the
        // dependency app being unable to prove nothing relevant to it changed.
        var lines2 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events2, summary2) = ProtocolV2Streaming.Split(lines2);
        Assert.True(summary2.TryGetProperty("selection", out var selection2), string.Join(" | ", lines2));

        // [THEN] the sibling bundle's unattributable cycle forces this bundle's own narrowing
        // off too — OnlyA runs, it is not silently skipped. Before the fix, `ran` was 0 and
        // `skipped` was 1 here even though the overall exit code and `total` still read as a
        // clean, successful run — exactly the "green but fewer tests than exist" defect #2492
        // reports. Asserting on the raw event stream (not just the selection counters) proves
        // the test genuinely EXECUTED, not merely that the counters claim it did.
        Assert.Single(events2);
        Assert.Equal("pass", events2[0].GetProperty("status").GetString());
        Assert.Equal("OnlyA", events2[0].GetProperty("name").GetString().Split('.')[^1]);
        Assert.Equal(1, selection2.GetProperty("ran").GetInt32());
        Assert.Equal(0, selection2.GetProperty("skipped").GetInt32());
        Assert.True(selection2.GetProperty("forcedFull").GetBoolean(),
            $"expected the dependency bundle's unattributable cycle to force this bundle's " +
            $"narrowing off too, got: {string.Join(" | ", lines2)}");
    }
}
