// Win32Stubs — installs a P/Invoke resolver redirecting Win32 imports to a Linux .so
// built from bc-linux's win32_stubs.c.
//
// Loud-failure contract (.claude/rules/loud-failures.md): if the shim can't be built
// or loaded, the resolver used to swallow the exception and return IntPtr.Zero. That
// let .NET's default native-library probing take over, which produced a confusing
// `DllNotFoundException: kernel32.dll.so not found` hundreds of frames away from the
// real cause (issue #1651) — worse, that diagnostic line was itself filtered out by
// Log's [Component] tag suppression at default verbosity (Log.cs), so the operator
// saw nothing but the deep BC stack trace. The resolver now throws directly with the
// real cause and a remediation step; see Win32StubsLoudFailureTests.cs.
using System.Reflection;
using System.Runtime.InteropServices;

namespace AlRunner;

internal static class Win32Stubs
{
    private static IntPtr _handle = IntPtr.Zero;
    private static bool _registered;

    /// <summary>C compilers tried, in order, to build the shim from source. "cc" is
    /// the POSIX-mandated name but is absent on some minimal distros (e.g. a bare
    /// WSL Ubuntu with no build-essential, see #1651) that still ship gcc or clang
    /// under their own name.</summary>
    internal static readonly string[] CandidateCompilers = { "cc", "gcc", "clang" };

