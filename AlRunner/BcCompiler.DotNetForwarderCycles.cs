// BcCompiler.DotNetForwarderCycles — a runtime-only dotnet install must not abort the compile (#5232).
//
// Without the reference packs, BC's DotNet alias binder reads the service tier's own netstandard-era
// copies of a few System assemblies (Microsoft.Win32.Registry, System.Security.AccessControl,
// System.Security.Principal.Windows, System.ComponentModel.Annotations, System.Numerics.Vectors).
// Those copies forward some types to `mscorlib`, and the runtime's `mscorlib` forwards the same types
// back to an assembly of the same name and public key token — which only the tier copy matches. BC's
// Cecil follows ExportedType.Resolve() around that loop without a bound: stack overflow, exit 134.
// With the packs present they come first and bind real definitions, so the loop never closes.
// see docs/limitations.md#dotnet-reference-packs
using Mono.Cecil;
using NavDotNet = Microsoft.Dynamics.Nav.CodeAnalysis.DotNet;

namespace AlRunner;

public sealed partial class BcCompiler
{
    /// <summary>
    /// The files under <paramref name="probingDirs"/> that must not be handed to BC's binder when the
    /// reference packs are missing: an assembly that has a same-named counterpart in
    /// <paramref name="runtimeDir"/> AND forwards types onward (a non-empty ExportedType table).
    /// Judged from the files, not from a name list, so a service-tier update that adds another such
    /// shim is covered by the same rule.
    ///
    /// TRAP: not simply "same name as a runtime assembly" — most of those overlaps (System.Text.Json,
    /// System.Collections.Immutable, ...) are real implementations the binder may legitimately read
    /// from the tier, and hiding them would change which definition an alias binds to.
    /// </summary>
    internal static HashSet<string> FindForwarderShimsShadowingTheRuntime(
        IEnumerable<string> probingDirs, string runtimeDir)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var runtimeFull = Path.GetFullPath(runtimeDir).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var dir in probingDirs)
        {
            if (!Directory.Exists(dir)) continue;
            if (Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(runtimeFull, StringComparison.Ordinal))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
            {
                if (!File.Exists(Path.Combine(runtimeDir, Path.GetFileName(file)))) continue;
                try
                {
                    using var module = ModuleDefinition.ReadModule(file,
                        new ReaderParameters { ReadingMode = ReadingMode.Deferred });
                    if (module.ExportedTypes.Count > 0)
                        hidden.Add(Path.GetFullPath(file));
                }
                catch (BadImageFormatException) { /* not a managed assembly: nothing to follow */ }
            }
        }
        return hidden;
    }

    /// <summary>
    /// The locator BC's binder is given: <paramref name="locator"/> as it is, unless the packs are
    /// missing (<see cref="DotNetRefPackGap.AliasesCannotBind"/>) — the only state in which the loop
    /// can close — in which case the shims it runs through are hidden.
    /// </summary>
    internal static NavDotNet.IAssemblyLocator GuardAgainstForwarderLoops(
        NavDotNet.IAssemblyLocator locator, IEnumerable<string> probingPaths, string runtimeDir,
        DotNetRefPackGap? gap)
        => gap is { AliasesCannotBind: true }
            ? new ForwarderShimHidingAssemblyLocator(locator,
                FindForwarderShimsShadowingTheRuntime(probingPaths, runtimeDir))
            : locator;

    /// <summary>
    /// Answers "not found" for the files in <c>hidden</c> and defers to the wrapped locator for
    /// everything else. BC's Cecil resolver treats a null path as an unresolved assembly, which
    /// ends the forwarder chain instead of looping it.
    /// </summary>
    internal sealed class ForwarderShimHidingAssemblyLocator : NavDotNet.IAssemblyLocator
    {
        private readonly NavDotNet.IAssemblyLocator _inner;
        private readonly HashSet<string> _hidden;

        internal ForwarderShimHidingAssemblyLocator(NavDotNet.IAssemblyLocator inner, HashSet<string> hidden)
        {
            _inner = inner;
            _hidden = hidden;
        }

        public IEnumerable<string> ProbingPaths => _inner.ProbingPaths;

        public string? GetPathToAssembly(string assemblyName)
        {
            var path = _inner.GetPathToAssembly(assemblyName);
            return path != null && _hidden.Contains(Path.GetFullPath(path)) ? null : path;
        }
    }
}
