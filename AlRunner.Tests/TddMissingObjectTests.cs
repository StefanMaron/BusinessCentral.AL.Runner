// #5431: --tdd with a test that names a CODEUNIT no app declares (`Missing: Codeunit "No Such Codeunit"`, AL0185).
// Runner-specific (--tdd turning a compile error into a generated object the tests run against), so it lives
// here, not in the al-language corpus. The design: docs/tdd-missing-object.md.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddMissingObjectTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    internal static readonly string FixtureRoot = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TddMissingObject");

    private readonly string _scratch;

    public TddMissingObjectTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-missing-object");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    internal static (string StdOut, string StdErr, int Exit) RunRunner(
        (string Name, string Value)? env, params string[] extraArgs)
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
        if (env is { } e) psi.Environment[e.Name] = e.Value;
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

    internal static string HashTree(string dir) => string.Join("\n",
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(dir, f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));

    internal static string[] StubsOf(JsonElement test) =>
        test.TryGetProperty("generatedStubs", out var s)
            ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();

    internal static string CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        return to;
    }

    // "Another Missing Codeunit" sorts before "No Such Codeunit", so it takes the first free id of the app's range
    // (65320-65324 are the fixture's own codeunits) whichever order the compile reported anything in.
    private const string NoSuchObject = "No Such Codeunit: codeunit 65326";
    private const string AnotherObject = "Another Missing Codeunit: codeunit 65325";
    private const string CalcStub = "No Such Codeunit: procedure \"Calc\"(Arg1: Integer): Integer";
    private const string BonusStub = "No Such Codeunit: procedure \"Bonus\"(Arg1: Boolean): Integer";
    private const string PickStub = "No Such Codeunit: procedure \"Pick\"(Arg1: Integer): Integer";
    private const string TotalStub = "Another Missing Codeunit: procedure \"Total\"(Arg1: Decimal): Decimal";

    /// <summary>The outcome the ordinary run and the reversed-diagnostics run must both give.</summary>
    private static void AssertTheRun((string StdOut, string StdErr, int Exit) run)
    {
        var (stdout, stderr, exit) = run;
        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var root = doc.RootElement;
        var tests = root.GetProperty("tests").EnumerateArray().ToList();
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));

        // An object that exists runs its real body and names no stub; so does a test that touches nothing missing.
        foreach (var name in new[] { "ExistingObject_RunsItsRealBody", "Unrelated_NamesNoStub" })
        {
            var t = Find(name);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Empty(StubsOf(t));
        }

        // A missing member runs to the stub, through a local variable and through a global one, and each test
        // names the object AND the procedure it reaches.
        void AssertStubbed(string name, params string[] stubs)
        {
            var t = Find(name);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Equal(stubs, StubsOf(t));
        }
        AssertStubbed("LocalVariable_RunsToTheStub", NoSuchObject, CalcStub);
        AssertStubbed("GlobalVariable_RunsToTheStub", NoSuchObject, BonusStub);
        AssertStubbed("SameObject_InASecondFile", NoSuchObject, CalcStub);
        AssertStubbed("DisagreeingShapes_TheFirstFileDecides", NoSuchObject, PickStub);
        AssertStubbed("AnotherObject_TakesTheNextId", AnotherObject, TotalStub);
        // A test that only declares the object (and runs it: Run is the codeunit's own, never a generated procedure)
        // names the object and nothing else.
        AssertStubbed("DeclaredOnly_NamesTheObject", NoSuchObject);

        // The stub is empty: the test's own assertion is the red of the loop, and it still names the stubs.
        var own = Find("LocalVariable_AssertsItsOwnResult");
        Assert.Equal("fail", own.GetProperty("status").GetString());
        Assert.Contains("Expected 25, got 0", own.GetProperty("message").GetString());
        Assert.Equal(new[] { NoSuchObject, CalcStub }, StubsOf(own));

        // "Pick" is an Integer in the first file and a Boolean in the later one: the earlier file decides, so the
        // later one no longer compiles and is FAILED, naming the conversion, and carries no stub.
        var lost = Find("DisagreeingShapes_TheSecondFileLoses");
        Assert.Equal("fail", lost.GetProperty("status").GetString());
        Assert.Contains("Cannot implicitly convert type 'Integer' to 'Boolean'", lost.GetProperty("message").GetString());
        Assert.Empty(StubsOf(lost));

        // A Text argument fixes no length: the member is not generated, its file is FAILED naming it.
        var refused = Find("TextArgument_IsRefused");
        Assert.Equal("fail", refused.GetProperty("status").GetString());
        Assert.Contains("AL0132", refused.GetProperty("message").GetString());
        Assert.Contains("Fire", refused.GetProperty("message").GetString());
        Assert.Empty(StubsOf(refused));

        // Counts last, so a red names the test that broke before it reads as a bare count.
        Assert.Equal(11, root.GetProperty("total").GetInt32());
        Assert.Equal(8, root.GetProperty("passed").GetInt32());
        Assert.Equal(3, root.GetProperty("failed").GetInt32());

        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        // One object per name, in name order, with the ids that order gives, in the file that names it first.
        Assert.Equal(new[]
        {
            "--tdd: generated codeunit \"Another Missing Codeunit\" (id 65325) in A2SecondFileTests.Codeunit.al: no app of the run declares it",
            "--tdd: generated codeunit \"No Such Codeunit\" (id 65326) in A1ObjectTests.Codeunit.al: no app of the run declares it",
        }, lines.Where(l => l.StartsWith("--tdd: generated codeunit")).ToList());
        Assert.Contains("--tdd: generated 6 member(s) this run:", stderr);
        Assert.Contains("--tdd: 7 test(s) reach generated stubs this run:", stderr);
        Assert.DoesNotContain("no members were generated", stdout + stderr);
    }

    /// <summary>
    /// A codeunit no app declares is added as an empty object to the test's own file, and the members its call sites
    /// need are generated against it; a codeunit that exists keeps its real body; what cannot be generated is FAILED
    /// without taking the other files down; nothing is written.
    /// </summary>
    [SkippableFact]
    public void MissingCodeunit_RunsToGeneratedObject_AndNothingIsWritten()
    {
        TestArtifacts.SkipIfMissing();
        var app = CopyFolder(FixtureRoot, Path.Combine(_scratch, "app"));
        var before = HashTree(app);

        var run = RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

        AssertTheRun(run);
        Assert.Equal(before, HashTree(app));
    }

    /// <summary>The compile reports its diagnostics in an order that changes between runs. Fed backwards, the run
    /// gives the outcome of the ordinary one: ids, the shape "Pick" is generated with, and the annotations.</summary>
    [SkippableFact]
    public void MissingCodeunit_ReversedDiagnosticOrder_GivesTheSameOutcome()
    {
        TestArtifacts.SkipIfMissing();
        var app = CopyFolder(FixtureRoot, Path.Combine(_scratch, "app"));

        var run = RunRunner(("AL_RUNNER_TDD_DIAG_ORDER", "reverse"),
            "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

        AssertTheRun(run);
    }

    /// <summary>
    /// Room for ONE more codeunit id in the app's range: the first object by name takes it, the second is refused and
    /// the run says that the id was the reason, naming it; the files naming only the refused one are FAILED as before.
    /// </summary>
    [SkippableFact]
    public void OneFreeId_GoesToTheFirstName_TheOtherIsRefusedAndNamed()
    {
        TestArtifacts.SkipIfMissing();
        var app = CopyFolder(FixtureRoot, Path.Combine(_scratch, "app"));
        var manifest = Path.Combine(app, "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"to\": 65339", "\"to\": 65325"));

        var (stdout, stderr, exit) = RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{app}\"");

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Contains("--tdd: generated codeunit \"Another Missing Codeunit\" (id 65325) in A2SecondFileTests.Codeunit.al: no app of the run declares it", stderr);
        Assert.Contains("--tdd: codeunit \"No Such Codeunit\" not generated - no free codeunit id in the app's idRanges for it", stderr);
        Assert.DoesNotContain("--tdd: generated codeunit \"No Such Codeunit\"", stderr);
        // Every file names "No Such Codeunit", so none of them compiles.
        Assert.All(tests, t => Assert.Equal("fail", t.GetProperty("status").GetString()));
        Assert.All(tests, t => Assert.Empty(StubsOf(t)));
        Assert.Contains(tests, t => t.GetProperty("message").GetString()!.Contains("AL0185"));
    }
}
