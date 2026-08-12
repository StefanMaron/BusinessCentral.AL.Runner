// DefineFlagIntegrationTests — real RED→GREEN guard for --define / --preprocessor-symbols.
//
// Ghost test problem: the previous runner-extras suite gated BOTH the helper and
// the assertion on #if MY_TEST_SYMBOL, so it passed 0=0 without the flag and 1=1
// with it — both green regardless of whether the symbol ever reached ParseOptions.
// Deleting the `.Concat(_extraPreprocessorSymbols ?? [])` line would leave every
// test green.
//
// Fix: the [Test] codeunit below asserts UNCONDITIONALLY that GetCompiledBranch()=1.
// Only the helper is gated by #if. Without --define the #else branch compiles and
// the assertion fails (FAIL, non-zero exit). With --define MY_TEST_SYMBOL the #if
// branch compiles and the assertion passes (PASS, exit 0 with --strict).
//
// Test A proves the flag is necessary (RED without it).
// Test B proves the flag works (GREEN with --define).
// Test C proves the alias --preprocessor-symbols works (GREEN).
// Reverting the .Concat line makes B and C fail → real guard.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Serialized with the other runner-subprocess integration tests: each spawns a real
// `dotnet run --project AlRunner` process (native BC engine, R2R/EventPipe). Running
// several concurrently under xUnit's default parallelization contends for shared
// caches and native process state and has produced SIGBUS crashes (exit code 135 =
// 128+SIGBUS) — a flake that does not reproduce when the same invocation runs alone.
[Collection("server-serial")]
public sealed class DefineFlagIntegrationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public DefineFlagIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-define-flag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a minimal AL package to <paramref name="dir"/>:
    ///   - app.json (no dependencies, id range 62100..62109)
    ///   - Assert codeunit (integer AreEqual)
    ///   - Test codeunit: asserts UNCONDITIONALLY that GetCompiledBranch() == 1.
    ///     Only the helper (GetCompiledBranch) is #if-gated.
    ///     Without MY_TEST_SYMBOL: #else → exit(0) → 1≠0 → FAIL.
    ///     With    MY_TEST_SYMBOL: #if  → exit(1) → 1=1 → PASS.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "f1a2b3c4-d5e6-7890-abcd-ef1234567890",
          "name": "Define Flag Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 62100, "to": 62109 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), """
        codeunit 62101 "DFT Assert"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "DefineFlagTest.Codeunit.al"), """
        codeunit 62100 "Define Flag Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "DFT Assert";

            // The assertion is UNCONDITIONAL: always expects 1.
            // Only GetCompiledBranch is #if-gated.
            // Without MY_TEST_SYMBOL: #else compiles → exit(0) → 1≠0 → FAIL.
            // With    MY_TEST_SYMBOL: #if  compiles → exit(1) → 1=1 → PASS.
            [Test]
            procedure SymbolDefinedBranchMustBe1()
            begin
                Assert.AreEqual(1, GetCompiledBranch(), 'MY_TEST_SYMBOL must be defined');
            end;

            local procedure GetCompiledBranch(): Integer
            begin
        #if MY_TEST_SYMBOL
                exit(1);
        #else
                exit(0);
        #endif
            end;
        }
        """);
    }

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --strict");
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// Without --define the #else branch compiles, the unconditional assert(1==0)
    /// fails, and the runner exits non-zero (--strict).  This is the RED proof.
    /// </summary>
    [SkippableFact]
    public void WithoutDefine_TestFails()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();

        Assert.NotEqual(0, exit);
        Assert.Contains("FAIL", output);
    }

    /// <summary>
    /// With --define MY_TEST_SYMBOL the #if branch compiles, the assert(1==1)
    /// passes, and the runner exits 0.  Reverting the .Concat line breaks this test.
    /// </summary>
    [SkippableFact]
    public void WithDefine_TestPasses()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--define MY_TEST_SYMBOL");

        Assert.Equal(0, exit);
        Assert.Contains("PASS", output);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }

    /// <summary>
    /// Same as WithDefine_TestPasses but uses --preprocessor-symbols (the batch alias).
    /// </summary>
    [SkippableFact]
    public void WithPreprocessorSymbols_TestPasses()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--preprocessor-symbols MY_TEST_SYMBOL");

        Assert.Equal(0, exit);
        Assert.Contains("PASS", output);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }
}
