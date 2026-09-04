using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2681. AlRunner.csproj's <c>&lt;Version&gt;</c> is the version EVERY build that is not
/// a release reports — a local build, a dev build, a CI matrix build, a clone. Only
/// publish.yml overrides it (<c>-p:Version=</c>), so the default is what a contributor,
/// and every gap report they paste <c>--help</c> into, actually sees.
///
/// It sat at <c>2.0.0-preview.1</c> through ten releases (latest tag v2.10.0 when this was
/// found), because nothing exercises the default path during a release and nothing failed
/// when it drifted. That is the same shape as #2010's hardcoded <c>_BCVersion</c> pin,
/// which rotted unnoticed and took a release down after the tag was already pushed.
///
/// The fix is NOT to keep it current — a real version number here would need updating every
/// release and would conflict between every concurrent PR that touched it. It is to make the
/// default say "this is a build off main", naming nothing specific, so it never rots and
/// never needs a bump. <c>0.0.0-</c> is already this repo's marker for a non-release build:
/// bc-tests.yml's pack mirror packs with <c>-p:Version=0.0.0-ci</c>.
/// </summary>
public class RunnerVersionDefaultTests
{
    private static string CsprojVersion()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AlRunner", "AlRunner.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not locate AlRunner/AlRunner.csproj from the test working directory");
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "AlRunner", "AlRunner.csproj"));
        var m = Regex.Match(text, @"<Version>([^<]+)</Version>");
        Assert.True(m.Success, "AlRunner.csproj carries no <Version> element");
        return m.Groups[1].Value.Trim();
    }

    [Fact]
    public void CsprojVersion_IsANonReleaseMarker_NotSomethingThatLooksLikeAShippedRelease()
    {
        var version = CsprojVersion();
        Assert.True(
            version.StartsWith("0.0.0-", System.StringComparison.Ordinal),
            $"AlRunner.csproj <Version> is '{version}'. Every non-release build reports this, and "
            + "--help pastes it into gap reports, so it must not look like a shipped release. Use a "
            + "'0.0.0-<marker>' form (the repo already packs CI mirrors as 0.0.0-ci). Do NOT set it to "
            + "the current release number: that has to be bumped every release, conflicts between "
            + "concurrent PRs, and silently rots the moment someone forgets — see #2681 and #2010.");
    }

    [Fact]
    public void CsprojVersion_NamesNoSpecificReleaseOrDate_SoItNeverNeedsBumping()
    {
        var version = CsprojVersion();
        Assert.DoesNotMatch(new Regex(@"\d+\.\d+\.\d+(?!\-)"), version.Replace("0.0.0-", ""));
        Assert.DoesNotMatch(new Regex(@"20\d\d"), version);
    }
}
