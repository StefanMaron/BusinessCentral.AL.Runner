// --tdd call shapes (#5146, #5228, #5161): where in the test the missing member is named decides
// whether anything is generated, and which tests are annotated with it. One run of the
// TddCallShapes fixture is shared by every test here — each asserts the tests of its own issue.
//
// Runner-specific (--tdd turning a compile error into a generated stub the test runs against), so
// it lives here, not in the al-language corpus: the claim is about the generator, not about BC.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class TddRunResult : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _scratch = TestScratch.Dir("al-runner-tdd-call-shapes");
    private readonly string[] _folders;
    private readonly Lazy<(string StdErr, int Exit, JsonDocument Doc)> _run;

    protected static readonly string ShapesFixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TddCallShapes");

    // The run happens on first read, which is after the test's own TestArtifacts.SkipIfMissing():
    // a box without artifacts skips visibly there (and fails on a CI leg), never reaching the run.
    public TddRunResult(params string[] folders)
    {
        _folders = folders;
        _run = new Lazy<(string, int, JsonDocument)>(Execute);
    }

    /// <summary>Set before the first read to run against a cache root another run shares: a second run on
    /// one cache is a warm run, which a fresh cache per instance never is.</summary>
    public string? CacheRoot { get; init; }

    /// <summary>Set before the first read to run without --tdd.</summary>
    public bool Plain { get; init; }

    public string StdErr => _run.Value.StdErr;
    public int Exit => _run.Value.Exit;
    public List<JsonElement> Tests => _run.Value.Doc.RootElement.GetProperty("tests").EnumerateArray().ToList();

    private (string, int, JsonDocument) Execute()
    {
        Directory.CreateDirectory(_scratch);
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($"{(Plain ? "" : " --tdd")} --cache \"{CacheRoot ?? Path.Combine(_scratch, "cache")}\" --output-json");
        foreach (var f in _folders) args.Append($" \"{f}\"");
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
        lock (outSb) lock (errSb)
        {
            // A run that stopped before its summary prints no JSON: the test's own exit-code
            // assertion then names what happened, instead of a parse error naming nothing.
            var json = outSb.ToString().Trim();
            return (errSb.ToString(), p.ExitCode, JsonDocument.Parse(json.Length == 0 ? "{\"tests\":[]}" : json));
        }
    }

    public JsonElement Find(string name) =>
        Tests.Single(t => t.GetProperty("name").GetString()!.Contains(name));

    public string[] StubsOf(string name) =>
        Find(name).TryGetProperty("generatedStubs", out var s)
            ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();

    /// <summary>The message and the stack trace together: a refused test's top-line message names
    /// the first diagnostic of its object, and the others are in the trace.</summary>
    public string Failure(string name)
    {
        var t = Find(name);
        return t.GetProperty("message").GetString() + "\n" +
               (t.TryGetProperty("stackTrace", out var st) ? st.GetString() : "");
    }

    public void Dispose()
    {
        if (_run.IsValueCreated) _run.Value.Doc.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }
}

/// <summary>One --tdd run of the single-folder TddCallShapes fixture, shared by the tests of the class.</summary>
public sealed class TddCallShapeRun : TddRunResult
{
    public TddCallShapeRun() : base(ShapesFixture) { }
}

public sealed class TddCallShapeTests : IClassFixture<TddCallShapeRun>
{
    private readonly TddCallShapeRun _run;

    public TddCallShapeTests(TddCallShapeRun run) => _run = run;

    private const string Target = "Tdd Shape Target Cu";

    /// <summary>
    /// #5146: <c>Assert.AreEqual(25, Target.CalcPoints(250), '...')</c> — the outer procedure's
    /// parameters are Variant, so they fix no type, and the expected value beside the call does.
    /// Before, nothing was generated and every test of the codeunit failed to compile.
    /// </summary>
    [SkippableFact]
    public void MissingProcedureNestedInAVariantParameter_TakesItsTypeFromTheOtherVariantArgument()
    {
        TestArtifacts.SkipIfMissing();

        var integer = _run.Find("NestedIntegerExpected_FailsOnItsOwnAssertion");
        Assert.Equal("fail", integer.GetProperty("status").GetString());
        Assert.Contains("Expected:<25>. Actual:<0>", integer.GetProperty("message").GetString());
        Assert.Equal(new[] { $"{Target}: procedure \"CalcPoints\"(Arg1: Integer): Integer" },
            _run.StubsOf("NestedIntegerExpected_FailsOnItsOwnAssertion"));

        var boolean = _run.Find("NestedBooleanExpected_FailsOnItsOwnAssertion");
        Assert.Equal("fail", boolean.GetProperty("status").GetString());
        Assert.Contains("Assert.AreEqual failed", boolean.GetProperty("message").GetString());
        Assert.Equal(new[] { $"{Target}: procedure \"IsGold\"(Arg1: Integer): Boolean" },
            _run.StubsOf("NestedBooleanExpected_FailsOnItsOwnAssertion"));

        // The expected value is a Decimal variable: the type comes from its declaration.
        Assert.Equal("pass", _run.Find("NestedDecimalVariableExpected_PassesAgainstTheDefault").GetProperty("status").GetString());
        Assert.Equal(new[] { $"{Target}: procedure \"CalcRate\"(Arg1: Integer): Decimal" },
            _run.StubsOf("NestedDecimalVariableExpected_PassesAgainstTheDefault"));
    }

