// RecordPatches.NclMetaExtensionObjects: the NCLMetaApplicationObject for a tableextension,
// pageextension or reportextension id, served from NCLMetadata_GetMetaApplicationObjectByType
// (#5384).
using System.Collections.Concurrent;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Apps.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // One object per (extension kind, id), never replaced: it carries the MethodNumberCounter that
    // gives each of the extension's methods its coverage scope id, and BC keeps that counter for the
    // object's lifetime (CodeCoveragePatches.EnsureMethodNumberCounter).
    private static readonly ConcurrentDictionary<(int Kind, int Id), object> _metaExtensionObjects = new();

    private static void ClearMetaExtensionObjects() => _metaExtensionObjects.Clear();

    /// <summary>
    /// BC's NCLMetadata.GetMetaApplicationObject sends every extension type to
    /// GetExtensionApplicationObject, which answers an NCLTableExtension / NCLPageExtension /
    /// NCLReportExtension. With code coverage recording, ALCodeEnvironment reads only
    /// MethodNumberCounter off it, so the answer here is BC's own empty extension object for any
    /// extension whose CLR type is loaded, source-compiled or precompiled. OBSERVABLY EQUIVALENT:
    /// the same internal factory BC's CreateEmptyMetaApplicationObject family uses, owned by the app
    /// the CLR type was registered under (the one member BC's constructor insists on). Null for an id
    /// nothing declares, so the caller's not-found throw stays. Citations: corpus "Test Code Cov Table
    /// Extension", runner-extras code-coverage-extension-metadata, #5384. TRAP: a new extension kind
    /// needs its own entry in <see cref="ExtensionKindShape"/>.
    /// </summary>
    internal static object? EnsureExtensionMetaApplicationObject(ObjectType kind, int id)
    {
        if (ExtensionKindShape(kind) is not var (ncl, factory, clrPrefix, clrBase))
            return null;
        if (_metaExtensionObjects.TryGetValue(((int)kind, id), out var cached))
            return cached;
        var clrType = FindExtensionClrType(clrPrefix, clrBase, id);
        if (clrType == null)
            return null;

        var create = BcShape.RequiredMethod(ncl, factory, BindingFlags.NonPublic | BindingFlags.Static,
            "extension metadata for code coverage (#5384)", $"{ncl.Name}.{factory}",
            "without it a call into an extension procedure fails while code coverage is recording",
            new[] { typeof(INCLMetaApplicationObjectLoader), typeof(int), typeof(NavAppRuntimeMetadata) });
        // The base constructor refuses a null owning app. Same owner BC's delta retriever states for a
        // source-compiled extension (NavReportSync.ReportExtensionOwningApp): the app its CLR type
        // was registered under, an empty id when the runner does not know it.
        var appId = BcRuntime.OwningAppIdFor(clrType, new ApplicationObjectId(kind, id));
        var owner = InstallExecutionContext.CreateRuntimeMetadata(appId ?? Guid.Empty, clrPrefix + id,
            string.Empty, "1.0.0.0", appId is { } g ? AppPackageIdentity.RuntimePackageIdFor(g) : Guid.Empty);
        var meta = create.Invoke(null, new object?[] { RunnerMetaApplicationObjectLoader.Instance, id, owner })
            ?? throw new BcShapeGapException("extension metadata for code coverage (#5384)", $"{ncl.Name}.{factory}",
                "factory returned null");
        return _metaExtensionObjects.GetOrAdd(((int)kind, id), meta);
    }

    private static (Type Ncl, string Factory, string ClrPrefix, string ClrBase)? ExtensionKindShape(ObjectType kind)
        => kind switch
        {
            ObjectType.TableExtension => (typeof(NCLTableExtension),
                "CreateEmptyNCLTableExtension", "TableExtension", "NavRecordExtension"),
            ObjectType.PageExtension => (typeof(NCLPageExtension),
                "CreateEmptyNCLPageExtension", "PageExtension", "NavFormExtension"),
            ObjectType.ReportExtension => (typeof(NCLReportExtension),
                "CreateEmptyNCLReportExtension", "ReportExtension", "NavReportExtension"),
            _ => null,
        };

    private static Type? FindExtensionClrType(string clrPrefix, string clrBase, int id)
    {
        var name = clrPrefix + id;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // A previous server/watch generation of this module (#1901, #4099).
            if (BcRuntime.IsStaleBundleAssembly(asm)) continue;
            try
            {
                var t = AssemblyTypeIndex.For(asm).FindFirst(name, t =>
                {
                    for (var b = t.BaseType; b != null; b = b.BaseType)
                        if (b.Name == clrBase) return true;
                    return false;
                });
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }
}
