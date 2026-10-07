using Xunit;

namespace AlRunner.Tests;

/// <summary>#5420: the three outcomes of <see cref="LiveCdnGate.Require(string?, bool)"/>.</summary>
public sealed class LiveCdnGateTests
{
    [Fact]
    public void ReachableCdn_LetsTheTestRun()
    {
        LiveCdnGate.Require(null, runningOnCi: false);
        LiveCdnGate.Require(null, runningOnCi: true);
    }

    [Fact]
    public void UnreachableCdn_OnADevBox_IsAVisibleSkipNamingTheObservation()
    {
        var skip = Assert.Throws<SkipException>(
            () => LiveCdnGate.Require("TaskCanceledException: the request was canceled", runningOnCi: false));

        Assert.Contains("did not answer", skip.Message, StringComparison.Ordinal);
        Assert.Contains("TaskCanceledException", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableCdn_OnCi_FailsInsteadOfSkipping()
    {
        // Not a SkipException: a leg whose live-CDN tests all skipped would be green having run none.
        var failure = Assert.ThrowsAny<Exception>(
            () => LiveCdnGate.Require("TaskCanceledException: the request was canceled", runningOnCi: true));

        Assert.IsNotType<SkipException>(failure);
        Assert.Contains("not a skip", failure.Message, StringComparison.Ordinal);
        Assert.Contains("TaskCanceledException", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealRequire_OnACiShapedEnvironment_FailsInsteadOfSkipping()
    {
        // The overload the live-CDN facts actually call, with the probe and the environment injected
        // (never the process's own), so a Require() that ignored CI would turn every such fact into a skip.
        using var _ = LiveCdnGate.OverrideForTests(
            () => "TaskCanceledException: stalled",
            name => name == "GITHUB_ACTIONS" ? "true" : null);

        var failure = Assert.ThrowsAny<Exception>(() => LiveCdnGate.Require());

        Assert.IsNotType<SkipException>(failure);
        Assert.Contains("not a skip", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealRequire_OnADevBoxEnvironment_Skips()
    {
        using var _ = LiveCdnGate.OverrideForTests(() => "TaskCanceledException: stalled", _ => null);

        Assert.Throws<SkipException>(() => LiveCdnGate.Require());
    }

    [Fact]
    public void ProbeWithRetry_AnAttemptThatRecovers_IsReachable()
    {
        var calls = 0;
        var gaps = 0;

        var result = LiveCdnGate.ProbeWithRetry(() => ++calls == 1 ? "slow" : null, () => gaps++);

        Assert.Null(result);
        Assert.Equal(2, calls);
        Assert.Equal(1, gaps);
    }

    [Fact]
    public void ProbeWithRetry_TwoFailures_ReportTheSecondAndRetryOnlyOnce()
    {
        var calls = 0;

        var result = LiveCdnGate.ProbeWithRetry(() => $"failure {++calls}", () => { });

        Assert.Equal("failure 2", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ProbeWithRetry_AFirstSuccess_IsNotRetried()
    {
        var calls = 0;

        Assert.Null(LiveCdnGate.ProbeWithRetry(() => { calls++; return null; }, () => throw new InvalidOperationException("no gap expected")));
        Assert.Equal(1, calls);
    }
}
