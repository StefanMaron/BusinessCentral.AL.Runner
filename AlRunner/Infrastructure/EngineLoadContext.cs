using System.Reflection;
using System.Runtime.Loader;

namespace AlRunner.Infrastructure;

/// <summary>
/// The single ALC every engine-internal Resolving/probing hook must use instead of the
/// literal <see cref="AssemblyLoadContext.Default"/>.
///
/// <para><b>Why this exists.</b> Every one of these hooks predates the per-BC-minor
/// engine-variant packaging (#2024 item 3). They were written when this assembly (the
/// current build's al-runner/AlRunner.Engine.dll) WAS the process's root app, so "the
/// ALC that owns me" and "Default" were the same object and the distinction never
/// mattered. Under the launcher/engine split, the launcher loads this assembly into a
/// dedicated, non-Default <see cref="AssemblyLoadContext"/> (see the launcher's
/// EngineHost) — <see cref="AssemblyLoadContext.Default"/>'s trusted-platform-assemblies
/// list is frozen from the LAUNCHER's own (BC-free) deps.json before any of this code
/// runs, and does not know about this engine variant's private-versioned dependency
/// closure (System.Text.Json 10, DiagnosticSource 10, the Azure/Identity closure, …).
/// Loading — or even attempting to load — a different-versioned copy of a
/// TPA-registered simple name INTO Default throws FileLoadException 0x80131621;
/// confirmed empirically (see the PR description for #2024 item 3's canary).
///
/// <para><b>The fix.</b> <see cref="AssemblyLoadContext.GetLoadContext"/> on THIS
/// assembly returns whichever ALC actually loaded it — the launcher's dedicated engine
/// ALC when hosted that way, or the process's real Default ALC in the original
/// single-binary shape (a plain `dotnet exec al-runner.dll`, a unit test host, or any
/// other embedder that hasn't adopted the split). Both shapes keep working: this class
/// makes the choice at each call site instead of hardcoding it.</para>
/// </summary>
public static class EngineLoadContext
{
    /// <summary>
    /// The ALC that owns this assembly RIGHT NOW. Not cached: while this assembly is
    /// only ever loaded into one ALC per process in practice, resolving it live is
    /// cheap and removes any ordering requirement on when the first caller runs.
    /// </summary>
    public static AssemblyLoadContext Current =>
        AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()) ?? AssemblyLoadContext.Default;

    /// <summary>
    /// Loads an in-memory-compiled AL test/dependency assembly into <see cref="Current"/>
    /// instead of <see cref="AssemblyLoadContext.Default"/>. The static
    /// <c>Assembly.Load(byte[])</c> overload is HARDWIRED to Default regardless of the
    /// calling assembly's own ALC (documented .NET behavior — see
    /// <c>AssemblyLoadContext.LoadFromStream</c> vs <c>Assembly.Load(byte[])</c>), which
    /// is exactly wrong once the engine itself runs from a dedicated, non-Default ALC:
    /// an AL test DLL loaded via Default would resolve its OWN, SEPARATE copy of
    /// Microsoft.Dynamics.Nav.Ncl.dll et al — a different type identity than the one the
    /// engine (and BcRuntime's reflection-based dispatch) is actually using, producing
    /// InvalidCastException / MissingMethodException chaos rather than a clean failure.
    /// <see cref="AssemblyLoadContext.LoadFromStream"/> loads into the SPECIFIC ALC
    /// instance it's called on, so this keeps every BC-touching assembly — engine and
    /// AL output alike — sharing one ALC and one set of BC type identities.
    /// </summary>
    public static Assembly LoadFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return Current.LoadFromStream(ms);
    }
}
