// StartSessionIsolationGuardTests — the plumbing behind #2805's StartSession guard.
//
// BC refuses StartSession called from inside a test codeunit unless the TestRunner declares
// TestIsolation = Disabled. It is the FIRST statement of ALSession.ALStartSessionAsyncImpl,
// before the timeout check and before a session id is assigned:
//
//     if (session.TestExecution != null
//         && (!session.TestExecution.CommitTestCodeunits
//             || !session.TestExecution.CommitTestFunctions))
//         throw new NavTestStartSessionNotAllowedException();
//
// BcRuntime.AlRunnerStartSession now mirrors it, reading two ambient facts: whether a [Test]
// body is executing (BcRuntime.InTestExecutionScope, off BC's own executingTestCodeUnit field)
// and this run's isolation mode (TestExecutor.ActiveIsolation).
//
// The REFUSAL itself is proved end-to-end by the corpus — codeunit 60397 in
// session/TestStartSessionRecord.al, green on all eight BC versions and adjudicated by a real
// service tier, which is the only thing that can settle what BC does. These tests pin the half
// the corpus cannot see: that ActiveIsolation actually tracks the executor, and that the
// message text is the one the corpus matches on.
//
// Why that matters rather than being a tautology: the guard is CONDITIONAL. If ActiveIsolation
// silently stopped following the executor's Isolation property, the guard would keep reading
// whatever it last saw — refusing under Disabled, or permitting under Codeunit — and every
// symptom would look like a test-authoring mistake rather than a stale global. Nothing else
// covers that, because the corpus only ever runs under Codeunit isolation.

using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class StartSessionIsolationGuardTests
{
    // The exact substring the corpus asserts (Assert.ExpectedError is a StrPos match), and the
    // one the adapted runner-extras tests assert too. Duplicated here deliberately: if the
    // runner's wording drifts, this fails fast in the unit suite instead of only surfacing on
    // an eight-leg corpus run.
    private const string CorpusPinnedSubstring =
        "can only be started in tests that are run by a TestRunner that has TestIsolation set to Disabled";

    [Fact]
    public void TheRefusalMessage_ContainsTheSubstringTheCorpusPins()
    {
        var ex = BcRuntime.MakeStartSessionNotAllowedInTestException();

        Assert.Contains(CorpusPinnedSubstring, ex.Message, StringComparison.Ordinal);
        // BC's full sentence, so a reader of the runner's output sees what BC would have said.
        Assert.Equal(
            "Sessions can only be started in tests that are run by a TestRunner that has "
            + "TestIsolation set to Disabled.",
            ex.Message);
    }

    [Theory]
    [InlineData(TestIsolation.Codeunit)]
    [InlineData(TestIsolation.Test)]
    [InlineData(TestIsolation.Disabled)]
    public void ActiveIsolation_FollowsTheExecutorThatWasConfigured(TestIsolation mode)
    {
        var previous = TestExecutor.ActiveIsolation;
        try
        {
            var executor = new TestExecutor { Isolation = mode };

            Assert.Equal(mode, executor.Isolation);
            Assert.Equal(mode, TestExecutor.ActiveIsolation);
        }
        finally
        {
            new TestExecutor { Isolation = previous };
        }
    }

    [Fact]
    public void ActiveIsolation_ReflectsTheMostRecentlyConfiguredExecutor()
    {
        // The guard reads a static because it is called from a BC seam with no executor in
        // hand. This pins the consequence: the LAST executor configured wins, which is correct
        // for the runner's one-executor-at-a-time model and is exactly the assumption that
        // would break silently if that model ever changed.
        var previous = TestExecutor.ActiveIsolation;
        try
        {
            new TestExecutor { Isolation = TestIsolation.Codeunit };
            Assert.Equal(TestIsolation.Codeunit, TestExecutor.ActiveIsolation);

            new TestExecutor { Isolation = TestIsolation.Disabled };
            Assert.Equal(TestIsolation.Disabled, TestExecutor.ActiveIsolation);

            // And back, so this is not passing on a one-way latch.
            new TestExecutor { Isolation = TestIsolation.Codeunit };
            Assert.Equal(TestIsolation.Codeunit, TestExecutor.ActiveIsolation);
        }
        finally
        {
            new TestExecutor { Isolation = previous };
        }
    }

    [Fact]
    public void ADefaultExecutor_IsCodeunitIsolation_SoTheGuardIsArmedUnlessAskedOtherwise()
    {
        // Negative direction for the whole feature: if the default were Disabled, the guard
        // would be inert for every ordinary run and #2805 would still be open while looking
        // fixed.
        var previous = TestExecutor.ActiveIsolation;
        try
        {
            var executor = new TestExecutor();
            Assert.Equal(TestIsolation.Codeunit, executor.Isolation);
            Assert.NotEqual(TestIsolation.Disabled, TestExecutor.ActiveIsolation);
        }
        finally
        {
            new TestExecutor { Isolation = previous };
        }
    }
}
