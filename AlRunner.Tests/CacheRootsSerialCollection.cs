// CacheRootsSerialCollection — AlRunner.Infrastructure.CacheRoots (issue #1821) is
// PROCESS-WIDE mutable static state (a single `_override` field). CacheRootsTests
// exercises SetOverride/ResetForTests directly; several BcAppSymbolCache test classes
// call BcAppSymbolCache.Get()/GetTableExtensions() IN-PROCESS (not via a spawned
// subprocess), which internally resolves its on-disk path through
// CacheRoots.Resolve("bc-symbols") — so those tests are reading the same shared
// mutable state, just through one more layer of indirection.
//
// xunit runs each test class as its own collection and runs collections IN PARALLEL by
// default (parallelizeTestCollections=true — see xunit.runner.json). Left alone, a
// BcAppSymbolCache test's Get() could resolve mid-test against whatever override
// CacheRootsTests happens to have set at that exact moment on another thread, serving
// it a MISS/HIT against the wrong directory for a reason that has nothing to do with
// its own cache key — the same class of accidental-parallelism bug #1696 hit for
// RecordPatches (see RecordPatchesSerialCollection). DisableParallelization makes this
// collection run on its own, so every class that touches CacheRoots — directly or via
// BcAppSymbolCache — gets the process-static override to itself for the duration.
//
// Tests that spawn the real runner as a SUBPROCESS (CacheRootsIsolationTests,
// SourceDepCacheEnumMetadataTests, etc.) do NOT need to join this collection: each
// subprocess gets its own fresh CacheRoots static state, so there is nothing here for
// them to race with.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CacheRootsSerialCollection
{
    public const string Name = "cache-roots-serial";
}