    /// <summary>
    /// #5146, the other direction: when the other Variant argument fixes no single type — a Text
    /// literal (a length nothing fixes), a sibling that is itself missing, siblings of different
    /// types — nothing is generated, and the test is reported failed naming the missing symbol.
    /// </summary>
    [SkippableFact]
    public void MissingProcedureNestedInAVariantParameter_RefusesWhenTheOtherArgumentsFixNoSingleType()
    {
        TestArtifacts.SkipIfMissing();

        foreach (var (test, symbol) in new[]
        {
            ("NestedTextExpected_Refuses", "TierName"),
            ("BothSidesMissing_Refuses", "ExpectedOf"),
            ("VariantSiblingsDisagree_Refuses", "ValueOf"),
            // 'a' fixes no type and 2 fixes Integer: one typed sibling must not decide it.
            ("MixedTextAndIntegerSiblings_Refuses", "PMixed"),
        })
        {
            Assert.Equal("fail", _run.Find(test).GetProperty("status").GetString());
            Assert.Contains("did not compile", _run.Failure(test));
            Assert.Contains(symbol, _run.Failure(test));
            Assert.Empty(_run.StubsOf(test));
        }
        var summary = _run.StdErr[_run.StdErr.IndexOf("--tdd: generated", StringComparison.Ordinal)..];
        foreach (var refused in new[] { "TierName", "ExpectedOf", "ActualOf", "ValueOf", "PMixed" })
            Assert.DoesNotContain(refused, summary);
    }

    /// <summary>
    /// #5228: <c>Target.Existing(5, 7)</c> where only <c>Existing(A)</c> is declared (AL0126). The
    /// generated overload sits next to the existing procedure, the test runs against it, and the
    /// test calling the existing one-argument procedure is untouched.
    /// </summary>
    [SkippableFact]
    public void ExistingProcedureCalledWithAnExtraArgument_GetsAGeneratedOverload()
    {
        TestArtifacts.SkipIfMissing();

        var overload = new[] { $"{Target}: procedure \"Existing\"(Arg1: Integer; Arg2: Integer): Integer" };

        var runs = _run.Find("ExistingWithExtraParam_RunsAgainstGeneratedOverload");
        Assert.Equal("pass", runs.GetProperty("status").GetString());
        Assert.Equal(overload, _run.StubsOf("ExistingWithExtraParam_RunsAgainstGeneratedOverload"));

        var fails = _run.Find("ExistingWithExtraParam_FailsOnItsOwnAssertion");
        Assert.Equal("fail", fails.GetProperty("status").GetString());
        Assert.Contains("Expected:<12>. Actual:<0>", fails.GetProperty("message").GetString());
        Assert.Equal(overload, _run.StubsOf("ExistingWithExtraParam_FailsOnItsOwnAssertion"));

        // The original procedure still binds to its own body and reaches no generated member.
        Assert.Equal("pass", _run.Find("ExistingWithItsOwnParameters_IsNotAnnotated").GetProperty("status").GetString());
        Assert.Empty(_run.StubsOf("ExistingWithItsOwnParameters_IsNotAnnotated"));

        // A bare statement cannot tell a void overload from a discarded return value: refused.
        Assert.Equal("fail", _run.Find("BareStatementWithExtraArgument_Refuses").GetProperty("status").GetString());
        Assert.Contains("did not compile", _run.Failure("BareStatementWithExtraArgument_Refuses"));
        Assert.Contains("Existing", _run.Failure("BareStatementWithExtraArgument_Refuses"));

        // AL0126 also names a built-in method called with the wrong argument count (Codeunit.Run):
        // there is no declared procedure to overload, so nothing is generated.
        Assert.Equal("fail", _run.Find("BuiltInMethodWithWrongArgumentCount_Refuses").GetProperty("status").GetString());
        Assert.Contains("'Run'", _run.Failure("BuiltInMethodWithWrongArgumentCount_Refuses"));
        Assert.DoesNotContain("\"Run\"(", _run.StdErr);

        // Same argument count, different argument types: two overloads, and the test calling both
        // runs (one Integer overload would overflow converting 1.5).
        var both = _run.Find("SameArityDifferentTypes_GetTwoOverloads");
        Assert.Equal("pass", both.GetProperty("status").GetString());
        Assert.Equal(new[]
        {
            $"{Target}: procedure \"Existing\"(Arg1: Decimal; Arg2: Integer): Integer",
            $"{Target}: procedure \"Existing\"(Arg1: Integer; Arg2: Integer): Integer",
        }, _run.StubsOf("SameArityDifferentTypes_GetTwoOverloads").OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Two tests share one overload: it is generated once.
        var generated = _run.StdErr[_run.StdErr.IndexOf("--tdd: generated", StringComparison.Ordinal)..];
        generated = generated[..generated.IndexOf("test(s) reach generated stubs", StringComparison.Ordinal)];
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(generated,
            System.Text.RegularExpressions.Regex.Escape("\"Existing\"(Arg1: Integer; Arg2: Integer)")).Count);
    }

