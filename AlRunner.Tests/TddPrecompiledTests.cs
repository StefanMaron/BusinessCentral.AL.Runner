// #5037: --tdd with the implementing codeunit arriving as a PACKAGE (`al-runner --tdd test`, the app
// loaded from the test folder's .alpackages), not as a source folder of the run. Runner-specific
// (--tdd turning a compile error into a generated stub the tests run against), so it lives here, not in
// the al-language corpus. Two shapes of package: symbols plus a precompiled DLL (.deps-bin, Tier 1), and
// symbols plus the app's own source (compiled by the run, Tier 3). The design: docs/tdd-precompiled.md.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddPrecompiledTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TddPrecompiled");

    private const string DepAppId = "3c5e7a10-8b24-4d96-b1f3-5a7d2e9c4086";
    private const string DepFileStem = "AL_Runner_Fixtures_Tdd_Precompiled_Dep_1.0.0.0";

    private readonly string _scratch;

    public TddPrecompiledTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-precompiled");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>The package of the fixture's dep app: what a compiled .app carries, with the ids of the
    /// methods in the DLL (the call dispatches by them), and optionally the source.</summary>
    private static byte[] BuildDepApp(bool embedSource)
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepAppId}" Name="Tdd Precompiled Dep" Publisher="AL Runner Fixtures" Version="1.0.0.0" Target="Cloud" ShowMyCode="true" PropagateDependencies="false" />
              <IdRanges><IdRange MinObjectId="65300" MaxObjectId="65319" /></IdRanges>
              <Dependencies />
            </Package>
            """;
        var symbols = """
            {"Codeunits":[{"Id":65300,"Name":"Precompiled Points","Methods":[
              {"Id":-2135483332,"Name":"Existing","ReturnTypeDefinition":{"Name":"Integer"},"Properties":[]},
              {"Id":1516892452,"Name":"Twice","ReturnTypeDefinition":{"Name":"Integer"},"Parameters":[{"Name":"Value","TypeDefinition":{"Name":"Integer"}}],"Properties":[]}
            ],"ReferenceSourceFileName":"PrecompiledPoints.Codeunit.al","Properties":[]}],
            "AppId":"@APPID@","Name":"Tdd Precompiled Dep","Publisher":"AL Runner Fixtures","Version":"1.0.0.0"}
            """.Replace("@APPID@", DepAppId);
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="json" ContentType="" /><Default Extension="al" ContentType="" /></Types>""");
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols);
            if (embedSource)
                Add("src/PrecompiledPoints.Codeunit.al", File.ReadAllText(Path.Combine(FixtureRoot, "dep", "PrecompiledPoints.Codeunit.al")));
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(DepAppId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }

    /// <summary>The fixture's test folder copied under the scratch directory, with the dep app as a package
    /// in its .alpackages and, for the DLL shape, the precompiled DLL in its .deps-bin.</summary>
    private string MakeTestFolder(string name, bool withDll)
    {
        var dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(Path.Combine(dir, ".alpackages"));
        foreach (var f in Directory.GetFiles(Path.Combine(FixtureRoot, "test")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        File.WriteAllBytes(Path.Combine(dir, ".alpackages", DepFileStem + ".app"), BuildDepApp(embedSource: !withDll));
        if (withDll)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".deps-bin"));
            File.Copy(Path.Combine(FixtureRoot, DepFileStem + ".dll"), Path.Combine(dir, ".deps-bin", DepFileStem + ".dll"));
        }
        return dir;
    }

    private static (string StdOut, string StdErr, int Exit) RunRunner(params string[] extraArgs) =>
        RunRunnerWithEnv(null, extraArgs);

    private static (string StdOut, string StdErr, int Exit) RunRunnerWithEnv(
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

    private static string HashTree(string dir) => string.Join("\n",
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(dir, f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));

    private static string[] StubsOf(JsonElement test) =>
        test.TryGetProperty("generatedStubs", out var s)
            ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();

    private const string CalcPointsStub = "Precompiled Points: procedure \"CalcPoints\"(Arg1: Integer): Integer";
    private const string ShapedStub = "Precompiled Points: procedure \"Shaped\"(Arg1: Integer): Integer";
    private const string BonusStub = "Precompiled Points: procedure \"Bonus\"(Arg1: Boolean): Integer";

    /// <summary>The outcome both package shapes must give.</summary>
    private static void AssertTheRun((string StdOut, string StdErr, int Exit) run)
    {
        var (stdout, stderr, exit) = run;
        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var root = doc.RootElement;
        var tests = root.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(14, root.GetProperty("total").GetInt32());
        Assert.Equal(7, root.GetProperty("passed").GetInt32());
        Assert.Equal(7, root.GetProperty("failed").GetInt32());
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));

        // The members the package declares run their real bodies and name no stub.
        foreach (var name in new[] { "ExistingMember_RunsTheRealBody", "Unrelated_NamesNoStub" })
        {
            var t = Find(name);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Empty(StubsOf(t));
        }

        // A missing member runs to the stub, from either file, and every such test names it.
        foreach (var name in new[] { "MissingMember_RunsToTheStub", "SameMember_InASecondFile" })
        {
            var t = Find(name);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Equal(new[] { CalcPointsStub }, StubsOf(t));
        }
        var second = Find("SecondMember_ThroughAGlobalVariable");
        Assert.Equal("pass", second.GetProperty("status").GetString());
        Assert.Equal(new[] { BonusStub }, StubsOf(second));

        // One member called inside an expression and through an assignment: the stub takes the shape of the
        // call that fixes it, and both tests name it, whichever call the compiler reported first.
        foreach (var name in new[] { "MixedShapes_TheCallThatFixesTheTypes", "MixedShapes_TheCallInsideAnExpression" })
        {
            var t = Find(name);
            Assert.Equal("pass", t.GetProperty("status").GetString());
            Assert.Equal(new[] { ShapedStub }, StubsOf(t));
        }

        // The stub is empty: the test's own assertion is the red of the loop, and it still names the stub.
        var own = Find("MissingMember_AssertsItsOwnResult");
        Assert.Equal("fail", own.GetProperty("status").GetString());
        Assert.Contains("Expected 25, got 0", own.GetProperty("message").GetString());
        Assert.Equal(new[] { CalcPointsStub }, StubsOf(own));

        // What --tdd refuses to stub drops its object, reported FAILED naming the missing symbol.
        foreach (var name in new[] { "TextArgument_IsRefused", "StubbableCall_InTheSameDroppedFile", "ArrayElementQualifier_IsRefused", "PlainSite_OfAMemberAnotherSiteCannotStub_IsRefused", "NewOverloadOfAnExistingProcedure_IsRefused", "BareStatement_IsRefused" })
        {
            var t = Find(name);
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Matches("AL01(32|26)", t.GetProperty("message").GetString());
            Assert.Contains("Precompiled Points", t.GetProperty("message").GetString());
            Assert.Empty(StubsOf(t));
        }

        Assert.Contains("--tdd: generated 3 member(s) this run:", stderr);
        Assert.Contains("--tdd: 6 test(s) reach generated stubs this run:", stderr);
        // A refusal says why, once per member.
        Assert.Contains("--tdd: not generated beside precompiled Precompiled Points: \"Fire\" - no call to it fixes its parameter and return types", stderr);
        Assert.Contains("--tdd: not generated beside precompiled Precompiled Points: \"MixedCall\" - a call to it is not through a plain variable", stderr);
        Assert.DoesNotContain("no members were generated", stdout + stderr);
    }

    /// <summary>
    /// The implementing codeunit is a precompiled DLL plus its symbols (Tier 1). Each missing procedure is
    /// stubbed beside the object; the procedures it does declare keep running their real bodies; what
    /// cannot be stubbed is reported FAILED without taking the other files down; nothing is written.
    /// </summary>
    [SkippableFact]
    public void PrecompiledDll_MissingProceduresRunToStubsBesideTheObject()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-dll", withDll: true);
        var before = HashTree(test);

        var run = RunRunner("--tdd", $"--cache \"{Path.Combine(_scratch, "cache-dll")}\"", "--output-json", $"\"{test}\"");

        AssertTheRun(run);
        // In memory only: nothing the run read was rewritten, and no stub file was left behind.
        Assert.Equal(before, HashTree(test));
    }

    /// <summary>The same outcome when the package carries the app's own source and no DLL (Tier 3): the stub
    /// sits beside the object the run compiled from it, not in it.</summary>
    [SkippableFact]
    public void PackagedSource_MissingProceduresRunToStubsBesideTheObject()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-src", withDll: false);
        var before = HashTree(test);

        var run = RunRunner("--tdd", $"--cache \"{Path.Combine(_scratch, "cache-src")}\"", "--output-json", $"\"{test}\"");

        AssertTheRun(run);
        Assert.Equal(before, HashTree(test));
    }

    /// <summary>
    /// The compiler reports the missing calls in an order that changes between runs. Fed in the opposite
    /// order, the run must give the outcome of the ordinary one: the shape of a member called both ways
    /// comes from the call that fixes it, not from whichever was reported first.
    /// </summary>
    [SkippableFact]
    public void PrecompiledDll_ReversedDiagnosticOrder_GivesTheSameOutcome()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-reversed", withDll: true);

        var run = RunRunnerWithEnv(("AL_RUNNER_TDD_DIAG_ORDER", "reverse"),
            "--tdd", $"--cache \"{Path.Combine(_scratch, "cache-reversed")}\"", "--output-json", $"\"{test}\"");

        AssertTheRun(run);
    }

    /// <summary>
    /// A test app whose idRanges hold no free codeunit id for the stub: nothing is generated, every test that
    /// names a missing member is FAILED as before, and the run says that the id was the reason.
    /// </summary>
    [SkippableFact]
    public void NoFreeCodeunitId_RefusesAndSaysSo()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-no-ids", withDll: true);
        var ids = Directory.GetFiles(test, "*.al")
            .Select(f => System.Text.RegularExpressions.Regex.Match(File.ReadAllText(f), @"^codeunit (\d+) ", System.Text.RegularExpressions.RegexOptions.Multiline))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var manifest = Path.Combine(test, "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest)
            .Replace("\"from\": 65320", $"\"from\": {ids.Min()}").Replace("\"to\": 65339", $"\"to\": {ids.Max()}"));

        var (stdout, stderr, exit) = RunRunner("--tdd", $"--cache \"{Path.Combine(_scratch, "cache-no-ids")}\"", "--output-json", $"\"{test}\"");

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.All(tests, t => Assert.Empty(StubsOf(t)));
        Assert.Contains("no free codeunit id in the app's idRanges for the generated stub", stderr);
        Assert.Contains("no members were generated", stdout + stderr);
        // Every file of the fixture names a missing member, so each object is dropped and each test FAILED.
        Assert.All(tests, t => Assert.Equal("fail", t.GetProperty("status").GetString()));
    }
}
