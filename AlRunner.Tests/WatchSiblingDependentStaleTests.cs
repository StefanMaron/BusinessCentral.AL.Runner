// WatchSiblingDependentStaleTests — issue #4125.
//
// A parent-of-many-apps bundle: a dependency app and a test app that depends on it. WatchSibling-
// SymbolsIncrementalTests (#2672) edits BOTH apps every cycle, so the dependent's own files always
// change. Here the dependency changes ALONE: the dependent's files are byte-identical, which is the
// only case where "my files are unchanged" can be wrong about "my compile is unchanged".
//
// The session is one process, one cycle per phase:
//   c1  start, passes
//   c2  edit ONLY the dependent                   -> incremental path still taken (the control)
//   c3  dependency loses a procedure the dependent calls -> AL0132, not a run-time Function ID error
//   c4  dependency gains it back                  -> compiles and passes again
//   c5  dependency body edit, surface unchanged   -> the dependent sees the new behaviour, no rebuild
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingDependentStaleTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "41250000-0000-4000-8000-00000000d001";
    private const string TestAppId = "41250000-0000-4000-8000-00000000d002";
    private const string DepLine = "[sibling-symbols] WSI Sibling Dep 1.0.0.0: ";
    private const string TestsRebuildLine = "[watch] WSI Sibling Tests: FULL REBUILD";

    internal static void WriteApps(string root)
    {
        var depDir = Path.Combine(root, "wsi-dep");
        var testDir = Path.Combine(root, "wsi-tests");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{DepAppId}}", "name": "WSI Sibling Dep", "publisher": "AL Runner",
          "version": "1.0.0.0", "dependencies": [],
          "idRanges": [ { "from": 64060, "to": 64064 } ], "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}", "name": "WSI Sibling Tests", "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepAppId}}", "name": "WSI Sibling Dep", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "idRanges": [ { "from": 64065, "to": 64069 } ], "runtime": "14.0"
        }
        """);
    }

    internal static void WriteDep(string root, int? extraAnswer)
    {
        var extra = extraAnswer is { } n
            ? $"\n    procedure Extra(): Integer\n    begin\n        exit({n});\n    end;\n"
            : "";
        WatchEdit.Replace(Path.Combine(root, "wsi-dep", "Answer.Codeunit.al"), $$"""
        codeunit 64060 "WSI Answer"
        {
            procedure Value(): Integer
            begin
                exit(42);
            end;
        {{extra}}
        }
        """);
    }

    // Edit N adds a marker test, and --show-pass prints it, so the output says which version of
    // this file a cycle compiled (#4707). Procedures are only ever ADDED: removing or renaming one
    // is a different full-rebuild reason (#5102) and would blur what c2 controls for.
    internal static void WriteTests(string root, int edit)
    {
        var markers = string.Concat(Enumerable.Range(2, Math.Max(0, edit - 1)).Select(n =>
            $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n"));
        WatchEdit.Replace(Path.Combine(root, "wsi-tests", "AnswerTests.Codeunit.al"), $$"""
        codeunit 64065 "WSI Answer Tests"
        {
            Subtype = Test;

            [Test]
            procedure AnswerMatches()
            var
                Answer: Codeunit "WSI Answer";
            begin
                if Answer.Value() + Answer.Extra() <> 100 then
                    Error('WSI answered %1, expected 100', Answer.Value() + Answer.Extra());
            end;
        {{markers}}
        }
        """);
    }

    [SkippableFact]
    public async Task Watch_SiblingDependencyAloneChanges_TheUnchangedDependentIsRecompiledAgainstIt()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-sibling-dependent-4125");
        var cacheDir = TestScratch.Dir("al-runner-watch-sibling-dependent-4125-cache");
        WriteApps(root);
        WriteDep(root, 58);
        WriteTests(root, 1);

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // The parent directory is the bundle, so the dependency is a sibling app group.
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

        string DumpAll() { lock (lines) return string.Join("\n", lines.Select((l, i) => $"[{i}][{l.Stream}] {l.Text}")); }

        // The [watch] FULL REBUILD line is written to stderr and the closing marker to stdout, and
        // list order is pump order, not program order (#1843, #2653). So a cycle's text is read as
        // "from where the quiet before the edit ended", after the output has been quiet again for a
        // while, never as a window closed by the marker's index.
        async Task WaitUntilQuiet()
        {
            int seen;
            do
            {
                lock (lines) seen = lines.Count;
                await Task.Delay(1500);
                if (p.HasExited) throw new TimeoutException($"watch exited early (exit={p.ExitCode}).\n{DumpAll()}");
            } while (lines.Count != seen);
        }

        async Task<string> Cycle(Action? edit)
        {
            int start;
            lock (lines) start = lines.Count;
            edit?.Invoke();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(240);
            while (true)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, start);
                if (found.Count > 0) break;
                if (p.HasExited) throw new TimeoutException($"watch exited early (exit={p.ExitCode}).\n{DumpAll()}");
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"watch marker not seen.\n{DumpAll()}");
                await Task.Delay(200);
            }
            await WaitUntilQuiet();
            lock (lines) return WatchOutputSlicing.MergedJoin(lines, start, lines.Count);
        }

        try
        {
            var c1 = await Cycle(null);
            Assert.True(c1.Contains("PASS") && c1.Contains("AnswerMatches"), "cycle 1 did not pass:\n" + c1);
            Assert.DoesNotContain("FAIL", c1);

            // c2, the control: only the dependent changes, so its incremental baseline is still taken.
            var c2 = await Cycle(() => WriteTests(root, 2));
            Assert.True(c2.Contains("Marker_Edit2") && c2.Contains("PASS"), "cycle 2 did not run the edited test:\n" + c2);
            Assert.DoesNotContain("FAIL", c2);
            Assert.DoesNotContain(TestsRebuildLine, c2);
            Assert.True(c2.Contains(DepLine + "RAD incremental (fast path)"),
                "cycle 2 recompiled the unchanged sibling:\n" + c2);

            // c3: the dependency loses Extra() and the dependent is not touched. BC rejects the
            // dependent at compile time; the run-time Function ID failure is the stale-output symptom.
            var c3 = await Cycle(() => WriteDep(root, null));
            Assert.DoesNotContain("Function ID", c3);
            Assert.True(c3.Contains("AL0132"), "cycle 3 did not report AL0132 for the dependent:\n" + c3);
            Assert.True(c3.Contains(TestsRebuildLine) && c3.Contains("'WSI Sibling Dep'"),
                "cycle 3 did not say the dependent was rebuilt because the dependency changed:\n" + c3);

            // c4: the procedure is back, the dependent is still untouched: it must compile and pass.
            var c4 = await Cycle(() => WriteDep(root, 58));
            Assert.DoesNotContain("AL0132", c4);
            Assert.DoesNotContain("FAIL", c4);
            Assert.True(c4.Contains("PASS") && c4.Contains("Marker_Edit2"), "cycle 4 did not pass again:\n" + c4);
            Assert.True(c4.Contains(TestsRebuildLine), "cycle 4 did not rebuild the dependent:\n" + c4);

            // c5: a body-only edit keeps the surface, so the dependent is NOT rebuilt but still runs
            // the dependency's new code.
            var c5 = await Cycle(() => WriteDep(root, 59));
            Assert.True(c5.Contains("WSI answered 101"), "cycle 5 did not run the edited dependency:\n" + c5);
            Assert.DoesNotContain(TestsRebuildLine, c5);
            Assert.True(c5.Contains(DepLine + "RAD incremental (fast path)"),
                "cycle 5 did not take the fast path for the edited sibling:\n" + c5);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    // The same stale answer without --watch: the dependent's AL-output cache key did not move when
    // only the sibling changed, so a warm --cache served the compile of the OLD surface.
    [SkippableFact]
    public void Cli_SiblingDependencyAloneChanges_AWarmCacheDoesNotServeTheDependentsOldCompile()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-cli-sibling-dependent-4125");
        var cacheDir = TestScratch.Dir("al-runner-cli-sibling-dependent-4125-cache");
        try
        {
            WriteApps(root);
            WriteDep(root, 58);
            WriteTests(root, 1);

            (int Exit, string Output) Run()
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                        + $" \"{root}\" --show-pass --cache \"{cacheDir}\"",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
                };
                using var proc = Process.Start(psi)!;
                var stderr = proc.StandardError.ReadToEndAsync();
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return (proc.ExitCode, stdout + "\n" + stderr.Result);
            }

            var cold = Run();
            Assert.True(cold.Exit == 0 && cold.Output.Contains("PASS"), $"cold run did not pass (exit {cold.Exit}):\n{cold.Output}");

            WriteDep(root, null);
            var warm = Run();
            Assert.DoesNotContain("Function ID", warm.Output);
            Assert.True(warm.Output.Contains("AL0132"), $"warm run did not report AL0132 (exit {warm.Exit}):\n{warm.Output}");
            Assert.Equal(3, warm.Exit);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
