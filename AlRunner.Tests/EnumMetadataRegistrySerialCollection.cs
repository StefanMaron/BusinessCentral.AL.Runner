// EnumMetadataRegistrySerialCollection — serialises the classes that MUTATE
// AlEnumMetadataRegistry's process-wide statics (#4195).
//
// AlEnumMetadataRegistry is a `static class` (AlRunner/Patches/EnumMetadataPatches.cs), so its
// registrations are one table shared by the whole test process. Seven classes call
// AlEnumMetadataRegistry.Clear() in a constructor or Dispose to isolate themselves — which
// isolates each from ITSELF and from nothing else: xunit gives every test class its own
// collection by default and runs collections in parallel (xunit.runner.json:
// parallelizeTestCollections true, maxParallelThreads 4). So one class's Clear() lands in the
// middle of another's arrange/act.
//
// MEASURED before the fix, running those seven classes together on one tree: 2 of 6 runs red,
// and the failures moved between runs — EnumExtensionSidecarRoundTripTests reporting
// "Expected: True, Actual: False" and EnumSidecarEnumLevelPropertyRoundTripTests reporting
// "Expected: 3" against Actual 0 on one run and 1 on another. A count that differs between two
// runs of the same code is a foreign Clear() landing at different points, not an assertion
// that is wrong.
//
// Same stopgap shape as PermissionMetadataStaticsSerialCollection (#3062) and
// RecordPatchesSerialCollection, and it WOULD carry the same caveat — that it only protects
// classes which remember to join — except that EnumMetadataRegistryCollectionGuardTests now
// enforces membership, so a new mutating class fails a test rather than reintroducing the race
// silently.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnumMetadataRegistrySerialCollection
{
    public const string Name = "enum-metadata-registry-serial";
}
