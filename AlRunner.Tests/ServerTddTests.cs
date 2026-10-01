// #5034: --tdd under --server, as runTests' per-request `tdd` field (default: the --tdd startup
// flag). Runner-specific — --tdd turning a compile error into generated stubs the tests run
// against — so it lives here, not in the al-language corpus. Mechanism: docs/server-mode.md#tdd.
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerTddTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string TwoFolderRoot = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "TddTwoFolder");

    private const string EmptyCalc = """
        codeunit 65300 "SrvTdd Calc"
        {
        }
        """;

    private const string RealCalc = """
        codeunit 65300 "SrvTdd Calc"
        {
            procedure DoubleIt(Value: Integer): Integer
            begin
                exit(Value * 2);
            end;

            procedure NameLength(Name: Text): Integer
            begin
                exit(StrLen(Name));
            end;

            procedure TripleIt(Value: Integer): Integer
            begin
                exit(Value * 3);
            end;
        }
        """;

    // DoubleIt / TripleIt: an Integer literal argument and an Integer assignment target —
    // generated with an empty body (#5147). DoubleIt's test expects 42 and fails on its own
    // Error(); TripleIt's expects 0, the empty stub's default, and passes.
    private const string CalcTests = """
        codeunit 65301 "SrvTdd Calc Tests"
        {
            Subtype = Test;

            [Test]
            procedure DoubleIt_ReturnsTwice()
            var
                Calc: Codeunit "SrvTdd Calc";
                Result: Integer;
            begin
                Result := Calc.DoubleIt(21);
                if Result <> 42 then
                    Error('DoubleIt returned %1', Result);
            end;

            [Test]
            procedure TripleIt_OfZero_IsZero()
            var
                Calc: Codeunit "SrvTdd Calc";
                Result: Integer;
            begin
                Result := Calc.TripleIt(0);
                if Result <> 0 then
                    Error('TripleIt returned %1', Result);
            end;
        }
        """;

    // NameLength: a Text argument, whose length no call site fixes — refused, never generated.
    private const string RefusedTests = """
        codeunit 65302 "SrvTdd Refused Tests"
        {
            Subtype = Test;

            [Test]
            procedure NameLength_CountsCharacters()
            var
                Calc: Codeunit "SrvTdd Calc";
                Result: Integer;
            begin
                Result := Calc.NameLength('Gold');
                if Result <> 4 then
                    Error('NameLength returned %1', Result);
            end;
        }
        """;

    private const string HealthyTests = """
        codeunit 65303 "SrvTdd Healthy Tests"
        {
            Subtype = Test;

            [Test]
            procedure Unrelated_Passes()
            begin
                if 1 + 2 <> 3 then
                    Error('arithmetic broke');
            end;
        }
        """;

    private static string Bundle(string prefix, bool withRefused)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "c5034000-0000-4a11-9111-000000000001",
          "name": "Server Tdd SX",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 65300, "to": 65319 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Calc.Codeunit.al"), EmptyCalc);
        File.WriteAllText(Path.Combine(dir, "CalcTests.Codeunit.al"), CalcTests);
        File.WriteAllText(Path.Combine(dir, "HealthyTests.Codeunit.al"), HealthyTests);
        if (withRefused) File.WriteAllText(Path.Combine(dir, "RefusedTests.Codeunit.al"), RefusedTests);
        return dir;
    }

    private sealed record Response(Dictionary<string, JsonElement> Tests, JsonElement Summary, string Raw)
    {
        public int ExitCode => Summary.GetProperty("exitCode").GetInt32();
        public string Status(string method) => Tests[method].GetProperty("status").GetString()!;
        public string Message(string method) => Tests[method].GetProperty("message").GetString()!;
        public string? ErrorKind(string method)
            => Tests[method].TryGetProperty("errorKind", out var k) ? k.GetString() : null;
        public string[] Stubs(string method)
            => Tests[method].TryGetProperty("generatedStubs", out var g)
                ? g.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>();
    }

    private const string DoubleItStub = "SrvTdd Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer";
    private const string TripleItStub = "SrvTdd Calc: procedure \"TripleIt\"(Arg1: Integer): Integer";

    /// <summary>#5147: DoubleIt's test ran against the empty stub and failed on its OWN Error() —
    /// not a compile failure, not the old "generated stub" error — and names the stub it ran against.</summary>
    private static void AssertFailedOnOwnAssertion(Response r)
    {
        Assert.True(r.Status("DoubleIt_ReturnsTwice") == "fail", r.Raw);
        Assert.True(r.ErrorKind("DoubleIt_ReturnsTwice") != "compile", r.Raw);
        Assert.Contains("DoubleIt returned 0", r.Message("DoubleIt_ReturnsTwice"), StringComparison.Ordinal);
        Assert.DoesNotContain("generated stub", r.Message("DoubleIt_ReturnsTwice"), StringComparison.Ordinal);
        Assert.Equal(new[] { DoubleItStub }, r.Stubs("DoubleIt_ReturnsTwice"));
    }

    /// <summary>#5147: TripleIt's test asserts the default, so it passes against the empty stub —
    /// and its line still names the stub it ran against; the unrelated test's line names none.</summary>
    private static void AssertDefaultPassFlagged(Response r)
    {
        Assert.True(r.Status("TripleIt_OfZero_IsZero") == "pass", r.Raw);
        Assert.Equal(new[] { TripleItStub }, r.Stubs("TripleIt_OfZero_IsZero"));
        Assert.True(r.Status("Unrelated_Passes") == "pass", r.Raw);
        Assert.Empty(r.Stubs("Unrelated_Passes"));
    }

    private static async Task<Response> Send(CliServer server, string[] bundles, bool? tdd, bool affectedOnly = false)
    {
        var request = new Dictionary<string, object?>
        {
            ["command"] = "runTests",
            ["sourcePaths"] = bundles,
            ["packagePaths"] = Array.Empty<string>(),
        };
        if (tdd != null) request["tdd"] = tdd;
        if (affectedOnly) request["affectedOnly"] = true;
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(request), TimeSpan.FromSeconds(240));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        return new Response(
            events.ToDictionary(e => e.GetProperty("name").GetString()!.Split('.').Last(), e => e, StringComparer.Ordinal),
            summary, raw);
    }

    private static void AssertCompileFailure(Response r, string method, string names)
    {
        Assert.True(r.Status(method) == "fail", r.Raw);
        Assert.True(r.ErrorKind(method) == "compile", r.Raw);
        Assert.Contains(names, r.Message(method), StringComparison.Ordinal);
    }

    /// <summary>
    /// The issue's acceptance, on one server: without tdd a missing symbol is a compile failure
    /// with no test lines; with tdd each test runs against the empty generated stub and reports
    /// its own result, naming the stub (#5147), the refused call reports its test FAILED naming
    /// the symbol, and the unrelated test passes; the next request without tdd sees none of it;
    /// once the procedures are written the next request passes, unrestarted.
    /// </summary>
    [SkippableFact]
    public async Task MissingProcedures_GeneratedOrReportedPerTest_ThenPassOnceWritten()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-tdd", withRefused: true);
        // With the AL-output cache on: a tdd request must neither write the module it compiled
        // with a stub nor be served one, or the next plain request would run it.
        var cache = TestScratch.Dir("al-runner-server-tdd-cache");
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--cache", cache });

            var plain = await Send(server, new[] { bundle }, tdd: null);
            Assert.True(plain.ExitCode == 3, plain.Raw);
            Assert.Empty(plain.Tests);

            var redMark = server.StdErrMark;
            var red = await Send(server, new[] { bundle }, tdd: true);
            Assert.True(red.ExitCode == 1, red.Raw);
            Assert.Equal(new[] { "DoubleIt_ReturnsTwice", "NameLength_CountsCharacters", "TripleIt_OfZero_IsZero", "Unrelated_Passes" },
                red.Tests.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            // Generated: each test ran against the empty stub and reports its own result.
            AssertFailedOnOwnAssertion(red);
            AssertDefaultPassFlagged(red);
            // Refused: the object is excluded, and its test is a line naming the missing symbol.
            AssertCompileFailure(red, "NameLength_CountsCharacters", "NameLength");
            Assert.Contains("AL0132", red.Message("NameLength_CountsCharacters"), StringComparison.Ordinal);
            Assert.Empty(red.Stubs("NameLength_CountsCharacters"));
            // Stderr is read asynchronously, so wait for the line rather than read StdErr (#5096).
            await server.StdErrSinceAsync(redMark, "SrvTdd Calc: procedure \"DoubleIt\"(Arg1: Integer): Integer");
            // #5147: the summary lists every test that ran against a stub, with its own result.
            await server.StdErrSinceAsync(redMark, "--tdd: 2 test(s) ran against generated stubs this request:");
            await server.StdErrSinceAsync(redMark, $"SrvTdd Calc Tests.TripleIt_OfZero_IsZero (pass): {TripleItStub}");
            await server.StdErrSinceAsync(redMark, $"SrvTdd Calc Tests.DoubleIt_ReturnsTwice (fail): {DoubleItStub}");

            var plainAgain = await Send(server, new[] { bundle }, tdd: false);
            Assert.True(plainAgain.ExitCode == 3, plainAgain.Raw);
            Assert.Empty(plainAgain.Tests);

            // Unchanged source, tdd again: generated and reported afresh, not served from a cache
            // entry that holds neither the refused object's test nor the dependents' annotation.
            var redAgain = await Send(server, new[] { bundle }, tdd: true);
            Assert.True(redAgain.ExitCode == 1, redAgain.Raw);
            AssertFailedOnOwnAssertion(redAgain);
            AssertDefaultPassFlagged(redAgain);
            AssertCompileFailure(redAgain, "NameLength_CountsCharacters", "NameLength");

            File.WriteAllText(Path.Combine(bundle, "Calc.Codeunit.al"), RealCalc);
            var green = await Send(server, new[] { bundle }, tdd: true);
            Assert.True(green.ExitCode == 0, green.Raw);
            Assert.Equal(4, green.Tests.Count);
            Assert.All(green.Tests.Keys, k => Assert.True(green.Status(k) == "pass", green.Raw));
            Assert.All(green.Tests.Keys, k => Assert.Empty(green.Stubs(k)));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }

    /// <summary>The --tdd startup flag is the default for a request that omits the field, and
    /// the field overrides it either way — the testIsolation shape.</summary>
    [SkippableFact]
    public async Task StartupFlag_IsTheDefault_AndTheFieldOverridesIt()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-tdd-default", withRefused: false);
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--tdd", "--no-cache" });

            var byDefault = await Send(server, new[] { bundle }, tdd: null);
            Assert.True(byDefault.ExitCode == 1, byDefault.Raw);
            AssertFailedOnOwnAssertion(byDefault);
            AssertDefaultPassFlagged(byDefault);

            var off = await Send(server, new[] { bundle }, tdd: false);
            Assert.True(off.ExitCode == 3, off.Raw);
            Assert.Empty(off.Tests);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// affectedOnly after a tdd request that generated a stub: the module compiled with the stub
    /// is not what is on disk, so an unchanged next request must compile the source afresh — not
    /// replay the stubbed module, and not skip tests on coverage measured against it.
    /// </summary>
    [SkippableFact]
    public async Task AffectedOnly_UnchangedRequestAfterStub_CompilesTheSourceOnDisk()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-tdd-affected", withRefused: false);
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

            var red = await Send(server, new[] { bundle }, tdd: true, affectedOnly: true);
            Assert.True(red.ExitCode == 1, red.Raw);
            AssertFailedOnOwnAssertion(red);

            var plain = await Send(server, new[] { bundle }, tdd: false, affectedOnly: true);
            Assert.True(plain.ExitCode == 3, plain.Raw);
            Assert.Empty(plain.Tests);
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// affectedOnly once the real procedure replaces the stub: the tests that ran against the stub
    /// are selected — the one that failed there, and (#5147) the one that PASSED there, whose
    /// coverage was measured on the stub and must not let it be skipped — and both pass against
    /// the real code. Never select too few.
    /// </summary>
    [SkippableFact]
    public async Task AffectedOnly_StubReplacedByRealProcedure_SelectsTheTestAndItPasses()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-tdd-affected-real", withRefused: false);
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

            var red = await Send(server, new[] { bundle }, tdd: true, affectedOnly: true);
            Assert.True(red.ExitCode == 1, red.Raw);
            Assert.True(red.Status("TripleIt_OfZero_IsZero") == "pass", red.Raw);

            File.WriteAllText(Path.Combine(bundle, "Calc.Codeunit.al"), RealCalc);
            var green = await Send(server, new[] { bundle }, tdd: true, affectedOnly: true);
            Assert.True(green.ExitCode == 0, green.Raw);
            Assert.True(green.Tests.ContainsKey("DoubleIt_ReturnsTwice"), green.Raw);
            Assert.True(green.Status("DoubleIt_ReturnsTwice") == "pass", green.Raw);
            Assert.True(green.Tests.ContainsKey("TripleIt_OfZero_IsZero"), green.Raw);
            Assert.True(green.Status("TripleIt_OfZero_IsZero") == "pass", green.Raw);
            Assert.Empty(green.Stubs("TripleIt_OfZero_IsZero"));
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// #5079 through tdd: a module a tdd request compiled holds generated members, so a later
    /// plain request for another directory with the same app id must compile its own source —
    /// exit 3 with no test lines, as a fresh server answers — not run the stubbed module.
    /// </summary>
    [SkippableFact]
    public async Task TddRequestThenPlainRequest_OtherDirectorySameId_CompilesItsOwnSource()
    {
        TestArtifacts.SkipIfMissing();
        var x = Bundle("al-runner-server-tdd-reuse-x", withRefused: false);
        var y = Bundle("al-runner-server-tdd-reuse-y", withRefused: false);
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
            var red = await Send(server, new[] { x }, tdd: true);
            Assert.True(red.ExitCode == 1, red.Raw);

            var plain = await Send(server, new[] { y }, tdd: false);
            Assert.True(plain.ExitCode == 3, plain.Raw);
            Assert.Empty(plain.Tests);
        }
        finally
        {
            try { Directory.Delete(x, recursive: true); } catch { }
            try { Directory.Delete(y, recursive: true); } catch { }
        }
    }

    // OnRun calls a procedure the app lacks: without tdd, execute refuses to compile it.
    private const string ExecMissing = """
        codeunit 65304 "SrvTdd Exec"
        {
            trigger OnRun()
            var
                Calc: Codeunit "SrvTdd Calc";
                Result: Integer;
            begin
                Result := Calc.DoubleIt(5);
            end;
        }
        """;

    private static string ExecBundle(string prefix, string appIdSuffix)
    {
        var dir = TestScratch.Dir(prefix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "c5034000-0000-4a11-9111-{{appIdSuffix}}",
          "name": "Server Tdd Exec SX {{appIdSuffix}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 65300, "to": 65319 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Calc.Codeunit.al"), EmptyCalc);
        File.WriteAllText(Path.Combine(dir, "Exec.Codeunit.al"), ExecMissing);
        return dir;
    }

    private static async Task<int> ExecuteExit(CliServer server, string dir)
    {
        var line = await server.SendAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "execute", ["sourcePaths"] = new[] { dir },
        }), TimeSpan.FromSeconds(240));
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("exitCode").GetInt32();
    }

    /// <summary>`execute` does not read `tdd`, and a --tdd startup flag is not its default: it
    /// compiles the code as written (exit 3), never a generated stub.</summary>
    [SkippableFact]
    public async Task Execute_UnderStartupTdd_CompilesWithoutTdd()
    {
        TestArtifacts.SkipIfMissing();
        var dir = ExecBundle("al-runner-server-tdd-exec-startup", "000000000011");
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--tdd", "--no-cache" });
            var exit = await ExecuteExit(server, dir);
            Assert.True(exit == 3, $"exit {exit}\n{server.StdErr}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>A tdd runTests request's mode ends with it: the next execute compiles without it.</summary>
    [SkippableFact]
    public async Task Execute_AfterTddRunTests_CompilesWithoutTdd()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Bundle("al-runner-server-tdd-exec-after", withRefused: false);
        var dir = ExecBundle("al-runner-server-tdd-exec-after-x", "000000000012");
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
            var red = await Send(server, new[] { bundle }, tdd: true);
            Assert.True(red.ExitCode == 1, red.Raw);
            var exit = await ExecuteExit(server, dir);
            Assert.True(exit == 3, $"exit {exit}\n{server.StdErr}");
        }
        finally
        {
            try { Directory.Delete(bundle, recursive: true); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string HashDir(string dir) => string.Join("\n",
        Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetFileName(f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));

    /// <summary>
    /// #5037's two-bundle shape over the server: the members the test bundle calls are generated
    /// into the app bundle, the app is recompiled within the request, and each test runs against
    /// the empty stub — these only call it, so each passes and names it (#5147). Nothing reaches
    /// the disk, and nothing reaches the next request.
    /// </summary>
    [SkippableFact]
    public async Task TwoBundles_GenerateIntoTheAppBundle_AndLeaveNothingBehind()
    {
        TestArtifacts.SkipIfMissing();
        var app = Path.Combine(TwoFolderRoot, "app");
        var test = Path.Combine(TwoFolderRoot, "test");
        var before = HashDir(app);

        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
        var red = await Send(server, new[] { app, test }, tdd: true);
        Assert.True(red.ExitCode == 0, red.Raw);
        Assert.Equal(4, red.Tests.Count);
        foreach (var (method, proc) in new[]
                 {
                     ("VariableArg_GeneratesDecimalParameter", "CalcBasePoints"),
                     ("LiteralArg_GeneratesIntegerParameter", "CalcLit"),
                     ("EnumValueArg_GeneratesEnumParameter", "CalcTier"),
                 })
        {
            Assert.True(red.Status(method) == "pass", red.Raw);
            var stub = Assert.Single(red.Stubs(method));
            Assert.StartsWith($"Loyalty Points: procedure \"{proc}\"(Arg1: ", stub, StringComparison.Ordinal);
        }
        Assert.True(red.Status("Unrelated_StillPasses") == "pass", red.Raw);
        Assert.Empty(red.Stubs("Unrelated_StillPasses"));
        Assert.Equal(before, HashDir(app));

        var plain = await Send(server, new[] { app, test }, tdd: false);
        Assert.True(plain.ExitCode == 3, plain.Raw);
        Assert.Empty(plain.Tests);
    }
}
