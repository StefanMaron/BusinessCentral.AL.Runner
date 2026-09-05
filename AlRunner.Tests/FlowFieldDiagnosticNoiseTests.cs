// FlowFieldDiagnosticNoiseTests — a PASSING run must not dump bare .NET stack traces.
//
// A green al-language corpus run (exit 0, every test passing) printed 184 KB of stderr:
// 618 stack-frame lines across 102 `--- End of stack trace from previous location ---`
// markers, with no header line anywhere to say what they belonged to. Every CI job log on
// every PR carried it, and a green run read as broken.
//
// The whole 618 lines came from three calls to ONE statement —
// FlowFieldPatches' `catch (Exception ex)` in RecordImpl_CalcFieldsAsync_3, which wrote
// `ex.StackTrace` as its own separate Console.Error line. Two of those three traces are
// ~308 frames each, because the exception is BC's own NavNCLStackOverflowException
// ("...can be caused by recursive function calls...") raised by FlowFieldPatches' depth
// guard 50 levels into a cyclic FlowField formula, rethrown with ExceptionDispatchInfo at
// every level on the way out.
//
// Nothing is wrong on that path. The AL that reaches it is
// `asserterror <Record>.CalcFields(<self-referencing FlowField>)` — the refusal IS the
// expected result, asserted on real BC by the corpus
// (TestCalcFormulaFlowFieldValueTests.Record_CalcFields_SelfReferencingFormula_RaisesTheRecursionError)
// and by this file's fixture here. The exception is still rethrown to AL exactly as before;
// only the printing changed, so .claude/rules/loud-failures.md is untouched — nothing is
// swallowed, and no default value is returned in place of a real answer.
//
// The bug was a printing one, and specifically a HALF-suppressed diagnostic. The header
// line was `[FlowFieldPatches] ex: ...`, which Log.FilteredWriter drops at default
// verbosity by design — it is an internal diagnostic. The trace went out as a SECOND
// Console.Error.WriteLine whose text is a bare `   at ...` block with no `[Component]`
// prefix, so the same filter could not recognise it and let all 618 lines through. The fix
// folds header and trace into one tagged write, so the filter sees the tag and handles both
// together: silent by default, complete under --verbose. That is why the verbose arm below
// is not optional — it is what separates "gated" from "deleted", the exact distinction
// #2210/#2239 were about.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class FlowFieldDiagnosticNoiseTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "FlowFieldRecursionDiagnostics");

    /// <summary>A .NET stack-frame line: leading whitespace, `at `, then a managed name.</summary>
    private static readonly Regex StackFrameLine =
        new(@"^\s+at [A-Za-z_<]", RegexOptions.Compiled);

    private static (string Stdout, string Stderr, int Exit) Run(string alCacheDir, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(' ').Append(TestBuildConfig.BcVersionArg);
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append($" \"{Fixture}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // The two arms differ ONLY by the --verbose flag, so an ambient AL_RUNNER_VERBOSE
        // would make the default arm assert against a verbose run and pass for the wrong
        // reason (or fail for one).
        psi.Environment.Remove("AL_RUNNER_VERBOSE");

        var so = new StringBuilder();
        var se = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        // Parameterless WaitForExit() — the int overload does NOT drain the async readers,
        // so without this the captured text is racy and truncated.
        p.WaitForExit();
        lock (so) lock (se) return (so.ToString(), se.ToString(), p.ExitCode);
    }

    private static string NewCacheDir([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        TestScratch.Dir(Path.Combine("al-runner-flowfield-noise", name));

    private static void AssertFixturePassed(string stdout, string stderr, int exit)
    {
        Assert.True(exit == 0 && stdout.Contains("pass:        2") && stdout.Contains("fail:        0"),
            $"fixture must compile and pass cleanly (exit {exit}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    private static string[] FrameLines(string stderr) =>
        stderr.Split('\n').Where(l => StackFrameLine.IsMatch(l.TrimEnd('\r'))).ToArray();

    /// <summary>
    /// RED (pre-fix): 310 frame lines and 51 rethrow markers on stderr for a run that
    /// passed both its tests. GREEN: not one frame line, and no marker.
    /// </summary>
    [SkippableFact]
    public void DefaultRun_PassingFlowFieldRecursion_PrintsNoBareStackTrace()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (stdout, stderr, exit) = Run(alCacheDir);
            AssertFixturePassed(stdout, stderr, exit);

            var frames = FrameLines(stderr);
            Assert.True(frames.Length == 0,
                $"a passing run must not print bare .NET stack frames on stderr; found "
                + $"{frames.Length}, first: {(frames.Length > 0 ? frames[0].Trim() : "")}");
            Assert.DoesNotContain("End of stack trace from previous location", stderr);
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The other direction, and the reason the fix is a gate rather than a deletion: under
    /// --verbose the SAME run must still carry the full diagnostic — the `[FlowFieldPatches]`
    /// header, the exception BC actually raised, and the frames behind it. A fix that merely
    /// deleted the write would pass the arm above and fail this one.
    /// </summary>
    [SkippableFact]
    public void VerboseRun_PassingFlowFieldRecursion_StillPrintsTheDiagnosticWithItsTrace()
    {
        TestArtifacts.SkipIfMissing();
        var alCacheDir = NewCacheDir();
        try
        {
            var (stdout, stderr, exit) = Run(alCacheDir, "--verbose");
            AssertFixturePassed(stdout, stderr, exit);

            Assert.Contains("[FlowFieldPatches] ex: NavNCLStackOverflowException", stderr);
            Assert.Contains("This can be caused by recursive function calls", stderr);
            Assert.Contains("AlRunner.Patches.FlowFieldPatches.CalcFlowFieldValuesCore", stderr);
            Assert.True(FrameLines(stderr).Length > 0,
                $"--verbose must still carry the frames behind the diagnostic:\n{stderr}");
        }
        finally
        {
            try { Directory.Delete(alCacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The defect was structural, not local to one call site: a `[Component]`-tagged header
    /// written by one Console.Error call followed by a bare `ex.StackTrace` written by the
    /// NEXT one is always half-suppressed, because Log's filter matches per write and a
    /// stack trace carries no tag. Pin every site that had the shape, so a future edit that
    /// splits one back into two writes fails here rather than in a CI log six months later.
    /// </summary>
    [Fact]
    public void NoPatchWritesABareStackTraceAsItsOwnConsoleErrorCall()
    {
        var patchesDir = Path.Combine(RepoRoot, "AlRunner", "Patches");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(patchesDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (!t.StartsWith("Console.Error.WriteLine(", StringComparison.Ordinal)) continue;
                // A write whose ENTIRE argument is a stack trace has no `[Component]` tag for
                // Log.FilteredWriter to match, so it escapes suppression on its own.
                if (Regex.IsMatch(t, @"^Console\.Error\.WriteLine\(\s*[A-Za-z_][A-Za-z0-9_.]*\.StackTrace\b"))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}: {t}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a bare `.StackTrace` written as its own Console.Error call escapes Log's "
            + "[Component] filter and prints at default verbosity; fold it into the tagged "
            + "header line instead:\n  " + string.Join("\n  ", offenders));
    }
}
