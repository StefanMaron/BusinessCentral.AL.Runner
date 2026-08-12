// TestArtifacts — one source of truth for "can this machine run a test that needs BC
// artifacts?", and the only sanctioned way to say "it cannot".
//
// Why this file exists
// --------------------
// Twenty-three test classes each declared their own private ArtifactsPresent(), in three
// mutually inconsistent spellings:
//
//   a) `~/.bcartifacts.cache/sandbox` only            — 6 classes
//   b) `~/.local/share/al-runner/artifacts/<ver>` only — 15 classes
//   c) (a) with a fallback to (b)                      — 2 classes
//
// Nothing in this repo ever creates (a). `.github/workflows/test-matrix.yml` provisions
// `~/.local/share/al-runner/artifacts/<version>` (service tier), `~/.al-runner/platform-apps`,
// `~/.al-runner/test-apps` and `~/.cache/al-runner/ncl-cecil` — no `.bcartifacts.cache`
// anywhere. So on CI the six classes in group (a) always took the "environment unavailable"
// branch. It only ever looked provisioned on a dev box with a multi-GB BC sandbox artifact
// cached, which is where each of those gates was written.
//
// The gate drifted unnoticed because it was copied, and the fix was applied per class as
// each copy was noticed rather than centrally. Same reasoning as TestBuildConfig, which
// exists because the build configuration and BC version had drifted apart across suites.
// Hence: one helper, and a drift guard in TestArtifactsGateTests that fails the build if a
// class starts spelling the check itself again.
//
// Why the unavailable branch must THROW
// -------------------------------------
// Those gates were `if (!ArtifactsPresent()) { …; return; }`. An early return from a test
// method is a PASS: xUnit has nothing to distinguish "asserted everything" from "asserted
// nothing", so the run reported `Failed: 0, Passed: 485, Skipped: 0` while a chunk of those
// 485 executed no assertions at all. That is the silent-default anti-pattern from
// .claude/rules/loud-failures.md wearing test clothing — a green tick that means nothing ran.
//
// xUnit v2 has no dynamic skip of its own (Assert.Skip is absent from the shipped
// xunit.assert 2.9.3 surface, and its Xunit.Sdk.SkipException reports Failed because the v2
// execution engine never learned the $XunitDynamicSkip$ token). Xunit.SkippableFact supplies
// it: throw Xunit.SkipException from a [SkippableFact]/[SkippableTheory] and the result is a
// real Skipped, counted in the run summary and attributed with a reason.
//
// So a test that cannot run now says so out loud. And on CI it does not even get to skip:
// see CiMissingArtifactsMessage — a leg where the shared gate itself goes stale would
// otherwise skip everything, visibly and accurately, and still report green.

using Xunit;

namespace AlRunner.Tests;

internal static class TestArtifacts
{
    /// <summary>
    /// The BC service-tier artifacts directory `.github/workflows/test-matrix.yml`
    /// provisions (AlRunner.csproj's EnsureBCServiceTierDlls writes here too), relative
    /// to the home directory. Holds one subdirectory per BC version.
    /// </summary>
    private static readonly string[] StandardCacheRelative = [".local", "share", "al-runner", "artifacts"];

    /// <summary>
    /// The legacy BcContainerHelper artifact cache. Nothing in this repo creates it; it is
    /// still honoured because a dev box that has downloaded a full BC sandbox artifact can
    /// genuinely run these tests from it (Program.cs scans it as a default package cache).
    /// </summary>
    private static readonly string[] LegacyCacheRelative = [".bcartifacts.cache", "sandbox"];

