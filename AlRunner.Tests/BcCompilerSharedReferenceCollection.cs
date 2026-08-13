// BcCompilerSharedReferenceCollection — serialises the tests that drive BcCompiler's
// process-wide symbol-reference loader statics.
//
// GetSharedReferences memoises the reference loader, its signature, the JSON loader chain,
// the resolved dep list, the "current app identity" and the .app scan-metadata cache in
// STATIC fields (deliberately: the whole point of the memo is that it outlives any single
// compile — see BcCompiler and issue #1831). A test that asserts on rebuild COUNTS is
// therefore reading shared mutable state, and xunit runs test collections in parallel
// (maxParallelThreads: 4 since #1818), so anything else compiling AL in the same process
// would perturb the count.
//
// DisableParallelization gives this collection the statics to itself for its duration.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BcCompilerSharedReferenceCollection
{
    public const string Name = "bccompiler-shared-references";
}
