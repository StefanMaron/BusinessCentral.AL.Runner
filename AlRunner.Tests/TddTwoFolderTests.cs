// #5037: --tdd with the implementing app and its tests passed as two source folders
// (`al-runner --tdd app test`). Runner-specific (--tdd turning a compile error into generated
// stubs the tests run against), so it lives here, not in the al-language corpus.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddTwoFolderTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "TddTwoFolder");
    private static readonly string AppDir = Path.Combine(FixtureRoot, "app");

    private readonly string _scratch;

    public TddTwoFolderTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-two-folder");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static (string StdOut, string StdErr, int Exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (outSb) lock (errSb) return (outSb.ToString(), errSb.ToString(), p.ExitCode);
    }

    private static string HashDir(string dir) => string.Join("\n",
        Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetFileName(f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));

    private static JsonElement FindTest(List<JsonElement> tests, string nameContains) =>
        tests.Single(t => t.GetProperty("name").GetString()!.Contains(nameContains));

    /// <summary>
    /// The members the test bundle calls are generated into the APP bundle, the app is
    /// recompiled with them, and each test runs against the empty stub (#5147). The tests only
    /// call the procedures, so each passes and names the signature it ran against; the call
    /// could not have run unless the member reached the module the test ran against.
    /// </summary>
    [SkippableFact]
    public void TwoSourceFolders_GenerateIntoTheAppBundleAndRunToTheStub()
    {
        TestArtifacts.SkipIfMissing();
        var before = HashDir(AppDir);

        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json",
            $"\"{AppDir}\"", $"\"{Path.Combine(FixtureRoot, "test")}\"");

        // Every test passes: the exit code is the tests' own, not a --tdd verdict.
        Assert.True(exit == 0, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.Equal(4, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        var tests = root.GetProperty("tests").EnumerateArray().ToList();

        void AssertRanToStub(string testName, string signature, string procName)
        {
            var t = FindTest(tests, testName);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Contains($"\"{procName}\"(", signature);
            Assert.Equal(new[] { $"Loyalty Points: procedure {signature}" },
                t.GetProperty("generatedStubs").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Contains($"Loyalty Points: procedure {signature}", stderr);
        }
        AssertRanToStub("VariableArg_GeneratesDecimalParameter", "\"CalcBasePoints\"(Arg1: Decimal): Integer", "CalcBasePoints");
        AssertRanToStub("LiteralArg_GeneratesIntegerParameter", "\"CalcLit\"(Arg1: Integer): Integer", "CalcLit");
        AssertRanToStub("EnumValueArg_GeneratesEnumParameter", "\"CalcTier\"(Arg1: Enum \"Two Folder Tier\"): Integer", "CalcTier");
        var unrelated = FindTest(tests, "Unrelated_StillPasses");
        Assert.Equal("pass", unrelated.GetProperty("status").GetString());
        Assert.False(unrelated.TryGetProperty("generatedStubs", out _));
        Assert.Contains("--tdd: generated 3 member(s) this run:", stderr);
        Assert.Contains("--tdd: 3 test(s) ran against generated stubs this run:", stderr);

        // In memory only: the app's files on disk are what they were.
        Assert.Equal(before, HashDir(AppDir));
    }

    /// <summary>
    /// The test app's only object calls a member --tdd refuses to generate (a Text argument).
    /// Excluding it leaves nothing to emit; each test must still be reported FAILED naming the
    /// missing symbol — not EMIT-ZERO, exit 3 and no test at all — and the closing line's
    /// claim that failures were reported must then be true.
    /// </summary>
    [SkippableFact]
    public void OnlyObjectCannotBeGenerated_EachTestReportedFailed()
    {
        TestArtifacts.SkipIfMissing();

        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{Path.Combine(_scratch, "cache-testonly")}\"", "--output-json",
            $"\"{AppDir}\"", $"\"{Path.Combine(FixtureRoot, "testonly")}\"");

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        Assert.DoesNotContain("EMIT-ZERO", stderr);
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        foreach (var name in new[] { "TextArg_RefusedAndReportedFailed", "SecondTest_AlsoReportedFailed" })
        {
            var t = FindTest(tests, name);
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Contains("CalcByName", t.GetProperty("message").GetString());
            Assert.Contains("AL0132", t.GetProperty("message").GetString());
        }
        Assert.Contains("every missing symbol was reported as a failed test instead", stderr);
    }

    /// <summary>
    /// With no missing symbol and no failure, the closing line must not claim that missing
    /// symbols were reported as failed tests (#5037).
    /// </summary>
    [SkippableFact]
    public void NothingMissing_ClosingLineClaimsNoReportedFailures()
    {
        TestArtifacts.SkipIfMissing();

        var (stdout, stderr, exit) = RunRunner(
            "--tdd", $"--cache \"{Path.Combine(_scratch, "cache-app")}\"", $"\"{AppDir}\"");

        Assert.DoesNotContain("every missing symbol was reported", stdout + stderr);
        Assert.Contains("--tdd: no members were generated this run — no test referenced a missing symbol.", stdout);
        Assert.True(exit is 0 or 1, $"exit {exit}\n{stderr}");
    }

    /// <summary>
    /// --watch --tdd (#2002) over the two folders, one edit cycle: cycle 1 generates all three
    /// members into the app; the developer then writes CalcLit for real in the app. Cycle 2 must
    /// generate from the edited file again — CalcLit's test runs against the real procedure and
    /// no longer names a stub (a stale overlay would have kept the stub, or declared CalcLit
    /// twice), while CalcBasePoints is still generated and its test still names it (#5147).
    /// </summary>
    [SkippableFact]
    public async Task Watch_OneEditCycle_RegeneratesFromTheEditedApp()
    {
        TestArtifacts.SkipIfMissing();

        var app = Path.Combine(_scratch, "app");
        var test = Path.Combine(_scratch, "test");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(test);
        foreach (var f in Directory.GetFiles(AppDir)) File.Copy(f, Path.Combine(app, Path.GetFileName(f)));
        foreach (var f in Directory.GetFiles(Path.Combine(FixtureRoot, "test"))) File.Copy(f, Path.Combine(test, Path.GetFileName(f)));
        var cuPath = Path.Combine(app, "LoyaltyPoints.Codeunit.al");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{app}\" \"{test}\" --tdd --watch --show-pass --cache \"{Path.Combine(_scratch, "cache-watch")}\"",
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

        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(80).Select(l => $"[{l.Stream}] {l.Text}")); }
        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException($"watch exited early (exit {p.ExitCode}).\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            throw new TimeoutException($"watch marker not seen.\n{DumpTail()}");
        }
        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }
        string Line(string segment, string testName) =>
            segment.Split('\n').FirstOrDefault(l => l.Contains(testName)) ?? $"<no line for {testName}>";

        try
        {
            const string calcLitStub = "ran against generated stub(s): Loyalty Points: procedure \"CalcLit\"(Arg1: Integer): Integer";
            const string calcBaseStub = "ran against generated stub(s): Loyalty Points: procedure \"CalcBasePoints\"(Arg1: Decimal): Integer";
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(180));
            var cycle1 = Segment(0, m1);
            Assert.StartsWith("PASS", Line(cycle1, "LiteralArg_GeneratesIntegerParameter").TrimStart());
            Assert.Contains(calcLitStub, cycle1);
            Assert.Contains(calcBaseStub, cycle1);
            Assert.Contains("--tdd: 3 test(s) ran against generated stubs this run:", cycle1);

            var original = await File.ReadAllTextAsync(cuPath);
            var lastBrace = original.LastIndexOf('}');
            var edited = original[..lastBrace]
                + "    procedure CalcLit(Amount: Integer): Integer\n    begin\n        exit(Amount);\n    end;\n"
                + original[lastBrace..];
            WatchEdit.Replace(cuPath, edited);

            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);
            Assert.StartsWith("PASS", Line(cycle2, "LiteralArg_GeneratesIntegerParameter").TrimStart());
            Assert.DoesNotContain(calcLitStub, cycle2);
            Assert.Contains(calcBaseStub, cycle2);
            Assert.Contains("--tdd: 2 test(s) ran against generated stubs this run:", cycle2);
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
