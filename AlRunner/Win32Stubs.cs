// Win32Stubs — installs a P/Invoke resolver redirecting Win32 imports to a Linux .so
// built from bc-linux's win32_stubs.c.
using System.Reflection;
using System.Runtime.InteropServices;

namespace AlRunner;

internal static class Win32Stubs
{
    private static IntPtr _handle = IntPtr.Zero;
    private static bool _registered;

    private static readonly HashSet<string> _libs = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32", "kernel32.dll", "user32", "user32.dll", "wintrust", "wintrust.dll",
        "nclcsrts", "nclcsrts.dll", "dhcpcsvc", "dhcpcsvc.dll", "ntdsapi", "ntdsapi.dll",
        "advapi32", "advapi32.dll", "secur32", "secur32.dll", "iphlpapi", "iphlpapi.dll",
        "wtsapi32", "wtsapi32.dll", "userenv", "userenv.dll", "netapi32", "netapi32.dll",
        "psapi", "psapi.dll", "ws2_32", "ws2_32.dll", "shlwapi", "shlwapi.dll",
    };

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            TryRegister(asm);
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryRegister(e.LoadedAssembly);
    }

    private static void TryRegister(Assembly asm)
    {
        var n = asm.GetName().Name ?? "";
        if (!n.Contains("Nav.")) return;
        try { NativeLibrary.SetDllImportResolver(asm, Resolver); }
        catch (InvalidOperationException) { /* already registered */ }
    }

    private static IntPtr Resolver(string library, Assembly asm, DllImportSearchPath? sp)
    {
        if (!_libs.Contains(library)) return IntPtr.Zero;
        try { return GetOrBuild(); }
        catch (Exception ex) { Console.WriteLine($"[Win32Stubs] build failed for {library}: {ex.Message}"); return IntPtr.Zero; }
    }

    private static IntPtr GetOrBuild()
    {
        if (_handle != IntPtr.Zero) return _handle;
        var src = LocateStubSource();
        var dir = Path.Combine(Path.GetTempPath(), "alrunner-v2-win32-stubs");
        Directory.CreateDirectory(dir);
        var cFile = Path.Combine(dir, "win32_stubs.c");
        var soFile = Path.Combine(dir, "libwin32_stubs.so");
        File.Copy(src, cFile, overwrite: true);
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cc",
            $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
        { RedirectStandardError = true, UseShellExecute = false })!;
        proc.WaitForExit(10000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"cc failed: {proc.StandardError.ReadToEnd()}");
        _handle = NativeLibrary.Load(soFile);
        return _handle;
    }

    /// <summary>
    /// Locate <c>win32_stubs.c</c>. Three resolution paths, in order:
    ///   1. Beside the binary (<c>Win32Stubs/win32_stubs.c</c> next to
    ///      <c>al-runner.dll</c>). This is the layout produced by
    ///      <c>dotnet build</c> and <c>dotnet pack</c> — the .c file is copied
    ///      to the output via <c>AlRunner.csproj</c>'s <c>&lt;Content&gt;</c> item.
    ///   2. Walk up from <see cref="AppContext.BaseDirectory"/> looking for an
    ///      <c>AlRunner/Win32Stubs/</c> sibling — covers the dev workflow when
    ///      running from source via <c>dotnet run</c> without publish.
    ///   3. Environment override <c>AL_RUNNER_WIN32_STUBS_C</c> — full path.
    /// Throws <see cref="FileNotFoundException"/> with all three paths in the
    /// message if none resolves, so the diagnosis is trivial.
    /// </summary>
    private static string LocateStubSource()
    {
        var envOverride = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_C");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            return envOverride;

        var beside = Path.Combine(AppContext.BaseDirectory, "Win32Stubs", "win32_stubs.c");
        if (File.Exists(beside)) return beside;

        // Walk up looking for AlRunner/Win32Stubs/win32_stubs.c (dev/source layout).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AlRunner", "Win32Stubs", "win32_stubs.c");
            if (File.Exists(candidate)) return candidate;
            var inside = Path.Combine(dir.FullName, "Win32Stubs", "win32_stubs.c");
            if (File.Exists(inside) && dir.Name == "AlRunner") return inside;
        }

        throw new FileNotFoundException(
            "Win32 stubs source (win32_stubs.c) not found. Searched:\n"
            + $"  - {beside}\n"
            + $"  - $AL_RUNNER_WIN32_STUBS_C (={Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_C") ?? "<unset>"})\n"
            + "  - parent dirs up to 10 levels above the binary, looking for AlRunner/Win32Stubs/win32_stubs.c\n"
            + "Set AL_RUNNER_WIN32_STUBS_C to the absolute path of win32_stubs.c, or rebuild "
            + "the al-runner tool so the file ships in the output directory.");
    }
}
