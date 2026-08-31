// Issue #2236: MissingDependencyException.ToDetailedMessage() used to recommend
// `al-runner provision` / `--auto-provision` for EVERY Microsoft-publisher dependency,
// even one that neither command can ever satisfy — a country-localization app (e.g. "IRS
// Forms") is not in the w1 (worldwide) artifact set those commands download, no matter how
// many times they are re-run. The repo owner ran exactly that advice, twice, against a real
// US customer extension and got a byte-identical message both times.
//
// This file pins the fix separately from the download plumbing (#2236's own instruction:
// "write this test first and make it pass before the download work — it is valuable on its
// own"): a missing dependency that IS one of the apps `provision`/`--auto-provision` can
// actually fetch keeps the existing advice; one that ISN'T gets a message naming the real
// reason (country localization) and the flag that actually helps (--country), not a repeat
// of advice already proven not to work.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class MissingDependencyCountryMessageTests
{
    private static MissingDependencyException Make(string publisher, string name)
        => new(publisher, name, "27.5.0.0", Guid.NewGuid(), new[] { "/some/package-cache" });

    [Fact]
    public void ToDetailedMessage_KnownW1PlatformApp_StillRecommendsProvision()
    {
        var ex = Make("Microsoft", "Base Application");
        var msg = ex.ToDetailedMessage("28.4.53241.53989");

        Assert.Contains("al-runner provision", msg);
        Assert.Contains("--auto-provision", msg);
        Assert.DoesNotContain("--country", msg);
        Assert.DoesNotContain("localization", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToDetailedMessage_KnownTestFrameworkApp_StillRecommendsProvision()
    {
        var ex = Make("Microsoft", "Library Assert");
        var msg = ex.ToDetailedMessage("28.4.53241.53989");

        Assert.Contains("al-runner provision", msg);
        Assert.DoesNotContain("--country", msg);
    }

    [Fact]
    public void ToDetailedMessage_UnknownMicrosoftApp_NamesCountryLocalizationInsteadOfDeadAdvice()
    {
        // "IRS Forms" is the exact real-world repro from #2236: Microsoft-published, but not
        // part of the curated w1 platform-apps set and not part of the test-toolkit set —
        // `al-runner provision --platform-apps` / `--test-apps` cannot fetch it, so the
        // message must not tell the reader to run either.
        var ex = Make("Microsoft", "IRS Forms");
        var msg = ex.ToDetailedMessage("27.5.53238.55217");

        Assert.Contains("--country", msg);
        Assert.Contains("w1", msg);
        Assert.Contains("localization", msg, StringComparison.OrdinalIgnoreCase);
        // The dead advice must be GONE, not merely supplemented — repeating it is exactly
        // what produced the byte-identical message the repo owner saw twice.
        Assert.DoesNotContain("al-runner provision --platform-apps", msg);
        Assert.DoesNotContain("al-runner provision --test-apps", msg);
        Assert.DoesNotContain("        al-runner provision\n", msg);
    }

    [Fact]
    public void ToDetailedMessage_NonMicrosoftDep_StillRecommendsPackageCacheNotCountry()
    {
        var ex = Make("Contoso", "Contoso Extension");
        var msg = ex.ToDetailedMessage();

        Assert.Contains("--package-cache", msg);
        Assert.DoesNotContain("--country", msg);
        Assert.DoesNotContain("al-runner provision", msg);
    }
}