    /// <summary>This machine's home directory, or null when it cannot be determined.</summary>
    internal static string? HomeDir()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home)) return home;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : profile;
    }

    internal static string StandardCacheDir(string home) => Path.Combine([home, .. StandardCacheRelative]);

    internal static string LegacyCacheDir(string home) => Path.Combine([home, .. LegacyCacheRelative]);

    /// <summary>True when BC artifacts are provisioned on this machine.</summary>
    internal static bool Present() => PresentIn(HomeDir());

    /// <summary>
    /// <see cref="Present"/> against an explicit home directory, so the detection itself is
    /// testable against a constructed layout rather than only against whatever the current
    /// machine happens to have.
    /// </summary>
    internal static bool PresentIn(string? home)
    {
        if (string.IsNullOrEmpty(home)) return false;

        // Legacy full-sandbox cache: presence of the sandbox root is enough — the version
        // dirs underneath are what Program.cs scans.
        if (Directory.Exists(LegacyCacheDir(home))) return true;

        // What CI provisions. The ROOT alone is not provisioning: the download step creates
        // artifacts/<version>/, and an empty (or wiped) root carries no service-tier DLLs.
        var standard = StandardCacheDir(home);
        return Directory.Exists(standard) && Directory.EnumerateDirectories(standard).Any();
    }

    /// <summary>
    /// Why <see cref="PresentIn"/> said no, naming both probed paths. Naming them is the
    /// point: the defect this replaces was a gate looking somewhere nobody populates, and a
    /// reason that lists its candidates makes that visible the first time it is wrong.
    /// </summary>
    internal static string MissingReason(string? home)
    {
        if (string.IsNullOrEmpty(home))
            return "BC artifacts not provisioned: no home directory (HOME unset and no user profile), "
                 + "so neither artifact cache could be probed.";

        return $"BC artifacts not provisioned: no version directory under '{StandardCacheDir(home)}' "
             + $"and no '{LegacyCacheDir(home)}'. Provision with "
             + "`dotnet build AlRunner.slnx -p:AllowBcArtifactDownload=true` "
             + "(see .github/workflows/test-matrix.yml).";
    }

    /// <summary>
    /// Skip the calling test — visibly — when BC artifacts are not provisioned.
    /// The caller must be a <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>;
    /// TestArtifactsGateTests.EveryTestThatCanSkipIsDeclaredSkippable enforces that.
    /// </summary>
    internal static void SkipIfMissing() => SkipIfMissingIn(HomeDir(), RunningOnCi);

    /// <summary>
    /// <see cref="SkipIfMissing"/> against an explicit environment.
    ///
    /// On CI this FAILS instead of skipping — see <see cref="CiMissingArtifactsMessage"/>
    /// for why a visible skip is not enough there.
    /// </summary>
    internal static void SkipIfMissingIn(string? home, bool runningOnCi)
    {
        if (PresentIn(home)) return;

        var reason = MissingReason(home);
        if (runningOnCi) Assert.Fail(CiMissingArtifactsMessage(reason));
        throw new SkipException(reason);
    }

    /// <summary>
    /// Why a skip is refused on CI.
    ///
    /// A visible skip fixes "reported Passed having asserted nothing" but leaves the
    /// level above it open: if the workflow moves where it provisions artifacts,
    /// <see cref="Present"/> answers false for EVERY test, all of them skip — visibly,
    /// with an accurate reason — and the leg is still GREEN. That is the same defect this
    /// file exists to kill, and the drift guards in TestArtifactsGateTests would all still
    /// pass, because nothing about the per-class gates changed.
    ///
    /// On a CI leg the artifacts are provisioned by construction (test-matrix.yml builds
    /// with AllowBcArtifactDownload=true and downloads platform/test apps before
    /// `dotnet test`). So "artifacts missing" there is never a legitimate skip; it is a
    /// provisioning defect, and the leg must go red naming both layouts it searched.
    ///
    /// Deliberately scoped to THIS gate rather than a blanket `Skipped: 0` assertion in the
    /// workflow: BcEngineFixture legitimately skips on the first pass after a cold Cecil
    /// rewrite (that is why the workflow re-runs those two classes in a second step), so a
    /// blanket assertion would fail every run for a correct and unrelated reason.
    /// </summary>
    internal static string CiMissingArtifactsMessage(string reason) =>
        "BC artifacts are missing on a CI leg, where provisioning is guaranteed by "
        + $"construction ({string.Join("/", CiEnvironmentVariables)} is set), so this is a "
        + "provisioning defect and NOT a legitimate skip — a leg where every artifact-gated "
        + "test skips would otherwise report green having run nothing. " + reason;

    /// <summary>
    /// The environment flags that mean "this is CI". GitHub Actions sets both; `CI` alone
    /// covers other providers.
    /// </summary>
    internal static readonly string[] CiEnvironmentVariables = ["GITHUB_ACTIONS", "CI"];

    internal static bool RunningOnCi => IsCiEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// True when any CI flag is set to something other than an explicit off value — a
    /// workflow that sets `CI=false` means what it says.
    /// </summary>
    internal static bool IsCiEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        foreach (var name in CiEnvironmentVariables)
        {
            var value = getEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Skip the calling test with an explicit reason — for environment prerequisites
    /// beyond the service-tier artifacts (platform apps, test toolkit, a warmed Cecil
    /// cache, a supported RID, …). The reason must name what was missing.
    /// </summary>
    internal static void SkipIf(bool condition, string reason)
    {
        if (condition) throw new SkipException(reason);
    }

    /// <summary>Skip when <paramref name="dir"/> is absent, naming it and what it holds.</summary>
    internal static void SkipIfDirectoryMissing(string dir, string what)
        => SkipIf(!Directory.Exists(dir), $"{what} not provisioned: '{dir}' does not exist.");

    /// <summary>
    /// The R2R platform apps (Base/System Application, …) the workflow downloads to
    /// <c>~/.al-runner/platform-apps</c>.
    /// </summary>
    internal static string PlatformAppsDir() => Path.Combine(HomeDir() ?? string.Empty, ".al-runner", "platform-apps");
}
