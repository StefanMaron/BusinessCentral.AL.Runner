// OutputFormatTests — RED→GREEN guard for --output-json / --output-junit.
//
// Ghost test problem: a test that only checks "some JSON came out" would still pass
// against an empty {} object. These assert on the SHAPE that CI tooling actually
// depends on: per-test name/status/exitCode fields in --output-json, and the
// testsuites/testsuite/testcase/failure structure JUnit consumers parse.
//
// Before this feature existed, --output-json was an unrecognized flag (rejected as
// an input path, producing a "path does not exist" failure) and --output-junit wrote
// nothing. Both assertions below are RED against that prior state.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why this used to be serialized with the
// other runner-subprocess integration tests and no longer is — #1809.
public sealed class OutputFormatTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public OutputFormatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-output-format", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // One passing test, one failing test — enough to prove both status values and
    // the exit-code field are wired correctly, without a large fixture.
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b6b9a1a1-2222-4444-8888-000000000006",
          "name": "OutputFormatFixture",
          "publisher": "Scratch",
          "version": "1.0.0.0",
          "platform": "1.0.0.0",
          "idRanges": [{ "from": 50910, "to": 50919 }],
          "runtime": "8.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Assert.al"), """
        codeunit 50911 "OF Assert"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), """
        codeunit 50910 "OF Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            var
                Assert: Codeunit "OF Assert";

            [Test]
            procedure TestPasses()
            begin
                Assert.AreEqual(4, 4, 'should be equal');
            end;

            [Test]
            procedure TestFails()
            begin
                Assert.AreEqual(5, 4, 'this assertion is designed to fail');
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
        foreach (var a in extraArgs) args.Append($" {a}");
        args.Append($" \"{_root}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        // Keep stderr separate — [bc]/[cache] banner lines would otherwise interleave
        // with the JSON payload on stdout in the merged buffer used by other tests here.
        p.ErrorDataReceived += (_, __) => { };
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void OutputJson_MixedResults_EmitsExpectedShape()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--output-json");

        Assert.Equal(1, exit); // strict-by-default: a failing test means nonzero exit

        // stdout must be JSON-only, per --help's documented contract ("Replace the normal
        // text output with per-test JSON on stdout") — no bundle/suite progress banners,
        // no [layered]/[bc] lines ahead of it. The whole trimmed stream must parse as one
        // JSON object; a caller doing JSON.parse(stdout) must succeed with no preprocessing.
        using var doc = JsonDocument.Parse(output.Trim());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());

        var tests = root.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(2, tests.Count);

        var failing = tests.Single(t => t.GetProperty("name").GetString() == "Codeunit50910.TestFails");
        Assert.Equal("fail", failing.GetProperty("status").GetString());
        Assert.Contains("Assert.AreEqual failed", failing.GetProperty("message").GetString());
        Assert.Contains("OF Tests", failing.GetProperty("stackTrace").GetString());

        var passing = tests.Single(t => t.GetProperty("name").GetString() == "Codeunit50910.TestPasses");
        Assert.Equal("pass", passing.GetProperty("status").GetString());
    }

    [SkippableFact]
    public void OutputJunit_MixedResults_WritesValidXmlWithFailureElement()
    {
        TestArtifacts.SkipIfMissing();

        var junitPath = Path.Combine(_root, "junit-out.xml");
        var (_, exit) = RunRunner($"--output-junit \"{junitPath}\"");

        Assert.Equal(1, exit);
        Assert.True(File.Exists(junitPath), "runner did not write the JUnit file");

        var xml = XDocument.Load(junitPath);
        var suites = xml.Root!;
        Assert.Equal("testsuites", suites.Name.LocalName);
        Assert.Equal("2", suites.Attribute("tests")!.Value);
        Assert.Equal("1", suites.Attribute("failures")!.Value);
        Assert.Equal("0", suites.Attribute("errors")!.Value);

        var testcases = suites.Descendants("testcase").ToList();
        Assert.Equal(2, testcases.Count);

        var failingCase = testcases.Single(tc => tc.Attribute("name")!.Value == "TestFails");
        var failureEl = failingCase.Element("failure");
        Assert.NotNull(failureEl);
        Assert.Contains("Assert.AreEqual failed", failureEl!.Attribute("message")!.Value);

        var passingCase = testcases.Single(tc => tc.Attribute("name")!.Value == "TestPasses");
        Assert.Null(passingCase.Element("failure"));
        Assert.Null(passingCase.Element("error"));
    }
}