    /// <summary>
    /// #5161: the generated procedure is called by an event subscriber, so the test that raises the
    /// event reaches it without calling it. The test raising a different event does not.
    /// </summary>
    [SkippableFact]
    public void TestRaisingAnEvent_IsAnnotatedWithTheStubItsSubscriberReaches()
    {
        TestArtifacts.SkipIfMissing();

        var reaching = _run.Find("RaisingASubscribedEvent_ReachesTheSubscribersStub");
        Assert.Equal("pass", reaching.GetProperty("status").GetString());
        Assert.Equal(new[] { $"{Target}: procedure \"CountEvent\"(Arg1: Integer): Integer" },
            _run.StubsOf("RaisingASubscribedEvent_ReachesTheSubscribersStub"));

        Assert.Equal("pass", _run.Find("RaisingAnotherEvent_IsNotAnnotated").GetProperty("status").GetString());
        Assert.Empty(_run.StubsOf("RaisingAnotherEvent_IsNotAnnotated"));

        // A second publisher declares an event of the same name: its subscriber belongs to it alone.
        Assert.Equal("pass", _run.Find("RaisingTheOtherPublisher_ReachesOnlyItsOwnSubscribersStub").GetProperty("status").GetString());
        Assert.Equal(new[] { $"{Target}: procedure \"CountOtherEvent\"(Arg1: Integer): Integer" },
            _run.StubsOf("RaisingTheOtherPublisher_ReachesOnlyItsOwnSubscribersStub"));
    }

    /// <summary>
    /// #5245: a table and a codeunit both named "Tdd Shape Publisher" declare OnCounted. The test
    /// raising the table's event reaches its subscriber's stub, and the codeunit's tests do not
    /// (and the reverse): a subscriber belongs to the object KIND it names, not to every object of
    /// that name.
    /// </summary>
    [SkippableFact]
    public void TableAndCodeunitPublishersOfOneName_KeepTheirOwnSubscribers()
    {
        TestArtifacts.SkipIfMissing();

        Assert.Equal("pass", _run.Find("RaisingTheTablePublisher_ReachesOnlyTheTableSubscribersStub").GetProperty("status").GetString());
        Assert.Equal(new[] { $"{Target}: procedure \"CountTableEvent\"(Arg1: Integer): Integer" },
            _run.StubsOf("RaisingTheTablePublisher_ReachesOnlyTheTableSubscribersStub"));
        Assert.Equal(new[] { $"{Target}: procedure \"CountEvent\"(Arg1: Integer): Integer" },
            _run.StubsOf("RaisingASubscribedEvent_ReachesTheSubscribersStub"));
    }

