// Standing BC's compiler assemblies up in a plain console process: resolve them out of the
// service-tier directory, and satisfy their Win32 P/Invokes from the runner's stub library.
//
// The runner does the same two things through DependencyLoader and Win32Stubs. This tool
// deliberately does not reference the runner (no build-order dependency on the thing it
// checks), so it repeats the small part it needs and nothing else.

using System.Reflection;
using System.Runtime.InteropServices;

namespace AlRunner.Tools.MetadataGroundTruth;

internal static class BcHost
{
    private static readonly HashSet<string> Win32 = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32", "kernel32.dll", "user32", "user32.dll", "wintrust", "wintrust.dll",
        "nclcsrts", "nclcsrts.dll", "advapi32", "advapi32.dll", "secur32", "secur32.dll",
        "iphlpapi", "iphlpapi.dll", "psapi", "psapi.dll", "ws2_32", "ws2_32.dll",
        "shlwapi", "shlwapi.dll", "netapi32", "netapi32.dll", "userenv", "userenv.dll",
        "wtsapi32", "wtsapi32.dll", "dhcpcsvc", "dhcpcsvc.dll", "ntdsapi", "ntdsapi.dll",
    };

    public static void Install(string serviceTierDir)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var file = new AssemblyName(e.Name).Name + ".dll";
            var path = Path.Combine(serviceTierDir, file);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        var stub = FindStubs();
        if (stub is null)
        {
            // Not fatal by itself — say so rather than let the first P/Invoke fail with a
            // DllNotFoundException naming kernel32, which reads as a broken machine.
            Console.Error.WriteLine(
                "[warn] Win32 stub library not found; BC assemblies that P/Invoke kernel32 and " +
                "friends will fail. Build it with AlRunner/Win32Stubs/build-prebuilt.sh, or set " +
                "AL_RUNNER_WIN32_STUBS to its path.");
            return;
        }

        var handle = NativeLibrary.Load(stub);
        DllImportResolver resolver = (name, _, _) => Win32.Contains(name) ? handle : IntPtr.Zero;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        { try { NativeLibrary.SetDllImportResolver(e.LoadedAssembly, resolver); } catch { } };
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            try { NativeLibrary.SetDllImportResolver(a, resolver); } catch { }
    }

    private static string? FindStubs()
    {
        var explicitPath = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        // Walk up from this assembly to the repository root — this tool lives in tools/, so
        // the runner's prebuilt stub is a known relative distance away.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AlRunner", "Win32Stubs", "libwin32_stubs.linux-x64.so");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
