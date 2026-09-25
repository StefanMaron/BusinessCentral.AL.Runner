// TestDataStaticsSerialCollection — serialises the classes that MUTATE the process-wide
// --test-data statics (#4220).
//
// TestDataOptions, TestDataNormalization, TestDataProvisioner, BackupReaderTool and
// BackupReaderServe are all `static class`, correctly so: in production they model one CLI invocation's flags. A test
// process runs many logical invocations at once, and xunit gives every test class its own
// collection and runs collections in parallel (xunit.runner.json: parallelizeTestCollections
// true, maxParallelThreads 4), so one class's write lands inside another's arrange/act.
//
// MEASURED, not inferred (#4220): instrumenting the TestDataNormalization.Enabled setter to
// record thread and stack caught the interleaving directly. Between the write that
// TestDataProvisioningTests makes to arm its un-normalized arm and the one that arms its
// normalized arm, TestDataCompanyNormalizationTests set Enabled to true TWICE from another
// thread. The un-normalized cache key was therefore computed with normalization ON, and
// NormalizedRun_AndUnnormalizedRun_DoNotShareAnInstallBaselineCacheKey compared two identical
// keys. The PR body has the timestamped write log.
//
// TRAP: joining a DIFFERENT DisableParallelization collection is equally safe, so the guard
// exempts it. That is a measured property of xunit 2.9.3, not an assumption — two such
// collections ran back to back with a 1.6 ms gap while the collection-less control pair
// overlapped completely, 5 runs out of 5. Re-measure it before relying on it under a
// different xunit. It is what makes this fix also cover TestDataLazyLoadPolicyTests,
// TestDataSameNamedTablesLoadTests and BackupRowProvenanceTests, which mutate the same
// statics from two other serial collections.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestDataStaticsSerialCollection
{
    public const string Name = "test-data-statics-serial";
}
