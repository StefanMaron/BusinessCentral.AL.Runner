// PermissionMetadataStaticsSerialCollection — serialises the classes that inject into
// RecordPatches' permission-metadata reflection statics (#3062).
//
// WithInjectedStatics overwrites RecordPatches._fPermissionSetLookup and _tSummary for the
// duration of one call and restores them in a finally. That is safe for ONE class at a time and
// only for one: xunit gives each test class its own collection by default and runs collections
// in parallel (xunit.runner.json sets maxParallelThreads 4), so a second class doing the same
// injection races the first and each sees the other's fake. Measured when
// PermissionMetadataMethodOverloadGuardTests joined
// PermissionMetadataNullForgivingGuardTests — the older class's ObjectName arm started
// reporting "LazyEx<T>(Func<T>)", the refusal belonging to the NEWER class's fake.
//
// Same stopgap shape as RecordPatchesSerialCollection, and the same caveat: it only works for
// classes that remember to join. Any future class calling WithInjectedStatics must carry
// [Collection(PermissionMetadataStaticsSerialCollection.Name)].
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PermissionMetadataStaticsSerialCollection
{
    public const string Name = "permission-metadata-statics-serial";
}
