// #5477: how the platform-apps attempt's captured output is put back in front of the user.
// The end-to-end claims (JSON on the real stdout, a startup line once) are in
// DeferredPlatformAppsProvisioningTests; these pin the replay's own rules without a subprocess.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DeferredPlatformAppsReplayTests
{
    private const string M = DeferredPlatformAppsAttempt.BeginMarker;

    private static (string Out, string Err) Replay(params (bool IsError, string Line)[] lines)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        new DeferredPlatformAppsAttempt.Result(0, lines).Replay(o, e);
        return (o.ToString(), e.ToString());
    }

    [Fact]
    public void NonErrorLines_GoToTheGivenStdout_ErrorLinesToTheGivenStderr()
    {
        var (o, e) = Replay((false, "{\"exitCode\":0}"), (true, "diagnostic"));

        Assert.Equal("{\"exitCode\":0}\n", o);
        Assert.Equal("diagnostic\n", e);
    }

    [Fact]
    public void EachStream_DropsItsLinesBeforeItsOwnMarker_AndTheMarkerItself()
    {
        var (o, e) = Replay(
            (false, "out-before"), (true, "err-before"), (false, M), (true, M),
            (false, "out-after"), (true, "err-after"));

        Assert.Equal("out-after\n", o);
        Assert.Equal("err-after\n", e);
    }

    [Fact]
    public void AStreamWithNoMarker_IsReplayedWhole_WhileTheOtherIsTrimmed()
    {
        // A child that did not reach the marker on one stream must cost duplicated lines, never lost ones.
        var (o, e) = Replay((false, "out-before"), (false, M), (false, "out-after"), (true, "err-1"), (true, "err-2"));

        Assert.Equal("out-after\n", o);
        Assert.Equal("err-1\nerr-2\n", e);
    }

    [Fact]
    public void NoMarkerAnywhere_ReplaysEverything()
    {
        var (o, e) = Replay((false, "a"), (true, "b"), (false, "c"));

        Assert.Equal("a\nc\n", o);
        Assert.Equal("b\n", e);
    }

    [Fact]
    public void OnlyTheFirstMarkerOfAStreamTrims()
    {
        var (o, _) = Replay((false, "x"), (false, M), (false, "y"), (false, M), (false, "z"));

        Assert.Equal("y\n" + M + "\nz\n", o);
    }
}
