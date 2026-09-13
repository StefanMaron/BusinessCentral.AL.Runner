// The runner-side mechanism behind #3292, after #4049: AlRunnerStartSession refuses while BC's own
// NavSession.AppInstallationContext (or AppUpgradeContext) is set, and InstallExecutionContext sets
// and clears that context around install triggers.
//
// The BC behaviour (StartSession during install returns false, leaves SessionId alone, runs no
// worker) is pinned upstream by corpus codeunit 60449, end to end through a real install trigger by
// tests/runner-extras/install-trigger-seed, and by InstallExecutionContextTests
// (IecStartSessionInsideInstallWasRefused). What only C# can pin here is the scope's lifetime:
// cleared on dispose, cleared when the install body throws, and cleared once. A context left set
// would refuse every StartSession for the rest of the process.

using System;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

// Serial: the TestIsolation state the #2805 guard reads first is process-wide, and so is the
// thread's NavCurrentThread.Session an install pass would set a context on.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallPassFlagSerialCollection
{
    public const string Name = "InstallPassFlag";
}

[Collection(InstallPassFlagSerialCollection.Name)]
public sealed class StartSessionInstallPassTests
{
    // No loaded assembly declares this codeunit, so outside an install StartSession reaches
    // dispatch and fails on lookup — the negative control for the refusal.
    private const int NoSuchCodeunit = 1_999_999_999;

    [Fact]
    public void NoSession_IsNotAnInstallOrUpgrade()
    {
        Assert.False(BcRuntime.IsInstallOrUpgradeInProgress(null));
    }

    [Fact]
    public void OutsideAnInstall_StartSessionReachesDispatch()
    {
        int slot = 777;
        var sessionId = new ByRef<int>(() => slot, v => slot = v);

        Assert.False(BcRuntime.IsInstallOrUpgradeInProgress(NavCurrentThread.Session));
        var ex = Record.Exception(() => BcRuntime.AlRunnerStartSession(
            DataError.ThrowError, sessionId, NoSuchCodeunit, null, null));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains($"codeunit {NoSuchCodeunit} is not present", ex!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScope_ClearsWhenTheInstallBodyThrows()
    {
        int clears = 0;
        Action installBody = () =>
        {
            using (new InstallExecutionContext.Scope(() => clears++))
                throw new InvalidOperationException("install trigger failed");
        };
        Assert.Throws<InvalidOperationException>(installBody);

        Assert.Equal(1, clears);
    }

    [Fact]
    public void TheScope_ClearsOnce_AndDoubleDisposeIsHarmless()
    {
        int clears = 0;
        var scope = new InstallExecutionContext.Scope(() => clears++);
        Assert.Equal(0, clears);
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(1, clears);
    }
}
