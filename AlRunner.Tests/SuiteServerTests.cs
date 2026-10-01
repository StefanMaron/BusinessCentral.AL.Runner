using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5111: what the classes converted to <see cref="SuiteServer"/> rely on. The parser and the
/// assertion helpers stand in for the CLI's PASS/FAIL lines and summary, so a helper that cannot
/// fail would turn every converted class green regardless of what the runner answered.
/// </summary>
public class SuiteServerTests
{
    private static readonly string[] OnePassOneFail =
    {
        """{"type":"test","name":"Codeunit69980.Passes","status":"pass","durationMs":5}""",
        """{"type":"test","name":"Codeunit69980.Fails","status":"fail","durationMs":1,"message":"NavNCLDialogException: SST-FAIL 42","errorKind":"runtime"}""",
        """{"type":"summary","exitCode":1,"passed":1,"failed":1,"errors":0,"total":2,"protocolVersion":2}""",
    };

    private static readonly string[] CompileFailure =
    {
        """{"type":"summary","exitCode":3,"passed":0,"failed":0,"errors":0,"total":0,"compilationErrors":[{"file":"m","errors":["Q.al(1,1): error AL0353: bad column"]}],"protocolVersion":2}""",
    };

    private static readonly string[] AllPass =
    {
        """{"type":"test","name":"Codeunit69980.Passes","status":"pass","durationMs":5}""",
        """{"type":"test","name":"Codeunit69980.AlsoPasses","status":"pass","durationMs":5}""",
        """{"type":"summary","exitCode":0,"passed":2,"failed":0,"errors":0,"total":2,"protocolVersion":2}""",
    };

    [Fact]
    public void Parse_ReadsTheSummaryTheTestsAndTheirMessages()
    {
        var r = ServerRunResult.Parse(OnePassOneFail, "");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(2, r.Total);
        Assert.Equal(1, r.Passed);
        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Errors);
        Assert.Equal("pass", r.StatusOf("Codeunit69980.Passes"));
        Assert.Equal("fail", r.StatusOf("Codeunit69980.Fails"));
        Assert.Null(r.StatusOf("Codeunit69980.NeverRan"));
        Assert.Equal("NavNCLDialogException: SST-FAIL 42", r.Tests.Single(t => t.Name == "Codeunit69980.Fails").Message);
        Assert.Empty(r.CompilationErrors);
    }

    [Fact]
    public void Parse_ReadsCompilationErrors()
    {
        var r = ServerRunResult.Parse(CompileFailure, "");

        Assert.Equal(3, r.ExitCode);
        Assert.Equal(new[] { "Q.al(1,1): error AL0353: bad column" }, r.CompilationErrors);
    }

    [Fact]
    public void Parse_RefusesAStreamWithNoSummary()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServerRunResult.Parse(OnePassOneFail[..2], "stderr text"));
        Assert.Contains("without a summary line", ex.Message);
        Assert.Contains("stderr text", ex.Message);
    }

    [Fact]
    public void AssertPassed_FailsForAFailedTest_AndForATestThatDidNotRun()
    {
        var r = ServerRunResult.Parse(OnePassOneFail, "");

        r.AssertPassed("Codeunit69980.Passes");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => r.AssertPassed("Codeunit69980.Fails"));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => r.AssertPassed("Codeunit69980.NeverRan"));
        // Exact name, not a prefix: the CLI's substring match is not widened into a weaker one.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => r.AssertPassed("Codeunit69980.Pass"));
    }

    [Fact]
    public void AssertNoFailures_FailsOnAFailedTest_AndOnACompileError_AndPassesWhenAllPass()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ServerRunResult.Parse(OnePassOneFail, "").AssertNoFailures());
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ServerRunResult.Parse(CompileFailure, "").AssertNoFailures());
        ServerRunResult.Parse(AllPass, "").AssertNoFailures();
    }

    [Fact]
    public void AssertCounts_ComparesAllThree()
    {
        var r = ServerRunResult.Parse(OnePassOneFail, "");

        r.AssertCounts(passed: 1, failed: 1, errors: 0);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => r.AssertCounts(passed: 2, failed: 0, errors: 0));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => r.AssertCounts(passed: 1, failed: 0, errors: 0));
    }

    [Fact]
    public void CheckCanary_ThrowsNamingTheRequest_WhenTheFingerprintMoved()
    {
        SuiteServer.CheckCanary("{\"command\":\"runTests\"}", "same", "same");
        var ex = Assert.Throws<InvalidOperationException>(
            () => SuiteServer.CheckCanary("{\"command\":\"runTests\",\"x\":1}", "first", "moved"));
        Assert.Contains("{\"command\":\"runTests\",\"x\":1}", ex.Message);
        Assert.Contains("first", ex.Message);
        Assert.Contains("moved", ex.Message);
    }

    /// <summary>
    /// The request path runs the canary after the request and fails the request when it moved.
    /// The stand-in canary answers differently from the server's baseline, so a request path that
    /// skipped the check would return the result instead of throwing.
    /// </summary>
    [SkippableFact]
    public async Task RunAsync_FailsTheRequest_WhenTheCanaryAfterItMoved()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = WriteBundle("sst-canary", passBody: "", failBody: "");
        var request = System.Text.Json.JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { bundle },
            packagePaths = Array.Empty<string>(),
        });
        var canaryRuns = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SuiteServer.RunAsync(request, null,
            (_, _) => { canaryRuns++; return Task.FromResult("a fingerprint no server produces"); }));

        Assert.Equal(1, canaryRuns);
        Assert.Contains("SuiteServer canary", ex.Message);
        Assert.Contains("sst-canary", ex.Message);
    }

    /// <summary>
    /// End to end on the shared server: a passing and a failing test, then a second bundle with
    /// the same object ids and different outcomes. Each request must report its own bundle.
    /// </summary>
    [SkippableFact]
    public async Task RunViaServer_ReportsEachRequestsOwnResults()
    {
        TestArtifacts.SkipIfMissing();

        var first = WriteBundle("sst-first", passBody: "", failBody: "Error('SST-FAIL %1', 42);");
        var second = WriteBundle("sst-second", passBody: "Error('SST-NOW-FAILS');", failBody: "");

        var r1 = await SuiteServer.RunViaServer(first);
        Assert.Equal(1, r1.ExitCode);
        Assert.Equal("pass", r1.StatusOf("Codeunit69981.Passes"));
        Assert.Equal("fail", r1.StatusOf("Codeunit69981.Fails"));
        Assert.Contains("SST-FAIL 42", r1.Tests.Single(t => t.Name == "Codeunit69981.Fails").Message);

        var r2 = await SuiteServer.RunViaServer(second);
        Assert.Equal("fail", r2.StatusOf("Codeunit69981.Passes"));
        Assert.Equal("pass", r2.StatusOf("Codeunit69981.Fails"));
        Assert.Contains("SST-NOW-FAILS", r2.Tests.Single(t => t.Name == "Codeunit69981.Passes").Message);
    }

    private static string WriteBundle(string name, string passBody, string failBody)
    {
        var dir = TestScratch.Dir("al-runner-" + name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "SuiteServer {{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0", "idRanges": [ { "from": 69980, "to": 69989 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit 69981 "SST Tests"
        {
            Subtype = Test;

            [Test]
            procedure Passes()
            begin
                {{passBody}}
            end;

            [Test]
            procedure Fails()
            begin
                {{failBody}}
            end;
        }
        """);
        return dir;
    }
}
