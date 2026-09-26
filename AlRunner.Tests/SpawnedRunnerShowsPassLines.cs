// SpawnedRunnerShowsPassLines — #4563.
//
// A default run hides PASS lines; AL_RUNNER_SHOW_PASS=1 turns them back on. About 65 test
// classes spawn the runner and assert a `PASS  CodeunitN.Method` line as their evidence that a
// named test ran, so this assembly sets the variable once for every process it starts. A class
// that pins the DEFAULT output removes it from its own ProcessStartInfo — RunSummaryOutputTests
// and CleanRunStartupVerbosityTests do.
using System.Runtime.CompilerServices;

namespace AlRunner.Tests;

internal static class SpawnedRunnerShowsPassLines
{
    internal const string Variable = "AL_RUNNER_SHOW_PASS";

    [ModuleInitializer]
    internal static void Enable() => Environment.SetEnvironmentVariable(Variable, "1");
}
