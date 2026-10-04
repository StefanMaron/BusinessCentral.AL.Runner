// A runner child that is MEANT to abort (exit 134/139) must not inherit the CI job's crash-dump
// switch: .github/workflows/bc-tests.yml exports DOTNET_DbgEnableMiniDump for the whole C# job and
// uploads crash-dumps/ as the artifact real crashes are diagnosed from, so an expected abort would
// write a heap dump into it (#5283). The value goes into the child's OWN ProcessStartInfo.Environment,
// never the host's (HostEnvironmentIsolationGuardTests, docs/test-isolation.md).
using System.Diagnostics;

namespace AlRunner.Tests;

/// <summary>
/// Marks a static method that builds the <see cref="ProcessStartInfo"/> of a runner child expected to
/// abort. <see cref="ExpectedRunnerAbortGuardTests"/> calls every marked method and refuses a start info
/// that leaves the crash-dump switch on. Mark the BUILDER, not the test, so the guard can read the start
/// info without running the abort.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ExpectedRunnerAbortAttribute : Attribute
{
}

internal static class CrashDump
{
    internal const string SwitchVariable = "DOTNET_DbgEnableMiniDump";

    /// <summary>Turns the crash-dump switch off for this child only; returns <paramref name="psi"/>.</summary>
    internal static ProcessStartInfo SwitchOff(this ProcessStartInfo psi)
    {
        psi.Environment[SwitchVariable] = "0";
        return psi;
    }
}
