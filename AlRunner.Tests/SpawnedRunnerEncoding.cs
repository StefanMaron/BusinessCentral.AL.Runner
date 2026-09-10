// SpawnedRunnerEncoding — the test host decodes a spawned runner's stdout as UTF-8, which is
// what the runner writes (#3738, Program.cs's console-encoding block).
//
// 158 classes in this assembly spawn the runner with RedirectStandardOutput and read it as
// text. None sets ProcessStartInfo.StandardOutputEncoding, and it does not need to: .NET falls
// back to the PARENT's Console.OutputEncoding when that property is null. Measured on this box
// (Windows 11, OEM cp850), with a child that writes "— em dash, → arrow" as UTF-8:
//
//   host default, no StandardOutputEncoding        dash-ok=False arrow-ok=False
//   host=UTF8,    no StandardOutputEncoding        dash-ok=True  arrow-ok=True
//   host=UTF8,    StandardOutputEncoding=UTF8      dash-ok=True  arrow-ok=True
//
// So one setting here covers every spawn site, and no per-call plumbing is needed. Without it,
// on Windows only, every assertion comparing captured output against a non-ASCII character the
// runner writes reads mojibake and fails — the reported symptom in SuiteEnumerationTests, and
// the same latent failure in BackupReaderFailureReportingTests, BundleFailureStageTests,
// CleanRunStartupVerbosityTests, CollateralFailureReportingTests and ExecFailureTests. CI is
// Linux, where both ends already default to UTF-8, which is why none of them was ever red there.
//
// A [ModuleInitializer], like BcEngineBootstrap's, because it must run before any test spawns
// anything; neither initializer depends on the other, so their order does not matter. Failure
// is tolerated rather than fatal — a test host whose stdout handle refuses reconfiguration
// should still run the suite — and recorded in Problem, which the encoding facts print when
// they fail, so a red run on such a host says why rather than looking like the runner's fault.
//
// This does change the test host's own console output code page on Windows, which is process-
// global. It does NOT make the encoding facts circular: with the production block removed and
// this initializer left installed, both facts in ConsoleOutputEncodingTests still FAIL
// (measured for the PR #3795 review), because the child sets its own encoding rather than
// inheriting the parent's code page.

using System.Runtime.CompilerServices;
using System.Text;

namespace AlRunner.Tests;

internal static class SpawnedRunnerEncoding
{
    /// <summary>Null when the encoding was set; the failure's message otherwise.</summary>
    internal static string? Problem { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentException)
        {
            Problem = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
