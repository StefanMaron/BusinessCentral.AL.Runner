// AlCacheSidecars — the completeness rule for an AL-output cache entry.
//
// A cache HIT skips Emit+Compile entirely, so every piece of state that emit
// populated as a SIDE EFFECT is lost unless it was persisted to a sidecar and
// replayed. Two such side effects exist today:
//
//   <key>.enum-registry.json  — AlEnumMetadataRegistry (BcCompiler.CaptureOutputter)
//   <key>.query-symbols.json  — the compilation's SymbolReference, which carries the
//                               BC-compiler-assigned query column ids. RecordPatches
//                               builds a query's MetaQuery design from it; without it
//                               NCLMetaQuery is null and BC throws a
//                               NullReferenceException inside
//                               NavQuery.ValidateTablesNotVirtual on the first Find.
//
// The query sidecar is only written for bundles that actually declare an AL query, so
// it is required for a HIT only when the bundle declares one. That also self-heals
// cache entries written before the sidecar existed: they simply miss once.
namespace AlRunner.Infrastructure;

public static class AlCacheSidecars
{
    public const string EnumRegistrySuffix = ".enum-registry.json";
    public const string QuerySymbolsSuffix = ".query-symbols.json";

    /// <summary>
    /// True when a cache entry carries every artifact a HIT needs. A bundle declaring an
    /// AL query additionally requires its query-symbols sidecar.
    /// </summary>
    public static bool IsCompleteEntry(
        bool dllExists, bool enumSidecarExists, bool bundleDeclaresQuery, bool querySidecarExists)
        => dllExists && enumSidecarExists && (!bundleDeclaresQuery || querySidecarExists);
}
