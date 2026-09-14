// BcCompiler.DeclaredReferences — an app compiles against what it declares, plus what those
// declarations propagate; an undeclared transitive dependency is AL0185 (#4096; BC's rule is
// CodeAnalysis ReferenceManager.ResolveDirectReferences). Narrow the SPEC list only: the loader
// must keep serving every module, because BC builds ReferenceModules through loader.GetDependencies.
using System.Collections.Concurrent;
using AlRunner.Infrastructure;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner;

public sealed partial class BcCompiler
{
    // Every app.json identity read in this process, by AppId. Filled by
    // InProcessAppPackager.ReadIdentity, which every source-app compile path goes through.
    private static readonly ConcurrentDictionary<Guid, BundleIdentity> _declaredReferences = new();

    internal static void RecordDeclaredReferences(BundleIdentity identity)
    {
        if (identity.AppId == Guid.Empty) return;
        _declaredReferences[identity.AppId] = identity;
    }

    /// <summary>Forget every recorded app.json. A --server request calls this first, so a
    /// declaration read for an earlier request's workspace cannot answer for this one.</summary>
    internal static void ResetDeclaredReferences() => _declaredReferences.Clear();

    /// <summary>
    /// The references <paramref name="appId"/> may see under the declarations recorded now, in
    /// a stable form; null when its app.json was not recorded (nothing is narrowed then).
    /// </summary>
    internal static string? DeclaredVisibilitySignature(Guid appId)
    {
        if (!_declaredReferences.TryGetValue(appId, out var self)) return null;
        return string.Join(";", AllowedReferences(self)
            .Select(d => $"{d.AppId:N}|{d.Publisher.ToLowerInvariant()}|{d.Name.ToLowerInvariant()}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal));
    }

    private static List<DependencyRef> AllowedReferences(BundleIdentity self)
    {
        var allowed = new List<DependencyRef>(self.Dependencies);
        foreach (var dep in self.Dependencies)
        {
            var declared = FindRecordedIdentity(dep);
            if (declared is { PropagateDependencies: true })
                allowed.AddRange(declared.Dependencies);
        }
        return allowed;
    }

    /// <summary>
    /// Keeps the specs <paramref name="currentAppId"/>'s app.json declares, plus the
    /// declarations of any declared source app that sets <c>propagateDependencies</c>.
    /// An app with no recorded app.json (a decompiled .app dependency, a bundle with no
    /// identity) is left unnarrowed. Propagation out of a real .app package needs nothing
    /// here: BC reads it from that package's own manifest through the loader.
    /// </summary>
    internal static NavCA.SymbolReferenceSpecification[] NarrowToDeclaredReferences(
        NavCA.SymbolReferenceSpecification[] specs, Guid? currentAppId)
    {
        if (currentAppId is not Guid selfId || !_declaredReferences.TryGetValue(selfId, out var self))
            return specs;

        var allowed = AllowedReferences(self);
        return specs.Where(s => IsAllowed(s, allowed)).ToArray();
    }

    private static BundleIdentity? FindRecordedIdentity(DependencyRef dep)
    {
        if (dep.AppId != Guid.Empty)
            return _declaredReferences.TryGetValue(dep.AppId, out var byId) ? byId : null;
        return _declaredReferences.Values.FirstOrDefault(i =>
            string.Equals(i.Name, dep.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Publisher, dep.Publisher, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowed(NavCA.SymbolReferenceSpecification spec, List<DependencyRef> allowed)
    {
        // The Microsoft platform apps stay visible whatever the manifest says: the runner
        // resolves the `application`/`platform` floors to whichever of these the package
        // cache holds, which is not always the propagating Application umbrella BC would
        // reference. Narrowing them would refuse Base Application objects BC accepts.
        if (DependencyResolver.IsMicrosoftPlatformApp(spec.Name ?? "", spec.Publisher ?? ""))
            return true;
        foreach (var d in allowed)
        {
            if (d.AppId != Guid.Empty && spec.AppId != Guid.Empty)
            {
                if (d.AppId == spec.AppId) return true;
                continue;
            }
            if (string.Equals(d.Name, spec.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Publisher, spec.Publisher, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
