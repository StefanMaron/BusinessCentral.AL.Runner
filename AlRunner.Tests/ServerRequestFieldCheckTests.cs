using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4952: <see cref="ServerProtocol.CheckFields"/> over parsed request lines — no runner spawn.
/// The request shapes marked ALchemist / LethAL are the ones those clients send today.
/// </summary>
public class ServerRequestFieldCheckTests
{
    private static RequestFieldCheck Check(string json) => ServerProtocol.CheckFields(ServerProtocol.Parse(json)!);

    [Theory]
    [InlineData("preprocessorSymbols")]
    [InlineData("symbols")]
    [InlineData("defines")]
    public void RunTests_SymbolField_IsRefusedWithTheDaemonWideHint(string field)
    {
        var r = Check($$"""{"command":"runTests","sourcePaths":["/a"],"{{field}}":["SYM"]}""");
        Assert.NotNull(r.Error);
        Assert.StartsWith($"runTests: unknown request field '{field}' ", r.Error);
        Assert.Contains("--define SYM", r.Error);
        Assert.Contains("--preprocessor-symbols", r.Error);
    }

    [Fact]
    public void RunTests_UnrelatedUnknownField_IsRefusedWithoutTheSymbolHint()
    {
        var r = Check("""{"command":"runtests","sourcePaths":["/a"],"testFilter":{"codeunitNames":["X"]},"cobertura":true}""");
        Assert.NotNull(r.Error);
        Assert.StartsWith("runTests: unknown request fields 'cobertura', 'testFilter' ", r.Error);
        Assert.DoesNotContain("--define", r.Error);
        Assert.Contains("Fields runTests reads: affectedOnly, coverage, includeFailing, packagePaths, perTestCoverage, sourcePaths, strictEnvironment, tdd, testIsolation.", r.Error);
    }

    [Fact]
    public void Execute_MiscasedKnownField_IsRefusedNamingTheRealField()
    {
        var r = Check("""{"command":"execute","SourcePaths":["/a"]}""");
        Assert.NotNull(r.Error);
        Assert.Contains("'SourcePaths'", r.Error);
        Assert.Contains("did you mean 'sourcePaths'?", r.Error);
    }

