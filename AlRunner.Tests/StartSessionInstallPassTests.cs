// The runner-side mechanism behind #3292: InstallTriggerRunner's install-pass flag, and
// AlRunnerStartSession's refusal while it is set.
//
// The BC behaviour (StartSession during install returns false, leaves SessionId alone, runs no
// worker) is pinned upstream by corpus codeunit 60449, and end to end through a real install
// trigger by tests/runner-extras/install-trigger-seed. What only C# can pin is the scope's
// lifetime: cleared on dispose, cleared when the install body throws, and nesting-safe. A flag
// left latched would refuse every StartSession for the rest of the process.

using System;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection("InstallPassFlag")]
public sealed class StartSessionInstallPassTests
{
    // No loaded assembly declares this codeunit, so outside the install pass StartSession
    // reaches dispatch and fails on lookup. That failure is the negative control: it shows the
    // install-pass refusal, not the lookup, is what answers false inside the pass.
    private const int NoSuchCodeunit = 1_999_999_999;

    [Fact]
    public void InsideTheInstallPass_StartSessionReturnsFalse_AndLeavesSessionIdAlone()
    {
        int slot = 777;
        var sessionId = new ByRef<int>(() => slot, v => slot = v);

        bool result;
        using (InstallTriggerRunner.EnterInstallPass())
            result = BcRuntime.AlRunnerStartSession(
                DataError.ThrowError, sessionId, NoSuchCodeunit, null, null);

        Assert.False(result);
        Assert.Equal(777, slot);
    }

    [Fact]
    public void OutsideTheInstallPass_StartSessionReachesDispatch()
    {
        int slot = 777;
        var sessionId = new ByRef<int>(() => slot, v => slot = v);

        Assert.False(InstallTriggerRunner.InInstallPass);
        var ex = Record.Exception(() => BcRuntime.AlRunnerStartSession(
            DataError.ThrowError, sessionId, NoSuchCodeunit, null, null));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains($"codeunit {NoSuchCodeunit} is not present", ex!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlag_IsClearedWhenTheInstallBodyThrows()
    {
        Action installBody = () =>
        {
            using (InstallTriggerRunner.EnterInstallPass())
            {
                Assert.True(InstallTriggerRunner.InInstallPass);
                throw new InvalidOperationException("install trigger failed");
            }
        };
        Assert.Throws<InvalidOperationException>(installBody);

        Assert.False(InstallTriggerRunner.InInstallPass);
    }

    [Fact]
    public void NestedScopes_KeepTheFlagUntilTheOutermostEnds_AndDoubleDisposeIsHarmless()
    {
        var outer = InstallTriggerRunner.EnterInstallPass();
        var inner = InstallTriggerRunner.EnterInstallPass();
        inner.Dispose();
        inner.Dispose();
        Assert.True(InstallTriggerRunner.InInstallPass);

        outer.Dispose();
        Assert.False(InstallTriggerRunner.InInstallPass);
    }
}
