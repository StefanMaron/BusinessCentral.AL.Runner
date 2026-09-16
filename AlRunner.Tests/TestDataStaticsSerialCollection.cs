// TestDataStaticsSerialCollection -- AlRunner.Infrastructure.TestDataNormalization.Enabled and
// TestDataOptions' Enabled/ExplicitBackupPath/CompanyOverride are PROCESS-WIDE mutable statics
// (issue #4220). Two test classes write them:
//
//   TestDataCompanyNormalizationTests   sets TestDataNormalization.Enabled = true in 16 tests
//   TestDataProvisioningTests           computes a cache key with the flag FALSE, then again
//                                       with it TRUE, and asserts the two keys differ
//
// xunit runs each test class as its own collection and runs collections IN PARALLEL
// (parallelizeTestCollections=true, maxParallelThreads=4 -- see xunit.runner.json). So the
// first of those two key computations can read `Enabled` while the other class is inside a
// test that has set it to true, both keys then come out equal, and
// NormalizedRun_AndUnnormalizedRun_DoNotShareAnInstallBaselineCacheKey fails an Assert.NotEqual
// for a reason that has nothing to do with the code it is testing.
//
// Measured on origin/main before the fix: 1 red in 12 consecutive runs of the three-class
// filter, with NO rebuild between them -- a genuine race rather than the deterministic
// first-run-after-build effect #4220 originally described. Setting parallelizeTestCollections
// = false made the same filter green with no code change, which is what identified the
// mechanism; this collection is that isolation scoped to the classes that need it rather than
// to the whole assembly.
//
// MsBucketWorkflowTests is deliberately NOT in this collection. It reads only the CONSTANT
// TestDataNormalization.FlagName and never the mutable flag, so it cannot corrupt or be
// corrupted by these statics. It does make the failure easier to observe -- it adds enough
// parallel work to widen the window in which the two writers overlap -- but "helps reproduce"
// is not "shares the state", and putting it here would serialise a class for no isolation
// benefit. The two-class filter alone was green 5/5 for the same reason: fewer collections in
// flight, narrower window, same latent race.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestDataStaticsSerialCollection
{
    public const string Name = "test-data-statics-serial";
}
