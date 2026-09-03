// ServerAffectedSelectionMultiSourcePathsTests — proves part of #2479: a warm --server
// process's affectedOnly narrowing must survive an edit to a DEPENDENCY app when the request
// has more than one sourcePaths entry (the app + separate test-app shape the README
// documents as supported).
//
// Root cause
// ------------------------------------------------------------------------------------------
// RunBundleForServer builds `selectionEnvironmentKey` — the signal affectedOnly's per-test
// coverage baseline uses to decide whether "the resolved environment changed in a way
// per-test coverage can't reason about" — from `effectivePkgDirs`, the FULL resolved package
// cache dir list. For a multi-sourcePaths request, RunLayeredPrePass/BuildSiblingSourceDeps
// (Program.cs) synthesize one workspace directory PER dependency app, under CacheRoots'
// "workspace-deps" root, and add it to the process-wide packageCacheDirs — that
// directory's PATH is keyed on the dependency's own source CONTENT, so it changes every time
// the dependency is edited, and old entries are never removed (the process-lifetime list only
// grows across requests). Folding that directory into the environment key made every
// dependency edit look like an "environment changed" event, permanently forcing
// affectedOnly to re-run the WHOLE closure on every later request — even though the
// incremental change model had ALREADY identified precisely which object changed
// (changedObjects was populated correctly; forcedFull was still true).
//
// The fix excludes every directory under the workspace-deps root from the environment key
// (effectivePkgDirs itself is untouched, so dependency resolution still sees them).
//
// This is the multi-sourcePaths sibling of ServerAffectedSelectionTests.cs, which only ever
// sends a single sourcePaths entry and therefore never exercises RunLayeredPrePass at all.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionMultiSourcePathsTests
{
    private static string MakeAppBundle(string root, string helperBody)
    {
        var dir = Path.Combine(root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-6001-4a11-9111-111111111111",
          "name": "Multi Affected App SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60360, "to": 60369 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Helper.al"), helperBody);
        return dir;
    }

    private static string MakeTestAppBundle(string root)
    {
        var dir = Path.Combine(root, "test-app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-6002-4a11-9222-222222222222",
          "name": "Multi Affected Test App SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "a1b2c3d4-6001-4a11-9111-111111111111", "name": "Multi Affected App SX",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60370, "to": 60379 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
        codeunit 60370 "Multi Affected Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure OnlyA()
            var
                H: Codeunit "Multi Affected Helper SX";
            begin
                if H.Value() <> 1 then
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
    public async Task AffectedOnly_DependencyEditBetweenRequests_StillNarrows()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-affected-multi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundle(root, """
        codeunit 60360 "Multi Affected Helper SX"
        {
            procedure Value(): Integer
            begin
                exit(1);
            end;
        }
        """);
        var testAppDir = MakeTestAppBundle(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        // Request 1: first cycle, necessarily a full run (no baseline yet).
        var lines1 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events1, summary1) = ProtocolV2Streaming.Split(lines1);
        Assert.Single(events1);
        Assert.Equal("pass", events1[0].GetProperty("status").GetString());

        // Edit the DEPENDENCY app (not the test-app) — a real content change, not a no-op.
        File.WriteAllText(Path.Combine(appDir, "Helper.al"), """
        codeunit 60360 "Multi Affected Helper SX"
        {
            procedure Value(): Integer
            var
                X: Integer;
            begin
                X := 1;
                exit(X);
            end;
        }
        """);

        // Request 2: same warm server process, same two sourcePaths.
        var lines2 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events2, summary2) = ProtocolV2Streaming.Split(lines2);
        Assert.Single(events2);
        Assert.Equal("pass", events2[0].GetProperty("status").GetString());

        Assert.True(summary2.TryGetProperty("selection", out var selection2), string.Join(" | ", lines2));
        // [THEN] narrowing survives a dependency edit — before the fix, the synthesized
        // workspace directory RunLayeredPrePass adds for the edited dependency changed
        // selectionEnvironmentKey, and forcedFull was true with reason "coverage baseline
        // environment changed (BC version/artifact/package cache)" despite changedObjects
        // already correctly identifying the edited codeunit.
        Assert.False(selection2.GetProperty("forcedFull").GetBoolean(),
            $"expected affectedOnly to narrow despite the dependency edit, got: {string.Join(" | ", lines2)}");
        Assert.Contains(selection2.GetProperty("changedObjects").EnumerateArray().Select(x => x.GetString()),
            x => x != null && x.Contains("Multi Affected Helper SX", StringComparison.Ordinal));

        // Request 3: nothing changed — narrowing must still hold (proves the fix didn't
        // just get lucky on request 2's specific key, but leaves a STABLE key going forward).
        var lines3 = await server.SendRequestStreamingAsync(RunTestsRequest(appDir, testAppDir));
        var (events3, summary3) = ProtocolV2Streaming.Split(lines3);
        Assert.True(summary3.TryGetProperty("selection", out var selection3), string.Join(" | ", lines3));
        Assert.False(selection3.GetProperty("forcedFull").GetBoolean(),
            $"expected narrowing to remain stable on an unchanged re-request, got: {string.Join(" | ", lines3)}");
    }

    // #2535: the mirror problem to the one above. `AffectedOnly_DependencyEditBetweenRequests_StillNarrows`
    // proves the CHANGED-object side survives a cross-bundle edit (#2492's PeekChangedObjects
    // union). This proves the COVERAGE-attribution side: a test whose own execution reaches
    // into a DEPENDENCY bundle's object must still get a real coverage entry (not "unmappable"),
    // so narrowing can tell which of two cross-bundle-calling tests is actually affected by an
    // edit — mirroring the single-bundle `AffectedOnly_ChangedObjectRunsOnlyIntersectingTests`
    // shape (edit one helper, only the test that calls it reruns) but with the helpers declared
    // in a SEPARATE (dependency) bundle from the tests that call them.
    //
    // Real-corpus measurement (Pageworks + Pageworks.Test, 1012 tests, instrumented attribution
    // loop): of 1012 tests, 897 were `unmappable` (a statement's FilePath pointed into the
    // Pageworks — dependency — bundle, which the Pageworks.Test module's own
    // TryGetTrackedObjectsByPath map has no entry for), 106 were not-passed/no-statements, and
    // only 9 mapped — every one of those 9 with a coverage-set SIZE OF EXACTLY 1 (their own
    // declaring codeunit only). That rules out the competing "coverage sets are over-attributed
    // and overlap everything" theory: mapped sets are minimal, not enormous — the defect is the
    // 897 unmappable, not over-broad coverage among the few that do map.
    private static string MakeAppBundleTwoHelpers(string root)
    {
        var dir = Path.Combine(root, "app2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-6003-4a11-9333-333333333333",
          "name": "Multi Affected App2 SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60380, "to": 60389 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "HelperA.al"), """
        codeunit 60380 "Multi Affected Helper2A SX"
        {
            procedure ValueA(): Integer
            begin
                exit(1);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "HelperB.al"), """
        codeunit 60381 "Multi Affected Helper2B SX"
        {
            procedure ValueB(): Integer
            begin
                exit(2);
            end;
        }
        """);
        return dir;
    }

    private static string MakeTestApp2Bundle(string root)
    {
        var dir = Path.Combine(root, "test-app2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-6004-4a11-9444-444444444444",
          "name": "Multi Affected Test App2 SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "a1b2c3d4-6003-4a11-9333-333333333333", "name": "Multi Affected App2 SX",
              "publisher": "AL Runner", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 60390, "to": 60399 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
        codeunit 60390 "Multi Affected Tests2 SX"
        {
            Subtype = Test;

            [Test]
            procedure OnlyA()
            var
                H: Codeunit "Multi Affected Helper2A SX";
            begin
                if H.ValueA() <> 1 then
                    Error('OnlyA failed');
            end;

            [Test]
            procedure OnlyB()
            var
                H: Codeunit "Multi Affected Helper2B SX";
            begin
                if H.ValueB() <> 2 then
                    Error('OnlyB failed');
            end;
        }
        """);
        return dir;
    }

    private static string RunTestsRequest2(string appDir, string testAppDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { appDir, testAppDir },
            packagePaths = Array.Empty<string>(),
            affectedOnly = true,
            perTestCoverage = true,
        });

    [SkippableFact]
    public async Task AffectedOnly_CrossBundleCoverage_RunsOnlyTheTestThatCallsTheEditedHelper()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-affected-crossbundle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appDir = MakeAppBundleTwoHelpers(root);
        var testAppDir = MakeTestApp2Bundle(root);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        // Request 1: first cycle (no baseline yet) — necessarily runs both tests and
        // records their per-test coverage, which for EACH test crosses into the
        // DEPENDENCY bundle's helper codeunit (a real cross-app call, not a hypothetical).
        var lines1 = await server.SendRequestStreamingAsync(RunTestsRequest2(appDir, testAppDir));
        var (events1, _) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(2, events1.Count);
        Assert.All(events1, e => Assert.Equal("pass", e.GetProperty("status").GetString()));

        // Edit HelperA only — OnlyB's helper (HelperB) is untouched.
        File.WriteAllText(Path.Combine(appDir, "HelperA.al"), """
        codeunit 60380 "Multi Affected Helper2A SX"
        {
            procedure ValueA(): Integer
            var
                X: Integer;
            begin
                X := 1;
                exit(X);
            end;
        }
        """);

        // Request 2: same warm process. [THEN] each test's coverage entry must have been
        // built from a REQUEST-WIDE file-to-object map (not a per-module one), so its
        // helper-codeunit statement resolved instead of making the whole test
        // "unmappable" -> always-run. Editing HelperA must rerun ONLY OnlyA — before the
        // fix, BOTH were unmappable and BOTH always reran regardless of what changed
        // (ran=2, skipped=0, indistinguishable from a from-scratch run).
        var lines2 = await server.SendRequestStreamingAsync(RunTestsRequest2(appDir, testAppDir));
        var (events2, summary2) = ProtocolV2Streaming.Split(lines2);
        Assert.True(summary2.TryGetProperty("selection", out var selection2), string.Join(" | ", lines2));
        Assert.False(selection2.GetProperty("forcedFull").GetBoolean(),
            $"expected affectedOnly to narrow across the bundle boundary, got: {string.Join(" | ", lines2)}");
        Assert.Equal(1, selection2.GetProperty("ran").GetInt32());
        Assert.Equal(1, selection2.GetProperty("skipped").GetInt32());
        Assert.Single(events2);
        Assert.EndsWith(".OnlyA", events2[0].GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Equal("pass", events2[0].GetProperty("status").GetString());
        Assert.Contains(selection2.GetProperty("changedObjects").EnumerateArray().Select(x => x.GetString()),
            x => x != null && x.Contains("Multi Affected Helper2A SX", StringComparison.Ordinal));
    }
}
