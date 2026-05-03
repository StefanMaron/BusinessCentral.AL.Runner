using AlRunner;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Integration tests for the --fail-on-stub flag (issue #1519).
///
/// Each test runs the pipeline with AL source written to temp dirs and verifies:
/// - Without FailOnStub: stub calls and no-ops silently pass (existing behavior).
/// - With FailOnStub: stub calls and no-ops fail with a RunnerGapException message.
/// </summary>
[Collection("Pipeline")]
public class FailOnStubTests
{
    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// Write AL source to a temp directory and return the dir path.
    private static string WriteTempAl(string fileName, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), source);
        return dir;
    }

    /// Run the pipeline with a single AL source written to a temp dir.
    private static PipelineResult RunWithAl(string al, bool failOnStub = false, int? timeoutSec = null)
    {
        var dir = WriteTempAl("Test.al", al);
        var pipeline = new AlRunnerPipeline();
        var opts = new PipelineOptions
        {
            TestIsolation = TestIsolation.Method,
            FailOnStub = failOnStub,
            InputPaths = { dir }
        };
        if (timeoutSec.HasValue)
            opts.TestTimeoutSeconds = timeoutSec.Value;
        return pipeline.Run(opts);
    }

    // ────────────────────────────────────────────────────────────────────────
    // AL snippets
    // ────────────────────────────────────────────────────────────────────────

    private const string CommitTestAl = """
        codeunit 99001 "Commit Test"
        {
            Subtype = Test;
            var Assert: Codeunit Assert;

            [Test]
            procedure CallCommit()
            begin
                Commit();
                Assert.IsTrue(true, 'Commit must not throw');
            end;
        }
        """;

    private const string RealHelperAl = """
        codeunit 99010 "Real Helper Source"
        {
            procedure GetValue(): Integer
            begin
                exit(42);
            end;
        }

        codeunit 99011 "Real Helper Test"
        {
            Subtype = Test;
            var Assert: Codeunit Assert;

            [Test]
            procedure CallRealHelper()
            var
                H: Codeunit "Real Helper Source";
            begin
                Assert.AreEqual(42, H.GetValue(), 'Real helper must return 42');
            end;
        }
        """;

    // ────────────────────────────────────────────────────────────────────────
    // Commit() no-op tests
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Commit_WithoutFailOnStub_Passes()
    {
        // Without the flag, Commit() is a silent no-op — existing behavior must not change.
        var result = RunWithAl(CommitTestAl, failOnStub: false);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed >= 1, "Expected at least 1 passing test");
    }

    [Fact]
    public void Commit_WithFailOnStub_FailsWithRunnerGapException()
    {
        // With --fail-on-stub, Commit() must fail the test with a message naming the call.
        var result = RunWithAl(CommitTestAl, failOnStub: true);
        Assert.NotEqual(0, result.ExitCode);
        // The failure output must name what was called
        var allOutput = result.StdOut + result.StdErr;
        Assert.Contains("Commit()", allOutput);
    }

    [Fact]
    public void Commit_WithFailOnStub_NoTimeout_FailsWithRunnerGapException()
    {
        // Verify the synchronous path (no background thread) also propagates FailOnStub.
        var result = RunWithAl(CommitTestAl, failOnStub: true, timeoutSec: 0);
        Assert.NotEqual(0, result.ExitCode);
        var allOutput = result.StdOut + result.StdErr;
        Assert.Contains("Commit()", allOutput);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Real helper (non-stub) — must always pass regardless of the flag
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RealHelper_WithFailOnStub_StillPasses()
    {
        // A codeunit compiled from source is never registered as an auto-stub.
        // --fail-on-stub must not block calls to real compiled codeunits.
        var result = RunWithAl(RealHelperAl, failOnStub: true);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Passed >= 1, "Real helper tests must pass");
    }

    [Fact]
    public void RealHelper_ReturnsConcreteValue_WithFailOnStub()
    {
        // Positive: the real helper returns 42, not a default value.
        var result = RunWithAl(RealHelperAl, failOnStub: true);
        Assert.Equal(0, result.ExitCode);
        // All tests in the helper suite pass
        Assert.Equal(0, result.Failed);
    }

    // ────────────────────────────────────────────────────────────────────────
    // StubCallGuard unit tests (no pipeline overhead)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StubCallGuard_CheckNoOp_ThrowsWhenFlagSet()
    {
        StubCallGuard.FailOnStub = true;
        try
        {
            var ex = Assert.Throws<RunnerGapException>(
                () => StubCallGuard.CheckNoOp("Commit()"));
            Assert.Contains("Commit()", ex.Message);
            Assert.Contains("no-op", ex.Message);
        }
        finally
        {
            StubCallGuard.FailOnStub = false;
        }
    }

    [Fact]
    public void StubCallGuard_CheckNoOp_DoesNotThrowWhenFlagNotSet()
    {
        StubCallGuard.FailOnStub = false;
        // Must not throw
        StubCallGuard.CheckNoOp("Commit()");
    }

    [Fact]
    public void StubCallGuard_CheckStub_ThrowsWhenFlagSet()
    {
        StubCallGuard.FailOnStub = true;
        try
        {
            var ex = Assert.Throws<RunnerGapException>(
                () => StubCallGuard.CheckStub("My Codeunit", "DoWork"));
            Assert.Contains("My Codeunit", ex.Message);
            Assert.Contains("DoWork", ex.Message);
            Assert.Contains("blank shell", ex.Message);
        }
        finally
        {
            StubCallGuard.FailOnStub = false;
        }
    }

    [Fact]
    public void StubCallGuard_CheckStub_DoesNotThrowWhenFlagNotSet()
    {
        StubCallGuard.FailOnStub = false;
        // Must not throw
        StubCallGuard.CheckStub("My Codeunit", "DoWork");
    }

    [Fact]
    public void StubCallGuard_CheckStubById_ThrowsForRegisteredId()
    {
        StubCallGuard.FailOnStub = true;
        StubCallGuard.AutoStubbedCodeunitNames[99999] = "Registered Stub";
        try
        {
            var ex = Assert.Throws<RunnerGapException>(
                () => StubCallGuard.CheckStubById(99999, "DoWork"));
            Assert.Contains("Registered Stub", ex.Message);
            Assert.Contains("blank shell", ex.Message);
        }
        finally
        {
            StubCallGuard.FailOnStub = false;
            StubCallGuard.AutoStubbedCodeunitNames.TryRemove(99999, out _);
        }
    }

    [Fact]
    public void StubCallGuard_CheckStubById_DoesNotThrowForUnregisteredId()
    {
        StubCallGuard.FailOnStub = true;
        try
        {
            // ID 88888 is not in AutoStubbedCodeunitNames — should not throw
            StubCallGuard.CheckStubById(88888, "DoWork");
        }
        finally
        {
            StubCallGuard.FailOnStub = false;
        }
    }
}
