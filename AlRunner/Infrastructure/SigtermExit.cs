using System.Runtime.InteropServices;

namespace AlRunner.Infrastructure;

/// <summary>
/// Makes SIGTERM run <see cref="AppDomain.ProcessExit"/> handlers on every runtime the runner targets.
///
/// On .NET 8 a SIGTERM raises ProcessExit and the process exits 143. On .NET 10 it does not: the
/// process dies by the signal with no handler run (measured with a bare console app, net8.0 against
/// net10.0, one SIGTERM each: the handler's file is written on net8.0 and absent on net10.0, exit 143
/// and -15). The runner keeps its cleanup in ProcessExit handlers — the scratch directories
/// (<c>ScratchDirs</c>), the phase log's process row, the shared package-dedup claims, the backup
/// reader — so BC 29, which runs on .NET 10, leaked all of it whenever a CI timeout or a user
/// sent SIGTERM. Registering the signal explicitly and exiting through <see cref="Environment.Exit"/>
/// restores the net8 behaviour, same exit code included, and changes nothing there.
/// SIGINT, SIGHUP and SIGQUIT behave the same on both runtimes (the process dies by the signal), so
/// they are left alone.
/// </summary>
internal static class SigtermExit
{
    private const int ExitCodeForSigterm = 128 + 15;

    private static PosixSignalRegistration? _registration;

    public static void Install()
    {
        if (OperatingSystem.IsWindows() || _registration != null) return;
        _registration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Environment.Exit(ExitCodeForSigterm);
        });
    }
}
