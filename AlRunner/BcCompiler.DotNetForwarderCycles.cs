// BcCompiler.DotNetForwarderCycles — a runtime-only dotnet install must not abort the compile (#5232).
//
// Without the reference packs, BC's DotNet alias binder reads the service tier's own netstandard-era
// copies of a few System assemblies. They forward some types to `mscorlib` (or to a facade that
// points back), and the runtime's facade forwards the same types back to an assembly of the same
// name and public key token — which only the tier copy matches. BC's Cecil follows
// ExportedType.Resolve() around that loop without a bound: stack overflow, exit 134. With the packs
// present they come first and bind real definitions, so the loop never closes. Which files form
// loops is measured per tier by FindForwarderLoopMembers; on tier 28.5.54151.55132 against runtime
// 8.0.30 they are Microsoft.Win32.Registry, System.Security.AccessControl,
// System.Security.Principal.Windows, System.ComponentModel.Annotations and System.Numerics.Vectors.
// see docs/limitations.md#dotnet-reference-packs
using Mono.Cecil;
using NavDotNet = Microsoft.Dynamics.Nav.CodeAnalysis.DotNet;

namespace AlRunner;

public sealed partial class BcCompiler
{
    /// <summary>
    /// The files outside <paramref name="runtimeDir"/> that sit on a type-forwarder LOOP when
    /// <paramref name="locator"/> resolves assembly references: the walk BC's
    /// <c>CecilAssemblyResolver</c> does (ask the locator for the scope's full name, read that file,
    /// look for the type there, follow the next ExportedType), with a repeat of (file, type) marking
    /// a loop and every file in the repeated stretch counted as a member.
    ///
    /// CLAIM: a file is hidden only if BC's own locator sends the walk round it. A real
    /// implementation that merely forwards one type to <c>System.Runtime</c> (the tier's
    /// System.Text.Json and System.Diagnostics.DiagnosticSource) ends the walk at a definition and is
    /// never selected. Judged from the files, not a name list, so a tier update that adds another
    /// looping shim is covered by the same rule (<c>DotNetForwarderCycleTests</c> pins the exact set
    /// on the real tier).
    ///
    /// TRAP: "has any ExportedType" is NOT the criterion; it also matches those real implementations
    /// and hiding them turned working JsonDocument/Activity aliases into AL0185 (#5232 review).
    /// </summary>
    internal static HashSet<string> FindForwarderLoopMembers(
        NavDotNet.IAssemblyLocator locator, IEnumerable<string> probingDirs, string runtimeDir)
    {
        var runtimeFull = Path.GetFullPath(runtimeDir).TrimEnd(Path.DirectorySeparatorChar);
        var modules = new Dictionary<string, ModuleDefinition?>(StringComparer.Ordinal);
        ModuleDefinition? Read(string path)
        {
            if (modules.TryGetValue(path, out var m)) return m;
            try { m = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred }); }
            catch (BadImageFormatException) { m = null; }
            return modules[path] = m;
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        var starts = probingDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal);
        foreach (var file in starts)
        {
            var module = Read(file);
            if (module == null) continue;
            foreach (var exported in module.ExportedTypes)
            {
                var current = exported;
                var chain = new List<string>();
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                while (current.Scope is AssemblyNameReference scope)
                {
                    var path = locator.GetPathToAssembly(scope.FullName);
                    if (path == null) break;
                    path = Path.GetFullPath(path);
                    var key = path + "|" + current.FullName;
                    if (seen.TryGetValue(key, out var first))
                    {
                        foreach (var member in chain.Skip(first))
                            if (!Path.GetDirectoryName(member)!.TrimEnd(Path.DirectorySeparatorChar)
                                    .Equals(runtimeFull, StringComparison.Ordinal))
                                members.Add(member);
                        break;
                    }
                    seen[key] = chain.Count;
                    chain.Add(path);
                    var target = Read(path);
                    if (target == null || target.GetType(current.FullName) != null) break;
                    var next = target.ExportedTypes.FirstOrDefault(e => e.FullName == current.FullName);
                    if (next == null) break;
                    current = next;
                }
            }
        }
        return members;
    }

    /// <summary>
    /// The locator BC's binder is given, and the factory built on it. Diagnoses the packs from the
    /// SAME (<paramref name="runtimeDir"/>, <paramref name="envDotnetRoot"/>) pair the probing path was
    /// built from, so the guard and the diagnosis cannot read different installs. When the packs are
    /// missing (<see cref="DotNetRefPackGap.AliasesCannotBind"/>), the only state in which the loop can
    /// close, the files on a loop are hidden; otherwise BC's own locator is used untouched.
    /// Extracted from <c>GetOrCreateDotNetFactory</c> so a test drives the real call chain with a
    /// forced install shape (#5232).
    /// </summary>
    internal static NavDotNet.IDotNetResolverFactory CreateDotNetResolverFactory(
        IReadOnlyList<string> probingPaths, string runtimeDir, string? envDotnetRoot,
        out NavDotNet.IAssemblyLocator locator)
    {
        NavDotNet.IAssemblyLocator inner = new NavDotNet.AssemblyLocator(probingPaths);
        locator = GuardAgainstForwarderLoops(
            inner, probingPaths, runtimeDir, DiagnoseDotNetRefPacks(runtimeDir, envDotnetRoot));
        return new NavDotNet.DotNetResolverFactory(locator);
    }

    internal static NavDotNet.IAssemblyLocator GuardAgainstForwarderLoops(
        NavDotNet.IAssemblyLocator locator, IEnumerable<string> probingPaths, string runtimeDir,
        DotNetRefPackGap? gap)
        => gap is { AliasesCannotBind: true }
            ? new ForwarderShimHidingAssemblyLocator(locator,
                FindForwarderLoopMembers(locator, probingPaths, runtimeDir))
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
