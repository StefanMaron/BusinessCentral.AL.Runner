// Issue #2926. ArtifactDownloader.VersionExists returns a plain bool, and it returns false for
// two things that are not the same: "the CDN answered 404, this build is not published" and
// "the probe never got an answer". BcArtifacts.ResolveProvisionTargetCore reads false as the
// first one and drops a tier; these two notices then printed that reading as fact.
//
// On a transient network fault — the address-family failure this issue was filed about, a DNS
// hiccup, a timeout — both sentences were false, and both sent the reader to check Microsoft's
// publishing instead of their own network. That is the reported defect, one layer up.
//
// The bool cannot carry a third state without changing ResolveProvisionTargetCore's contract,
// so what these tests pin is the weaker claim that holds either way.
using Xunit;

namespace AlRunner.Tests;

public sealed class ProvisionNoticeClaimsTests
{
    [Fact]
    public void CdnMinorNotice_DoesNotAssertTheBuildWasNeverPublished()
    {
        var notice = AlRunner.ProgramSupport.CdnMinorProvisionNotice("28.1.49838.53910", "28.1");

        // The claim it must not make: this is only knowable from a 404, and the caller cannot
        // tell a 404 from a failed probe.
        Assert.DoesNotContain("is not published", notice);
        Assert.DoesNotContain("not available", notice);
        // ...and the weaker claim it must still make, or the message stops being useful.
        Assert.Contains("could not be obtained from the CDN", notice);
        Assert.Contains("28.1.49838.53910", notice);
        Assert.Contains("provisioning the latest 28.1.x instead", notice);
        // The actionable line has to survive the rewording.
        Assert.Contains("al-runner provision --bc-version 28.1.49838.53910", notice);
    }

    [Fact]
    public void MajorFallbackWarning_DoesNotAssertTheCdnHasNothing()
    {
        var notice = AlRunner.ProgramSupport.MajorFallbackWarning("28.1.49838.53910", "28.1", "28");

        Assert.DoesNotContain("not available", notice);
        Assert.DoesNotContain("is not published", notice);
        Assert.Contains("could not be obtained from the CDN", notice);
        // It stays a warning: this tier IS degraded, whatever the reason for the miss.
        Assert.Contains("warning:", notice);
        Assert.Contains("KNOWN-DEGRADED", notice);
        Assert.Contains("Falling back to the latest 28.x", notice);
        Assert.Contains("al-runner provision --bc-version 28.1", notice);
    }

    [Fact]
    public void BothNotices_StillSayTheCdnWasConsulted()
    {
        // The mirror defect, from the comment at the top of DefaultProvisionTargetMessagingTests:
        // the offline branch once claimed a CDN check that never happened. These two branches
        // DID consult the CDN, so removing the word entirely would be the opposite error.
        Assert.Contains("CDN", AlRunner.ProgramSupport.CdnMinorProvisionNotice("28.1.49838.53910", "28.1"));
        Assert.Contains("CDN", AlRunner.ProgramSupport.MajorFallbackWarning("28.1.49838.53910", "28.1", "28"));
    }
}
