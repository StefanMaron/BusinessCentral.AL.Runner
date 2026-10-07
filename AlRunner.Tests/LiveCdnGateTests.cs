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
}