    private static readonly HashSet<string> _libs = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32", "kernel32.dll", "user32", "user32.dll", "wintrust", "wintrust.dll",
        "nclcsrts", "nclcsrts.dll", "dhcpcsvc", "dhcpcsvc.dll", "ntdsapi", "ntdsapi.dll",
        "advapi32", "advapi32.dll", "secur32", "secur32.dll", "iphlpapi", "iphlpapi.dll",
        "wtsapi32", "wtsapi32.dll", "userenv", "userenv.dll", "netapi32", "netapi32.dll",
        "psapi", "psapi.dll", "ws2_32", "ws2_32.dll", "shlwapi", "shlwapi.dll",
    };

    /// <summary>
    /// Issue #1673: kernel32/user32/etc. are the *real* Win32 libraries on Windows — this
    /// shim exists purely to fake them on Linux (see the type-level comment). Intercepting
    /// them on Windows too used to be harmless (the resolver's failure was swallowed and the
    /// default loader took over), but #1669 made the resolver throw directly, so on Windows
    /// that interception now breaks every AL codepath that touches one of these libraries
    /// (e.g. any install trigger reaching WindowsLanguageHelper via a bare TextConstant, see
    /// #1651) even though Windows never needed the shim in the first place.
    /// </summary>
    public static void Register() => Register(OperatingSystem.IsWindows());

    /// <summary>internal (not private) purely so <c>AlRunner.Tests</c> can exercise the
    /// Windows-vs-Linux branch deterministically without depending on the OS the test happens
    /// to run on — see Win32StubsLoudFailureTests.cs.</summary>
    internal static void Register(bool isWindows)
    {
        // Checked before touching _registered so a Register(isWindows: true) no-op call never
        // blocks a later, real Register() call in the same process from registering normally.
        if (isWindows) return;
        if (_registered) return;
        _registered = true;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            TryRegister(asm);
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryRegister(e.LoadedAssembly);
    }

    /// <summary>Test-only: observes whether <see cref="Register(bool)"/> has completed
    /// registration in this process. Never read from production code paths.</summary>
    internal static bool IsRegisteredForTests => _registered;

    /// <summary>Test-only: forget the cached handle/registration so a unit test can
    /// exercise <see cref="GetOrBuild"/> or <see cref="Register(bool)"/> from a clean slate.
    /// Never called from production code paths.</summary>
    internal static void ResetForTests()
    {
        _handle = IntPtr.Zero;
        _registered = false;
    }

    /// <summary>Test-only seam: when set, <see cref="GetOrBuild"/> probes for the
    /// shipped prebuilt stub under this directory instead of <see
    /// cref="AppContext.BaseDirectory"/>, so a unit test can drop a fixture .so
    /// somewhere temporary without touching the real install layout. Null in
    /// production. Never read from production code paths other than
    /// <see cref="GetOrBuild"/>'s own base-directory resolution below.</summary>
    internal static string? BaseDirectoryForTests;

    /// <summary>
    /// #1809: test-only seam so <see cref="IsOnPath"/> can be exercised with an empty
    /// search path ("no compiler reachable") WITHOUT
    /// <c>Environment.SetEnvironmentVariable("PATH", "")</c> — that call mutates
    /// process-wide state, and this class's own resolver runs on the
    /// AppDomain.AssemblyLoad event from arbitrary threads, so a test-owned PATH wipe
    /// races every other test in the assembly that spawns a "dotnet" child process
    /// via PATH lookup. Parallelizing AlRunner.Tests's collections (#1809) turned that
    /// from a latent cross-test hazard into a real one: up to 4 collections now spawn
    /// concurrently, any of which can land inside a PATH-restore window and get
    /// Win32Exception ("An error occurred trying to start process 'dotnet'"). Null in
    /// production and in every test that doesn't need this — only read here, never
    /// from production code paths.
    /// </summary>
    internal static string? PathEnvironmentForTests;

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
        // Deliberately NOT caught-and-defaulted here (loud-failures.md): a swallowed
        // exception means .NET's own DllImportResolver fallback takes over and the
        // real cause never surfaces. Let it propagate — it comes back to the P/Invoke
        // call site as a TypeInitializationException / DllNotFoundException whose
        // InnerException/Message is this exact, actionable text.
        return GetOrBuild(library);
    }

    /// <summary>internal (not private) purely so <c>AlRunner.Tests</c> can exercise the
    /// AL_RUNNER_WIN32_STUBS_SO override / no-compiler paths directly, via
    /// InternalsVisibleTo — see Win32StubsLoudFailureTests.cs.</summary>
    internal static IntPtr GetOrBuild(string library)
    {
        if (_handle != IntPtr.Zero) return _handle;

        var soOverride = Environment.GetEnvironmentVariable("AL_RUNNER_WIN32_STUBS_SO");
        if (!string.IsNullOrEmpty(soOverride))
        {
            if (!File.Exists(soOverride))
                throw new InvalidOperationException(
                    $"Win32Stubs: AL_RUNNER_WIN32_STUBS_SO is set to '{soOverride}' but that file does not exist. "
                    + "Unset it to build the shim from source, or point it at a valid prebuilt libwin32_stubs.so.");
            _handle = NativeLibrary.Load(soOverride);
            return _handle;
        }

        // #1672: try the shipped prebuilt stub (beside the binary, one per RID —
        // see AlRunner.csproj's BuildWin32PrebuiltStubs target) before falling back
        // to compiling from source. This is what lets a fresh Linux install with no
        // C toolchain resolve Win32 imports out of the box; the compile-from-source
        // path below still exists for RIDs the release pipeline didn't prebuild for
        // (or a dev tree running straight from `dotnet run`).
        var prebuilt = LocatePrebuiltSo(BaseDirectoryForTests ?? AppContext.BaseDirectory, File.Exists);
        if (prebuilt != null)
        {
            _handle = NativeLibrary.Load(prebuilt);
            return _handle;
        }

        var compiler = FindCompiler(cmd => IsOnPath(cmd));
        if (compiler == null)
            throw new InvalidOperationException(BuildNoCompilerMessage(library));

        var src = LocateStubSource();
        var dir = Path.Combine(Path.GetTempPath(), "alrunner-v2-win32-stubs");
        Directory.CreateDirectory(dir);
        var cFile = Path.Combine(dir, "win32_stubs.c");
        var soFile = Path.Combine(dir, "libwin32_stubs.so");
        File.Copy(src, cFile, overwrite: true);
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(compiler,
            $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
        { RedirectStandardError = true, UseShellExecute = false })!;
        proc.WaitForExit(10000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Win32Stubs: '{compiler}' failed to build the Linux Win32 P/Invoke shim needed to resolve "
                + $"'{library}' (required by {src}). Compiler output:\n{proc.StandardError.ReadToEnd()}");
        _handle = NativeLibrary.Load(soFile);
        return _handle;
    }

    /// <summary>The filename of the shipped prebuilt stub for the current process's
    /// RID, or null if this architecture has no shipped prebuilt (falls back to
    /// compile-from-source in that case). Named after the standard RID convention
    /// (<c>linux-x64</c>, <c>linux-arm64</c>) even though the file lives beside the
    /// binary rather than under a NuGet <c>runtimes/&lt;rid&gt;/native/</c> folder —
    /// PackAsTool nupkgs only extract <c>tools/&lt;tfm&gt;/any/</c>, so the RID lives
    /// in the filename instead of the directory structure. Linux-only: this whole
    /// resolver exists to redirect Win32 P/Invokes to a Linux .so (see the file
    /// header), so other OSes never reach here in practice, but the explicit guard
    /// keeps the contract obvious.</summary>
    internal static string? PrebuiltStubFileName()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "libwin32_stubs.linux-x64.so",
            Architecture.Arm64 => "libwin32_stubs.linux-arm64.so",
            _ => null,
        };
    }

    /// <summary>Pure/injectable lookup for the shipped prebuilt stub beside the
    /// binary: <c>&lt;baseDirectory&gt;/Win32Stubs/libwin32_stubs.&lt;rid&gt;.so</c>.
    /// Returns null (never throws) when there is no prebuilt for this RID or the
    /// file isn't there — both are legitimate "fall through to compile-from-source"
    /// cases, not errors.</summary>
    internal static string? LocatePrebuiltSo(string baseDirectory, Func<string, bool> exists)
    {
        var name = PrebuiltStubFileName();
        if (name is null) return null;
        var candidate = Path.Combine(baseDirectory, "Win32Stubs", name);
        return exists(candidate) ? candidate : null;
    }

    /// <summary>Returns the first name in <see cref="CandidateCompilers"/> for which
    /// <paramref name="exists"/> is true, or null if none are available. Pure/injectable
    /// so it can be unit-tested without touching the real PATH.</summary>
    internal static string? FindCompiler(Func<string, bool> exists)
    {
        foreach (var c in CandidateCompilers)
            if (exists(c))
                return c;
        return null;
    }

    private static bool IsOnPath(string command)
    {
        var pathVar = PathEnvironmentForTests ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir, command))) return true; }
            catch { /* malformed PATH entry — skip */ }
        }
        return false;
    }

    /// <summary>The loud, actionable message when no C compiler is available at all —
    /// pure/testable so its content (naming the library, the compilers tried, and both
    /// remediation options) is asserted directly rather than by scraping a live run.</summary>
    internal static string BuildNoCompilerMessage(string library) =>
        $"Win32Stubs: cannot resolve the Win32 import '{library}' — building the Linux "
        + $"P/Invoke shim requires a C compiler, and none of [{string.Join(", ", CandidateCompilers)}] "
        + "is on PATH. This blocks any AL code that reaches a Windows-only Win32 API through BC's "
        + "runtime (e.g. any install trigger that touches a TextConstant — see issue #1651). Fix by "
        + "either: (1) installing a C compiler (e.g. `apt install build-essential`) so it's on PATH, or "
        + "(2) building AlRunner/Win32Stubs/win32_stubs.c into a shared library yourself and pointing "
        + "AL_RUNNER_WIN32_STUBS_SO at it.";

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
