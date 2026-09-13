// WatchSiblingSourceDependencyStaleTests — the --watch half of issue #4025.
//
// WatchLayeredDependencyStaleTests (#2683) covers a dependency passed as a second bundle. This one
// passes only the test bundle and leaves the dependency as AL source in a sibling directory, so
// BuildSiblingSourceDeps supplies it. Each cycle re-synthesises the dependency into a new
// content-keyed workspace directory; the dependent must then EXECUTE that new package, not the
// module DependencyLoader compiled from the first cycle's package under the same AppId and version.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingSourceDependencyStaleTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "4025c000-0000-4000-8000-00000000c001";
    private const string TestAppId = "4025c000-0000-4000-8000-00000000c002";

    private static void WriteDepSource(string dir, int answer) =>
        File.WriteAllText(Path.Combine(dir, "Answer.Codeunit.al"), $$"""
        codeunit 64050 "WSS Answer"
        {
            procedure Value(): Integer
            begin
                exit({{answer}});
            end;
        }
        """);

    // The watcher observes the requested bundle; each cycle's edit touches this file too.
    private static void WriteTestSource(string dir, string marker) =>
        File.WriteAllText(Path.Combine(dir, "AnswerTests.Codeunit.al"), $$"""
        // {{marker}}
        codeunit 64055 "WSS Answer Tests"
        {
            Subtype = Test;

            [Test]
            procedure DependencyAnswerIs42()
            var
                Answer: Codeunit "WSS Answer";
            begin
                if Answer.Value() <> 42 then
                    Error('WSS dependency answered %1, expected 42', Answer.Value());
            end;
        }
        """);

    [SkippableFact]
    public async Task Watch_SiblingSourceDependencyEdited_ExecutesTheEditedCode()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-sibling-4025");
        var depDir = Path.Combine(root, "dep-app");
        var testDir = Path.Combine(root, "test-app");
        var cacheDir = TestScratch.Dir("al-runner-watch-sibling-4025-cache");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{DepAppId}}", "name": "Watch Sibling Dep WSS", "publisher": "AL Runner",
          "version": "1.0.0.0", "dependencies": [], "platform": "1.0.0.0",
          "idRanges": [ { "from": 64050, "to": 64054 } ], "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}", "name": "Watch Sibling Dep Tests WSS", "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepAppId}}", "name": "Watch Sibling Dep WSS", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "platform": "1.0.0.0", "idRanges": [ { "from": 64055, "to": 64059 } ], "runtime": "14.0"
        }
        """);
        WriteDepSource(depDir, 42);
        WriteTestSource(testDir, "cycle 1");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{testDir}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(60).Select(l => $"[{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(240);
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException($"watch exited early (exit={p.ExitCode}).\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            throw new TimeoutException($"watch marker not seen.\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        try
        {
            int m1 = await WaitForMarkerAfter(0);
            var cycle1 = Segment(0, m1);
            Assert.True(cycle1.Contains("PASS"), "cycle 1 did not pass:\n" + cycle1);
            Assert.DoesNotContain("FAIL", cycle1);

            // Same AppId, same version, new answer: only the dependency's code moved.
            WriteDepSource(depDir, 99);
            WriteTestSource(testDir, "cycle 2");
            int m2 = await WaitForMarkerAfter(m1 + 1);
            var cycle2 = Segment(m1 + 1, m2);
            Assert.True(cycle2.Contains("WSS dependency answered 99"),
                "cycle 2 did not run the edited sibling dependency:\n" + cycle2);

            // Negative: only the test bundle changes, so the dependency is served from its
            // content-keyed workspace directory, not re-synthesised — and still answers 99.
            WriteTestSource(testDir, "cycle 3");
            int m3 = await WaitForMarkerAfter(m2 + 1);
            var cycle3 = Segment(m2 + 1, m3);
            Assert.True(cycle3.Contains("[source-dep] cache HIT Watch Sibling Dep WSS "),
                "cycle 3 did not serve the unchanged dependency from cache:\n" + cycle3);
            Assert.DoesNotContain("[source-dep] WROTE Watch Sibling Dep WSS ", cycle3);
            Assert.True(cycle3.Contains("WSS dependency answered 99"),
                "cycle 3 did not run the edited sibling dependency:\n" + cycle3);

            // Back to 42: both caches HIT for content this process already compiled.
            WriteDepSource(depDir, 42);
            WriteTestSource(testDir, "cycle 4");
            int m4 = await WaitForMarkerAfter(m3 + 1);
            var cycle4 = Segment(m3 + 1, m4);
            Assert.True(cycle4.Contains("PASS"), "cycle 4 did not go back to passing:\n" + cycle4);
            Assert.DoesNotContain("FAIL", cycle4);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
