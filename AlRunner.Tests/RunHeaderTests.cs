// RunHeaderTests — #4599: one run header line in place of the `[bc] selected BC` line and the
// `al-runner — running N bundle(s)` banner. The spawned-runner side (default prints the header
// and not the two old lines; --verbose adds the artifact path) is in
// CleanRunStartupVerbosityTests and CountryFlagTests; these pin the line's text without a spawn.

using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunHeaderTests
{
    [Fact]
    public void OneApp_W1_IsVersionBcBuildAndSingularApp()
        => Assert.Equal("al-runner 2.11.0 · BC 28.1.49838.53910 · 1 app",
            ProgramSupport.RunHeader("2.11.0", "28.1.49838.53910", "w1", 1, watch: false));

    [Fact]
    public void SeveralApps_ArePlural()
        => Assert.Equal("al-runner 2.11.0 · BC 28.1.49838.53910 · 3 apps",
            ProgramSupport.RunHeader("2.11.0", "28.1.49838.53910", "w1", 3, watch: false));

    // #2236: a non-w1 country stays visible, beside the BC build it qualifies.
    [Fact]
    public void NonW1Country_IsNamedAfterTheBcBuild()
        => Assert.Equal("al-runner 2.11.0 · BC 28.1.49838.53910 [country: us] · 1 app",
            ProgramSupport.RunHeader("2.11.0", "28.1.49838.53910", "us", 1, watch: false));

    [Fact]
    public void WatchMode_SaysSoAfterTheAppCount()
        => Assert.Equal("al-runner 2.11.0 · BC 28.1.49838.53910 · 2 apps · watch mode (Ctrl+C to quit)",
            ProgramSupport.RunHeader("2.11.0", "28.1.49838.53910", "w1", 2, watch: true));

    [Fact]
    public void SelectedBcLine_NamesThePath_AndTheCountryOnlyWhenNotW1()
    {
        Assert.Equal("[bc] selected BC 28.1.49838.53910 (/a/28.1.49838.53910)",
            ProgramSupport.SelectedBcLine("28.1.49838.53910", "/a/28.1.49838.53910", "w1"));
        Assert.Equal("[bc] selected BC 28.1.49838.53910 (/a/28.1.49838.53910) [country: de]",
            ProgramSupport.SelectedBcLine("28.1.49838.53910", "/a/28.1.49838.53910", "de"));
    }
}
