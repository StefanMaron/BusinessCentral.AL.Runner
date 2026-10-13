// #5552 (found while re-measuring #3521): the PARTIAL-EMIT-DROP census counted object
// declarations with a line-anchored regex over the file text, so a commented-out object whose
// header starts a line was counted as declared, "dropped" by the compile, and the whole bundle
// refused with exit 3.
//
// Runner-specific: real BC has one parse. No "application" dependency
// (.claude/rules/no-base-app-in-csharp-tests.md); each test raises its own Error().
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PartialEmitDropCensusTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root = TestScratch.Dir("al-runner-partial-emit-drop-census");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private string Bundle(string name, string probeName)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
            {
              "id": "b5552000-0000-4000-8000-000000005552",
              "name": "EmitDropCensus5552",
              "publisher": "Repro5552",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 65520, "to": 65529 } ],
              "runtime": "14.0"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Probe.Codeunit.al"), $$"""
            codeunit 65520 "{{probeName}}"
            {
                Subtype = Test;

                [Test]
                procedure Runs()
                begin
                end;
            }
            """);
        return dir;
    }

    [SkippableFact]
    public void CommentedOutObject_AtLineStart_DoesNotFailTheBundle()
    {
        TestArtifacts.SkipIfMissing();
        var dir = Bundle("commented", "Census Commented Probe");
        // The header starts the line, inside a block comment: not a declaration.
        File.WriteAllText(Path.Combine(dir, "Old.al"), """
            /*
            codeunit 65521 "Old Experiment"
            {
            }
            */
            """);

        var (output, exit) = RunRunner(dir);

        Assert.DoesNotContain("PARTIAL-EMIT-DROP", output);
        Assert.Contains("PASS  Codeunit65520.Runs", output);
        Assert.Equal(0, exit);
    }
}