    [Theory]
    [InlineData("""{"command":"cancel","requestId":7}""")]
    [InlineData("""{"command":"shutdown","reason":"bye"}""")]
    [InlineData("""{"command":"bogus","x":1}""")]
    [InlineData("""{"x":1}""")]
    public void OtherCommands_AcceptExtraFields(string json)
    {
        var r = Check(json);
        Assert.Null(r.Error);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    // LethAL al-runner-server.ts
    [InlineData("""{"command":"runTests","sourcePaths":["/a","/b"],"packagePaths":["/p"],"coverage":true,"perTestCoverage":true,"testIsolation":"test"}""")]
    // ALchemist serverExecutionEngine.ts executeScratch
    [InlineData("""{"command":"execute","captureValues":true,"code":"x","iterationTracking":true}""")]
    [InlineData("""{"command":"runTests","sourcePaths":["/a"],"affectedOnly":true,"includeFailing":true}""")]
    [InlineData("""{"command":"runTests","sourcePaths":["/a"],"affectedOnly":true,"strictEnvironment":true}""")]
    // #5034
    [InlineData("""{"command":"runTests","sourcePaths":["/a"],"tdd":true}""")]
    // A field that asks for nothing is exact to ignore.
    [InlineData("""{"command":"runTests","sourcePaths":["/a"],"captureValues":false,"stubPaths":[],"code":""}""")]
    [InlineData("""{"command":"runTests","sourcePaths":["/a"],"code":null}""")]
    public void RequestsTheCommandReadsInFull_PassClean(string json)
    {
        var r = Check(json);
        Assert.Null(r.Error);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void RunTests_AlchemistShape_RunsWithOneWarningPerUnreadField()
    {
        // ALchemist serverExecutionEngine.ts runTests
        var r = Check("""{"command":"runtests","sourcePaths":["/a"],"captureValues":true,"iterationTracking":true,"coverage":true}""");
        Assert.Null(r.Error);
        Assert.Equal(
            new[]
            {
                "'captureValues' has no effect on runTests; only execute reads it — ignored.",
                "'iterationTracking' has no effect on runTests; only execute reads it — ignored.",
            },
            r.Warnings.OrderBy(w => w, StringComparer.Ordinal));
    }

    [Fact]
    public void StubPaths_NonEmpty_WarnsReadByNoCommand_OnBothCommands()
    {
        foreach (var cmd in new[] { "runTests", "execute" })
        {
            var r = Check($$"""{"command":"{{cmd}}","sourcePaths":["/a"],"stubPaths":["/s"]}""");
            Assert.Null(r.Error);
            Assert.Equal(new[] { "'stubPaths' is read by no command (v1 field) — ignored." }, r.Warnings);
        }
    }

    [Fact]
    public void Execute_IncludeFailing_WarnsOnlyRunTestsReadsIt()
    {
        var r = Check("""{"command":"execute","sourcePaths":["/a"],"includeFailing":true}""");
        Assert.Null(r.Error);
        Assert.Equal(new[] { "'includeFailing' has no effect on execute; only runTests reads it — ignored." }, r.Warnings);
    }

    [Fact]
    public void Execute_StrictEnvironment_WarnsOnlyRunTestsReadsIt()
    {
        var r = Check("""{"command":"execute","sourcePaths":["/a"],"strictEnvironment":true}""");
        Assert.Null(r.Error);
        Assert.Equal(new[] { "'strictEnvironment' has no effect on execute; only runTests reads it — ignored." }, r.Warnings);
    }

    [Fact]
    public void Execute_Tdd_WarnsOnlyRunTestsReadsIt()
    {
        var r = Check("""{"command":"execute","sourcePaths":["/a"],"tdd":true}""");
        Assert.Null(r.Error);
        Assert.Equal(new[] { "'tdd' has no effect on execute; only runTests reads it — ignored." }, r.Warnings);
    }

    // Every declared request field is either read by some command or is the one v1 field no
    // command reads. A property added to ServerRequest without classifying it would warn on
    // every request that uses it.
    [Fact]
    public void EveryDeclaredField_IsClassified()
    {
        var declared = ServerProtocol.DeclaredFields.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var classified = ServerProtocol.FieldsReadBy.Values.SelectMany(v => v).Append("stubPaths")
            .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(classified, declared);
        Assert.Equal(
            typeof(ServerRequest).GetProperties().Count(p => p.Name != nameof(ServerRequest.UnknownFields)),
            declared.Count);
    }

    [Fact]
    public void Summary_And_Execute_CarryWarningsOnlyWhenThereAreSome()
    {
        var none = JsonSerializer.Deserialize<JsonElement>(ServerProtocol.Summary(Array.Empty<TestResult>(), 0, false, warnings: Array.Empty<string>()));
        Assert.False(none.TryGetProperty("warnings", out _));
        var some = JsonSerializer.Deserialize<JsonElement>(ServerProtocol.Summary(Array.Empty<TestResult>(), 0, false, warnings: new[] { "w" }));
        Assert.Equal("w", some.GetProperty("warnings")[0].GetString());

        var exNone = JsonSerializer.Deserialize<JsonElement>(ServerProtocol.Execute(Array.Empty<TestResult>(), 0));
        Assert.False(exNone.TryGetProperty("warnings", out _));
        var exSome = JsonSerializer.Deserialize<JsonElement>(ServerProtocol.Execute(Array.Empty<TestResult>(), 0, warnings: new[] { "w" }));
        Assert.Equal("w", exSome.GetProperty("warnings")[0].GetString());
    }
}
