// Issue #2236: al-runner only ever downloaded the w1 (worldwide) BC artifact, so any
// codebase built against a country localization (e.g. a US customer extension depending on
// "IRS Forms") could not be auto-provisioned at all. These tests pin the pure, no-network
// pieces of the fix — URL construction, country normalization, and the ZIP-entry selection
// rule (including the Extensions/-vs-Applications.<CC>/ duplicate-basename trap) — without
// downloading a single byte, per .claude/rules/local-test-scope.md ("do not add a network
// dependency to the default unit-test run"). The real end-to-end download is verified
// manually against the live CDN (see the PR description), not here.
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

public sealed class ArtifactDownloaderCountryTests
{
    [Theory]
    [InlineData(null, "w1")]
    [InlineData("", "w1")]
    [InlineData("   ", "w1")]
    [InlineData("W1", "w1")]
    [InlineData("US", "us")]
    [InlineData(" us ", "us")]
    [InlineData("de", "de")]
    public void NormalizeCountry_TrimsLowercasesAndDefaultsToW1(string? input, string expected)
        => Assert.Equal(expected, ArtifactDownloader.NormalizeCountry(input));

    [Fact]
    public void BuildArtifactUrl_UsesCountryAsTheChannelSegment()
    {
        Assert.Equal(
            "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/28.4.53241.53989/w1",
            ArtifactDownloader.BuildArtifactUrl("28.4.53241.53989", "w1"));
        Assert.Equal(
            "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/28.4.53241.53989/us",
            ArtifactDownloader.BuildArtifactUrl("28.4.53241.53989", "us"));
    }

    [Theory]
    [InlineData("Extensions/Microsoft_Base Application_28.4.53241.53989.app", true)]
    [InlineData("Extensions/Microsoft_System Application_28.4.53241.53989.app", true)]
    [InlineData("Extensions/Microsoft_Business Foundation_28.4.53241.53989.app", true)]
    [InlineData("Extensions/Microsoft_Application_28.4.53241.53989.app", true)]
    [InlineData("Extensions/Microsoft_Application Test Library_28.4.53241.53989.app", true)]
    public void IsWantedPlatformAppEntry_W1_MatchesTheFiveCuratedApps(string entryName, bool expected)
        => Assert.Equal(expected, ArtifactDownloader.IsWantedPlatformAppEntry(entryName, isW1: true));

    [Fact]
    public void IsWantedPlatformAppEntry_W1_RejectsACountrySpecificMicrosoftApp()
    {
        // The exact real-world repro from #2236: "IRS Forms" is Microsoft-published but is
        // NOT one of the curated w1 apps — the w1 selection must not pick it up (there would
        // be nothing sane to do with it: w1 never ships it in the first place).
        Assert.False(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Extensions/Microsoft_IRS Forms_27.5.53238.55217.app", isW1: true));
    }

    [Fact]
    public void IsWantedPlatformAppEntry_NonW1Country_MatchesAnyMicrosoftAppUnderExtensions()
    {
        // A country channel is not "w1 plus extras": IRS Forms and its own transitive
        // Microsoft dependencies (the "_Exclude_*" apps) must all be pulled in, with no
        // hand-maintained per-country name list.
        Assert.True(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Extensions/Microsoft_IRS Forms_27.5.53238.55217.app", isW1: false));
        Assert.True(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Extensions/Microsoft__Exclude_APIV1__27.5.53238.55217.app", isW1: false));
        // The localized core set is still covered (it also starts with "microsoft_").
        Assert.True(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Extensions/Microsoft_Base Application_27.5.53238.55217.app", isW1: false));
    }

    [Fact]
    public void IsWantedPlatformAppEntry_RejectsTheApplicationsCountryFolderDuplicate()
    {
        // The exact trap #2236 measured: a country artifact ALSO ships
        // Applications.US/Microsoft_Base Application_....app — a DIFFERENT, smaller,
        // non-localized file with the SAME basename as the correct one under Extensions/.
        // Selecting by basename alone would silently ship the wrong file; anchoring on the
        // Extensions/ folder must reject this entry regardless of country.
        Assert.False(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Applications.US/Microsoft_Base Application_28.4.53241.53989.app", isW1: false));
        Assert.False(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Applications.US/Microsoft_Base Application_28.4.53241.53989.app", isW1: true));
    }

    [Fact]
    public void IsWantedPlatformAppEntry_RejectsNonAppFilesAndNonMicrosoftPublishers()
    {
        Assert.False(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Extensions/Microsoft_Base Application_28.4.53241.53989.dll", isW1: false));
        Assert.False(ArtifactDownloader.IsWantedPlatformAppEntry(
            "Extensions/Contoso_Custom Localization_1.0.0.0.app", isW1: false));
    }
}