    /// <summary>
    /// #5161, #5245: a subscriber naming its publisher by a bare object id adds no edge, as the guide
    /// says. Its stub is generated all the same, so the test raising the event runs against it
    /// unannotated: the absence is the graph's, not a refusal. (`Codeunit::65206` is a syntax error.)
    /// </summary>
    [SkippableFact]
    public void SubscriberNamingItsPublisherByBareId_AddsNoEdge()
    {
        TestArtifacts.SkipIfMissing();

        Assert.Equal("pass", _run.Find("RaisingAnEventSubscribedByBareObjectId_IsNotAnnotated").GetProperty("status").GetString());
        Assert.Empty(_run.StubsOf("RaisingAnEventSubscribedByBareObjectId_IsNotAnnotated"));
        Assert.Contains($"{Target}: procedure \"CountById\"(Arg1: Integer): Integer", _run.StdErr);
    }

    /// <summary>
    /// #5244: the first call site of a missing member is a bare statement, which anchors no type; the
    /// second is typed. The member is generated from the second, and the first test — refused when it
    /// alone decided the key — compiles against it, runs, and names it.
    /// </summary>
    [SkippableFact]
    public void BareStatementBeforeATypedCall_DoesNotRefuseTheMember()
    {
        TestArtifacts.SkipIfMissing();

        var stub = new[] { $"{Target}: procedure \"Ordered\"(Arg1: Integer): Integer" };
        Assert.Equal("pass", _run.Find("A_BareStatementFirst_StillReachesTheStubTheTypedCallGenerates").GetProperty("status").GetString());
        Assert.Equal(stub, _run.StubsOf("A_BareStatementFirst_StillReachesTheStubTheTypedCallGenerates"));
        Assert.Equal("pass", _run.Find("B_TypedCallSecond_GeneratesTheMember").GetProperty("status").GetString());
        Assert.Equal(stub, _run.StubsOf("B_TypedCallSecond_GeneratesTheMember"));

        // Generated once, however many call sites name it.
        var generated = _run.StdErr[_run.StdErr.IndexOf("--tdd: generated", StringComparison.Ordinal)..];
        generated = generated[..generated.IndexOf("test(s) reach generated stubs", StringComparison.Ordinal)];
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(generated, "\"Ordered\"\\(").Count);
    }

    /// <summary>
    /// #5244 across source folders: the same shape with the member in the app bundle, so the key goes
    /// through the cross-bundle attempt state instead of the in-compile one.
    /// </summary>
    [SkippableFact]
    public void BareStatementBeforeATypedCall_DoesNotRefuseTheMemberInAnotherSourceFolder()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests", "Fixtures", "TddRefusalOrder"));
        using var run = new TddRunResult(Path.Combine(root, "app"), Path.Combine(root, "test"));

        Assert.True(run.Exit == 0, $"exit {run.Exit}\n{run.StdErr}");
        var stub = new[] { "Order App Target: procedure \"CrossOrdered\"(Arg1: Integer): Integer" };
        Assert.Equal(2, run.Tests.Count);
        Assert.Equal("pass", run.Find("A_BareStatementFirst_StillReachesTheStubTheTypedCallGenerates").GetProperty("status").GetString());
        Assert.Equal(stub, run.StubsOf("A_BareStatementFirst_StillReachesTheStubTheTypedCallGenerates"));
        Assert.Equal("pass", run.Find("B_TypedCallSecond_GeneratesTheMemberIntoTheApp").GetProperty("status").GetString());
        Assert.Equal(stub, run.StubsOf("B_TypedCallSecond_GeneratesTheMemberIntoTheApp"));
    }

    /// <summary>
    /// #5228 with the existing procedure in another source folder: the overload is generated into
    /// the app bundle, which is recompiled before the test bundle compiles again.
    /// </summary>
    [SkippableFact]
    public void ExistingProcedureInAnotherSourceFolder_GetsAGeneratedOverload()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests", "Fixtures", "TddOverloadTwoFolder");
        using var run = new TddRunResult(Path.GetFullPath(Path.Combine(root, "app")), Path.GetFullPath(Path.Combine(root, "test")));

        Assert.True(run.Exit == 0, $"exit {run.Exit}\n{run.StdErr}");
        Assert.Equal(2, run.Tests.Count);
        Assert.Equal("pass", run.Find("ExtraArgument_RunsAgainstGeneratedOverload").GetProperty("status").GetString());
        Assert.Equal(new[] { "Overload Calc: procedure \"Existing\"(Arg1: Integer; Arg2: Integer): Integer" },
            run.StubsOf("ExtraArgument_RunsAgainstGeneratedOverload"));
        Assert.Equal("pass", run.Find("OwnArgument_StillRunsTheExistingProcedure").GetProperty("status").GetString());
        Assert.Empty(run.StubsOf("OwnArgument_StillRunsTheExistingProcedure"));
    }
}
