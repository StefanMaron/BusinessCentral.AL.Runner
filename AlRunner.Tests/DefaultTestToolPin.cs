// DefaultTestToolPin — #4905. A runner loads Microsoft's Test Runner app by default whenever its
// package caches hold it (#4816), and whether they do depends on the box: a developer who has run
// `al-runner provision` has <artifacts>/<ver>/test-apps, a CI leg does not have it while this
// suite runs. So every runner this assembly spawns inherits AL_RUNNER_DEFAULT_TEST_TOOL=off from
// the test host, and a test that wants the Test Runner says so through one of the two helpers
// below. DefaultTestToolPinTests holds the pin and refuses a spawn site that goes around it.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace AlRunner.Tests;

internal static class DefaultTestToolPin
{
    // A literal, not ProgramSupport.DefaultTestToolEnvVar: a module initializer must not touch an
    // AlRunner type before BcEngineBootstrap has rewritten Ncl. DefaultTestToolPinTests checks
    // that the two spellings agree.
    internal const string EnvVar = "AL_RUNNER_DEFAULT_TEST_TOOL";

    [ModuleInitializer]
    internal static void Pin() => Environment.SetEnvironmentVariable(EnvVar, "off");

    /// <summary>
    /// Loads the Test Runner app by default in <paramref name="psi"/>'s run, from a cache the run
    /// names itself, so the answer does not depend on the box. Fails when <paramref name="testAppsDir"/>
    /// does not hold the app, or <paramref name="psi"/>'s arguments do not pass it as a package cache.
    /// </summary>
    internal static void LoadFrom(ProcessStartInfo psi, string testAppsDir)
    {
        Assert.True(File.Exists(Path.Combine(testAppsDir, "Microsoft_Test Runner.app")),
            $"'{testAppsDir}' holds no Microsoft_Test Runner.app, so asking for the default load proves nothing.");
        var args = psi.ArgumentList.Count > 0 ? string.Join(' ', psi.ArgumentList) : psi.Arguments;
        Assert.True(args.Contains(testAppsDir, StringComparison.Ordinal),
            $"the run does not name '{testAppsDir}' as a package cache, so the load would come from whatever the box holds. Arguments: {args}");
        psi.Environment[EnvVar] = "on";
    }

    /// <summary>
    /// The user's default configuration: no pin at all. The outcome depends on what the box has
    /// provisioned, so the caller must first establish that the runner-owned caches hold the app.
    /// </summary>
    internal static void Unpin(ProcessStartInfo psi) => psi.Environment.Remove(EnvVar);
}
