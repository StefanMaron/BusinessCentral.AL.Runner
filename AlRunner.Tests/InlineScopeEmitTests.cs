using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for #4697: the runner compiles AL in BC's inline-scope emit, and the
/// readers that used to find a method's metadata on its nested <c>_Scope_</c> class find it on
/// the method (<c>AlRunner/Infrastructure/AlScopeKey.cs</c>).
///
/// Pinned here: the emitted shape itself, AL source declaration order of <c>[Test]</c>
/// procedures (TestExecutor reads each procedure's <c>[SignatureSpan]</c>), the AL call stack's
/// line, trigger marker and app tail (read off the method and its module), and that a warm
/// cache HIT serves the same answers. The plain-BC halves are corpus codeunits 60211 (call
/// stack) and 60878 (an order-dependent codeunit); coverage, DAP, value capture and iteration
/// tracking are pinned by their own suites.
/// </summary>
public class InlineScopeEmitTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle, params string[] extra)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var e in extra) args.Append(" \"").Append(e).Append('"');
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string WriteBundle(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4697000-0000-4000-8000-000000004697",
          "name": "InlineScope4697",
          "publisher": "Repro4697",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62790, "to": 62799 } ],
          "runtime": "14.0"
        }
        """);

        // Line numbers below are load-bearing: BC's stack line is the statement's line minus
        // the procedure's signature line.
        File.WriteAllText(Path.Combine(root, "InlineScope.al"), """
        table 62790 "ISE Row"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; PK; Integer) { }
                field(2; Name; Text[30]) { }
            }
            keys { key(PK; PK) { Clustered = true; } }

            trigger OnInsert()
            begin
                if Name = '' then
                    Error('ISE row needs a name');
            end;
        }

        codeunit 62791 "ISE Helper"
        {
            procedure Fail()
            begin
                Error('ISE helper failed');
            end;

            procedure Add(a: Integer; b: Integer): Integer
            var
                c: Integer;
            begin
                c := a + b;
                exit(c);
            end;

            procedure Add(a: Decimal; b: Decimal): Decimal
            begin
                exit(a + b);
            end;
        }

        codeunit 62792 "ISE Tests"
        {
            Subtype = Test;

            [Test]
            procedure Zeta_DeclaredFirst()
            var
                H: Codeunit "ISE Helper";
            begin
                if H.Add(1, 2) <> 3 then Error('Add(Integer) must answer 3');
                if H.Add(1.5, 2.5) <> 4 then Error('Add(Decimal) must answer 4');
            end;

            [Test]
            procedure Middle_ProcedureFrame()
            var
                H: Codeunit "ISE Helper";
                Stack: Text;
            begin
                asserterror H.Fail();
                Stack := GetLastErrorCallStack();
                if not Stack.Contains('"ISE Helper"(CodeUnit 62791).Fail line 2 - InlineScope4697 by Repro4697 version 1.0.0.0') then
                    Error('STACK[%1]', Stack);
            end;

            [Test]
            procedure Alpha_TriggerFrame()
            var
                R: Record "ISE Row";
                Stack: Text;
            begin
                R.PK := 1;
                asserterror R.Insert(true);
                Stack := GetLastErrorCallStack();
                if not Stack.Contains('"ISE Row"(Table 62790).OnInsert(Trigger) line 3 - InlineScope4697 by Repro4697 version 1.0.0.0') then
                    Error('STACK[%1]', Stack);
            end;
        }
        """);
        return root;
    }

    [SkippableFact]
    public void InlineEmit_DeclarationOrderCallStackAndWarmCache()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-inline-scope-4697");
        var bundle = WriteBundle(Path.Combine(scratch, "app"));
        var cache = Path.Combine(scratch, "cache");
        var dump = Path.Combine(scratch, "dump");
        try
        {
            var (cold, coldExit) = RunRunner(bundle, "--cache", cache, "--show-pass", "--verbose", "--dump-csharp", dump);
            Assert.True(coldExit == 0, $"cold run must pass:\n{cold}");
            Assert.Contains("[cache] MISS", cold, StringComparison.Ordinal);

            // The emit mode: procedures run on Ncl's ALMethodScope, with no per-method scope class.
            var tests = File.ReadAllText(Directory.GetFiles(dump, "ISE Tests.cs", SearchOption.AllDirectories).Single());
            Assert.Contains("new ALMethodScope<", tests, StringComparison.Ordinal);
            Assert.DoesNotContain("_Scope", tests, StringComparison.Ordinal);

            AssertDeclarationOrder(cold);

            var (warm, warmExit) = RunRunner(bundle, "--cache", cache, "--show-pass", "--verbose");
            Assert.True(warmExit == 0, $"warm run must pass:\n{warm}");
            Assert.Contains("[cache] HIT", warm, StringComparison.Ordinal);
            AssertDeclarationOrder(warm);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    private static void AssertDeclarationOrder(string output)
    {
        Assert.DoesNotContain("no resolvable AL declaration line", output, StringComparison.Ordinal);
        var order = Regex.Matches(output, @"^PASS\s+Codeunit62792\.(\w+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "Zeta_DeclaredFirst", "Middle_ProcedureFrame", "Alpha_TriggerFrame" }, order);
    }
}
