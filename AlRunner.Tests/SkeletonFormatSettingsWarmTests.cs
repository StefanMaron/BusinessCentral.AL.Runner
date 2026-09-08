// SkeletonFormatSettingsWarmTests - the FormatSettings warm says when it did not warm (#3462).
//
// BcRuntime.WarmSkeletonFormatSettings closes the first-touch race on NavSession's lazily
// built, unlocked FormatSettings dictionary (#3444). Its catch writes a stderr warning, but
// the body is `(_skeletonSession as NavSession)?.FormatSettings` - so when the skeleton
// session is null, or is not a NavSession, the null-conditional skipped the warm and NOTHING
// was written anywhere. The window the warm exists to close was then still open and no output
// said so. RED against main: the writer is empty on both skip arms.
using System;
using System.IO;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class SkeletonFormatSettingsWarmTests
{
    private static string Warm(object? session)
    {
        var err = new StringWriter();
        BcRuntime.WarmSkeletonFormatSettings(session, err);
        return err.ToString();
    }

    [Fact]
    public void Warm_WithNoSkeletonSession_SaysSoRatherThanSkippingSilently()
    {
        var written = Warm(null);

        Assert.Contains("FormatSettings warm skipped", written);
        Assert.Contains("no skeleton session", written);
    }

    [Fact]
    public void Warm_WithASessionThatIsNotANavSession_NamesTheTypeItGotInstead()
    {
        var written = Warm(new StringBuilderStandIn());

        Assert.Contains("FormatSettings warm skipped", written);
        Assert.Contains("NavSession", written);
        Assert.Contains(nameof(StringBuilderStandIn), written);
    }

    // The control arm. Without it "it always writes something" would satisfy both assertions
    // above and prove nothing: a warm that actually ran must stay silent, because this runs at
    // every runner start and a line on the happy path is noise on every single run.
    [Fact]
    public void Warm_WithTheRealSkeletonSession_WarmsAndWritesNothing()
    {
        // Same trigger TestPageEvaluatorFaultTests uses to make sure the runtime is up.
        _ = Microsoft.Dynamics.Nav.Runtime.NavDate.Create(
            DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Local));
        Assert.NotNull(BcRuntime.SkeletonSession);

        Assert.Equal(string.Empty, Warm(BcRuntime.SkeletonSession));
    }

    private sealed class StringBuilderStandIn { }
}
