// ServerCoverageNeverRunProcedureTests — issue #5186.
//
// A `--server` runTests request with `coverage:true` listed no entry for a procedure that never
// ran, where the CLI's --coverage-out reports every instrumented statement and a never-run one at
// 0 hits. The server's table was built from the scope types that recorded a hit, so a client
// could not tell "never ran" from "not instrumented".
//
// The claim is differential rather than a set of hard-coded numbers: the per-line table one
// server request returns must equal the CLI's Cobertura line table for the same bundle, same
// lines and same hit counts. Two bundles are measured, the second an edit of the first, so the
// answer after a recompile is checked against a CLI run of the edited source too — which is what
// catches a table that keeps the first request's hits, or lists a stale generation's statements
// a second time.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerCoverageNeverRunProcedureTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(240);

    private readonly string _root = TestScratch.Dir("al-runner-5186");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Line numbers are load-bearing: Called's exit is Calc.Codeunit.al line 5, NeverCalled's is
    // line 10, and the second codeunit's only statement is Untouched.Codeunit.al line 5.
    private const int CalledLine = 5;
    private const int NeverCalledLine = 10;
    private const int UntouchedLine = 5;

    private static void WriteBundle(string dir, bool testCallsCalled)
    {
        Directory.CreateDirectory(dir);
        // No "application" property — .claude/rules/no-base-app-in-csharp-tests.md.
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "5a3f0b11-5186-4a01-8001-000000005186",
          "name": "SNR Coverage Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 65420, "to": 65439 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Calc.Codeunit.al"), """
        codeunit 65420 "SNR Calc"
        {
            procedure Called(): Integer
            begin
                exit(1);
            end;

            procedure NeverCalled(): Integer
            begin
                exit(2);
            end;
        }
        """);
        // A whole object nothing calls: no scope of it can ever record a hit.
        File.WriteAllText(Path.Combine(dir, "Untouched.Codeunit.al"), """
        codeunit 65422 "SNR Untouched"
        {
            procedure Nobody(): Integer
            begin
                exit(3);
            end;
        }
        """);
        var body = testCallsCalled
            ? "if Calc.Called() <> 1 then\n                Error('x');"
            : "if 1 + 1 <> 2 then\n                Error('x');";
        File.WriteAllText(Path.Combine(dir, "T.Codeunit.al"), $$"""
        codeunit 65421 "SNR Tests"
        {
            Subtype = Test;

            [Test]
            procedure T()
            var
                Calc: Codeunit "SNR Calc";
            begin
                {{body}}
            end;
        }
        """);
    }

    /// <summary>The CLI's own line table for <paramref name="bundle"/>: (file name, line) -> hits.</summary>
    private Dictionary<(string File, int Line), int> CliTable(string bundle, string tag)
    {
        var coveragePath = Path.Combine(_root, $"cobertura-{tag}.xml");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache --coverage --coverage-out \"").Append(coveragePath).Append("\" \"")
            .Append(bundle).Append('"');
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
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();
        Assert.True(p.ExitCode == 0 && File.Exists(coveragePath), $"CLI {tag}: exit {p.ExitCode}\n{output}");

        var table = new Dictionary<(string, int), int>();
        foreach (var cls in XDocument.Load(coveragePath).Descendants("class"))
        {
            var file = Path.GetFileName(cls.Attribute("filename")!.Value.Replace('\\', '/'));
            foreach (var l in cls.Descendants("line"))
                table[(file, int.Parse(l.Attribute("number")!.Value))] = int.Parse(l.Attribute("hits")!.Value);
        }
        return table;
    }

    /// <summary>One server request's per-line table, rolled up the way Cobertura sums statements that share a line.</summary>
    private static (Dictionary<(string File, int Line), int> Table, int Statements, int DistinctStatements, string Diagnostic)
        ServerTable(IReadOnlyList<string> lines, CliServer server)
    {
        var diagnostic = $"--- response ---\n{string.Join("\n", lines)}\n--- stderr ---\n{server.StdErr}";
        var (_, summary) = ProtocolV2Streaming.Split(lines);
        Assert.True(summary.GetProperty("failed").GetInt32() == 0, diagnostic);
        Assert.True(summary.TryGetProperty("coverage", out var coverage), $"no coverage array.\n{diagnostic}");

        var table = new Dictionary<(string, int), int>();
        var identities = new List<(string, int, int, int, int)>();
        foreach (var f in coverage.EnumerateArray())
        {
            var file = Path.GetFileName(f.GetProperty("file").GetString()!.Replace('\\', '/'));
            foreach (var s in f.GetProperty("statements").EnumerateArray())
            {
                var line = s.GetProperty("line").GetInt32();
                table.TryGetValue((file, line), out var sum);
                table[(file, line)] = sum + s.GetProperty("hits").GetInt32();
                identities.Add((file, line, s.GetProperty("column").GetInt32(),
                    s.GetProperty("endLine").GetInt32(), s.GetProperty("endColumn").GetInt32()));
            }
        }
        return (table, identities.Count, identities.Distinct().Count(), diagnostic);
    }

    private static string Request(string bundle) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { bundle },
        packagePaths = Array.Empty<string>(),
        coverage = true,
    });

    private static string Show(Dictionary<(string File, int Line), int> t) =>
        string.Join(", ", t.OrderBy(kv => kv.Key.File, StringComparer.Ordinal).ThenBy(kv => kv.Key.Line)
            .Select(kv => $"{kv.Key.File}:{kv.Key.Line}={kv.Value}"));

    [SkippableFact]
    public async Task ServerCoverage_EqualsTheCliLineTable_AndDoesNotCarryAnEarlierRequestsHits()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Path.Combine(_root, "bundle");
        WriteBundle(bundle, testCallsCalled: true);

        var cliFirst = CliTable(bundle, "first");

        await using var server = await CliServer.StartAsync(new[] { "--cache", Path.Combine(_root, "cache") });

        // ── request 1: Called ran, NeverCalled and a whole untouched object did not ──
        var (first, firstStatements, firstDistinct, diag1) =
            ServerTable(await server.SendRequestStreamingAsync(Request(bundle), RequestTimeout), server);

        // The reported shape: the zero-hit statements are LISTED, at 0 — absent is the defect.
        Assert.True(first.TryGetValue(("Calc.Codeunit.al", NeverCalledLine), out var never),
            $"1: NeverCalled (line {NeverCalledLine}) has no entry. Server table: {Show(first)}\n{diag1}");
        Assert.Equal(0, never);
        Assert.True(first.TryGetValue(("Untouched.Codeunit.al", UntouchedLine), out var untouched),
            $"1: the never-touched codeunit (line {UntouchedLine}) has no entry. Server table: {Show(first)}\n{diag1}");
        Assert.Equal(0, untouched);
        // The other direction: a called line keeps its real count.
        Assert.Equal(1, first[("Calc.Codeunit.al", CalledLine)]);
        Assert.Equal(firstStatements, firstDistinct); // no statement listed twice

        // The whole claim: the server's table IS the CLI's.
        Assert.True(first.OrderBy(k => k.Key).SequenceEqual(cliFirst.OrderBy(k => k.Key)),
            $"1: server != CLI.\nserver: {Show(first)}\nCLI:    {Show(cliFirst)}\n{diag1}");

        // ── request 2, same server: the test no longer calls Called() ──
        WriteBundle(bundle, testCallsCalled: false);
        var cliSecond = CliTable(bundle, "second");
        var (second, secondStatements, secondDistinct, diag2) =
            ServerTable(await server.SendRequestStreamingAsync(Request(bundle), RequestTimeout), server);

        Assert.Equal(0, cliSecond[("Calc.Codeunit.al", CalledLine)]); // the control: the CLI says 0
        Assert.True(second.TryGetValue(("Calc.Codeunit.al", CalledLine), out var calledNow),
            $"2: Called (line {CalledLine}) vanished. Server table: {Show(second)}\n{diag2}");
        Assert.True(calledNow == 0,
            $"2: Called() did not run in this request but carries {calledNow} hit(s) — the first request's. {Show(second)}\n{diag2}");
        // A recompile loads a second generation; its twin of every statement must not be listed too.
        Assert.Equal(secondStatements, secondDistinct);
        Assert.True(second.OrderBy(k => k.Key).SequenceEqual(cliSecond.OrderBy(k => k.Key)),
            $"2: server != CLI.\nserver: {Show(second)}\nCLI:    {Show(cliSecond)}\n{diag2}");
    }
}
