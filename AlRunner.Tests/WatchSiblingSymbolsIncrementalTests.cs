// WatchSiblingSymbolsIncrementalTests — issue #2672.
//
// A parent-of-many-apps bundle: one directory holding a dependency app and a test app that
// depends on it, so EmitSiblingSymbols writes the dependency's symbols.json every --watch cycle.
// The dependency's symbol compile must reuse one BcCompiler across cycles and take the RAD fast
// path (the same EmitDepSymbolsIncremental mechanism #2669 gave the layered and source-dep paths),
// while the dependent still compiles against the dependency's CURRENT public surface.
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingSymbolsIncrementalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "26720000-0000-4000-8000-00000000d001";
    private const string TestAppId = "26720000-0000-4000-8000-00000000d002";
    private const string DepLine = "[sibling-symbols] WSI Sibling Dep 1.0.0.0: ";

    private static void WriteDep(string dir, string extraProcedures) =>
        WatchEdit.Replace(Path.Combine(dir, "Answer.Codeunit.al"), $$"""
        codeunit 64060 "WSI Answer"
        {
            procedure Value(): Integer
            begin
                exit(42);
            end;
        {{extraProcedures}}
        }
        """);

    // The procedure name carries the cycle, and --show-pass prints it, so the output says which
    // version of this file a cycle compiled (#4707).
    private static string TestName(int cycle) => $"AnswerMatches_Cycle{cycle}";

    private static void WriteTests(string dir, int cycle, string expression, int expected) =>
        WatchEdit.Replace(Path.Combine(dir, "AnswerTests.Codeunit.al"), $$"""
        codeunit 64065 "WSI Answer Tests"
        {
            Subtype = Test;

            [Test]
            procedure {{TestName(cycle)}}()
            var
                Answer: Codeunit "WSI Answer";
            begin
                if {{expression}} <> {{expected}} then
                    Error('WSI answered %1, expected {{expected}}', {{expression}});
            end;
        }
        """);

    private const string ExtraProcedure = """
            procedure Extra(): Integer
            begin
                exit(58);
            end;
        """;

    private const string ValueOverload = """
            procedure Extra(): Integer
            begin
                exit(58);
            end;

            procedure Value(Seed: Integer): Integer
            begin
                exit(Seed);
            end;
        """;

    /// <summary>A parent directory holding the dependency app and the test app that depends on it.</summary>
    private static (string Root, string DepDir, string TestDir) CreateFixture(string tag)
    {
        var root = TestScratch.Dir($"al-runner-sibling-symbols-2672-{tag}");
        var depDir = Path.Combine(root, "wsi-dep");
        var testDir = Path.Combine(root, "wsi-tests");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{DepAppId}}", "name": "WSI Sibling Dep", "publisher": "AL Runner",
          "version": "1.0.0.0", "dependencies": [], "platform": "1.0.0.0",
          "idRanges": [ { "from": 64060, "to": 64064 } ], "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}", "name": "WSI Sibling Tests", "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepAppId}}", "name": "WSI Sibling Dep", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "platform": "1.0.0.0", "idRanges": [ { "from": 64065, "to": 64069 } ], "runtime": "14.0"
        }
        """);
        WriteDep(depDir, "");
        WriteTests(testDir, 1, "Answer.Value()", 42);
        return (root, depDir, testDir);
    }

    // A one-shot run has no baseline to reuse, so the path line is noise there and stays off (#2672).
    [SkippableFact]
    public void Cli_SiblingDependency_DoesNotAnnounceTheSymbolCompilePath()
    {
        TestArtifacts.SkipIfMissing();

        var (root, _, _) = CreateFixture("cli");
        var cacheDir = TestScratch.Dir("al-runner-cli-sibling-symbols-2672-cache");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                    + $" \"{root}\" --cache \"{cacheDir}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            using var p = Process.Start(psi)!;
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var output = stdout + "\n" + stderrTask.Result;
            Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}:\n{output}");
            // The sibling symbols were really built: the dependent compiled and its test ran.
            Assert.Contains("PASS", output);
            Assert.DoesNotContain("WSI answered", output);
            Assert.DoesNotContain("[sibling-symbols]", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Watch_SiblingDependency_TakesTheFastPath_AndTheDependentSeesItsCurrentSurface()
    {
        TestArtifacts.SkipIfMissing();

        var (root, depDir, testDir) = CreateFixture("watch");
        var cacheDir = TestScratch.Dir("al-runner-watch-sibling-symbols-2672-cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // The parent directory is the bundle: that is what routes the dependency through
            // EmitSiblingSymbols rather than BuildSiblingSourceDeps.
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{root}\" --watch --show-pass --cache \"{cacheDir}\"",
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

        // One edit writes the dependency, then the tests. A writer stall longer than the quiet
        // window lets a cycle start between the two (#4707), so an edit can span several cycles:
        // First is the one after the edit, which saw the dependency write because it lands first;
        // Final is the one that compiled this edit's tests, the only one that saw the whole edit.
        async Task<(string First, string Final, int End)> WaitForEdit(int after, int cycle)
        {
            int end = await WaitForMarkerAfter(after + 1);
            var first = Segment(after + 1, end);
            int start = after + 1;
            while (!Segment(start, end).Contains(TestName(cycle)))
            {
                start = end + 1;
                end = await WaitForMarkerAfter(start);
            }
            return (first, Segment(start, end), end);
        }

        void AssertPassed(string cycle, string label)
        {
            Assert.True(cycle.Contains("PASS"), $"{label} did not pass:\n{cycle}");
            Assert.DoesNotContain("FAIL", cycle);
            Assert.DoesNotContain("WSI answered", cycle);
        }

        try
        {
            // Cycle 1: no baseline yet in this process.
            int m1 = await WaitForMarkerAfter(0);
            var cycle1 = Segment(0, m1);
            Assert.True(cycle1.Contains(DepLine + "full compile ("), "cycle 1 did not full-compile the sibling:\n" + cycle1);
            AssertPassed(cycle1, "cycle 1");

            // Cycle 2: the dependency gains a public procedure the dependent now calls. The fast
            // path must be taken AND its symbols must carry Extra(), or the dependent cannot bind it.
            WriteDep(depDir, ExtraProcedure);
            WriteTests(testDir, 2, "(Answer.Value() + Answer.Extra())", 100);
            var (dep2, cycle2, m2) = await WaitForEdit(m1, 2);
            Assert.True(dep2.Contains(DepLine + "RAD incremental (fast path)"),
                "cycle 2 did not take the fast path for the edited sibling:\n" + dep2);
            Assert.DoesNotContain(DepLine + "full compile (", dep2);
            AssertPassed(cycle2, "cycle 2");

            // Cycle 3: only the dependent changes; the unchanged sibling is not recompiled.
            WriteTests(testDir, 3, "(Answer.Value() + Answer.Extra())", 100);
            var (dep3, cycle3, m3) = await WaitForEdit(m2, 3);
            Assert.True(dep3.Contains(DepLine + "RAD incremental (fast path)"),
                "cycle 3 recompiled the unchanged sibling:\n" + dep3);
            Assert.DoesNotContain(DepLine + "full compile (", dep3);
            AssertPassed(cycle3, "cycle 3");

            // Cycle 4: an added overload is the #2548 shape the fast path must refuse. The writer
            // stalls here on purpose: the dependency's cycle runs to its end before the tests are
            // written, so the edit always spans two cycles (#4707). The first still compiles the
            // cycle-3 tests, which bind against the new surface and pass, so only the procedure
            // name tells the two cycles apart.
            WriteDep(depDir, ValueOverload);
            int stalled = await WaitForMarkerAfter(m3 + 1);
            Assert.Contains(TestName(3), Segment(m3 + 1, stalled));
            WriteTests(testDir, 4, "Answer.Value(7)", 7);
            var (dep4, cycle4, _) = await WaitForEdit(m3, 4);
            Assert.True(dep4.Contains(DepLine + "full compile ("),
                "cycle 4 did not fall back for an added overload:\n" + dep4);
            Assert.DoesNotContain(DepLine + "RAD incremental", dep4);
            Assert.Contains(TestName(4), cycle4);
            AssertPassed(cycle4, "cycle 4");
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
