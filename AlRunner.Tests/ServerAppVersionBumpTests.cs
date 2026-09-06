// ServerAppVersionBumpTests — issue #2556.
//
// A warm --server session compiles the SAME bundle directory across requests. Editing that
// bundle's app.json `version` between two requests aborted the run with an
// AppIdCollisionException naming the same directory as BOTH sides of the collision. One
// directory cannot collide with itself.
//
// The cause is ordering. DependencyLoader compares identity (Name + Publisher + VERSION)
// before it compares SourcePath, so a version bump on one tree matched #1850's
// "two apps, one id" guard and never reached the same-SourcePath branch that exists
// precisely for server mode's edit-and-rerun contract. The message it produced was written
// for two DIFFERENT paths ("one of these is a stale build ... (pathA) and (pathB)"), so with
// one path it read as nonsense on top of being wrong.
//
// Dedicated server, not SharedCliServer: ServerTests documents that every fact sharing that
// process must present a distinct AppId, and the negative fact below deliberately presents
// ONE AppId at TWO SourcePaths. Running it on the shared server would poison the AppId cache
// for every other fact in that class.
//
// Credit: found and fixed independently by Mikkel Mansa Vilhelmsen (@vhn) in his fork
// (commit 831080ea). The code here is not copied from it.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerAppVersionBumpTests
{
    private const string AppId = "b5555555-5555-5555-5555-555555555555";

    private static string WriteBundle(string suffix, string version, string appName, string appId = AppId, int idBase = 62280)
    {
        var root = TestScratch.Dir("al-runner-server-versionbump-" + suffix);
        Directory.CreateDirectory(root);

        // No "application": below — the Base Application floor is not the subject here and
        // costs ~70 s cold per invocation (.claude/rules/no-base-app-in-csharp-tests.md).
        // Spelling the property in prose is safe again as of #3064: the guard reads a .cs
        // file's string literals, not its comments, so this sentence is also the end-to-end
        // witness for that fix — reverting the guard to a raw-text scan turns this file red.
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "{{appName}}",
          "publisher": "Repro2556",
          "version": "{{version}}",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idBase}}, "to": {{idBase + 9}} } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Probe.Codeunit.al"), $$"""
        codeunit {{idBase}} "Version Bump Probe {{idBase}}"
        {
            Subtype = Test;

            [Test]
            procedure APassingTest()
            begin
                if 1 <> 1 then
                    Error('unreachable');
            end;
        }
        """);
        return root;
    }

    private static string Req(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
    });

    private static void SetVersion(string bundle, string version)
    {
        var path = Path.Combine(bundle, "app.json");
        var text = File.ReadAllText(path);
        var edited = text.Replace("\"version\": \"1.0.0.0\"", $"\"version\": \"{version}\"");
        Assert.NotEqual(text, edited); // guard: the substitution actually applied
        File.WriteAllText(path, edited);
    }

    [SkippableFact]
    public async Task BumpingTheVersionOfTheSameBundle_DoesNotCollideWithItself()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = WriteBundle("bump", "1.0.0.0", "Version Bump Probe");
        try
        {
            await using var server = await CliServer.StartAsync();

            // ── Request 1: baseline. Registers AppId -> (v1.0.0.0, this directory). ──
            var lines1 = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(180));
            var (_, d1) = ProtocolV2Streaming.Split(lines1);
            Assert.Equal(0, d1.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, d1.GetProperty("passed").GetInt32());

            // ── Request 2: same directory, version bumped. This is the whole issue. ──
            SetVersion(bundle, "1.0.0.1");
            var lines2 = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(180));
            var joined2 = string.Join(" | ", lines2);

            // Named explicitly so a future regression says WHY it failed rather than just
            // reporting a non-zero exit code.
            Assert.DoesNotContain("duplicate app id", joined2, StringComparison.OrdinalIgnoreCase);

            var (_, d2) = ProtocolV2Streaming.Split(lines2);
            Assert.Equal(0, d2.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, d2.GetProperty("passed").GetInt32());
            Assert.Equal(0, d2.GetProperty("failed").GetInt32());

            // ── Request 3: bump again, to prove the first bump did not merely get away
            //    with it once by overwriting a single stale entry. ──
            var path = Path.Combine(bundle, "app.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("1.0.0.1", "2.0.0.0"));
            var lines3 = await server.SendRequestStreamingAsync(Req(bundle), TimeSpan.FromSeconds(180));
            Assert.DoesNotContain("duplicate app id", string.Join(" | ", lines3), StringComparison.OrdinalIgnoreCase);
            var (_, d3) = ProtocolV2Streaming.Split(lines3);
            Assert.Equal(0, d3.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, d3.GetProperty("passed").GetInt32());
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    private static (string appDir, string testDir) MakeAppTestPair()
    {
        var root = TestScratch.Dir("al-runner-server-versionbump-dep");
        var appDir = Path.Combine(root, "app");
        var testDir = Path.Combine(root, "tests");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(testDir);

        const string depAppId = "c6666666-6666-6666-6666-666666666666";

        File.WriteAllText(Path.Combine(appDir, "app.json"), $$"""
        {
          "id": "{{depAppId}}",
          "name": "VB2556 Dep App",
          "publisher": "Repro2556",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62300, "to": 62309 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(appDir, "DepApp.al"), """
        codeunit 62300 "VB2556 Dep Helper"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "d7777777-7777-7777-7777-777777777777",
          "name": "VB2556 Dep Tests",
          "publisher": "Repro2556",
          "version": "1.0.0.0",
          "dependencies": [
            {
              "id": "{{depAppId}}",
              "name": "VB2556 Dep App",
              "publisher": "Repro2556",
              "version": "1.0.0.0"
            }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62310, "to": 62319 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "DepTests.al"), """
        codeunit 62310 "VB2556 Dep Tests"
        {
            Subtype = Test;

            [Test]
            procedure DependencyStillAnswers()
            var
                Helper: Codeunit "VB2556 Dep Helper";
            begin
                if Helper.Answer() <> 42 then
                    Error('the dependency module answered %1, not 42', Helper.Answer());
            end;
        }
        """);
        return (appDir, testDir);
    }

    private static string ReqBoth(string appDir, string testDir) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { appDir, testDir },
        packagePaths = Array.Empty<string>(),
    });

    [SkippableFact]
    public async Task BumpingADependencysVersion_DoesNotCollideWithItself()
    {
        TestArtifacts.SkipIfMissing();

        // A version bump across a dependency PAIR, which is the shape that motivated looking
        // at DependencyLoader.LoadAll's own cache check — the third site with the same
        // identity-before-path ordering, which #2556 does not name.
        //
        // Measured, and stated because it bounds what this test proves: this exercises the
        // TryGetByAppId/RegisterLoaded path, NOT LoadAll's branch. Reverting the LoadAll half
        // of the fix leaves this test green, in either bundle order (dependent first and app
        // first were both tried). #1892's cross-bundle dedup registers the app's own compile
        // before its dependent resolves it, so LoadAll finds an entry whose identity already
        // matches. The LoadAll change is kept as a consistency fix for a provably identical
        // ordering, not because this test covers it.
        var (appDir, testDir) = MakeAppTestPair();
        try
        {
            await using var server = await CliServer.StartAsync();

            var lines1 = await server.SendRequestStreamingAsync(ReqBoth(appDir, testDir), TimeSpan.FromSeconds(240));
            var (_, d1) = ProtocolV2Streaming.Split(lines1);
            Assert.Equal(0, d1.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, d1.GetProperty("passed").GetInt32());

            // Bump the dependency's own version AND the dependent's declaration of it, which
            // is what a caller bumping a version actually does.
            foreach (var manifest in new[] { Path.Combine(appDir, "app.json"), Path.Combine(testDir, "app.json") })
            {
                var text = File.ReadAllText(manifest);
                var edited = text.Replace("\"version\": \"1.0.0.0\"", "\"version\": \"1.0.0.1\"");
                Assert.NotEqual(text, edited);
                File.WriteAllText(manifest, edited);
            }

            var lines2 = await server.SendRequestStreamingAsync(ReqBoth(appDir, testDir), TimeSpan.FromSeconds(240));
            var joined2 = string.Join(" | ", lines2);
            Assert.DoesNotContain("duplicate app id", joined2, StringComparison.OrdinalIgnoreCase);

            var (_, d2) = ProtocolV2Streaming.Split(lines2);
            Assert.Equal(0, d2.GetProperty("exitCode").GetInt32());
            // The dependency still resolves and its body still answers 42 — so the fall-through
            // recompiled it rather than dropping it.
            Assert.Equal(1, d2.GetProperty("passed").GetInt32());
            Assert.Equal(0, d2.GetProperty("failed").GetInt32());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(appDir)!, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task TwoDifferentDirectoriesSharingOneAppId_StillCollide()
    {
        TestArtifacts.SkipIfMissing();

        // The negative direction, and the reason the fix is a REORDER rather than a
        // deletion: #1850's guard must keep firing for what it was written for. Without
        // this, "never throw" would satisfy the fact above.
        var first = WriteBundle("collide-a", "1.0.0.0", "Collide App A", idBase: 62280);
        var second = WriteBundle("collide-b", "1.0.0.0", "Collide App B", idBase: 62290);
        try
        {
            await using var server = await CliServer.StartAsync();

            var lines1 = await server.SendRequestStreamingAsync(Req(first), TimeSpan.FromSeconds(180));
            var (_, d1) = ProtocolV2Streaming.Split(lines1);
            Assert.Equal(0, d1.GetProperty("exitCode").GetInt32());

            var lines2 = await server.SendRequestStreamingAsync(Req(second), TimeSpan.FromSeconds(180));
            var joined2 = string.Join(" | ", lines2);

            // Both directories named, which is what makes the message actionable — and
            // what the self-collision message could never be.
            Assert.Contains("duplicate app id", joined2, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(first, joined2, StringComparison.Ordinal);
            Assert.Contains(second, joined2, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(first, recursive: true); } catch { }
            try { Directory.Delete(second, recursive: true); } catch { }
        }
    }
}
