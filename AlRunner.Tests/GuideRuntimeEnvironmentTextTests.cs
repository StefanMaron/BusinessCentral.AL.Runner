// GuideRuntimeEnvironmentTextTests — the proving tests for #4932: --guide must say what
// environment AL code sees by default (Production, not a sandbox, not SaaS), how a test changes
// it, and where the full explanation lives. Text assertions on PrintGuide, for the reason
// TestDataCompanyHelpTextTests gives: the claim is about the text this repository ships.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class GuideRuntimeEnvironmentTextTests
{
    private static string Guide()
    {
        var w = new StringWriter();
        ProgramSupport.PrintGuide(w);
        return w.ToString();
    }

    [Fact]
    public void Guide_StatesTheDefaultEnvironmentType()
    {
        var guide = Guide();

        Assert.Contains("RUNTIME ENVIRONMENT", guide, StringComparison.Ordinal);
        Assert.Contains("IsProduction() = true", guide, StringComparison.Ordinal);
        Assert.Contains("IsSandbox() = false", guide, StringComparison.Ordinal);
        Assert.Contains("IsSaaS() = false", guide, StringComparison.Ordinal);
    }

    /// <summary>The default alone would read as "the runner cannot be a sandbox"; the guide must
    /// name the calls that switch it, the same ones a service tier honours.</summary>
    [Fact]
    public void Guide_NamesHowATestSwitchesToSandbox()
    {
        var guide = Guide();

        Assert.Contains("SetTestTenantEnvironmentType(true)", guide, StringComparison.Ordinal);
        Assert.Contains("SetTestabilitySandbox", guide, StringComparison.Ordinal);
    }

    /// <summary>IsProduction() is !NavTenantSettingsHelper.IsSandbox() and reads no testability
    /// flag (NavUserAccount.dll 28.5.54151.55132), so only the tenant setter moves it. Naming the
    /// testability setters without that caveat promised a switch they do not make (PR #4933 review).</summary>
    [Fact]
    public void Guide_SaysWhichSetterMovesIsProduction()
    {
        var guide = Guide();

        Assert.Contains("IsSandbox() and IsSaaS() become true, IsProduction()", guide, StringComparison.Ordinal);
        Assert.Contains("with either, IsProduction() stays true", guide, StringComparison.Ordinal);
        Assert.Contains("(OnPrem scope)", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Guide_StatesTopologyAndLicenseDefaults()
    {
        var guide = Guide();

        Assert.Contains("Service topology: always on-premises", guide, StringComparison.Ordinal);
        Assert.Contains("named-user limit on User writes is never enforced (#4700)", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Guide_PointsAtTheEnvironmentTypeLimitationAnchor()
    {
        Assert.Contains("docs/limitations.md#environment-type", Guide(), StringComparison.Ordinal);
    }

    /// <summary>An installed tool has no docs/ directory beside it, so the relative paths the
    /// guide cites must be resolvable online.</summary>
    [Fact]
    public void Guide_SaysWhereDocsPathsResolveOnline()
    {
        Assert.Contains("https://github.com/StefanMaron/BusinessCentral.AL.Runner/blob/main/",
            Guide(), StringComparison.Ordinal);
    }
}
